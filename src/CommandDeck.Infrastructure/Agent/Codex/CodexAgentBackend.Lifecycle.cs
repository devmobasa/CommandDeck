using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public sealed partial class CodexAgentBackend
{
    // A process serves the authority it was admitted under. A session reuses it
    // only at that same epoch, or adopts a restricted process that login or model
    // discovery started; anything else is stopped and relaunched. Without an
    // admission a process is reused only while its epoch is current, and a new
    // one always launches restricted.
    private async ValueTask<CodexAppServerClient> EnsureStartedUnderGateAsync( AgentAdmission? admission, CancellationToken cancellationToken )
    {
        if ( TryGetReusableClient( out var running ) )
        {
            if ( admission is null )
            {
                if ( HasCurrentAuthority( ) )
                {
                    return running;
                }
            }
            else if ( CanReuseClient( admission ) )
            {
                _clientEpoch = admission.Epoch;
                _clientAuthority = admission.Authority;

                return running;
            }
        }

        await StopUnderGateAsync( cancellationToken: CancellationToken.None )
            .ConfigureAwait( false );

        if ( admission is null )
        {
            TrustLevel = AgentTrustLevels.Default;
            WebSearchEnabled = false;
        }

        var started = await StartUnderGateAsync( cancellationToken )
            .ConfigureAwait( false );

        _clientEpoch = admission?.Epoch;
        _clientAuthority = admission?.Authority;

        return started;
    }

    private bool TryGetReusableClient( [NotNullWhen( true )] out CodexAppServerClient? running )
    {
        running = _client;

        return running is { IsFaulted: false } && _runtimeHome is not null && SameTrustClass( _runtimeHome.TrustLevel, TrustLevel );
    }

    private async ValueTask<CodexAppServerClient> StartUnderGateAsync( CancellationToken cancellationToken )
    {
        _health = AgentHealthStates.Starting;

        try
        {
            _binary = await ResolveBinaryAsync( cancellationToken )
                .ConfigureAwait( false );

            _runtimeHome = CodexRuntimeHome.Create( _options.RuntimeDirectory, TrustLevel, WebSearchEnabled );
            var imported = _credentials.Read( );

            try
            {
                _runtimeHome.ImportCredential( imported );
            }
            finally
            {
                if ( imported is not null )
                {
                    CodexCredentialStore.Clear( imported );
                }
            }

            CodexLaunchRequest request = CreateLaunchRequest( _binary, _runtimeHome );

            var handle = await _launcherFactory( TrustLevel )
                .LaunchAsync( request, cancellationToken )
                .ConfigureAwait( false );

            CodexAppServerClient client;

            try
            {
                client = await CodexAppServerClient
                    .ConnectAsync( handle, _clientVersion, cancellationToken )
                    .ConfigureAwait( false );
            }
            catch
            {
                await handle
                    .DisposeAsync( )
                    .ConfigureAwait( false );

                throw;
            }

            lock ( _authenticationLock )
            {
                _accountUpdated = authMode => OnAccountUpdated( client, authMode );
                _loginCompleted = ( success, _, loginId ) => OnLoginCompleted( client, success, loginId );
                client.AccountUpdated += _accountUpdated;
                client.LoginCompleted += _loginCompleted;
                _client = client;
            }

            _health = AgentHealthStates.Ready;
            _lastError = null;
            using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
            refreshTimeout.CancelAfter( TimeSpan.FromSeconds( AgentLimits.StartupDeadlineSeconds ) );

            try
            {
                await RefreshLoginAsync( client, refreshTimeout.Token )
                    .ConfigureAwait( false );
            }
            catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
            {
                throw new AiOperationException( AiErrorCategories.Timeout, safeSummary: "Codex did not report its login state in time." );
            }

            return client;
        }
        catch ( AiOperationException exception )
        {
            _health = GetFailureHealth( exception );

            _lastError = new AiError { Category = exception.Category, Summary = exception.SafeSummary ?? "Codex could not start." };

            await StopUnderGateAsync( cancellationToken: CancellationToken.None )
                .ConfigureAwait( false );

            throw;
        }
        catch
        {
            await StopUnderGateAsync( cancellationToken: CancellationToken.None )
                .ConfigureAwait( false );

            throw;
        }
    }

    private async ValueTask<CodexBinaryInfo> ResolveBinaryAsync( CancellationToken cancellationToken )
    {
        if ( IsBinaryUnchanged( ) )
        {
            return _binary;
        }

        return await CodexBinaryLocator
            .LocateAsync( _options, cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask StopUnderGateAsync( bool logout = false, CancellationToken cancellationToken = default )
    {
        CodexAppServerClient? client;
        CodexRuntimeHome? runtimeHome;

        lock ( _authenticationLock )
        {
            client = _client;
            runtimeHome = _runtimeHome;

            if ( client is not null )
            {
                client.AccountUpdated -= _accountUpdated;
                client.LoginCompleted -= _loginCompleted;

                if ( !logout )
                {
                    CopyCredentialBack( );
                }
            }

            _client = null;
            _runtimeHome = null;
            _clientEpoch = null;
            _clientAuthority = null;
            _accountUpdated = null;
            _loginCompleted = null;

            if ( _health == AgentHealthStates.Ready )
            {
                _health = AgentHealthStates.Configured;
            }

            if ( _login.State == AgentAuthenticationStates.Pending )
            {
                _pendingLoginId = null;

                _login = BuildLoginState( _credentials.Exists ? AgentAuthenticationStates.Authenticated : AgentAuthenticationStates.Unauthenticated, null,
                    null );
            }
        }

        try
        {
            if ( CanLogout( logout, client ) )
            {
                try
                {
                    await client
                        .LogoutAsync( cancellationToken )
                        .ConfigureAwait( false );
                }
                catch ( JsonRpcException )
                {
                }
            }
        }
        finally
        {
            try
            {
                if ( client is not null )
                {
                    await client
                        .DisposeAsync( )
                        .ConfigureAwait( false );
                }
            }
            finally
            {
                runtimeHome?.Dispose( );
            }
        }
    }

    private static bool SameTrustClass( int running, int requested ) =>
        (running >= AgentTrustLevels.Unrestricted) == (requested >= AgentTrustLevels.Unrestricted);

    private IAgentProcessLauncher CreateDefaultLauncher( int trustLevel )
    {
        if ( trustLevel >= AgentTrustLevels.Unrestricted )
        {
            return new TransientUnitCodexLauncher( _options.SystemdRunPath );
        }
        else
        {
            return new BubblewrapCodexLauncher( _options.BubblewrapPath );
        }
    }

    private bool HasCurrentAuthority( )
    {
        return _clientEpoch is not { } epoch || _clientAuthority?.IsCurrent( epoch ) == true;
    }

    private bool CanReuseClient( AgentAdmission admission )
    {
        return _clientEpoch == admission.Epoch || _clientEpoch is null && TrustLevel < AgentTrustLevels.Unrestricted;
    }

    private CodexLaunchRequest CreateLaunchRequest( CodexBinaryInfo binary, CodexRuntimeHome runtimeHome )
    {
        return new( )
        {
            BinaryPath = binary.CanonicalPath,
            RuntimeHome = runtimeHome.Path,
            Generation = runtimeHome.Generation,
            TrustLevel = TrustLevel,
            WorkingDirectory = GetLaunchWorkingDirectory( runtimeHome ),
            SocketPath = TrustLevel >= AgentTrustLevels.Unrestricted ? runtimeHome.SocketPath : null
        };
    }

    private static string GetFailureHealth( AiOperationException exception )
    {
        if ( exception.Category == AiErrorCategories.Unsupported )
        {
            return AgentHealthStates.Incompatible;
        }

        return AgentHealthStates.Unavailable;
    }

    [MemberNotNullWhen( true, nameof( _binary ) )]
    private bool IsBinaryUnchanged( )
    {
        return _binary is not null && CodexBinaryLocator.HasSameIdentity( _binary.CanonicalPath, _binary.IdentityToken );
    }

    private static bool CanLogout( bool logout, [NotNullWhen( true )] CodexAppServerClient? client )
    {
        return logout && client is { IsFaulted: false };
    }

    private string GetLaunchWorkingDirectory( CodexRuntimeHome runtimeHome )
    {
        if ( TrustLevel >= AgentTrustLevels.Unrestricted )
        {
            return Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        }

        return runtimeHome.Path;
    }
}
