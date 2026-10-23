namespace CommandDeck;

public sealed partial class CodexAgentBackend : IAgentBackend
{
    private readonly AgentOptions _options;
    private readonly CodexCredentialStore _credentials;
    private readonly Func<int, IAgentProcessLauncher> _launcherFactory;
    private readonly IAiCredentialResolver _credentialResolver;
    private readonly string _clientVersion;
    private readonly SemaphoreSlim _gate = new( 1, 1 );
    private readonly object _authenticationLock = new( );
    private CodexBinaryInfo? _binary;
    private CodexAppServerClient? _client;

    private CodexRuntimeHome? _runtimeHome;

    // The authority epoch the running process was admitted under; null for a
    // restricted process started for login or model discovery.
    private long? _clientEpoch;
    private IAgentAuthorityEpoch? _clientAuthority;
    private AgentLoginState _login;
    private string _health = AgentHealthStates.Configured;
    private AiError? _lastError;
    private string? _pendingLoginId;
    private Action<string?>? _accountUpdated;
    private Action<bool, string?, string?>? _loginCompleted;
    private int _disposed;

    public CodexAgentBackend( AgentOptions options, CodexCredentialStore credentials, Func<int, IAgentProcessLauncher>? launcherFactory = null,
        IAiCredentialResolver? credentialResolver = null, string? clientVersion = null )
    {
        if ( options is null )
        {
            throw new ArgumentNullException( nameof( options ) );
        }

        _options = options;

        if ( credentials is null )
        {
            throw new ArgumentNullException( nameof( credentials ) );
        }

        _credentials = credentials;
        _launcherFactory = launcherFactory ?? CreateDefaultLauncher;
        _credentialResolver = credentialResolver ?? new AiCredentialResolver( );
        _clientVersion = clientVersion ?? "0.1.0";

        _login = new AgentLoginState
        {
            State = _credentials.Exists ? AgentAuthenticationStates.Authenticated : AgentAuthenticationStates.Unauthenticated,
            SupportedMethods = [AgentLoginMethods.DeviceCode, AgentLoginMethods.ApiKey]
        };
    }

    public string Id => AgentBackendIds.Codex;

    public int TrustLevel { get; private set; } = AgentTrustLevels.Default;

    public bool WebSearchEnabled { get; private set; }

    public bool IsRunning => _client is not null && !_client.IsFaulted;

    public string? BinaryPath => _binary?.CanonicalPath;

    public string? BinaryVersion => _binary?.Version;

    public bool? BinaryReviewed => _binary?.Reviewed;

    public string RuntimeGeneration => _runtimeHome?.Generation ?? string.Empty;

    public AgentBackendDescriptor Describe( ) => new( )
    {
        Id = Id,
        DisplayName = "Codex",
        Kind = AgentBackendKinds.CodexAppServer,
        ProcessLocality = AiLocality.Local,
        InferenceLocality = AiLocality.Remote,
        Available = _health is not AgentHealthStates.Unavailable and not AgentHealthStates.Incompatible,
        HealthState = IsRunning ? AgentHealthStates.Ready : _health,
        AuthenticationState = _login.State,
        Capabilities = new AgentBackendCapabilities
        {
            Conversation = true,
            Tools = true,
            Reasoning = true,
            ModelDiscovery = true,
            PreflightRequired = false,
            Usage = true,
            Login = true,
            ContractVersion = "codex-app-server-v2"
        },
        Explanation = _lastError?.Summary ?? "Codex runs locally; inference uses your OpenAI account.",
        Error = _lastError
    };

    public async ValueTask<AgentModelInventory> DiscoverModelsAsync( bool includeHidden, CancellationToken cancellationToken )
    {
        try
        {
            var client = await EnsureStartedAsync( cancellationToken )
                .ConfigureAwait( false );

            return await client
                .ListModelsAsync( includeHidden, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( AiOperationException exception )
        {
            return new AgentModelInventory
            {
                BackendId = Id,
                Error = new AiError { Category = exception.Category, Summary = exception.SafeSummary ?? "Model discovery failed." }
            };
        }
        catch ( Exception exception ) when ( exception is JsonRpcException or InvalidDataException or EndOfStreamException )
        {
            return new AgentModelInventory
            {
                BackendId = Id,
                Error = new AiError { Category = AiErrorCategories.MalformedResponse, Summary = "Codex returned an unusable model list." }
            };
        }
    }

    public async ValueTask<IAgentSession> StartSessionAsync( AgentSessionOptions options, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( options );

        AgentContractValidator.ValidateTrustLevel( options.TrustLevel );
        var admission = options.Admission;
        if ( NeedsUnrestrictedAdmission( options, admission ) )
        {
            throw new InvalidOperationException( "An unrestricted session requires an authority admission." );
        }

        CodexAppServerClient client;
        string cwd;
        string generation;

        await _gate
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            // Process admission: the authority the session was prepared under
            // must still be current where a process is launched or reused. A
            // revocation stops processes under this same gate, so a launch that
            // passes here is either newer than the change or stopped by it.
            if ( admission is { IsCurrent: false } )
            {
                throw new AgentAuthorityChangedException( );
            }

            TrustLevel = options.TrustLevel;
            WebSearchEnabled = options.WebSearchEnabled;

            client = await EnsureStartedUnderGateAsync( admission, cancellationToken )
                .ConfigureAwait( false );

            generation = _runtimeHome!.Generation;
            cwd = GetWorkingDirectory( options, _runtimeHome! );
        }
        finally
        {
            _gate.Release( );
        }

        var session = await client
            .StartThreadAsync( options, cwd, cancellationToken )
            .ConfigureAwait( false );

        lock ( _authenticationLock )
        {
            if ( ReferenceEquals( _client, client ) )
            {
                session.ProcessGeneration = generation;

                return session;
            }
        }

        await session
            .DisposeAsync( )
            .ConfigureAwait( false );

        throw new OperationCanceledException( "The Codex runtime was stopped." );
    }

    public async ValueTask StopAsync( CancellationToken cancellationToken )
    {
        await _gate
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            await StopUnderGateAsync( cancellationToken: CancellationToken.None )
                .ConfigureAwait( false );
        }
        finally
        {
            _gate.Release( );
        }
    }

    public async ValueTask StopBeforeAsync( long authorityEpoch, CancellationToken cancellationToken )
    {
        await _gate
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            if ( HasRevokedClient( authorityEpoch ) )
            {
                await StopUnderGateAsync( cancellationToken: CancellationToken.None )
                    .ConfigureAwait( false );
            }
        }
        finally
        {
            _gate.Release( );
        }
    }

    public async ValueTask StopGenerationAsync( string processGeneration, CancellationToken cancellationToken )
    {
        await _gate
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            if ( IsCurrentGeneration( processGeneration ) )
            {
                await StopUnderGateAsync( cancellationToken: CancellationToken.None )
                    .ConfigureAwait( false );
            }
        }
        finally
        {
            _gate.Release( );
        }
    }

    // Login and model discovery: no session, so no admission.
    private async ValueTask<CodexAppServerClient> EnsureStartedAsync( CancellationToken cancellationToken )
    {
        await _gate
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            return await EnsureStartedUnderGateAsync( admission: null, cancellationToken )
                .ConfigureAwait( false );
        }
        finally
        {
            _gate.Release( );
        }
    }

    private static AgentLoginState BuildLoginState( string state, string? message, string? accountType ) => new( )
    {
        State = state,
        Method = accountType == "apiKey" ? AgentLoginMethods.ApiKey : null,
        Message = message,
        SupportedMethods = [AgentLoginMethods.DeviceCode, AgentLoginMethods.ApiKey]
    };

    public async ValueTask DisposeAsync( )
    {
        if ( Interlocked.Exchange( ref _disposed, 1 ) != 0 )
        {
            return;
        }

        await StopAsync( CancellationToken.None )
            .ConfigureAwait( false );

        _gate.Dispose( );
    }

    private static bool NeedsUnrestrictedAdmission( AgentSessionOptions options, AgentAdmission? admission )
    {
        return admission is null && options.TrustLevel >= AgentTrustLevels.Unrestricted;
    }

    private static string GetWorkingDirectory( AgentSessionOptions options, CodexRuntimeHome runtimeHome )
    {
        if ( options.TrustLevel >= AgentTrustLevels.Unrestricted )
        {
            return Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        }

        return runtimeHome.Path;
    }

    private bool HasRevokedClient( long authorityEpoch )
    {
        return _client is not null && (_clientEpoch is null || _clientEpoch < authorityEpoch);
    }

    private bool IsCurrentGeneration( string processGeneration )
    {
        return _runtimeHome is not null && string.Equals( _runtimeHome.Generation, processGeneration, StringComparison.Ordinal );
    }
}
