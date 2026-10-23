using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private const int RelevanceProbeLimit = 20;
    private const int MaximumRelevanceExplanationCharacters = 160;

    private sealed record RelevanceAlias( string Id, string Alias );

    private sealed record RelevanceUsage( string Id, int Count );

    private sealed record RelevanceStep(
        string Query,
        IReadOnlyList<string> Acceptable,
        IReadOnlyList<string> Required,
        IReadOnlyList<string> Forbidden,
        bool ExpectEmpty,
        int? MaximumCount,
        string? Section );

    private sealed record RelevanceCase( string Id, string Rationale, bool Critical, string? DependsOn, int VisibleWindow, IReadOnlyList<RelevanceStep> Steps );

    private sealed record RelevanceFixture(
        JsonElement Items,
        IReadOnlyList<RelevanceAlias> Aliases,
        IReadOnlyList<RelevanceUsage> Usage,
        IReadOnlyList<RelevanceCase> Cases );

    private static RelevanceFixture ParseRelevanceFixture( JsonElement relevance )
    {
        if ( relevance.ValueKind != JsonValueKind.Object )
        {
            throw new InvalidOperationException( "Relevance fixture is invalid: relevanceCases must be an object." );
        }

        var visibleWindow = ReadVisibleWindow( relevance, "relevanceCases" );
        var items = relevance.GetProperty( "items" );
        var itemIds = ReadItemIds( items );
        var aliases = ReadAliases( relevance, itemIds );
        var usage = ReadUsage( relevance, itemIds );
        var cases = ReadCases( relevance, visibleWindow, itemIds, usage.Count > 0 );
        if ( cases.Count == 0 )
        {
            throw new InvalidOperationException( "Relevance fixture is invalid: no cases." );
        }

        return new RelevanceFixture( items, aliases, usage, cases );
    }

    private static int ReadVisibleWindow( JsonElement element, string owner )
    {
        if ( !TryGetRelevanceProperty( element, "visibleWindow", JsonValueKind.Number, out var value ) )
        {
            throw new InvalidOperationException( $"Relevance fixture is invalid: {owner} needs visibleWindow." );
        }

        var window = value.GetInt32( );
        if ( window is < 1 or > RelevanceProbeLimit )
        {
            throw new InvalidOperationException( $"Relevance fixture is invalid: {owner} visibleWindow must be 1-{RelevanceProbeLimit}." );
        }

        return window;
    }

    private static HashSet<string> ReadItemIds( JsonElement items )
    {
        if ( items.ValueKind != JsonValueKind.Array )
        {
            throw new InvalidOperationException( "Relevance fixture is invalid: items must be an array." );
        }

        HashSet<string> ids = new( StringComparer.Ordinal );
        foreach ( var item in items.EnumerateArray( ) )
        {
            var id = ReadRequiredString( item, "id", "item" );
            if ( !ids.Add( id ) )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: duplicate item '{id}'." );
            }

            ReadRequiredString( item, "kind", id );
            ReadRequiredString( item, "title", id );
            ReadRequiredString( item, "sourceId", id );
        }

        if ( ids.Count == 0 )
        {
            throw new InvalidOperationException( "Relevance fixture is invalid: no items." );
        }

        return ids;
    }

    private static List<RelevanceAlias> ReadAliases( JsonElement relevance, IReadOnlySet<string> itemIds )
    {
        List<RelevanceAlias> aliases = [];
        if ( !relevance.TryGetProperty( "aliases", out var values ) )
        {
            return aliases;
        }

        if ( values.ValueKind != JsonValueKind.Array )
        {
            throw new InvalidOperationException( "Relevance fixture is invalid: aliases must be an array." );
        }

        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        foreach ( var alias in values.EnumerateArray( ) )
        {
            var id = ReadRequiredString( alias, "id", "alias" );
            var name = ReadRequiredString( alias, "alias", id );
            RequireKnownId( itemIds, id, "alias" );

            if ( !seen.Add( name ) )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: duplicate alias '{name}'." );
            }

            aliases.Add( new RelevanceAlias( id, name ) );
        }

        return aliases;
    }

    private static List<RelevanceUsage> ReadUsage( JsonElement relevance, IReadOnlySet<string> itemIds )
    {
        List<RelevanceUsage> usage = [];
        if ( !relevance.TryGetProperty( "usage", out var values ) )
        {
            return usage;
        }

        if ( values.ValueKind != JsonValueKind.Array )
        {
            throw new InvalidOperationException( "Relevance fixture is invalid: usage must be an array." );
        }

        foreach ( var entry in values.EnumerateArray( ) )
        {
            var id = ReadRequiredString( entry, "id", "usage" );
            RequireKnownId( itemIds, id, "usage" );

            if ( !TryGetRelevanceProperty( entry, "count", JsonValueKind.Number, out var countValue ) )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: usage '{id}' needs count." );
            }

            var count = countValue.GetInt32( );
            if ( count is < 1 or > 20 )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: usage '{id}' count must be 1-20." );
            }

            usage.Add( new RelevanceUsage( id, count ) );
        }

        return usage;
    }

    private static List<RelevanceCase> ReadCases( JsonElement relevance, int defaultWindow, IReadOnlySet<string> itemIds, bool hasUsage )
    {
        if ( !TryGetRelevanceProperty( relevance, "cases", JsonValueKind.Array, out var values ) )
        {
            throw new InvalidOperationException( "Relevance fixture is invalid: cases must be an array." );
        }

        List<RelevanceCase> cases = [];
        HashSet<string> ids = new( StringComparer.Ordinal );
        foreach ( var testCase in values.EnumerateArray( ) )
        {
            var id = ReadRequiredString( testCase, "id", "case" );
            if ( !ids.Add( id ) )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: duplicate case '{id}'." );
            }

            var rationale = ReadRequiredString( testCase, "rationale", id );
            if ( rationale.Length > 400 )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{id}' rationale is too long." );
            }

            var dependsOn = OptionalString( testCase, "dependsOn" );
            if ( dependsOn is not null and not "usage" )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{id}' dependsOn is unknown." );
            }

            if ( dependsOn == "usage" && !hasUsage )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{id}' depends on usage." );
            }

            var window = CreateWindow( defaultWindow, testCase, id );
            var steps = ReadSteps( testCase, id, itemIds );
            var critical = HasTrueRelevanceProperty( testCase, "critical" );
            cases.Add( new RelevanceCase( id, rationale, critical, dependsOn, window, steps ) );
        }

        return cases;

        static int CreateWindow( int defaultWindow, JsonElement testCase, string id )
        {
            if ( testCase.TryGetProperty( "visibleWindow", out _ ) )
            {
                return ReadVisibleWindow( testCase, id );
            }

            return defaultWindow;
        }
    }

    private static List<RelevanceStep> ReadSteps( JsonElement testCase, string caseId, IReadOnlySet<string> itemIds )
    {
        if ( !TryGetRelevanceProperty( testCase, "steps", JsonValueKind.Array, out var values ) )
        {
            throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' needs steps." );
        }

        List<RelevanceStep> steps = [];
        foreach ( var step in values.EnumerateArray( ) )
        {
            if ( steps.Count == 8 )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' has too many steps." );
            }

            var query = ReadRequiredString( step, "query", caseId );
            if ( query.Length > 200 )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' query is too long." );
            }

            var acceptable = ReadIdList( step, "acceptable", caseId, itemIds );
            var required = ReadIdList( step, "required", caseId, itemIds );
            var forbidden = ReadIdList( step, "forbidden", caseId, itemIds );
            var expectEmpty = HasTrueRelevanceProperty( step, "expectEmpty" );
            int? maximumCount = null;
            if ( step.TryGetProperty( "maximumCount", out var maximumValue ) )
            {
                if ( maximumValue.ValueKind != JsonValueKind.Number )
                {
                    throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' maximumCount must be a number." );
                }

                var maximum = maximumValue.GetInt32( );
                if ( maximum is < 0 or > RelevanceProbeLimit )
                {
                    throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' maximumCount must be 0-{RelevanceProbeLimit}." );
                }

                maximumCount = maximum;
            }

            if ( expectEmpty && required.Count > 0 )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' cannot require rows and be empty." );
            }

            var section = OptionalString( step, "section" );

            if ( acceptable.Count == 0 && required.Count == 0 && forbidden.Count == 0 && !expectEmpty && maximumCount is null )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' has no assertion." );
            }

            steps.Add( new RelevanceStep( query, acceptable, required, forbidden, expectEmpty, maximumCount,
                section ) );
        }

        if ( steps.Count == 0 )
        {
            throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' has no steps." );
        }

        return steps;
    }

    private static List<string> ReadIdList( JsonElement step, string name, string caseId, IReadOnlySet<string> itemIds )
    {
        if ( !step.TryGetProperty( name, out var values ) )
        {
            return [];
        }

        if ( values.ValueKind != JsonValueKind.Array )
        {
            throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' {name} must be an array." );
        }

        List<string> ids = [];
        HashSet<string> seen = new( StringComparer.Ordinal );
        foreach ( var value in values.EnumerateArray( ) )
        {
            var id = value.GetString( );
            if ( string.IsNullOrWhiteSpace( id ) )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' {name} has an empty id." );
            }

            if ( !seen.Add( id ) )
            {
                throw new InvalidOperationException( $"Relevance fixture is invalid: case '{caseId}' repeats '{id}'." );
            }

            RequireKnownId( itemIds, id, caseId );
            ids.Add( id );
        }

        return ids;
    }

    private static string ReadRequiredString( JsonElement element, string name, string owner )
    {
        if ( !TryGetRelevanceProperty( element, name, JsonValueKind.String, out var value ) )
        {
            throw new InvalidOperationException( $"Relevance fixture is invalid: {owner} needs {name}." );
        }

        var text = value.GetString( );
        if ( string.IsNullOrWhiteSpace( text ) )
        {
            throw new InvalidOperationException( $"Relevance fixture is invalid: {owner} needs {name}." );
        }

        return text.Trim( );
    }

    private static void RequireKnownId( IReadOnlySet<string> itemIds, string id, string owner )
    {
        if ( !itemIds.Contains( id ) )
        {
            throw new InvalidOperationException( $"Relevance fixture is invalid: {owner} references unknown id '{id}'." );
        }
    }

    private static bool TryGetRelevanceProperty( JsonElement element, string name, JsonValueKind kind, out JsonElement value )
    {
        if ( !element.TryGetProperty( name, out value ) )
        {
            return false;
        }

        return value.ValueKind == kind;
    }

    private static bool HasTrueRelevanceProperty( JsonElement element, string name ) =>
        element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.True;
}
