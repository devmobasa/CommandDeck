using System.Text;

namespace CommandDeck;

public static partial class ProtocolRequestValidator
{
    private const int MaximumClipboardMatchIndex = 999;
    private const int MaximumClipboardQueryLimit = 100;

    private static void ValidateClipboardFields( ProtocolRequest request )
    {
        if ( !ProtocolRoutes.IsIn( request.Operation, ProtocolFamily.Clipboard ) )
        {
            if ( request.Clipboard is not null )
            {
                throw new InvalidOperationException( "Clipboard fields require a clipboard operation." );
            }

            return;
        }

        var value = request.Clipboard;
        if ( value is null )
        {
            throw new InvalidOperationException( "Clipboard operation requires clipboard fields." );
        }

        ValidateClipboardBounds( value );
        ValidateClipboardOwner( request.Operation, value );
        ValidateClipboardEntry( request.Operation, value );
        ValidateClipboardOperation( request, value );
    }

    private static void ValidateClipboardBounds( ClipboardRequest value )
    {
        CheckLength( value.Action, "clipboard.action", MaximumClipboardActionCharacters );
        CheckLength( value.Cursor, "clipboard.cursor", MaximumClipboardCursorCharacters );
        CheckLength( value.KindFilter, "clipboard.kindFilter", MaximumClipboardKindFilterCharacters );
        CheckLength( value.PreparationId, "clipboard.preparationId", MaximumClipboardPreparationIdCharacters );
        CheckLength( value.ReleaseId, "clipboard.releaseId", MaximumClipboardReleaseIdCharacters );
        CheckLength( value.EntryId, "clipboard.entryId", MaximumClipboardEntryIdCharacters );
        CheckLength( value.Incarnation, "clipboard.incarnation", MaximumClipboardIncarnationCharacters );
        CheckLength( value.SessionId, "clipboard.sessionId", MaximumClipboardSessionIdCharacters );
        CheckLength( value.FrontendInstance, "clipboard.frontendInstance", MaximumClipboardFrontendInstanceCharacters );

        if ( HasInvalidClipboardKeywords( value ) )
        {
            throw new InvalidOperationException( "Clipboard keywords exceed their limits." );
        }

        if ( HasInvalidClipboardRevisions( value ) )
        {
            throw new InvalidOperationException( "Clipboard revision, generation or match index is invalid." );
        }
    }

    private static void ValidateClipboardOwner( string operation, ClipboardRequest value )
    {
        if ( operation is ProtocolOperations.ClipboardOpen
            or ProtocolOperations.ClipboardClose
            or ProtocolOperations.LauncherOpenCancel
            or ProtocolOperations.ClipboardAction
            or ProtocolOperations.ClipboardActionsPrepare
            or ProtocolOperations.ClipboardReleaseAbort )
        {
            if ( HasInvalidFrontendIdentity( value ) )
            {
                throw new InvalidOperationException( "Clipboard operation requires the frontend instance and open generation." );
            }
        }

        if ( RequiresQueryGeneration( operation, value ) )
        {
            if ( !Guid.TryParseExact( value.SessionId, CompactGuidFormat, out _ ) )
            {
                throw new InvalidOperationException( "Clipboard operation requires a session ID." );
            }
        }
    }

    private static void ValidateClipboardEntry( string operation, ClipboardRequest value )
    {
        if ( RequiresEntryIdentity( operation, value ) )
        {
            if ( HasInvalidEntryIdentity( value ) )
            {
                throw new InvalidOperationException( "Clipboard entry identity and revision are required." );
            }
        }
    }

    private static void ValidateClipboardOperation( ProtocolRequest request, ClipboardRequest value )
    {
        if ( HasInvalidClipboardQuery( request, value ) )
        {
            throw new InvalidOperationException( "Clipboard query requires a generation and supported text fields." );
        }

        if ( HasInvalidClipboardAction( request, value ) )
        {
            throw new InvalidOperationException( "Clipboard action is unavailable." );
        }

        if ( HasInvalidPreparedAction( request, value ) )
        {
            throw new InvalidOperationException( "Clipboard action preparation supports explicit Open only." );
        }

        if ( HasInvalidReleaseId( request, value ) )
        {
            throw new InvalidOperationException( "Clipboard release identity is invalid." );
        }

        if ( HasInvalidClipboardSettings( request, value ) )
        {
            throw new InvalidOperationException( "Clipboard capture policy is invalid." );
        }
    }

    private static bool HasInvalidClipboardKeywords( ClipboardRequest value )
    {
        return value.Keywords is not null &&
               (value.Keywords.Count > MaximumClipboardKeywords ||
                value.Keywords.Any( static keyword =>
                    keyword is null || keyword.Any( char.IsControl ) || Encoding.UTF8.GetByteCount( keyword ) > MaximumClipboardKeywordBytes ));
    }

    private static bool HasInvalidClipboardRevisions( ClipboardRequest value )
    {
        return value.OpenGeneration is < 0 or > ClipboardProtocolLimits.MaximumJavascriptInteger ||
               value.QueryGeneration is < 0 or > ClipboardProtocolLimits.MaximumJavascriptInteger ||
               value.EntryRevision < 0 ||
               value.PolicyRevision < 0 ||
               value.StoreRevision < 0 ||
               value.MatchIndex is < 0 or > MaximumClipboardMatchIndex;
    }

    private static bool HasInvalidFrontendIdentity( ClipboardRequest value )
    {
        return !Guid.TryParseExact( value.FrontendInstance, CompactGuidFormat, out _ ) || value.OpenGeneration < 1;
    }

    private static bool RequiresQueryGeneration( string operation, ClipboardRequest value )
    {
        return operation is ProtocolOperations.ClipboardQuery or ProtocolOperations.ClipboardPreview or ProtocolOperations.ClipboardActionsPrepare ||
               operation == ProtocolOperations.ClipboardAction && value.Action != ClipboardActionIds.Clear;
    }

    private static bool RequiresEntryIdentity( string operation, ClipboardRequest value )
    {
        return operation is ProtocolOperations.ClipboardPreview or ProtocolOperations.ClipboardActionsPrepare ||
               operation == ProtocolOperations.ClipboardAction && value.Action != ClipboardActionIds.Clear;
    }

    private static bool HasInvalidEntryIdentity( ClipboardRequest value )
    {
        return value.EntryId?.Length is not (GuidHexCharacters or DigestLengths.Sha256HexCharacters) ||
               value.EntryId.Any( static character => !char.IsAsciiHexDigit( character ) ) ||
               !Guid.TryParseExact( value.Incarnation, CompactGuidFormat, out _ ) ||
               value.EntryRevision < 1;
    }

    private static bool HasInvalidClipboardQuery( ProtocolRequest request, ClipboardRequest value )
    {
        return request.Operation == ProtocolOperations.ClipboardQuery &&
               (request.Limit > MaximumClipboardQueryLimit ||
                value.QueryGeneration < 1 ||
                value.KindFilter is not (null
                    or ClipboardPresentationKinds.Text
                    or ClipboardPresentationKinds.Links
                    or ClipboardPresentationKinds.Images
                    or ClipboardPresentationKinds.Files) ||
                request.Expression.IncludeFilterIds.Count != 0 ||
                request.Expression.ExcludeFilterIds.Count != 0 ||
                request.Expression.Clauses.Any( static clause =>
                    clause.Field is not null and not QueryFieldIds.Title and not QueryFieldIds.Content and not QueryFieldIds.Metadata ));
    }

    private static bool HasInvalidClipboardAction( ProtocolRequest request, ClipboardRequest value )
    {
        return request.Operation == ProtocolOperations.ClipboardAction &&
               value.Action is not (ClipboardActionIds.Copy
                   or ClipboardActionIds.Pin
                   or ClipboardActionIds.Unpin
                   or ClipboardActionIds.Delete
                   or ClipboardActionIds.Keywords
                   or ClipboardActionIds.Clear
                   or ClipboardActionIds.SequenceStart);
    }

    private static bool HasInvalidPreparedAction( ProtocolRequest request, ClipboardRequest value )
    {
        return request.Operation == ProtocolOperations.ClipboardActionsPrepare && value.Action != ClipboardActionIds.Open;
    }

    private static bool HasInvalidReleaseId( ProtocolRequest request, ClipboardRequest value )
    {
        return request.Operation == ProtocolOperations.ClipboardReleaseAbort &&
               value.ReleaseId is not null &&
               !Guid.TryParseExact( value.ReleaseId, CompactGuidFormat, out _ );
    }

    private static bool HasInvalidClipboardSettings( ProtocolRequest request, ClipboardRequest value )
    {
        return request.Operation == ProtocolOperations.ClipboardSettingsSet &&
               (value.PolicyRevision < 1 || !Enum.IsDefined( value.Consent ) || value is { Consent: ClipboardConsent.None, Paused: true });
    }
}
