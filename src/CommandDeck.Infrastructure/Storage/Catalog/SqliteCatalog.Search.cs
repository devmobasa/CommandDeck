using Microsoft.Data.Sqlite;

namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    private const string EnabledSourceParameterPrefix = "$enabled_source_";

    private static string EnabledSourceParameterName( int index ) => EnabledSourceParameterPrefix + index;
    private const int MaximumFuzzyQueryCharacters = 64;
    private const int MaximumBrowseResults = 500;
    private const double BrowseLexicalScore = 0.25;
    private const double MinimumLexicalScore = 0.2;

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, int limit, CancellationToken cancellationToken )
    {
        var sources = IncludedCatalogSources( query );

        return await SearchSourcesAsync( query, sources, limit, cancellationToken )
            .ConfigureAwait( false );
    }

    private static string[] IncludedCatalogSources( ParsedQuery query )
    {
        return query
            .IncludedFilters.Where( static filter => string.Equals( filter.ProviderId, ProviderIds.Catalog, StringComparison.Ordinal ) )
            .Select( static filter => filter.SourceId )
            .Where( static source => !string.IsNullOrWhiteSpace( source ) )
            .Select( static source => source! )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );
    }

    public async ValueTask<IReadOnlyList<SearchResult>> SearchSourcesAsync( ParsedQuery query, IReadOnlyList<string> sourceIds, int limit,
        CancellationToken cancellationToken )
    {
        await InitializeAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        using var interrupt = InterruptOnCancellation( connection, cancellationToken );

        try
        {
            var results = await ReadMatchesAsync( query, connection, sourceIds, limit, cancellationToken )
                .ConfigureAwait( false );

            if ( NeedsFuzzyCandidates( query, results, limit ) )
            {
                await AddFuzzyCandidatesAsync( connection, query, sourceIds, limit, results, cancellationToken )
                    .ConfigureAwait( false );
            }

            // Open With actions are added by the runtime for the rows it shows.
            return results;
        }
        catch ( SqliteException exception ) when ( WasInterrupted( exception, cancellationToken ) )
        {
            throw new OperationCanceledException( "The catalog search was cancelled.", exception, cancellationToken );
        }
    }

    private static bool NeedsFuzzyCandidates( ParsedQuery query, List<SearchResult> results, int limit )
    {
        return query.Terms.Count > 0 && query.Text.Length <= MaximumFuzzyQueryCharacters && results.Count < limit;
    }

    private ValueTask<List<SearchResult>> ReadMatchesAsync( ParsedQuery query, SqliteConnection connection, IReadOnlyList<string> sourceIds, int limit,
        CancellationToken cancellationToken )
    {
        if ( query.Terms.Count == 0 )
        {
            return ReadBrowseMatchesAsync( connection, query, sourceIds, limit, cancellationToken );
        }

        return ReadRankedMatchesAsync( connection, query, sourceIds, limit, cancellationToken );
    }

    private async ValueTask<List<SearchResult>> ReadBrowseMatchesAsync( SqliteConnection connection, ParsedQuery query, IReadOnlyList<string> sourceIds,
        int limit, CancellationToken cancellationToken )
    {
        await using var command = CreateBrowseCommand( connection, sourceIds, limit );
        List<SearchResult> results = [];

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        var row = new CatalogSearchRowReader( reader );

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            var lexicalScore = BrowseLexicalScore;
            var frecencyBoost = AdaptiveScore( row.Kind, row.UseCount, row.LastUsedUnixMilliseconds );
            var score = Math.Clamp( lexicalScore + frecencyBoost, 0, MaximumSearchScore );
            var ranking = new RankingExplanation { FinalScore = score };
            ranking.Policies.AddRange( ReadIndexPolicies( row ) );

            var result = CreateCatalogSearchResult( row.StableId, row.Kind, row.Title, row.Subtitle, row.Path, row.Icon,
                row.Payload, row.ProviderId, row.SourceId, score, row.IsFavorite, SplitAliases( row.Aliases ),
                ranking: ranking );

            if ( MatchesFilterExpression( query, result ) )
            {
                results.Add( result );
            }
        }

        return results;
    }

    private static SqliteCommand CreateBrowseCommand( SqliteConnection connection, IReadOnlyList<string> sourceIds, int limit )
    {
        var command = connection.CreateCommand( );
        var enabledSourceParameters = string.Join( ", ", sourceIds.Select( ( _, index ) => EnabledSourceParameterName( index ) ) );

        var enabledSourceClause = SourceRestriction( sourceIds, enabledSourceParameters );

        command.CommandText = $"""
                               SELECT i.stable_id, i.kind, i.title, i.subtitle, i.path, i.icon,
                                      i.payload_json, i.provider_id, i.source_id,
                                      COALESCE(u.use_count, 0) AS use_count,
                                      COALESCE(u.last_used_unix_ms, 0) AS last_used_unix_ms,
                                      EXISTS(SELECT 1 FROM favorites f WHERE f.stable_id = i.stable_id) AS is_favorite,
                                      COALESCE((SELECT group_concat(ordered_alias.alias, char(31))
                                                FROM (SELECT a.alias FROM aliases a
                                                      WHERE a.stable_id = i.stable_id
                                                      ORDER BY a.created_unix_ms, a.alias COLLATE NOCASE)
                                                     ordered_alias), '') AS aliases,
                                      i.index_policy_json
                               FROM catalog_items i
                               LEFT JOIN usage u ON u.stable_id = i.stable_id
                               WHERE 1 = 1
                                 {enabledSourceClause}
                                 {HomeSearchClause( "i" )}
                               ORDER BY COALESCE(u.last_used_unix_ms, 0) DESC, i.title COLLATE NOCASE
                               LIMIT $limit;
                               """;

        for ( var index = 0; index < sourceIds.Count; index++ )
        {
            command.Parameters.AddWithValue( EnabledSourceParameterName( index ), sourceIds[index] );
        }

        AddHomeSearchParameters( command );
        command.Parameters.AddWithValue( "$limit", Math.Clamp( limit, 1, MaximumBrowseResults ) );

        return command;
    }

    private static string SourceRestriction( IReadOnlyList<string> sourceIds, string enabledSourceParameters )
    {
        if ( sourceIds.Count == 0 )
        {
            return string.Empty;
        }

        return $" AND i.source_id IN ({enabledSourceParameters})";
    }

    internal static string BuildFtsQuery( ParsedQuery query, IReadOnlyList<string>? sourceIds = null )
    {
        List<string> fragments = [];

        foreach ( var clause in query.Expression.Clauses.Where( static value => !value.Excluded ) )
        {
            var clean = EscapeFtsText( clause );
            if ( clean.Length == 0 )
            {
                continue;
            }

            var field = FtsFieldPrefix( clause );
            fragments.Add( $"{field}\"{clean}\"*" );
        }

        var lexical = fragments.Count == 0 ? "\"\"" : string.Join( " AND ", fragments );

        foreach ( var clause in query.Expression.Clauses.Where( static value => value.Excluded ) )
        {
            var clean = EscapeFtsText( clause );
            var field = FtsFieldPrefix( clause );
            lexical += $" NOT {field}\"{clean}\"*";
        }

        var sources = sourceIds ?? [];
        if ( sources.Count == 0 )
        {
            return lexical;
        }

        var sourceQuery = string.Join( " OR ",
            sources.Select( static source => $"source_id : \"{source.Replace( "\"", "\"\"", StringComparison.Ordinal )}\"" ) );

        return $"{lexical} AND ({sourceQuery})";
    }

    private static string EscapeFtsText( QueryClause clause )
    {
        return clause
            .Text.Replace( "\"", "\"\"", StringComparison.Ordinal )
            .Trim( );
    }

    private static string FtsFieldPrefix( QueryClause clause )
    {
        return clause.Field switch
        {
            QueryFieldIds.Title => "title : ",
            QueryFieldIds.Metadata => "subtitle : ",
            QueryFieldIds.Path => "path : ",
            QueryFieldIds.Content => "body : ",
            QueryFieldIds.Config => "config_keys : ",
            _ => string.Empty
        };
    }

    private static bool MatchesFilterExpression( ParsedQuery query, SearchResult result ) =>
        (query.IncludedFilters.Count == 0 || query.IncludedFilters.Any( filter => SearchFilterRegistry.Matches( filter, result ) )) &&
        !query.ExcludedFilters.Any( filter => SearchFilterRegistry.Matches( filter, result ) );

    private static double RankToScore( double rank )
    {
        var magnitude = Math.Abs( rank );

        return Math.Clamp( magnitude / (1.0 + magnitude), MinimumLexicalScore, 1.0 );
    }

    private double AdaptiveScore( string resultKind, long useCount, long lastUsedUnixMilliseconds )
    {
        if ( ShouldSkipAdaptiveScore( lastUsedUnixMilliseconds ) )
        {
            return 0;
        }

        return TimeAwareRankingPolicy.Score( resultKind, useCount, lastUsedUnixMilliseconds, _timeProvider.GetUtcNow( ), _rankingOptions.TimeAware );
    }

    private bool ShouldSkipAdaptiveScore( long lastUsedUnixMilliseconds )
    {
        return !_history.LearnFromUse || !_rankingOptions.Adaptive.Enabled || lastUsedUnixMilliseconds < AdaptiveCutoffUnixMilliseconds( );
    }

    private long AdaptiveCutoffUnixMilliseconds( ) => SqliteUsageHistoryStore.AdaptiveCutoffUnixMilliseconds( _timeProvider
        .GetUtcNow( )
        .ToUnixTimeMilliseconds( ), _rankingOptions );
}
