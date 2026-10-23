using System.Globalization;

namespace CommandDeck;

// A reversible HEY change on the box item behind a row. The message names what
// happened; the launcher shows it with Undo when the row has an undo action.
internal sealed partial class HeyActionHandler
{
    private const int MaximumTriageSubjectCharacters = 48;

    private async ValueTask<ActionExecutionResult> TriageHeyThreadAsync( SearchResult result, CommandAction action, CancellationToken cancellationToken )
    {
        var change = action.Arguments.GetValueOrDefault( ActionArgumentNames.Change );
        if ( IsInvalidTriageChange( action, change, out var postingId ) )
        {
            return ActionExecutionResult.Failure( "This HEY change is not valid." );
        }

        long? boxId = null;

        if ( action.Arguments.TryGetValue( ActionArgumentNames.BoxId, out var boxText ) )
        {
            if ( CannotReadBoxId( boxText, out var box ) )
            {
                return ActionExecutionResult.Failure( "This HEY change is not valid." );
            }

            boxId = box;
        }

        if ( hey is null )
        {
            return ActionExecutionResult.Failure( "The HEY CLI is not installed." );
        }

        var failure = await hey
            .TriageAsync( change!, postingId, boxId, cancellationToken )
            .ConfigureAwait( false );

        if ( failure is null )
        {
            return ActionExecutionResult.Success( TriageMessage( change!, result.Title ) );
        }

        return ActionExecutionResult.Failure( failure.Message );
    }

    internal static string TriageMessage( string change, string subject )
    {
        var cut = GetSubjectCut( subject );
        var title = subject.Length > MaximumTriageSubjectCharacters ? subject[..cut] + "…" : subject;

        return change switch
        {
            HeyTriageChanges.Seen => $"Marked “{title}” seen",
            HeyTriageChanges.Unseen => $"Marked “{title}” unseen",
            HeyTriageChanges.ReplyLater => $"Moved “{title}” to Reply Later",
            HeyTriageChanges.SetAside => $"Set “{title}” aside",
            HeyTriageChanges.MoveBack => $"Moved “{title}” back",
            HeyTriageChanges.BubbleUp => $"“{title}” bubbles up tomorrow",
            _ => $"Cancelled the bubble-up for “{title}”"
        };
    }

    private static int GetSubjectCut( string subject )
    {
        if ( char.IsHighSurrogate( subject.ElementAtOrDefault( MaximumTriageSubjectCharacters - 2 ) ) )
        {
            return MaximumTriageSubjectCharacters - 2;
        }

        return MaximumTriageSubjectCharacters - 1;
    }

    private static bool IsInvalidTriageChange( CommandAction action, string? change, out long postingId )
    {
        postingId = default;

        return !HeyTriageChanges.IsKnown( change ) ||
               !long.TryParse( action.Arguments.GetValueOrDefault( ActionArgumentNames.PostingId ), NumberStyles.None, CultureInfo.InvariantCulture,
                   out postingId ) ||
               postingId <= 0;
    }

    private static bool CannotReadBoxId( string boxText, out long box )
    {
        box = default;

        return !long.TryParse( boxText, NumberStyles.None, CultureInfo.InvariantCulture, out box ) || box <= 0;
    }
}
