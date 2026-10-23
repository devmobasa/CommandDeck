namespace CommandDeck;

public static partial class QueryParser
{
    private static string OriginalText( string? raw, QueryExpression? expression ) =>
        (string.IsNullOrWhiteSpace( raw ) ? expression?.LiteralQuery : raw)?.Trim( ) ?? string.Empty;

    // Prompts and invocation argv are payloads, not search clauses. Decide at
    // their leading boundary before interpreting '-' or field/filter prefixes.
    private static bool TryParseLiteralPayload( string original, QueryExpression seed, IReadOnlyDictionary<string, string>? aliases,
        ContributionRegistrySnapshot? registry, out LiteralPayload payload )
    {
        payload = null!;

        if ( original.Length == 0 )
        {
            return false;
        }

        var text = original;
        List<string> includes = [.. seed.IncludeFilterIds];
        if ( !TryReadPayloadPrefix( ref text, includes, aliases, registry ) )
        {
            return false;
        }

        var ai = includes.Count == 1 && string.Equals( includes[0], SearchFilterIds.Ai, StringComparison.OrdinalIgnoreCase );
        if ( HasIncompatibleLiteralFilters( includes, ai ) )
        {
            return false;
        }

        string[] terms;
        if ( ai )
        {
            terms = text.Length == 0 ? [] : [text];
        }
        else
        {
            terms = SplitArguments( text );

            if ( CannotParseApplicationArguments( aliases, registry, includes, terms ) )
            {
                return false;
            }

            ParsedQuery invocationQuery = new( ) { Original = original, Text = text, Terms = terms };
            if ( !ApplicationInvocation.TryParse( invocationQuery, out _ ) )
            {
                return false;
            }
        }

        var expression = CreateLiteralExpression( original, seed, includes );
        payload = new LiteralPayload( text, terms, expression, ai );

        return true;
    }

    // The argv rules of application invocations and `>` commands, not a shell:
    // whitespace separates words, and a word wholly in double quotes is one
    // argument without them. Quotes inside a word stay literal; an unterminated
    // quote throws InvalidOperationException.
    public static string[] SplitArguments( string text ) => TokenizeSyntax( text )
        .Select( Unquote )
        .ToArray( );

    private static QueryExpression WithLiteralQuery( QueryExpression expression, string original ) => new( )
    {
        LiteralQuery = original,
        IncludeFilterIds = expression.IncludeFilterIds,
        ExcludeFilterIds = expression.ExcludeFilterIds,
        Clauses = expression.Clauses
    };

    private static bool TryReadPayloadPrefix( ref string text, List<string> includes, IReadOnlyDictionary<string, string>? aliases,
        ContributionRegistrySnapshot? registry )
    {
        var colon = text.IndexOf( ':' );
        if ( HasNoFilterPrefix( text, colon ) )
        {
            return true;
        }

        var seededAi = IsSeededAiQuery( includes );
        var filter = ResolvePayloadFilter( text, aliases, registry, colon, seededAi );
        if ( HasInvalidPayloadFilter( seededAi, filter ) )
        {
            return false;
        }

        if ( ShouldConsumePayloadFilter( seededAi, filter ) )
        {
            AddUnique( includes, filter.Id );

            text = text[(colon + 1)..]
                .TrimStart( );
        }

        return true;
    }

    private static bool IsSearchSyntax( string value, IReadOnlyDictionary<string, string>? aliases, ContributionRegistrySnapshot? registry )
    {
        var token = value.TrimStart( '-' );
        var colon = token.IndexOf( ':' );

        return colon > 0 && (QueryFieldIds.All.Contains( token[..colon] ) || ResolveToken( token[..colon], aliases, registry ) is not null);
    }

    private sealed record LiteralPayload( string Text, IReadOnlyList<string> Terms, QueryExpression Expression, bool IsAi );

    private static bool HasIncompatibleLiteralFilters( List<string> includes, bool ai )
    {
        return !ai &&
               (includes.Count > 1 || includes.Count == 1 && !string.Equals( includes[0], SearchFilterIds.Applications, StringComparison.OrdinalIgnoreCase ));
    }

    private static bool CannotParseApplicationArguments( IReadOnlyDictionary<string, string>? aliases, ContributionRegistrySnapshot? registry,
        List<string> includes, string[] terms )
    {
        return terms.Length < 2 ||
               IsSearchSyntax( terms[1], aliases, registry ) ||
               includes.Count == 0 &&
               terms[1]
                   .StartsWith( '-', StringComparison.Ordinal ) &&
               !terms[1]
                   .StartsWith( "--", StringComparison.Ordinal );
    }

    private static QueryExpression CreateLiteralExpression( string original, QueryExpression seed, List<string> includes )
    {
        return Validate( new QueryExpression { LiteralQuery = original, IncludeFilterIds = includes, ExcludeFilterIds = [.. seed.ExcludeFilterIds] } );
    }

    private static bool HasNoFilterPrefix( string text, int colon )
    {
        return colon <= 0 ||
               text[..colon]
                   .Any( char.IsWhiteSpace );
    }

    private static bool IsSeededAiQuery( List<string> includes )
    {
        return includes.Count == 1 && string.Equals( includes[0], SearchFilterIds.Ai, StringComparison.OrdinalIgnoreCase );
    }

    private static FilterDescriptor? ResolvePayloadFilter( string text, IReadOnlyDictionary<string, string>? aliases, ContributionRegistrySnapshot? registry,
        int colon, bool seededAi )
    {
        if ( seededAi )
        {
            return SearchFilterRegistry.FindByToken( text[..colon] );
        }
        else
        {
            return ResolveToken( text[..colon], aliases, registry );
        }
    }

    private static bool HasInvalidPayloadFilter( bool seededAi, FilterDescriptor? filter )
    {
        return !seededAi && filter is not { Id: SearchFilterIds.Ai or SearchFilterIds.Applications };
    }

    private static bool ShouldConsumePayloadFilter( bool seededAi, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] FilterDescriptor? filter )
    {
        return filter is not null && (filter.Id == SearchFilterIds.Ai || !seededAi);
    }
}
