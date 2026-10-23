using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

/// <summary>
/// Reads HEY refinements out of a <c>hey:</c> query. Plain words are the search
/// text; a quoted phrase becomes HEY's exact phrase; <c>-word</c> excludes a
/// word. Refinements take one value each: <c>from:</c>, <c>to:</c>,
/// <c>subject:</c>, <c>label:</c>, and the fixed vocabularies of <c>in:</c>,
/// <c>has:</c>, and <c>date:</c>, which accept a few friendlier spellings.
/// </summary>
public sealed record HeyQuery
{
    public HeySearchQuery Search { get; init; } = new( );

    // Why the query cannot be sent: an unknown in:, has:, or date: value.
    public string? Problem { get; init; }

    // box: lists one box instead of searching; the words then filter its threads.
    public HeyBox? Box { get; init; }
    public IReadOnlyList<string> Words { get; init; } = [];

    public static IReadOnlyList<string> RefinementNames { get; } = ["from", "to", "subject", "in", "label", "has", "date", "box"];

    internal const string Imbox = "imbox";
    internal const string Feed = "feed";

    private static readonly (string Refinement, string Canonical, string? Completion, string Description, string[] Aliases)[] VocabularyRows =
    [
        ("in", Imbox, null, "Search the Imbox", []),
        ("in", Feed, null, "Search The Feed", ["the-feed"]),
        ("in", "papertrail", null, "Search the Paper Trail", ["paper-trail", "trail"]),
        ("in", "trash", null, "Search the Trash", []),
        ("has", "any", null, "Any attachment", ["attachment", "attachments"]),
        ("has", "images", null, "Images", ["image"]),
        ("has", "pdfs", null, "PDFs", ["pdf"]),
        ("has", "calendar_invites", "invites", "Calendar invites", ["invite"]),
        ("has", "documents", null, "Documents", ["document", "docs"]),
        ("has", "spreadsheets", null, "Spreadsheets", ["spreadsheet", "sheets"]),
        ("has", "presentations", null, "Presentations", ["presentation", "slides"]),
        ("has", "media", null, "Audio and video", []),
        ("has", "zip_files", "zip", "Zip files", []),
        ("date", "last_7_days", "7d", "The last 7 days", ["week"]),
        ("date", "last_30_days", "30d", "The last 30 days", ["month"]),
        ("date", "last_90_days", "90d", "The last 90 days", []),
        ("date", "year", null, "This year", []),
    ];

    internal static (string Value, string Description)[] CompletionChoices( string refinement ) => VocabularyRows
        .Where( row => row.Refinement == refinement )
        .Select( row => (row.Completion ?? row.Canonical, row.Description) )
        .ToArray( );

    private static Dictionary<string, string> ParserVocabulary( string refinement ) => VocabularyRows
        .Where( row => row.Refinement == refinement )
        .SelectMany( row => row
            .Aliases.Append( row.Canonical )
            .Concat( row.Completion is null ? [] : new[] { row.Completion } )
            .Select( alias => KeyValuePair.Create( alias, row.Canonical ) ) )
        .ToDictionary( StringComparer.OrdinalIgnoreCase );

    public static IReadOnlyDictionary<string, string> Boxes { get; } = ParserVocabulary( "in" );

    public static IReadOnlyDictionary<string, string> Attachments { get; } = ParserVocabulary( "has" );

    public static IReadOnlyDictionary<string, string> Dates { get; } = ParserVocabulary( "date" );

    public static HeyQuery Parse( ParsedQuery query )
    {
        ArgumentNullException.ThrowIfNull( query );

        List<string> words = [];
        List<string> excluded = [];
        string? exact = null;
        string? problem = null;
        Dictionary<string, string> refinements = new( StringComparer.OrdinalIgnoreCase );

        foreach ( var clause in Clauses( query ) )
        {
            // title:, path:, and the other fields of local search mean nothing to HEY.
            if ( clause.Field is not null )
            {
                problem ??= $"HEY has no {clause.Field}: refinement. Use {SupportedRefinements}.";

                continue;
            }

            var (name, value) = SplitRefinement( clause.Text );

            if ( name is not null )
            {
                problem ??= AddRefinement( refinements, name, value, clause.Excluded );

                continue;
            }

            if ( clause.Excluded )
            {
                excluded.Add( clause.Text );
            }
            else if ( IsUnquotedPhrase( exact, clause ) )
            {
                exact = clause.Text;
            }
            else
            {
                words.Add( clause.Text );
            }
        }

        problem ??= Vocabulary( refinements, "in", Boxes, "imbox, feed, papertrail, or trash" ) ??
                    Vocabulary( refinements, "has", Attachments, "any, images, pdfs, invites, documents, spreadsheets, presentations, media, or zip" ) ??
                    Vocabulary( refinements, "date", Dates, "7d, 30d, 90d, or year" );

        var box = refinements.TryGetValue( "box", out var boxToken ) ? HeyBoxes.FindByToken( boxToken ) : null;
        if ( IsUnknownBox( box, boxToken ) )
        {
            problem ??= $"HEY has no box:{boxToken}. Use {string.Join( ", ", HeyBoxes.All.Select( static value => value.Token ) )}.";
        }
        else if ( HasConflictingRefinements( refinements, box ) )
        {
            problem ??= "box: lists one box. To search a box, use in:imbox, in:feed, or in:papertrail.";
        }

        return new HeyQuery
        {
            Problem = problem,
            Box = box,
            Words = exact is null ? words : [.. words, exact],
            Search = new HeySearchQuery
            {
                Text = string.Join( ' ', words ),
                Exact = exact,
                None = excluded.Count == 0 ? null : string.Join( ' ', excluded ),
                From = refinements.GetValueOrDefault( "from" ),
                To = refinements.GetValueOrDefault( "to" ),
                Subject = refinements.GetValueOrDefault( "subject" ),
                Label = refinements.GetValueOrDefault( "label" ),
                Box = Canonical( refinements, "in", Boxes ),
                Attachment = Canonical( refinements, "has", Attachments ),
                Date = Canonical( refinements, "date", Dates )
            }
        };
    }

    // @hey keeps its text unparsed; read the same clauses a hey: query has.
    private static List<QueryClause> Clauses( ParsedQuery query )
    {
        if ( HasStructuredOrEmptyQuery( query ) )
        {
            return query.Expression.Clauses;
        }

        try
        {
            return QueryParser.Parse( query.Text ).Expression.Clauses;
        }
        catch ( InvalidOperationException )
        {
            return [new QueryClause { Text = query.Text }];
        }
    }

    // "from:jane" or from:"Jane Cooper"; anything else is ordinary text.
    public static (string? Name, string Value) SplitRefinement( string text )
    {
        var colon = text.IndexOf( ':', StringComparison.Ordinal );
        if ( colon <= 0 )
        {
            return (null, text);
        }

        var name = text[..colon];
        if ( !RefinementNames.Contains( name, StringComparer.OrdinalIgnoreCase ) )
        {
            return (null, text);
        }

        var value = text[(colon + 1)..]
            .Trim( );

        if ( IsQuoted( value ) )
        {
            value = value[1..^1]
                .Trim( );
        }

        return (name.ToLowerInvariant( ), value);
    }

    private const string SupportedRefinements = "subject:, from:, to:, label:, in:, has:, or date:";

    // A value still being typed (from: alone) waits. HEY cannot leave a
    // refinement out, and each takes one value.
    private static string? AddRefinement( Dictionary<string, string> refinements, string name, string value, bool excluded )
    {
        if ( value.Length == 0 )
        {
            return null;
        }

        if ( excluded )
        {
            return $"HEY can't leave out {name}:{value}; a minus only leaves out words.";
        }

        if ( HasConflictingRefinement( refinements, name, value, out var earlier ) )
        {
            return $"HEY takes one {name}: value; keep {name}:{earlier} or {name}:{value}.";
        }

        refinements.TryAdd( name, value );

        return null;
    }

    private static string? Vocabulary( Dictionary<string, string> refinements, string name, IReadOnlyDictionary<string, string> values, string accepted )
    {
        if ( HasUnknownRefinement( refinements, name, values, out var value ) )
        {
            return $"HEY does not know {name}:{value}. Use {accepted}.";
        }
        else
        {
            return null;
        }
    }

    private static string? Canonical( Dictionary<string, string> refinements, string name, IReadOnlyDictionary<string, string> values ) =>
        refinements.TryGetValue( name, out var value ) && values.TryGetValue( value, out var canonical ) ? canonical : null;

    private static bool IsUnquotedPhrase( string? exact, QueryClause clause )
    {
        return exact is null && clause.Text.Contains( ' ', StringComparison.Ordinal );
    }

    private static bool IsUnknownBox( HeyBox? box, string? boxToken )
    {
        return boxToken is not null && box is null;
    }

    private static bool HasConflictingRefinements( Dictionary<string, string> refinements, HeyBox? box )
    {
        return box is not null && refinements.Count > 1;
    }

    private static bool HasStructuredOrEmptyQuery( ParsedQuery query )
    {
        return query.Expression.Clauses.Count > 0 || query.Text.Length == 0;
    }

    private static bool IsQuoted( string value )
    {
        return value is ['"', .., '"'];
    }

    private static bool HasConflictingRefinement( Dictionary<string, string> refinements, string name, string value, [NotNullWhen( true )] out string? earlier )
    {
        earlier = default;

        return refinements.TryGetValue( name, out earlier ) && !string.Equals( earlier, value, StringComparison.OrdinalIgnoreCase );
    }

    private static bool HasUnknownRefinement( Dictionary<string, string> refinements, string name, IReadOnlyDictionary<string, string> values,
        [NotNullWhen( true )] out string? value )
    {
        value = default;

        return refinements.TryGetValue( name, out value ) && !values.ContainsKey( value );
    }
}
