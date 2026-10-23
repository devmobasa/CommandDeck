using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CommandDeck;

/// <summary>
/// The only boundary between CommandDeck and the HEY CLI. hey keeps its own
/// sign-in (the system keyring, refreshed by hey itself); CommandDeck never
/// reads or stores a HEY credential. Every call runs a fixed argv from the root
/// directory, so no repository-local <c>.hey/config.json</c> applies, with a
/// cleared environment and bounded output, memory, and time. Process, JSON, and
/// HEY failures become typed outcomes; only caller cancellation propagates.
/// </summary>
public sealed partial class HeyClient
{
    private const int BaseSearchArgumentCount = 2;

    private const string PageOption = "--page";

    private const string JsonOption = "--json";
    private const string BubbleCommand = "bubble";
    private const string MoveCommand = "move";
    private const string StatusCommand = "status";
    private const string SearchCommand = "search";

    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds( 3 );

    // Below the coordinator's scoped deadline, so a slow HEY still answers with
    // a "took too long" row instead of being abandoned without one.
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds( 4500 );

    public const int StatusStandardOutputBytes = 64 * ByteUnits.BytesPerKibibyte;
    public const int ReadStandardOutputBytes = 4 * ByteUnits.BytesPerMebibyte;

    public const int StandardErrorBytes = 64 * ByteUnits.BytesPerKibibyte;

    // An address-space cap, not a heap cap: the Go runtime reserves its heap
    // ranges up front, and hey 1.6 fails at 512 MiB ("failed to reserve page
    // summary memory") while it runs at 1 GiB.
    public const long MaximumAddressSpaceBytes = 2L * ByteUnits.BytesPerGibibyte;

    public const int MaximumThreads = 50;
    public const int MaximumTextCharacters = 512;
    public const int MaximumRefinementCharacters = 256;

    private static readonly string[] ForwardedEnvironment =
    [
        EnvironmentVariableNames.Home, XdgEnvironmentNames.ConfigHome, XdgEnvironmentNames.DataHome, XdgEnvironmentNames.StateHome,
        XdgEnvironmentNames.CacheHome,
        XdgEnvironmentNames.RuntimeDirectory, EnvironmentVariableNames.DbusSessionBusAddress, EnvironmentVariableNames.MiseDataDirectory
    ];

    // Resolving walks PATH and reads file headers, and every plain query of
    // three characters and each step of an action asks for the binary, so the
    // answer is kept briefly; an install or removal shows within this time.
    public static readonly TimeSpan ExecutableLifetime = TimeSpan.FromSeconds( 5 );

    private readonly BoundedProcessRunner _run;
    private readonly Func<string?> _resolveUncached;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _time;
    private ResolvedExecutable? _resolved;

    private sealed record ResolvedExecutable( string? Path, long ResolvedAt );

    public HeyClient( BoundedProcessRunner? runner = null, Func<string?>? resolve = null, Func<string, string?>? environment = null,
        TimeProvider? timeProvider = null )
    {
        _run = runner ?? DefaultRunAsync;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _resolveUncached = resolve ?? new HeyExecutable( _environment ).Resolve;
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool IsInstalled => ResolveExecutable( ) is not null;

    // The binary a launched terminal or HEY TUI should run.
    public string? Executable => ResolveExecutable( );

    private string? ResolveExecutable( )
    {
        var now = _time.GetTimestamp( );
        var resolved = Volatile.Read( ref _resolved );
        if ( HasFreshExecutable( now, resolved ) )
        {
            return resolved.Path;
        }

        var path = _resolveUncached( );
        Volatile.Write( ref _resolved, new ResolvedExecutable( path, now ) );

        return path;
    }

    // hey reaches the keyring over the session bus, so the bus address is
    // forwarded; HEY_NONINTERACTIVE keeps it from ever prompting to sign in.
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

    // The instance Omarchy's HEY launchers share, and its terminal window's app id.
    public const string TuiInstance = "omarchy";
    public const string TuiAppId = "org.omarchy.hey";

    // A running HEY TUI listens on its instance socket and opens a thread or The
    // Screener itself; false when none is running or it did not take the request.
    public ValueTask<bool> OpenInRunningTuiAsync( long topicId, CancellationToken cancellationToken )
    {
        if ( topicId > 0 )
        {
            return RemoteAsync( TuiTopic( topicId ), cancellationToken );
        }
        else
        {
            return ValueTask.FromResult( false );
        }
    }

    public ValueTask<bool> OpenScreenerInRunningTuiAsync( CancellationToken cancellationToken ) =>
        RemoteAsync( [TuiScreener], cancellationToken );

    public const string TuiScreener = "--screener";

    public static string[] TuiTopic( long topicId ) => ["--topic", topicId.ToString( CultureInfo.InvariantCulture )];

    private async ValueTask<bool> RemoteAsync( IReadOnlyList<string> destination, CancellationToken cancellationToken )
    {
        var executable = ResolveExecutable( );
        if ( executable is null )
        {
            return false;
        }

        var result = await RunAsync( executable, ["tui", "--instance", TuiInstance, .. destination, "--remote"], StatusTimeout, StatusStandardOutputBytes,
                cancellationToken )
            .ConfigureAwait( false );

        return result is { ExitCode: 0, TimedOut: false, OutputLimitExceeded: false, StandardErrorLimitExceeded: false, MemoryLimitExceeded: false };
    }

    public async ValueTask<HeyStatus> GetStatusAsync( CancellationToken cancellationToken )
    {
        var executable = ResolveExecutable( );
        if ( executable is null )
        {
            return new HeyStatus( HeyAvailability.NotInstalled );
        }

        var result = await RunAsync( executable, ["auth", StatusCommand, JsonOption], StatusTimeout, StatusStandardOutputBytes, cancellationToken )
            .ConfigureAwait( false );

        if ( result is null )
        {
            return new HeyStatus( HeyAvailability.Failed );
        }

        return ParseStatus( result );
    }

    public async ValueTask<HeyListing> SearchAsync( HeySearchQuery query, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( query );

        var arguments = SearchArguments( query );
        if ( arguments is null )
        {
            return HeyListing.Failed( HeyFailureKind.Rejected, "Type something to search HEY for." );
        }

        return await ReadListingAsync( arguments, ParseSearch, cancellationToken )
            .ConfigureAwait( false );
    }

    public async ValueTask<HeyListing> ListBoxAsync( string cliName, int limit, CancellationToken cancellationToken )
    {
        if ( !HeyBoxes.IsCliName( cliName ) )
        {
            throw new ArgumentOutOfRangeException( nameof( cliName ), "Only known HEY boxes can be listed." );
        }

        var bounded = Math
            .Clamp( limit, 1, MaximumThreads )
            .ToString( CultureInfo.InvariantCulture );

        return await ReadListingAsync( ["box", "view", cliName, JsonOption, "--limit=" + bounded], ParseBox, cancellationToken )
            .ConfigureAwait( false );
    }

    internal static List<string>? SearchArguments( HeySearchQuery query )
    {
        var text = Clean( query.Text, MaximumTextCharacters );
        List<string> arguments = [SearchCommand, JsonOption];
        AddRefinement( arguments, "exact", query.Exact );
        AddRefinement( arguments, "none", query.None );
        AddRefinement( arguments, "from", query.From );
        AddRefinement( arguments, "to", query.To );
        AddRefinement( arguments, "subject", query.Subject );
        AddRefinement( arguments, "in", query.Box );
        AddRefinement( arguments, "date", query.Date );
        AddRefinement( arguments, "label", query.Label );
        AddRefinement( arguments, "attachment", query.Attachment );

        if ( IsEmptySearch( text, arguments ) )
        {
            return null;
        }

        // Everything after -- is the query, even text that starts with a dash.
        if ( text.Length > 0 )
        {
            arguments.Add( "--" );
            arguments.Add( text );
        }

        return arguments;
    }

    private static void AddRefinement( List<string> arguments, string name, string? value )
    {
        var cleaned = Clean( value, MaximumRefinementCharacters );
        if ( cleaned.Length > 0 )
        {
            arguments.Add( $"--{name}={cleaned}" );
        }
    }

    private async ValueTask<HeyListing> ReadListingAsync( IReadOnlyList<string> arguments, Func<string, HeyListing> parse, CancellationToken cancellationToken )
    {
        var executable = ResolveExecutable( );
        if ( executable is null )
        {
            return HeyListing.Failed( HeyFailureKind.NotInstalled, "The HEY CLI is not installed." );
        }

        var result = await RunAsync( executable, arguments, ReadTimeout, ReadStandardOutputBytes, cancellationToken )
            .ConfigureAwait( false );

        if ( result is null )
        {
            return HeyListing.Failed( HeyFailureKind.Failed, "The HEY CLI could not start." );
        }

        if ( result.TimedOut )
        {
            return HeyListing.Failed( HeyFailureKind.TimedOut, "HEY took too long to answer." );
        }

        if ( HasSearchExceededLimits( result ) )
        {
            return HeyListing.Failed( HeyFailureKind.Failed, "HEY answered with more than CommandDeck reads." );
        }

        // A failing hey explains itself in an error envelope; a success envelope
        // from a failing process is never trusted.
        if ( HasSearchFailed( result ) )
        {
            return new HeyListing { Failure = FailureOf( result ) };
        }

        return parse( result.StandardOutput );
    }

    private async ValueTask<ProcessResult?> RunAsync( string executable, IReadOnlyList<string> arguments, TimeSpan timeout, int standardOutputBytes,
        CancellationToken cancellationToken )
    {
        ProcessExecutionLimits limits = CreateExecutionLimits( standardOutputBytes );

        try
        {
            return await _run( executable, arguments, timeout, limits, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException or UnauthorizedAccessException )
        {
            return null;
        }
    }

    private static ValueTask<ProcessResult> DefaultRunAsync( string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, ProcessExecutionLimits limits,
        CancellationToken cancellationToken ) =>
        ProcessRunner.RunBoundedAsync( fileName, arguments, workingDirectory: "/", timeout, limits, cancellationToken );

    private bool HasFreshExecutable( long now, [NotNullWhen( true )] ResolvedExecutable? resolved )
    {
        return resolved is not null && _time.GetElapsedTime( resolved.ResolvedAt, now ) < ExecutableLifetime;
    }

    private Dictionary<string, string> CreateEnvironment( )
    {
        var environment = ProcessEnvironmentDefaults.CreateEnvironment( _environment( EnvironmentVariableNames.ExecutablePath ) );
        environment[EnvironmentVariableNames.NoColor] = "1";
        environment["HEY_NONINTERACTIVE"] = "1";

        return environment;
    }

    private static bool IsEmptySearch( string text, List<string> arguments )
    {
        return text.Length == 0 && arguments.Count == BaseSearchArgumentCount;
    }

    private static bool HasSearchExceededLimits( ProcessResult result )
    {
        return result.OutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static bool HasSearchFailed( ProcessResult result )
    {
        return result.ExitCode != 0 || result.StandardErrorLimitExceeded;
    }

    private ProcessExecutionLimits CreateExecutionLimits( int standardOutputBytes )
    {
        return new( )
        {
            MaximumStandardOutputBytes = standardOutputBytes,
            MaximumStandardErrorBytes = StandardErrorBytes,
            MaximumMemoryBytes = MaximumAddressSpaceBytes,
            ClearEnvironment = true,
            EnvironmentVariables = BuildEnvironment( )
        };
    }
}
