using System.Text;
using System.Text.Json;

namespace CommandDeck;

public sealed class HerdrAppInventory
{
    public required HerdrInventory Inventory { get; init; }
    public required IReadOnlyList<HerdrAgent> Agents { get; init; }
}

/// <summary>
/// The only boundary between CommandDeck and the Herdr desktop app's CLI
/// (herdr-app). It reads the spaces, tabs, and agents the open window shows and
/// changes which of them it shows. Every call has a fixed argv whose only data
/// is one JSON object, a cleared environment, and bounded output, memory, and
/// time. The CLI talks to a running window and never opens one: without a
/// window, listing yields nothing and changes report that the app did not
/// answer. Ordinary failures become typed outcomes; only caller cancellation
/// propagates as an exception.
/// </summary>
public sealed partial class HerdrAppClient
{
    private const string ListCommand = "ls";
    private const string DoCommand = "do";
    private const string IdArgument = "id";
    private const string NameArgument = "name";

    public const string Executable = "herdr-app";

    public static readonly TimeSpan ListTimeout = TimeSpan.FromMilliseconds( 250 );
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds( 1 );

    // tab.new waits for its terminal to start, and the window answers within
    // five seconds or says it did not.
    public static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds( 6 );

    public const int ListStandardOutputBytes = ByteUnits.BytesPerMebibyte;
    public const int CommandStandardOutputBytes = 64 * ByteUnits.BytesPerKibibyte;
    public const int StandardErrorBytes = 16 * ByteUnits.BytesPerKibibyte;
    public const long MaximumMemoryBytes = 128L * ByteUnits.BytesPerMebibyte;

    // Never forwards HERDR_APP_HOME: the daemon always reads the default window.
    // A relative XDG_CONFIG_HOME is dropped too, so the CLI and the window
    // lookup both resolve the home under ~/.config.
    private static readonly string[] ForwardedEnvironment =
        [EnvironmentVariableNames.Home, XdgEnvironmentNames.ConfigHome, XdgEnvironmentNames.RuntimeDirectory];

    private readonly BoundedProcessRunner _run;
    private readonly Func<bool> _available;
    private readonly Func<string, string?> _environment;
    private readonly HerdrCommandLog _failures;
    private readonly Func<int?>? _listener;
    private int _focusInProgress;

    // listener reads which process listens on the app's socket; with the real
    // runner it defaults to asking the kernel, and with another runner to none.
    public HerdrAppClient( BoundedProcessRunner? runner = null, Func<bool>? available = null, Func<string, string?>? environment = null,
        Action<string>? log = null, TimeProvider? time = null, Func<int?>? listener = null )
    {
        _listener = listener ?? (runner is null ? HerdrAppWindows.ReadListener : null);
        _run = runner ?? DefaultRunAsync;
        _available = available ?? (static ( ) => ProcessRunner.ExistsOnPath( Executable ));
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _failures = new( "herdr_app.command_failed", log, time ?? TimeProvider.System );
    }

    public bool IsInstalled => _available( );

    internal IReadOnlyDictionary<string, string> BuildEnvironment( )
    {
        var environment = ProcessEnvironmentDefaults.CreateEnvironment( _environment( EnvironmentVariableNames.ExecutablePath ) );

        foreach ( var name in ForwardedEnvironment )
        {
            var value = _environment( name );
            if ( !string.IsNullOrEmpty( value ) && (name != XdgEnvironmentNames.ConfigHome || Path.IsPathRooted( value )) )
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    // The open window's spaces, tabs, and agents, or null without a window or
    // on any failure; a partial listing is never returned.
    public async ValueTask<HerdrAppInventory?> ListAsync( CancellationToken cancellationToken )
    {
        if ( !_available( ) )
        {
            return null;
        }

        ProcessResult result;

        try
        {
            result = await RunAsync( [ListCommand], ListTimeout, ListStandardOutputBytes, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return null;
        }

        if ( result.TimedOut || HasExceededLimits( result ) || result.ExitCode != 0 )
        {
            return null;
        }

        return ParseInventory( result.StandardOutput );
    }

    // Shows a space, a tab, or an agent's pane with one command, then raises the
    // window. Herdr's tab.select and pane.focus show the target's own tab and
    // space and change nothing when the target is gone. The socket's listener is read first and handed to the activation,
    // which raises a window only while the same app still listens.
    public async ValueTask<HerdrFocusOutcome> FocusAsync( string targetKind, string targetId, CancellationToken cancellationToken,
        Func<int?, CancellationToken, ValueTask<ActionExecutionResult>>? activateWindow = null )
    {
        cancellationToken.ThrowIfCancellationRequested( );

        if ( IsInvalidFocusTarget( targetKind, targetId ) )
        {
            return Failure( "Herdr could not switch to this result.", "The target is not a valid Herdr identifier." );
        }

        if ( !_available( ) )
        {
            return Failure( "The Herdr app is not on the daemon PATH." );
        }

        if ( Interlocked.CompareExchange( ref _focusInProgress, 1, 0 ) != 0 )
        {
            return Failure( "Another Herdr focus action is already in progress." );
        }

        try
        {
            var listener = _listener?.Invoke( );
            var focused = await SelectAsync( targetKind, targetId, cancellationToken )
                .ConfigureAwait( false );

            if ( !focused.Succeeded || activateWindow is null )
            {
                return focused;
            }

            var activation = await activateWindow( listener, cancellationToken )
                .ConfigureAwait( false );

            return activation.Succeeded
                ? focused
                : Failure( activation.Message ?? "Herdr switched, but could not focus its window.", activation.DetailMarkdown );
        }
        finally
        {
            Interlocked.Exchange( ref _focusInProgress, 0 );
        }
    }

    // A new tab is added without changing what the window shows.
    public ValueTask<HerdrTabOutcome> CreateTabAsync( string spaceId, string name, CancellationToken cancellationToken ) =>
        EditTabAsync( CreateTabCommand, Arguments( [("space", spaceId), (NameArgument, name)], focus: false ), spaceId, name, "Created Herdr tab.",
            cancellationToken );

    public ValueTask<HerdrTabOutcome> RenameTabAsync( string tabId, string name, CancellationToken cancellationToken ) =>
        EditTabAsync( "tab.rename", Arguments( [(IdArgument, tabId), (NameArgument, name)] ), tabId, name, "Renamed Herdr tab.", cancellationToken );

    private const string CreateTabCommand = "tab.new";

    // A lost reply may hide a switch that happened, so it is reported once and
    // never retried.
    private async ValueTask<HerdrFocusOutcome> SelectAsync( string targetKind, string targetId, CancellationToken cancellationToken )
    {
        (var command, var argument, var target, var done) = targetKind switch
        {
            HerdrTargetKinds.AppSpace => ("space.select", IdArgument, "That Herdr space", "Switched Herdr space."),
            HerdrTargetKinds.AppPane => ("pane.focus", "pane", "That Herdr agent's pane", "Switched to the Herdr agent."),
            _ => ("tab.select", IdArgument, "That Herdr tab", "Switched Herdr tab.")
        };

        var answer = await SendAsync( command, Arguments( [(argument, targetId)] ), created: false, CommandTimeout, cancellationToken )
            .ConfigureAwait( false );

        return answer.Reply switch
        {
            AppReply.Ok => new HerdrFocusOutcome { Succeeded = true, Message = done },
            AppReply.Refused => Failure( $"{target} no longer exists. Search again.", answer.Detail ),
            AppReply.Unconfirmed => Failure( "Herdr did not confirm the switch; it may already have happened." ),
            AppReply.NoAnswer => Failure( "The Herdr app did not answer. Is its window open?", answer.Detail ),
            _ => Failure( "Herdr could not switch to this result.", answer.Detail )
        };
    }

    private async ValueTask<HerdrTabOutcome> EditTabAsync( string command, string arguments, string targetId, string name, string done,
        CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );

        if ( IsInvalidTabEdit( targetId, name ) )
        {
            return new( false, "Choose a valid Herdr target and a name of 1–512 characters without control characters." );
        }

        if ( !_available( ) )
        {
            return new( false, "The Herdr app is not on the daemon PATH." );
        }

        var create = command == CreateTabCommand;
        var answer = await SendAsync( command, arguments, created: create, create ? CreateTimeout : CommandTimeout, cancellationToken )
            .ConfigureAwait( false );

        return answer.Reply switch
        {
            AppReply.Ok => new( true, done ),

            // A lost acknowledgement must never cause an automatic second creation.
            AppReply.Unconfirmed => new( false, "Herdr did not confirm the change; it may already have happened. Refresh before trying again." ),
            AppReply.NoAnswer => new( false, "The Herdr app did not answer. Is its window open?" ),
            AppReply.Refused when answer.Reason is { } reason => new( false, $"Herdr refused the change: {reason}" ),
            _ => new( false, "Herdr could not change this tab. Refresh to check that the space or tab still exists." )
        };
    }

    private async ValueTask<AppAnswer> SendAsync( string command, string arguments, bool created, TimeSpan timeout, CancellationToken cancellationToken )
    {
        ProcessResult result;

        try
        {
            result = await RunAsync( [DoCommand, command, arguments], timeout, CommandStandardOutputBytes, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return new( AppReply.Failed, null );
        }

        if ( result.TimedOut || HasExceededLimits( result ) )
        {
            return new( AppReply.Unconfirmed, null );
        }

        return ReadReply( result, created );
    }

    private async ValueTask<ProcessResult> RunAsync( string[] arguments, TimeSpan timeout, int standardOutputBytes, CancellationToken cancellationToken )
    {
        ProcessExecutionLimits limits = new( )
        {
            MaximumStandardOutputBytes = standardOutputBytes,
            MaximumStandardErrorBytes = StandardErrorBytes,
            MaximumMemoryBytes = MaximumMemoryBytes,
            ClearEnvironment = true,
            EnvironmentVariables = BuildEnvironment( )
        };

        // The runner may start the process before it looks at the token, and a
        // tab's second step must not run once the caller has cancelled.
        cancellationToken.ThrowIfCancellationRequested( );

        try
        {
            var result = await _run( Executable, arguments, timeout, limits, cancellationToken )
                .ConfigureAwait( false );
            _failures.Record( arguments, result, LoggedError( result ) );

            return result;
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            _failures.Log( arguments, "outcome=start_failed", exception.Message );

            throw;
        }
    }

    // One JSON object carries every argument, so no value is read as an option
    // or reinterpreted as a number, boolean, or array.
    private static string Arguments( (string Name, string Value)[] values, bool? focus = null )
    {
        using MemoryStream buffer = new( );

        using ( Utf8JsonWriter writer = new( buffer ) )
        {
            writer.WriteStartObject( );

            foreach ( (var name, var value) in values )
            {
                writer.WriteString( name, value );
            }

            if ( focus is { } shown )
            {
                writer.WriteBoolean( "focus", shown );
            }

            writer.WriteEndObject( );
        }

        return Encoding.UTF8.GetString( buffer.ToArray( ) );
    }

    private static bool IsInvalidFocusTarget( string targetKind, string targetId ) =>
        !HerdrTargetKinds.IsApp( targetKind ) || !HerdrClient.IsValidIdentifier( targetId );

    private static bool IsInvalidTabEdit( string targetId, string name ) => !HerdrClient.IsValidIdentifier( targetId ) || !IsValidTabName( name );

    internal static bool IsValidTabName( string name ) =>
        !string.IsNullOrWhiteSpace( name ) && name.Length <= HerdrClient.MaximumLabelCharacters && !name.Any( char.IsControl );

    private static bool HasExceededLimits( ProcessResult result ) => result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;

    private static HerdrFocusOutcome Failure( string message, string? detail = null ) => new( ) { Succeeded = false, Message = message, Detail = detail };

    private static ValueTask<ProcessResult> DefaultRunAsync( string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, ProcessExecutionLimits limits,
        CancellationToken cancellationToken ) =>
        ProcessRunner.RunBoundedAsync( fileName, arguments, workingDirectory: null, timeout, limits, cancellationToken );

    // Reason is Herdr's own explanation, cleaned for display.
    private readonly record struct AppAnswer( AppReply Reply, string? Reason )
    {
        public string? Detail => Reason is null ? null : $"Herdr said: `{Reason.Replace( '`', '\'' )}`";
    }

    private enum AppReply
    {
        Ok,

        // The window answered with an error, such as a space or tab it no longer has.
        Refused,

        // Timed out, overflowed, or ended without a readable acknowledgement:
        // the command may have run.
        Unconfirmed,

        // Usage error or no window: the CLI exits 2 for both.
        NoAnswer,
        Failed
    }
}
