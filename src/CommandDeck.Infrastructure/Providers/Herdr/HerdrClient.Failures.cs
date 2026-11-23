namespace CommandDeck;

// Failed commands leave one rate-limited log line; see HerdrCommandLog.
public sealed partial class HerdrClient
{
    private const int SignalExitCodeBase = 128;

    private static bool Crashed( ProcessResult result ) =>
        result is { TimedOut: false, ExitCode: > SignalExitCodeBase } && !HasExceededLimits( result );

    private void RecordFailure( IReadOnlyList<string> arguments, ProcessResult result ) => _failures.Record( arguments, result );

    private void Log( IReadOnlyList<string> arguments, string outcome, string error ) => _failures.Log( arguments, outcome, error );
}
