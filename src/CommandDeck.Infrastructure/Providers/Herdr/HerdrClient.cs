using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CommandDeck;

/// <summary>
/// The only boundary between CommandDeck and the Herdr CLI. Every call targets
/// the default session with a fixed argv, a cleared environment, and bounded
/// output, memory, and time. Ordinary process, JSON, and Herdr failures become
/// typed outcomes; only caller cancellation propagates as an exception.
/// </summary>
public sealed partial class HerdrClient
{
    private const string WorkspaceIdField = "workspace_id";
    private const string TabInfoType = "tab_info";
    private const string TabIdField = "tab_id";
    private const string WorkspaceInfoType = "workspace_info";

    private const string NoFocusOption = "--no-focus";
    private const string WorkspaceCommand = "workspace";
    private const int ProjectListOutputBytes = ByteUnits.BytesPerMebibyte;
    private const string LabelOption = "--label";
    private const string CreateCommand = "create";
    private const string CwdOption = "--cwd";
    private const string ListCommand = "list";
    private const string PaneCommand = "pane";
    private const int MaximumPathCharacters = 4096;
    private const string WorkspaceOption = "--workspace";
    private const string RenameCommand = "rename";
    private const string TabCommand = "tab";
    private const string SessionOption = "--session";
    private const string DefaultCommand = "default";
    private const string StatusCommand = "status";
    private const string ServerCommand = "server";
    private const string JsonOption = "--json";
    private const string FocusCommand = "focus";
    private const int MaximumAgentStatusCharacters = 32;

    public const string Executable = "herdr";
    public const int MaximumWorkspaces = 128;
    public const int MaximumTabs = 512;
    public const int MaximumIdentifierCharacters = 512;
    public const int MaximumLabelCharacters = 512;

    public static readonly TimeSpan StatusTimeout = TimeSpan.FromMilliseconds( 250 );
    public static readonly TimeSpan ListTimeout = TimeSpan.FromMilliseconds( 250 );
    public static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds( 1 );

    public const int StatusStandardOutputBytes = 32 * ByteUnits.BytesPerKibibyte;
    public const int StatusStandardErrorBytes = 16 * ByteUnits.BytesPerKibibyte;
    public const int WorkspaceListStandardOutputBytes = 64 * ByteUnits.BytesPerKibibyte;
    public const int WorkspaceListStandardErrorBytes = 16 * ByteUnits.BytesPerKibibyte;
    public const int TabListStandardOutputBytes = 256 * ByteUnits.BytesPerKibibyte;
    public const int TabListStandardErrorBytes = 32 * ByteUnits.BytesPerKibibyte;
    public const int FocusStandardOutputBytes = 64 * ByteUnits.BytesPerKibibyte;
    public const int FocusStandardErrorBytes = 32 * ByteUnits.BytesPerKibibyte;
    public const long MaximumMemoryBytes = 128L * ByteUnits.BytesPerMebibyte;

    private static readonly string[] ForwardedEnvironment =
        [EnvironmentVariableNames.Home, XdgEnvironmentNames.ConfigHome, XdgEnvironmentNames.RuntimeDirectory];

    private readonly BoundedProcessRunner _run;
    private readonly Func<bool> _available;
    private readonly Func<string, string?> _environment;

    public HerdrClient( BoundedProcessRunner? runner = null, Func<bool>? available = null, Func<string, string?>? environment = null )
    {
        _run = runner ?? DefaultRunAsync;
        _available = available ?? (static ( ) => ProcessRunner.ExistsOnPath( Executable ));
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public bool IsInstalled => _available( );

    public static bool IsValidIdentifier( string? value ) =>
        !string.IsNullOrEmpty( value ) && value.Length <= MaximumIdentifierCharacters && !value.Any( char.IsControl );

    // Never forwards HERDR_SESSION or HERDR_SOCKET_PATH; --session default and
    // this allow-list decide which socket the CLI resolves.
    internal IReadOnlyDictionary<string, string> BuildEnvironment( )
    {
        Dictionary<string, string> environment = CreateEnvironment( );

        foreach ( var name in ForwardedEnvironment )
        {
            var value = _environment( name );
            if ( !string.IsNullOrEmpty( value ) )
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    public async ValueTask<HerdrStatus> GetStatusAsync( CancellationToken cancellationToken )
    {
        if ( !_available( ) )
        {
            return Status( HerdrAvailability.NotInstalled, "Herdr is not on the daemon PATH." );
        }

        ProcessResult result;

        try
        {
            result = await RunAsync( [StatusCommand, ServerCommand, JsonOption], StatusTimeout, StatusStandardOutputBytes, StatusStandardErrorBytes,
                    cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return Status( HerdrAvailability.Failed, "Herdr status could not start." );
        }

        var failure = DescribeProcessFailure( result, "Herdr status" );
        if ( failure is not null )
        {
            return Status( HerdrAvailability.Failed, failure );
        }

        return ParseStatus( result.StandardOutput );
    }

    public async ValueTask<HerdrInventory?> ListAsync( CancellationToken cancellationToken )
    {
        if ( !_available( ) )
        {
            return null;
        }

        var workspaces = StartWorkspaceListing( cancellationToken );
        var tabs = StartTabListing( cancellationToken );

        try
        {
            await Task
                .WhenAll( workspaces, tabs )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return null;
        }

        // Both halves must be fresh; a stale or missing half is never combined.
        var workspaceList = ParseWorkspaces( workspaces.Result );
        var tabList = ParseTabs( tabs.Result );
        if ( HasMissingInventory( workspaceList, tabList ) )
        {
            return null;
        }

        return new HerdrInventory { Workspaces = workspaceList, Tabs = tabList };
    }

    private async ValueTask<HerdrFocusOutcome> DispatchFocusAsync( string targetKind, string targetId, CancellationToken cancellationToken )
    {
        if ( IsInvalidFocusTarget( targetKind, targetId ) )
        {
            return Failure( "Herdr could not switch to this result.", "The target is not a valid Herdr identifier." );
        }

        if ( !_available( ) )
        {
            return Failure( "Herdr is not on the daemon PATH." );
        }

        ProcessResult result;

        try
        {
            result = await RunAsync( [targetKind, FocusCommand, targetId], FocusTimeout, FocusStandardOutputBytes, FocusStandardErrorBytes, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return Failure( "Herdr could not switch to this result.", "The Herdr client could not start." );
        }

        // The request may have reached the server before the acknowledgement was
        // lost, so a timeout is reported once and never retried.
        if ( result.TimedOut )
        {
            return Failure( "Herdr did not confirm the switch; it may already have happened." );
        }

        if ( HasExceededLimits( result ) )
        {
            return Failure( "Herdr could not switch to this result.", "The Herdr client exceeded its output or memory limit." );
        }

        if ( result.ExitCode != 0 )
        {
            return Failure( DescribeFocusError( result.StandardError, targetKind ) );
        }

        return VerifyFocusAcknowledgement( result.StandardOutput, targetKind, targetId );
    }

    private async ValueTask<ProcessResult> RunAsync( IReadOnlyList<string> arguments, TimeSpan timeout, int standardOutputBytes, int standardErrorBytes,
        CancellationToken cancellationToken )
    {
        string[] argv = [SessionOption, DefaultCommand, .. arguments];
        ProcessExecutionLimits limits = CreateProcessLimits( standardOutputBytes, standardErrorBytes );

        return await _run( Executable, argv, timeout, limits, cancellationToken )
            .ConfigureAwait( false );
    }

    private static ValueTask<ProcessResult> DefaultRunAsync( string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, ProcessExecutionLimits limits,
        CancellationToken cancellationToken ) =>
        ProcessRunner.RunBoundedAsync( fileName, arguments, workingDirectory: null, timeout, limits, cancellationToken );

    private static string? DescribeProcessFailure( ProcessResult result, string operation )
    {
        if ( result.TimedOut )
        {
            return $"{operation} timed out.";
        }

        if ( HasExceededLimits( result ) )
        {
            return $"{operation} exceeded its output or memory limit.";
        }

        if ( result.ExitCode != 0 )
        {
            return $"{operation} exited with code {result.ExitCode.ToString( CultureInfo.InvariantCulture )}.";
        }

        return null;
    }

    private static HerdrStatus
        Status( HerdrAvailability availability, string detail, string? version = null, long? protocol = null, string? socketPath = null ) => new( )
        {
            Availability = availability,
            Detail = detail,
            Version = version,
            Protocol = protocol,
            SocketPath = socketPath
        };

    private static HerdrFocusOutcome Failure( string message, string? detail = null ) => new( ) { Succeeded = false, Message = message, Detail = detail };

    private Dictionary<string, string> CreateEnvironment( )
    {
        return ProcessEnvironmentDefaults.CreateEnvironment( _environment( EnvironmentVariableNames.ExecutablePath ) );
    }

    private Task<ProcessResult> StartWorkspaceListing( CancellationToken cancellationToken )
    {
        return RunAsync( [WorkspaceCommand, ListCommand], ListTimeout, WorkspaceListStandardOutputBytes, WorkspaceListStandardErrorBytes, cancellationToken )
            .AsTask( );
    }

    private Task<ProcessResult> StartTabListing( CancellationToken cancellationToken )
    {
        return RunAsync( [TabCommand, ListCommand], ListTimeout, TabListStandardOutputBytes, TabListStandardErrorBytes, cancellationToken )
            .AsTask( );
    }

    private static bool IsInvalidFocusTarget( string targetKind, string targetId )
    {
        return targetKind is not HerdrTargetKinds.Workspace and not HerdrTargetKinds.Tab || !IsValidIdentifier( targetId );
    }

    private ProcessExecutionLimits CreateProcessLimits( int standardOutputBytes, int standardErrorBytes )
    {
        return new( )
        {
            MaximumStandardOutputBytes = standardOutputBytes,
            MaximumStandardErrorBytes = standardErrorBytes,
            MaximumMemoryBytes = MaximumMemoryBytes,
            ClearEnvironment = true,
            EnvironmentVariables = BuildEnvironment( )
        };
    }

    private static bool HasExceededLimits( ProcessResult result )
    {
        return result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static bool HasMissingInventory( [NotNullWhen( false )] List<HerdrWorkspace>? workspaceList, [NotNullWhen( false )] List<HerdrTab>? tabList )
    {
        return workspaceList is null || tabList is null;
    }
}
