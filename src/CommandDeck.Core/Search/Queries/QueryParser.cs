namespace CommandDeck;

public static partial class QueryParser
{
    private const int MaximumFilterCount = 32;
    private const int MaximumClauseCount = 64;
    private const int MaximumClauseTextCharacters = 512;

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create( "0123456789abcdefABCDEF" );

    public static ParsedQuery Parse( string? raw, QueryExpression? typedExpression = null, IReadOnlyDictionary<string, string>? aliases = null,
        ContributionRegistrySnapshot? registry = null )
    {
        var original = OriginalText( raw, typedExpression );
        var text = original;
        string? provider = null;
        var calculator = false;
        var commandMode = false;
        var aiMode = false;
        IReadOnlyList<string>? literalTerms = null;
        var expression = CopyAndValidate( typedExpression );
        if ( text.StartsWith( '=' ) )
        {
            calculator = true;
            provider = ProviderIds.Calculator;

            text = text[1..]
                .TrimStart( );

            expression = WithLiteralQuery( expression, original );
        }
        else if ( text.StartsWith( '>' ) )
        {
            commandMode = true;

            text = text[1..]
                .TrimStart( );
        }
        else if ( text.StartsWith( '@' ) )
        {
            var separator = text.IndexOfAny( [' ', ':'] );
            if ( separator > 1 )
            {
                provider = text[1..separator]
                    .Trim( )
                    .ToLowerInvariant( );

                text = text[(separator + 1)..]
                    .TrimStart( );
            }
        }
        else if ( text.StartsWith( AgentQueryPrefixes.LegacyCodex, StringComparison.OrdinalIgnoreCase ) )
        {
            provider = ProviderIds.Assistant;
            aiMode = true;

            text = text[AgentQueryPrefixes.LegacyCodex.Length..]
                .TrimStart( );

            expression.Clauses.Clear( );
            expression = WithLiteralQuery( expression, original );
            literalTerms = text.Length == 0 ? [] : [text];
        }
        else if ( TryParseLiteralPayload( text, expression, aliases, registry, out var payload ) )
        {
            text = payload.Text;
            expression = payload.Expression;
            literalTerms = payload.Terms;
            aiMode = payload.IsAi;
        }
        else
        {
            expression = ParseTextExpression( text, expression, aliases, registry );

            text = string.Join( ' ', expression
                .Clauses.Where( static clause => !clause.Excluded )
                .Select( static clause => clause.Text ) );
        }

        var included = ResolveFilters( expression.IncludeFilterIds, "include", registry );
        var excluded = ResolveFilters( expression.ExcludeFilterIds, "exclude", registry );
        var excludedIds = GetExcludedFilterIds( excluded );
        var conflict = FindConflictingFilter( included, excludedIds );
        if ( conflict is not null )
        {
            throw new InvalidOperationException( $"Filter '{conflict}' cannot be both included and excluded." );
        }

        var includedProviders = GetIncludedProviders( included );
        if ( includedProviders.Length == 1 )
        {
            provider = includedProviders[0];
        }

        if ( string.Equals( provider, ProviderIds.Assistant, StringComparison.Ordinal ) )
        {
            aiMode = true;
        }

        return new ParsedQuery
        {
            Original = original,
            Text = text,
            Expression = expression,
            ProviderFilter = provider,
            IncludedFilters = included,
            ExcludedFilters = excluded,
            HasExplicitFilter =
                included.Length > 0 ||
                excluded.Length > 0 ||
                expression.Clauses.Any( static clause => clause.Excluded || clause.Field is not null ) ||
                !string.IsNullOrWhiteSpace( provider ),
            IsCalculator = calculator,
            IsCommandMode = commandMode,
            IsAiMode = aiMode,
            ExplicitBackendId = aiMode ? AgentBackendIds.FromQueryPrefix( original ) : null,
            Terms = literalTerms ??
                    expression
                        .Clauses.Where( static clause => !clause.Excluded )
                        .Select( static clause => clause.Text )
                        .ToArray( )
        };
    }

    private static QueryExpression ParseTextExpression( string text, QueryExpression seed, IReadOnlyDictionary<string, string>? aliases,
        ContributionRegistrySnapshot? registry )
    {
        List<string> includes = [.. seed.IncludeFilterIds];
        List<string> excludes = [.. seed.ExcludeFilterIds];
        List<QueryClause> clauses = [.. seed.Clauses];
        foreach ( var rawToken in TokenizeSyntax( text ) )
        {
            var excluded = rawToken.StartsWith( '-', StringComparison.Ordinal ) && rawToken.Length > 1;
            var token = excluded ? rawToken[1..] : rawToken;
            var colon = token.IndexOf( ':' );
            if ( colon > 0 )
            {
                var prefix = token[..colon];
                var descriptor = ResolveToken( prefix, aliases, registry );
                if ( descriptor is not null )
                {
                    AddUnique( excluded ? excludes : includes, descriptor.Id );
                    var remainder = Unquote( token[(colon + 1)..] );
                    if ( remainder.Length > 0 )
                    {
                        clauses.Add( new QueryClause { Text = remainder, Excluded = excluded } );
                    }

                    continue;
                }

                if ( QueryFieldIds.All.Contains( prefix ) )
                {
                    var value = Unquote( token[(colon + 1)..] );
                    if ( value.Length == 0 )
                    {
                        continue;
                    }

                    clauses.Add( new QueryClause { Field = prefix.ToLowerInvariant( ), Text = value, Excluded = excluded } );

                    continue;
                }
            }

            var ordinary = Unquote( colon > 0 && token.EndsWith( ':' ) ? token[..^1] : token );
            if ( ordinary.Length > 0 )
            {
                clauses.Add( new QueryClause { Text = ordinary, Excluded = excluded } );
            }
        }

        return Validate( new QueryExpression
        {
            LiteralQuery = seed.LiteralQuery,
            IncludeFilterIds = includes,
            ExcludeFilterIds = excludes,
            Clauses = clauses
        } );
    }

    private static FilterDescriptor? ResolveToken( string token, IReadOnlyDictionary<string, string>? aliases, ContributionRegistrySnapshot? registry )
    {
        var builtIn = registry is null ? SearchFilterRegistry.FindByToken( token ) : registry.FindByToken( token );
        if ( IsExtensionShortToken( registry, builtIn, token ) )
        {
            throw new InvalidOperationException( "The extension filter is unavailable." );
        }

        if ( CanReturnBuiltInFilter( aliases, builtIn ) )
        {
            return builtIn;
        }

        if ( !aliases.TryGetValue( token
                .Trim( )
                .TrimEnd( ':' ), out var filterId ) )
        {
            return null;
        }

        var result = registry is null ? SearchFilterRegistry.FindById( filterId ) : registry.FindById( filterId );
        if ( result is null )
        {
            throw new InvalidOperationException( $"Filter alias '{token}' refers to an unavailable filter." );
        }

        return result;
    }

    private static QueryExpression CopyAndValidate( QueryExpression? expression )
    {
        if ( expression is null )
        {
            return Validate( new QueryExpression( ) );
        }

        if ( HasMissingExpressionCollections( expression ) )
        {
            throw new InvalidOperationException( "Query expression collections must be arrays." );
        }

        if ( expression.Clauses.Any( static clause => clause is null ) )
        {
            throw new InvalidOperationException( "Query expression clauses cannot contain null." );
        }

        return Validate( new QueryExpression
        {
            LiteralQuery = expression.LiteralQuery,
            IncludeFilterIds = [.. expression.IncludeFilterIds],
            ExcludeFilterIds = [.. expression.ExcludeFilterIds],
            Clauses = expression
                .Clauses.Select( static clause => new QueryClause { Field = clause.Field, Text = clause.Text, Excluded = clause.Excluded } )
                .ToList( )
        } );
    }

    private static QueryExpression Validate( QueryExpression expression )
    {
        ArgumentNullException.ThrowIfNull( expression );

        if ( expression.LiteralQuery?.Length > ProtocolLimits.MaximumQueryCharacters )
        {
            throw new InvalidOperationException( "Literal queries exceed the query text limit." );
        }

        if ( HasMissingExpressionCollections( expression ) )
        {
            throw new InvalidOperationException( "Query expression collections must be arrays." );
        }

        if ( ExceedsExpressionLimits( expression ) )
        {
            throw new InvalidOperationException( "Query expression exceeds its item limits." );
        }

        EnsureUnique( expression.IncludeFilterIds, "included filter" );
        EnsureUnique( expression.ExcludeFilterIds, "excluded filter" );

        foreach ( var clause in expression.Clauses )
        {
            if ( IsInvalidClause( clause ) )
            {
                throw new InvalidOperationException( "Query clauses require 1-512 characters of text." );
            }

            if ( HasUnknownClauseField( clause ) )
            {
                throw new InvalidOperationException( $"Unknown query field '{clause.Field}'." );
            }
        }

        List<QueryClause> clauses = [];
        foreach ( var clause in expression.Clauses )
        {
            if ( IsNewClause( clauses, clause ) )
            {
                clauses.Add( clause );
            }
        }

        return new QueryExpression
        {
            LiteralQuery = expression.LiteralQuery,
            IncludeFilterIds = [.. expression.IncludeFilterIds],
            ExcludeFilterIds = [.. expression.ExcludeFilterIds],
            Clauses = clauses
        };
    }

    public static QueryExpression ValidateShape( QueryExpression expression ) => CopyAndValidate( expression );

    private static FilterDescriptor[] ResolveFilters( IReadOnlyList<string> ids, string kind, ContributionRegistrySnapshot? registry ) =>
        ids
            .Select( id => ResolveFilter( id, kind, registry ) )
            .ToArray( );

    private static void EnsureUnique( IReadOnlyList<string> values, string label )
    {
        HashSet<string> unique = new( StringComparer.OrdinalIgnoreCase );
        foreach ( var value in values )
        {
            if ( string.IsNullOrWhiteSpace( value ) || !unique.Add( value ) )
            {
                throw new InvalidOperationException( $"Query expression has an invalid duplicate {label}." );
            }
        }
    }

    private static void AddUnique( List<string> values, string value )
    {
        if ( !values.Contains( value, StringComparer.OrdinalIgnoreCase ) )
        {
            values.Add( value );
        }
    }

    private static string Unquote( string value )
    {
        var trimmed = value.Trim( );
        if ( trimmed is ['"', .., '"'] )
        {
            return trimmed[1..^1];
        }
        else
        {
            return trimmed;
        }
    }

    private static List<string> TokenizeSyntax( string text )
    {
        List<string> tokens = [];
        System.Text.StringBuilder current = new( );
        var quoted = false;
        foreach ( var character in text )
        {
            if ( character == '"' )
            {
                quoted = !quoted;
                current.Append( character );
            }
            else if ( IsTokenBoundary( quoted, character ) )
            {
                if ( current.Length > 0 )
                {
                    tokens.Add( current.ToString( ) );
                    current.Clear( );
                }
            }
            else
            {
                current.Append( character );
            }
        }

        if ( quoted )
        {
            throw new InvalidOperationException( "Query contains an unterminated quote." );
        }

        if ( current.Length > 0 )
        {
            tokens.Add( current.ToString( ) );
        }

        return tokens;
    }

    private static HashSet<string> GetExcludedFilterIds( FilterDescriptor[] excluded )
    {
        return excluded
            .Select( static value => value.Id )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );
    }

    private static string? FindConflictingFilter( FilterDescriptor[] included, HashSet<string> excludedIds )
    {
        return included
            .Select( static value => value.Id )
            .FirstOrDefault( excludedIds.Contains );
    }

    private static string[] GetIncludedProviders( FilterDescriptor[] included )
    {
        return included
            .Select( static value => value.ProviderId! )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToArray( );
    }

    private static bool IsExtensionShortToken( ContributionRegistrySnapshot? registry, FilterDescriptor? builtIn, string token )
    {
        return builtIn is null &&
               registry is not null &&
               token.Length == ExtensionStableIds.FilterTokenPrefix.Length + ExtensionStableIds.FilterKeyCharacters &&
               token.StartsWith( ExtensionStableIds.FilterTokenPrefix, StringComparison.OrdinalIgnoreCase ) &&
               token
                   .AsSpan( ExtensionStableIds.FilterTokenPrefix.Length )
                   .IndexOfAnyExcept( HexDigits ) <
               0;
    }

    private static bool CanReturnBuiltInFilter( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] IReadOnlyDictionary<string, string>? aliases,
        FilterDescriptor? builtIn )
    {
        return builtIn is not null || aliases is null;
    }

    private static bool HasMissingExpressionCollections( QueryExpression expression )
    {
        return expression.IncludeFilterIds is null || expression.ExcludeFilterIds is null || expression.Clauses is null;
    }

    private static bool ExceedsExpressionLimits( QueryExpression expression )
    {
        return expression.IncludeFilterIds.Count + expression.ExcludeFilterIds.Count > MaximumFilterCount || expression.Clauses.Count > MaximumClauseCount;
    }

    private static bool IsInvalidClause( QueryClause? clause )
    {
        return clause is null || string.IsNullOrWhiteSpace( clause.Text ) || clause.Text.Length > MaximumClauseTextCharacters;
    }

    private static bool HasUnknownClauseField( QueryClause clause )
    {
        return clause.Field is not null && !QueryFieldIds.All.Contains( clause.Field );
    }

    private static bool IsNewClause( List<QueryClause> clauses, QueryClause clause )
    {
        return !clauses.Any( existing => string.Equals( existing.Field, clause.Field, StringComparison.OrdinalIgnoreCase ) &&
                                         string.Equals( existing.Text, clause.Text, StringComparison.OrdinalIgnoreCase ) &&
                                         existing.Excluded == clause.Excluded );
    }

    private static bool IsTokenBoundary( bool quoted, char character )
    {
        return char.IsWhiteSpace( character ) && !quoted;
    }

    private static FilterDescriptor ResolveFilter( string id, string kind, ContributionRegistrySnapshot? registry )
    {
        var filter = registry is null ? SearchFilterRegistry.FindById( id ) : registry.FindById( id );
        if ( filter is not { Available: true } )
        {
            throw new InvalidOperationException( $"Unknown or unavailable {kind} filter '{id}'." );
        }

        return filter;
    }
}
