using System.Globalization;

namespace CommandDeck;

/// <summary>
/// The reversible HEY changes the launcher offers, each paired with its undo:
/// seen and unseen, Reply Later or Set Aside and back to the box a thread came
/// from, and a bubble-up tomorrow and its cancellation. Nothing here sends,
/// trashes, or reports mail.
/// </summary>
public static class HeyTriageChanges
{
    public const string Seen = "seen";
    public const string Unseen = "unseen";
    public const string ReplyLater = "reply-later";
    public const string SetAside = "set-aside";
    public const string MoveBack = "move-back";
    public const string BubbleUp = "bubble-up";
    public const string BubblePop = "bubble-pop";

    public static bool IsKnown( string? change ) =>
        change is Seen or Unseen or ReplyLater or SetAside or MoveBack or BubbleUp or BubblePop;
}

// Triage changes act on the box item (the posting), not the thread. HEY never
// retries a mutation on its own, and neither does CommandDeck.
public sealed partial class HeyClient
{
    public static readonly TimeSpan TriageTimeout = TimeSpan.FromSeconds( 8 );

    private long _changeGeneration;

    // Counts the changes that may have landed. A listing read before the
    // latest one no longer shows HEY's state and must be read again.
    public long ChangeGeneration => Interlocked.Read( ref _changeGeneration );

    public async ValueTask<HeyFailure?> TriageAsync( string change, long postingId, long? boxId, CancellationToken cancellationToken )
    {
        var arguments = TriageArguments( change, postingId, boxId );
        if ( arguments is null )
        {
            return new HeyFailure( HeyFailureKind.Rejected, "This HEY change is not valid." );
        }

        var executable = ResolveExecutable( );
        if ( executable is null )
        {
            return new HeyFailure( HeyFailureKind.NotInstalled, "The HEY CLI is not installed." );
        }

        var result = await RunAsync( executable, arguments, TriageTimeout, StatusStandardOutputBytes, cancellationToken )
            .ConfigureAwait( false );

        if ( result is null )
        {
            return new HeyFailure( HeyFailureKind.Failed, "The HEY CLI could not start." );
        }

        // A timed-out change may still have landed; say so rather than guess.
        if ( result.TimedOut )
        {
            Interlocked.Increment( ref _changeGeneration );

            return new HeyFailure( HeyFailureKind.TimedOut, "HEY did not confirm the change in time; check it in HEY." );
        }

        if ( HasTriageExceededLimits( result ) )
        {
            Interlocked.Increment( ref _changeGeneration );

            return GetUnconfirmedTriageFailure( result );
        }

        // Success requires a complete, bounded reply as well as the exit code.
        if ( result.ExitCode == 0 )
        {
            Interlocked.Increment( ref _changeGeneration );

            return null;
        }

        return FailureOf( result );
    }

    internal static List<string>? TriageArguments( string change, long postingId, long? boxId )
    {
        if ( postingId <= 0 )
        {
            return null;
        }

        var id = postingId.ToString( CultureInfo.InvariantCulture );

        return change switch
        {
            HeyTriageChanges.Seen => ["seen", id, JsonOption],
            HeyTriageChanges.Unseen => ["unseen", id, JsonOption],
            HeyTriageChanges.ReplyLater => [MoveCommand, id, "--to=laterbox", JsonOption],
            HeyTriageChanges.SetAside => [MoveCommand, id, "--to=asidebox", JsonOption],
            HeyTriageChanges.MoveBack when boxId > 0 => [MoveCommand, id, "--to=" + boxId.Value.ToString( CultureInfo.InvariantCulture ), JsonOption],
            HeyTriageChanges.BubbleUp => [BubbleCommand, "up", id, "--tomorrow", JsonOption],
            HeyTriageChanges.BubblePop => [BubbleCommand, "pop", id, JsonOption],
            _ => null
        };
    }

    private static bool HasTriageExceededLimits( ProcessResult result )
    {
        return result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static HeyFailure GetUnconfirmedTriageFailure( ProcessResult result )
    {
        if ( result.StandardErrorLimitExceeded )
        {
            return FailureOf( result );
        }

        return new HeyFailure( HeyFailureKind.Failed, "HEY did not confirm the change; check it in HEY." );
    }
}
