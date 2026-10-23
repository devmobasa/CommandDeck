using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CommandDeck;

// hey answers every data command with an envelope: {"ok":true,"data":...} or
// {"ok":false,"error":...,"code":...}. The data is HEY's own payload, read
// field by field so unknown fields never matter and missing ones fail closed.
public sealed partial class HeyClient
{
    public const string DefaultWebOrigin = "https://app.hey.com";
    public const int MaximumSubjectCharacters = 300;
    public const int MaximumSummaryCharacters = 400;
    public const int MaximumSenderCharacters = 200;

    private static HeyStatus ParseStatus( ProcessResult result )
    {
        if ( result.TimedOut )
        {
            return new HeyStatus( HeyAvailability.Failed );
        }

        if ( result.ExitCode != 0 )
        {
            var failure = FailureOf( result );

            return CreateFailedStatus( failure );
        }

        var envelope = ReadEnvelope( result.StandardOutput );
        if ( envelope.Failure is not null )
        {
            return new HeyStatus( HeyAvailability.Failed );
        }

        using ( envelope.Document )
        {
            var authenticated = IsAuthenticated( envelope );

            return CreateAuthenticationStatus( authenticated );
        }
    }

    // One result per thread: {id?, topic_id, subject, updated_at, messages:[entry]}.
    private static HeyListing ParseSearch( string json )
    {
        var envelope = ReadEnvelope( json );
        if ( envelope.Failure is not null )
        {
            return new HeyListing { Failure = envelope.Failure };
        }

        using ( envelope.Document )
        {
            if ( envelope.Data.ValueKind != JsonValueKind.Array )
            {
                return Malformed( );
            }

            List<HeyThread> threads = [];

            foreach ( var match in envelope
                         .Data.EnumerateArray( )
                         .Take( MaximumThreads ) )
            {
                var topicId = ReadId( match, "topic_id" );

                if ( match.ValueKind != JsonValueKind.Object )
                {
                    continue;
                }

                if ( topicId is null )
                {
                    continue;
                }

                var entry = FirstEntry( match );
                threads.Add( CreateMatchedThread( match, topicId.Value, entry ) );
            }

            return new HeyListing { Threads = threads, HasMore = IsPaginationNotice( envelope.Notice ) || envelope.Data.GetArrayLength( ) > MaximumThreads };
        }
    }

    // When hey stops paging with matches left, its notice says where to go on
    // ("Search stopped after 1 pages. Continue with --page 2."); a notice that
    // names no page says nothing about more matches.
    private static bool IsPaginationNotice( string? notice ) =>
        notice is not null && notice.Contains( PageOption, StringComparison.Ordinal );

    // HEY's box payload with its postings: {postings:[posting + topic_id], next_page?}.
    private static HeyListing ParseBox( string json )
    {
        var envelope = ReadEnvelope( json );
        if ( envelope.Failure is not null )
        {
            return new HeyListing { Failure = envelope.Failure };
        }

        using ( envelope.Document )
        {
            if ( CannotReadPostings( envelope, out var postings ) )
            {
                return Malformed( );
            }

            List<HeyThread> threads = [];

            foreach ( var posting in postings
                         .EnumerateArray( )
                         .Take( MaximumThreads ) )
            {
                // A bundle row stands for one sender's stream and has no thread.
                var topicId = ReadId( posting, "topic_id" );

                if ( posting.ValueKind != JsonValueKind.Object )
                {
                    continue;
                }

                if ( topicId is null )
                {
                    continue;
                }

                threads.Add( CreatePostingThread( posting, topicId.Value ) );
            }

            return new HeyListing
            {
                Threads = threads,
                HasMore = !string.IsNullOrWhiteSpace( ReadString( envelope.Data, "next_page" ) ) || postings.GetArrayLength( ) > MaximumThreads
            };
        }
    }

    // HEY marks a posting that has bubbled up with bubbled_up, and one waiting
    // to with a bubble_up_schedule; hey leaves either out when it is not set.
    private static bool IsBubbledUp( JsonElement posting ) =>
        posting.TryGetProperty( "bubbled_up", out var bubbled ) && bubbled.ValueKind == JsonValueKind.True ||
        posting.TryGetProperty( "bubble_up_schedule", out var schedule ) && schedule.ValueKind == JsonValueKind.Object;

    // A failing hey writes its error envelope to stderr when stdout is not a
    // terminal, which is always the case here; stdout is checked too, and the
    // exit code is the last resort. Error output past its limit was cut and
    // hey stopped, so its exit code explains nothing either.
    internal static HeyFailure FailureOf( ProcessResult result )
    {
        foreach ( var stream in new[] { result.StandardError, result.StandardOutput } )
        {
            var failure = ReadErrorEnvelope( stream );
            if ( failure is not null )
            {
                return failure;
            }
        }

        if ( result.StandardErrorLimitExceeded )
        {
            return new HeyFailure( HeyFailureKind.Failed, "HEY failed with more error output than CommandDeck reads." );
        }

        return new HeyFailure( HeyFailureKind.Failed, $"The HEY CLI exited with code {result.ExitCode.ToString( CultureInfo.InvariantCulture )}." );
    }

    // Only a JSON object with "ok": false is an error envelope.
    private static HeyFailure? ReadErrorEnvelope( string text )
    {
        if ( string.IsNullOrWhiteSpace( text ) )
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse( text );
            var root = document.RootElement;

            return ReadEnvelopeFailure( root );
        }
        catch ( JsonException )
        {
            return null;
        }
    }

    private static HeyFailure ErrorFrom( JsonElement root ) =>
        new( FailureKind( ReadString( root, "code" ) ),
            Clean( ReadString( root, "error" ), MaximumSummaryCharacters ) is { Length: > 0 } message ? message : "HEY refused the request." );

    private static HeyListing Malformed( ) =>
        HeyListing.Failed( HeyFailureKind.Failed, "HEY answered in a shape CommandDeck does not read." );

    private readonly record struct Envelope( JsonDocument? Document, JsonElement Data, string? Notice, HeyFailure? Failure );

    private static Envelope ReadEnvelope( string json )
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse( json );
        }
        catch ( JsonException )
        {
            return new Envelope( null, default, null, new HeyFailure( HeyFailureKind.Failed, "HEY answered with malformed JSON." ) );
        }

        var root = document.RootElement;
        if ( CannotReadEnvelopeStatus( root, out var ok ) )
        {
            document.Dispose( );

            return new Envelope( null, default, null, new HeyFailure( HeyFailureKind.Failed, "HEY answered with malformed JSON." ) );
        }

        if ( ok.ValueKind != JsonValueKind.True )
        {
            var failure = ErrorFrom( root );
            document.Dispose( );

            return new Envelope( null, default, null, failure );
        }

        var data = root.TryGetProperty( "data", out var value ) ? value : default;

        return new Envelope( document, data, ReadString( root, "notice" ), null );
    }

    private static HeyFailureKind FailureKind( string? code ) => code switch
    {
        "auth" => HeyFailureKind.SignedOut,
        "network" => HeyFailureKind.Network,
        "rate_limit" => HeyFailureKind.RateLimited,
        "usage" or "validation" or "not_found" or "ambiguous" or "forbidden" or "conflict" => HeyFailureKind.Rejected,
        _ => HeyFailureKind.Failed
    };

    private static JsonElement? FirstEntry( JsonElement match )
    {
        if ( TryReadFirstMessage( match, out var messages ) )
        {
            return messages[0];
        }
        else
        {
            return null;
        }
    }

    // HEY names a sender by the address it sent as when that differs from
    // the contact, the way its own rows do.
    private static string SenderName( JsonElement item )
    {
        var alternative = Clean( ReadString( item, "alternative_sender_name" ), MaximumSenderCharacters );
        if ( alternative.Length > 0 )
        {
            return alternative;
        }

        if ( CannotReadCreator( item, out var creator ) )
        {
            return "";
        }

        var name = Clean( ReadString( creator, "name" ), MaximumSenderCharacters );

        return name.Length > 0 ? name : Clean( ReadString( creator, "email_address" ), MaximumSenderCharacters );
    }

    private static string? SenderAddress( JsonElement item )
    {
        if ( CannotReadCreator( item, out var creator ) )
        {
            return null;
        }

        var address = Clean( ReadString( creator, "email_address" ), MaximumSenderCharacters );

        return address.Length > 0 ? address : null;
    }

    private static string Subject( string? value ) =>
        Clean( value, MaximumSubjectCharacters ) is { Length: > 0 } subject ? subject : "(no subject)";

    // A thread opens on HEY's own origin, never a host taken from a payload, the
    // same rule HEY's Omarchy plugin follows for its web actions.
    public static string ThreadUrl( long topicId ) =>
        DefaultWebOrigin + "/topics/" + topicId.ToString( CultureInfo.InvariantCulture );

    private static long? ReadId( JsonElement item, string name )
    {
        if ( TryReadPositiveId( item, name, out var id ) )
        {
            return id;
        }
        else
        {
            return null;
        }
    }

    private static string? ReadString( JsonElement item, string name )
    {
        if ( TryReadStringValue( item, name, out var value ) )
        {
            return value.GetString( );
        }
        else
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadTime( JsonElement item, string name )
    {
        if ( TryReadValidTime( item, name, out var value ) )
        {
            return value;
        }
        else
        {
            return null;
        }
    }

    // Mail text is shown inert: control and format characters become spaces,
    // runs of whitespace collapse, and the length is bounded.
    internal static string Clean( string? value, int maximumLength )
    {
        if ( string.IsNullOrEmpty( value ) )
        {
            return "";
        }

        StringBuilder builder = new( Math.Min( value.Length, maximumLength ) );
        var pendingSpace = false;

        foreach ( var character in value )
        {
            if ( IsWhitespaceOrControl( character ) )
            {
                pendingSpace = builder.Length > 0;

                continue;
            }

            // A space is kept only before another character, so both must fit.
            if ( pendingSpace )
            {
                if ( builder.Length + 2 > maximumLength )
                {
                    break;
                }

                builder.Append( ' ' );
                pendingSpace = false;
            }

            builder.Append( character );

            if ( builder.Length >= maximumLength )
            {
                break;
            }
        }

        // The bound must not cut a surrogate pair in half, nor end on the
        // space before it.
        if ( EndsWithHighSurrogate( builder ) )
        {
            builder.Length--;
        }

        if ( EndsWithSpace( builder ) )
        {
            builder.Length--;
        }

        return builder.ToString( );
    }

    private static HeyStatus CreateFailedStatus( HeyFailure failure )
    {
        if ( failure.Kind == HeyFailureKind.SignedOut )
        {
            return new HeyStatus( HeyAvailability.SignedOut );
        }

        return new HeyStatus( HeyAvailability.Failed );
    }

    private static bool IsAuthenticated( Envelope envelope )
    {
        return envelope.Data.ValueKind == JsonValueKind.Object &&
               envelope.Data.TryGetProperty( "authenticated", out var value ) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static HeyStatus CreateAuthenticationStatus( bool authenticated )
    {
        if ( authenticated )
        {
            return new HeyStatus( HeyAvailability.SignedIn );
        }

        return new HeyStatus( HeyAvailability.SignedOut );
    }

    private static HeyThread CreateMatchedThread( JsonElement match, long topicId, JsonElement? entry )
    {
        return new HeyThread
        {
            TopicId = topicId,
            PostingId = ReadId( match, "id" ),
            Subject = Subject( ReadString( match, "subject" ) ),
            Sender = entry is null ? "" : SenderName( entry.Value ),
            SenderAddress = entry is null ? null : SenderAddress( entry.Value ),
            Summary = Clean( entry is null ? null : ReadString( entry.Value, "summary" ), MaximumSummaryCharacters ),
            At = (entry is null ? null : ReadTime( entry.Value, "created_at" )) ?? ReadTime( match, "updated_at" ),
            AppUrl = ThreadUrl( topicId )
        };
    }

    private static HeyThread CreatePostingThread( JsonElement posting, long topicId )
    {
        return new HeyThread
        {
            TopicId = topicId,
            PostingId = ReadId( posting, "id" ),
            BoxId = ReadId( posting, "box_id" ),
            Subject = Subject( ReadString( posting, "name" ) ),
            Sender = SenderName( posting ),
            SenderAddress = SenderAddress( posting ),
            Summary = Clean( ReadString( posting, "summary" ), MaximumSummaryCharacters ),
            At = ReadTime( posting, "active_at" ) ?? ReadTime( posting, "created_at" ),
            Seen = posting.TryGetProperty( "seen", out var seen ) && seen.ValueKind == JsonValueKind.True,
            BubbledUp = IsBubbledUp( posting ),
            AppUrl = ThreadUrl( topicId )
        };
    }

    private static HeyFailure? ReadEnvelopeFailure( JsonElement root )
    {
        if ( TryReadErrorStatus( root ) )
        {
            return ErrorFrom( root );
        }

        return null;
    }

    private static bool IsWhitespaceOrControl( char character )
    {
        return char.IsWhiteSpace( character ) || char.IsControl( character ) || CharUnicodeInfo.GetUnicodeCategory( character ) == UnicodeCategory.Format;
    }

    private static bool EndsWithHighSurrogate( StringBuilder builder )
    {
        return builder.Length > 0 && char.IsHighSurrogate( builder[^1] );
    }

    private static bool EndsWithSpace( StringBuilder builder )
    {
        return builder.Length > 0 && builder[^1] == ' ';
    }

    private static bool CannotReadPostings( Envelope envelope, out JsonElement postings )
    {
        postings = default;

        return envelope.Data.ValueKind != JsonValueKind.Object ||
               !envelope.Data.TryGetProperty( "postings", out postings ) ||
               postings.ValueKind != JsonValueKind.Array;
    }

    private static bool CannotReadEnvelopeStatus( JsonElement root, out JsonElement ok )
    {
        ok = default;

        return root.ValueKind != JsonValueKind.Object || !root.TryGetProperty( "ok", out ok );
    }

    private static bool TryReadFirstMessage( JsonElement match, out JsonElement messages )
    {
        messages = default;

        return match.TryGetProperty( "messages", out messages ) &&
               messages.ValueKind == JsonValueKind.Array &&
               messages.GetArrayLength( ) > 0 &&
               messages[0].ValueKind == JsonValueKind.Object;
    }

    private static bool CannotReadCreator( JsonElement item, out JsonElement creator )
    {
        creator = default;

        return !item.TryGetProperty( "creator", out creator ) || creator.ValueKind != JsonValueKind.Object;
    }

    private static bool TryReadPositiveId( JsonElement item, string name, out long id )
    {
        id = default;

        return item.ValueKind == JsonValueKind.Object &&
               item.TryGetProperty( name, out var value ) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64( out id ) &&
               id > 0;
    }

    private static bool TryReadStringValue( JsonElement item, string name, out JsonElement value )
    {
        value = default;

        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty( name, out value ) && value.ValueKind == JsonValueKind.String;
    }

    private static bool TryReadValidTime( JsonElement item, string name, out DateTimeOffset value )
    {
        value = default;

        return DateTimeOffset.TryParse( ReadString( item, name ), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value ) && value.Year > 1;
    }

    private static bool TryReadErrorStatus( JsonElement root )
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty( "ok", out var ok ) && ok.ValueKind == JsonValueKind.False;
    }
}
