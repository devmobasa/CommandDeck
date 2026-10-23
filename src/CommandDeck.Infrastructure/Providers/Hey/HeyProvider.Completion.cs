using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

// While the last word of a hey: query is a refinement with an unfinished value
// (in:, has:pd, date:, box:re), the rows are its values instead of a search.
// They run nothing; Enter or Tab puts the value into the query.
public sealed partial class HeyProvider
{
    private const string CompletionResultPrefix = ResultIdPrefixes.Hey + "complete:";

    public const string CompleteActionId = "complete";

    private static readonly Dictionary<string, (string Value, string Description)[]> CompletionValues = new( StringComparer.Ordinal )
    {
        ["in"] = HeyQuery.CompletionChoices( "in" ),
        ["has"] = HeyQuery.CompletionChoices( "has" ),
        ["date"] = HeyQuery.CompletionChoices( "date" ),
        ["box"] = [.. HeyBoxes.All.Select( static box => (box.Token, "List " + box.Label) )]
    };

    private static List<SearchResult>? CompletionRows( SearchRequest request )
    {
        var query = request.Query;
        if ( EndsAtWordBoundary( query ) )
        {
            return null;
        }

        var start = query.LastIndexOfAny( [' ', '\t'] ) + 1;
        var word = query[start..];
        var colon = word.IndexOf( ':', StringComparison.Ordinal );
        if ( CannotReadCompletionValues( word, colon, out var values ) )
        {
            return null;
        }

        var name = word[..colon]
            .ToLowerInvariant( );

        var typed = word[(colon + 1)..];
        if ( IsComplete( name, typed ) )
        {
            return null;
        }

        var matching = values
            .Where( value => value.Value.StartsWith( typed, StringComparison.OrdinalIgnoreCase ) )
            .ToArray( );

        if ( matching.Length == 0 )
        {
            return null;
        }

        var filterId = GetCompletionFilter( request );

        return
        [
            .. matching.Select( ( value, index ) =>
            {
                var completed = query[..start] + name + ":" + value.Value + " ";

                return new SearchResult
                {
                    Id = CompletionResultPrefix + name + ":" + value.Value,
                    Kind = ResultKinds.Status,
                    Title = name + ":" + value.Value,
                    Subtitle = value.Description,
                    Icon = name == "box" ? BoxIcon : ThreadIcon,
                    ProviderId = ProviderIds.Hey,
                    SourceId = SourceIds.Hey,
                    Score = HomeScore( index ),
                    CanCustomize = false,
                    Metadata = new( StringComparer.Ordinal ) { [ResultMetadataKeys.CompletionText] = completed },
                    Actions = [ApplyQueryAction( CompleteActionId, "Use " + name + ":" + value.Value, completed, filterId )]
                };
            } )
        ];
    }

    // A value the parser already accepts is finished: the query searches.
    private static bool IsComplete( string name, string typed ) => name switch
    {
        "in" => HeyQuery.Boxes.ContainsKey( typed ),
        "has" => HeyQuery.Attachments.ContainsKey( typed ),
        "date" => HeyQuery.Dates.ContainsKey( typed ),
        _ => HeyBoxes.FindByToken( typed ) is not null
    };

    private static bool EndsAtWordBoundary( string query )
    {
        return query.Length == 0 || char.IsWhiteSpace( query[^1] );
    }

    private static string GetCompletionFilter( SearchRequest request )
    {
        if ( request.Expression?.IncludeFilterIds.Contains( SearchFilterIds.Hey, StringComparer.OrdinalIgnoreCase ) == true )
        {
            return SearchFilterIds.Hey;
        }

        return "";
    }

    private static bool CannotReadCompletionValues( string word, int colon, [NotNullWhen( false )] out (string Value, string Description)[]? values )
    {
        values = default;

        return colon <= 0 ||
               !CompletionValues.TryGetValue( word[..colon]
                   .ToLowerInvariant( ), out values );
    }
}
