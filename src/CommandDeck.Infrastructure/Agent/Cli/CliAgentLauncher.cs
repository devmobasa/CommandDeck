using System.Diagnostics;

namespace CommandDeck;

public sealed class CliAgentLaunchRequest
{
    public required string BinaryPath { get; init; }

    /// <summary>The install tree bound read-only at its real path (a script resolves its own directory).</summary>
    public required string InstallPath { get; init; }

    public required string HomePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>( StringComparer.Ordinal );
    public bool StandardInput { get; init; }
}

public interface ICliAgentProcess : IAsyncDisposable
{
    Stream Output { get; }
    Stream Input { get; }
    Task<int> Exited { get; }
    string Diagnostics { get; }
    void Terminate( );
}

public interface ICliAgentLauncher
{
    ValueTask<ICliAgentProcess> LaunchAsync( CliAgentLaunchRequest request, CancellationToken cancellationToken );
}

/// <summary>
/// Runs a CLI agent inside the same Bubblewrap profile as the Codex App
/// Server: the real home is hidden, <c>/run</c> and <c>/tmp</c> are private,
/// the environment is rebuilt from an allowlist, and only the owned home, the
/// scratch directory, and the CLI's own install tree are visible.
/// </summary>
public sealed class BubblewrapCliAgentLauncher( string bubblewrapPath ) : ICliAgentLauncher
{
    public static IReadOnlyList<string> BuildArguments( CliAgentLaunchRequest request )
    {
        ArgumentNullException.ThrowIfNull( request );

        var home = request.HomePath;
        List<string> arguments = AgentSandboxArguments.Create( );
        AgentSandboxArguments.AddResolverBind( arguments );

        arguments.AddRange( [
            "--dir", home,
            "--bind", home, home,
            "--dir", request.WorkingDirectory,
            "--bind", request.WorkingDirectory, request.WorkingDirectory,
            "--ro-bind", request.InstallPath, request.InstallPath,
            "--chdir", request.WorkingDirectory,
            "--clearenv",
            "--setenv", EnvironmentVariableNames.Home, home,
        ] );

        AgentSandboxArguments.AddXdgEnvironment( arguments, home );

        arguments.AddRange( [
            "--setenv", EnvironmentVariableNames.ExecutablePath, ProcessEnvironmentDefaults.SystemExecutablePath,
            "--setenv", EnvironmentVariableNames.Language, ProcessEnvironmentDefaults.Utf8Locale,
            "--setenv", EnvironmentVariableNames.Terminal, "dumb",
            "--setenv", EnvironmentVariableNames.NoColor, "1"
        ] );

        foreach ( (var key, var value) in request.Environment.OrderBy( static pair => pair.Key, StringComparer.Ordinal ) )
        {
            if ( IsInvalidEnvironmentEntry( key, value ) )
            {
                throw new InvalidOperationException( "A CLI agent environment entry is invalid." );
            }

            arguments.AddRange( ["--setenv", key, value] );
        }

        arguments.AddRange( ["--", request.BinaryPath] );
        arguments.AddRange( request.Arguments );
        CliAgentProfile.AssertSafe( arguments );

        return arguments;
    }

    public ValueTask<ICliAgentProcess> LaunchAsync( CliAgentLaunchRequest request, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( request );

        if ( !File.Exists( bubblewrapPath ) )
        {
            throw new AiOperationException( AiErrorCategories.Unavailable, safeSummary: "Bubblewrap is not installed; the sandboxed agent cannot start." );
        }

        Directory.CreateDirectory( AgentSandboxArguments.XdgDirectory( request.HomePath ) );
        var arguments = BuildArguments( request );
        ProcessStartInfo startInfo = CreateStartInfo( request );
        startInfo.Environment.Clear( );
        startInfo.Environment[EnvironmentVariableNames.ExecutablePath] = ProcessEnvironmentDefaults.SystemExecutablePath;
        startInfo.Environment[EnvironmentVariableNames.Language] = ProcessEnvironmentDefaults.Utf8Locale;

        foreach ( var argument in arguments )
        {
            startInfo.ArgumentList.Add( argument );
        }

        Process process = new( ) { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if ( !process.Start( ) )
            {
                throw new AiOperationException( AiErrorCategories.Unavailable, safeSummary: "The sandboxed agent process could not start." );
            }
        }
        catch ( Exception exception ) when ( exception is System.ComponentModel.Win32Exception or InvalidOperationException )
        {
            process.Dispose( );

            throw new AiOperationException( AiErrorCategories.Unavailable, safeSummary: "The sandboxed agent process could not start." );
        }

        cancellationToken.ThrowIfCancellationRequested( );

        return ValueTask.FromResult<ICliAgentProcess>( new CliAgentProcess( process, request.StandardInput ) );
    }

    private static bool IsInvalidEnvironmentEntry( string key, string value )
    {
        return key.Contains( '=' ) || key.Contains( '\0' ) || value.Contains( '\0' );
    }

    private ProcessStartInfo CreateStartInfo( CliAgentLaunchRequest request )
    {
        return new( bubblewrapPath )
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory,
            CreateNoWindow = true
        };
    }
}

public sealed class CliAgentProcess : ICliAgentProcess
{
    private const int DiagnosticReadBufferCharacters = 1024;
    private const int StopTimeoutSeconds = 3;

    private const int MaximumDiagnosticCharacters = 8 * 1024;
    private readonly Process _process;
    private readonly Task _stderrDrain;
    private readonly System.Text.StringBuilder _diagnostics = new( );
    private int _disposed;

    public CliAgentProcess( Process process, bool keepStandardInput )
    {
        _process = process;
        Output = process.StandardOutput.BaseStream;
        Input = process.StandardInput.BaseStream;

        if ( !keepStandardInput )
        {
            try
            {
                process.StandardInput.Close( );
            }
            catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
            {
            }
        }

        Exited = WaitForExitAsync( process );
        _stderrDrain = Task.Run( DrainStandardErrorAsync );
    }

    public Stream Output { get; }

    public Stream Input { get; }

    public Task<int> Exited { get; }

    public string Diagnostics
    {
        get
        {
            lock ( _diagnostics )
            {
                return _diagnostics.ToString( );
            }
        }
    }

    public void Terminate( )
    {
        try
        {
            if ( !_process.HasExited )
            {
                _process.Kill( entireProcessTree: true );
            }
        }
        catch ( InvalidOperationException )
        {
        }
        catch ( System.ComponentModel.Win32Exception )
        {
        }
    }

    private static async Task<int> WaitForExitAsync( Process process )
    {
        await process
            .WaitForExitAsync( )
            .ConfigureAwait( false );

        return process.ExitCode;
    }

    private async Task DrainStandardErrorAsync( )
    {
        try
        {
            var buffer = new char[DiagnosticReadBufferCharacters];

            while ( true )
            {
                var read = await _process
                    .StandardError.ReadAsync( buffer )
                    .ConfigureAwait( false );

                if ( read <= 0 )
                {
                    return;
                }

                lock ( _diagnostics )
                {
                    if ( _diagnostics.Length < MaximumDiagnosticCharacters )
                    {
                        _diagnostics.Append( buffer, 0, Math.Min( read, MaximumDiagnosticCharacters - _diagnostics.Length ) );
                    }
                }
            }
        }
        catch ( Exception exception ) when ( exception is IOException or ObjectDisposedException or InvalidOperationException )
        {
        }
    }

    public async ValueTask DisposeAsync( )
    {
        if ( Interlocked.Exchange( ref _disposed, 1 ) != 0 )
        {
            return;
        }

        Terminate( );

        try
        {
            await Exited
                .WaitAsync( TimeSpan.FromSeconds( StopTimeoutSeconds ) )
                .ConfigureAwait( false );
        }
        catch ( TimeoutException )
        {
        }

        try
        {
            await _stderrDrain
                .WaitAsync( TimeSpan.FromSeconds( 1 ) )
                .ConfigureAwait( false );
        }
        catch ( TimeoutException )
        {
        }

        _process.Dispose( );
    }
}
