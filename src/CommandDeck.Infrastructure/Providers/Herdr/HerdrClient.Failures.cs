namespace CommandDeck;

/// <summary>
/// Leaves evidence of a failed Herdr command: one log line with the command,
/// how it ended, and the first line of its error output. The command is only
/// its first two words, so labels and paths stay out of the log. A stopped
/// server can fail every search, so the same failure logs once a minute.
/// </summary>
public sealed partial class HerdrClient
{
    private const int SignalExitCodeBase = 128;
    private const int LoggedCommandWords = 2;
    private const int MaximumLoggedErrorCharacters = 240;
    private const int MaximumTrackedFailures = 32;

    private static readonly TimeSpan RepeatedFailureLogInterval = TimeSpan.FromMinutes( 1 );

    private readonly Dictionary<string, DateTimeOffset> _loggedFailures = new( StringComparer.Ordinal );

    private static bool Crashed( ProcessResult result ) =>
        result is { TimedOut: false, ExitCode: > SignalExitCodeBase } && !HasExceededLimits( result );

    private void RecordFailure( IReadOnlyList<string> arguments, ProcessResult result )
    {
        if ( DescribeOutcome( result ) is { } outcome )
        {
            Log( arguments, outcome, result.StandardError );
        }
    }

    private void Log( IReadOnlyList<string> arguments, string outcome, string error )
    {
        if ( _log is null )
        {
            return;
        }

        var line = ($"event=herdr.command_failed command=\"{string.Join( ' ', arguments.Take( LoggedCommandWords ) )}" +
            $"\" {outcome} error=\"{FirstLine( error )}\"");
        if ( ShouldLog( line ) )
        {
            _log( line );
        }
    }

    private bool ShouldLog( string line )
    {
        var now = _time.GetUtcNow( );

        lock ( _loggedFailures )
        {
            if ( _loggedFailures.TryGetValue( line, out var logged ) && now - logged < RepeatedFailureLogInterval )
            {
                return false;
            }

            if ( _loggedFailures.Count >= MaximumTrackedFailures )
            {
                _loggedFailures.Clear( );
            }

            _loggedFailures[line] = now;

            return true;
        }
    }

    private static string? DescribeOutcome( ProcessResult result )
    {
        if ( result.TimedOut )
        {
            return "outcome=timeout";
        }

        if ( HasExceededLimits( result ) )
        {
            return "outcome=limit";
        }

        return result.ExitCode switch
        {
            0 => null,
            > SignalExitCodeBase => $"outcome=signal exit_code={result.ExitCode} signal={SignalName( result.ExitCode - SignalExitCodeBase )}",
            _ => $"outcome=exit exit_code={result.ExitCode}"
        };
    }

    private static string SignalName( int signal ) => signal switch
    {
        4 => "SIGILL",
        6 => "SIGABRT",
        7 => "SIGBUS",
        9 => "SIGKILL",
        11 => "SIGSEGV",
        15 => "SIGTERM",
        _ => signal.ToString( System.Globalization.CultureInfo.InvariantCulture )
    };

    private static string FirstLine( string error )
    {
        var line = error.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).FirstOrDefault( ) ?? string.Empty;

        return new string( line.Where( static character => !char.IsControl( character ) && character != '"' ).Take( MaximumLoggedErrorCharacters ).ToArray( ) );
    }
}
