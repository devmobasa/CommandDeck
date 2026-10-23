using Microsoft.Data.Sqlite;

namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    private const string CandidateSourceParameterPrefix = "$candidate_source_";

    private static string CandidateSourceParameterName( int index ) => CandidateSourceParameterPrefix + index;
    private const double MinimumFuzzyLexicalScore = 0.50;
    private const double MaximumSearchScore = 1.2;
    private const double MinimumTitleWeightDivisor = 0.01;

    private async ValueTask AddFuzzyCandidatesAsync( SqliteConnection connection, ParsedQuery query, IReadOnlyList<string> sourceIds, int limit,
        List<SearchResult> results, CancellationToken cancellationToken )
    {
        var firstTerm = query
            .Terms[0]
            .Trim( );

        if ( HasInvalidFuzzyPrefix( firstTerm ) )
        {
            return;
        }

        var useTitleIndex = sourceIds.Count > 1 &&
                            await ProbeBoundedTitleWalkAsync( connection, sourceIds, firstTerm[0], cancellationToken )
                                .ConfigureAwait( false );

        await using var command = CreateFuzzyCandidateCommand( connection, sourceIds, firstTerm[0], useTitleIndex );

        var existing = ExistingResultIds( results );

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        var row = new CatalogSearchRowReader( reader );

        while ( results.Count < limit &&
                await reader
                    .ReadAsync( cancellationToken )
                    .ConfigureAwait( false ) )
        {
            var stableId = row.StableId;
            if ( !existing.Add( stableId ) )
            {
                continue;
            }

            var title = row.Title;
            var subtitle = row.Subtitle;
            var path = row.Path;
            var searchSubtitle = SearchMetadata( subtitle, row.MetadataInSearch );
            var searchPath = SearchMetadata( path, row.MetadataInSearch );
            var titleScore = FuzzyMatcher.Score( query.Text, title );
            var metadataScore = FuzzySubtitleScore( query, searchSubtitle );
            var pathScore = FuzzyPathScore( query, searchPath );
            var lexicalScore = Math.Max( titleScore, Math.Max( metadataScore, pathScore ) );
            if ( lexicalScore < MinimumFuzzyLexicalScore )
            {
                continue;
            }

            var frecencyBoost = AdaptiveScore( row.Kind, row.UseCount, row.LastUsedUnixMilliseconds );
            var score = Math.Clamp( lexicalScore + frecencyBoost, 0, MaximumSearchScore );
            var matches = BuildFuzzyMatches( query.Text, title, searchSubtitle, searchPath );

            List<RankingContribution> contributions = [new( RankingContributionKinds.Lexical, "Bounded fuzzy fallback", lexicalScore )];

            if ( frecencyBoost > 0 )
            {
                var multiplier = TimeAwareRankingPolicy.Multiplier( row.Kind, _rankingOptions.TimeAware );

                contributions.Add( new( RankingContributionKinds.Frecency, $"Recent {TimeAwareRankingPolicy.Category( row.Kind )} use " + $"×{multiplier:0.##}",
                    frecencyBoost ) );

                contributions.Add(
                    new( RankingContributionKinds.Adaptive, $"Adaptive history expires after {_rankingOptions.Adaptive.RetentionDays} days", 0 ) );
            }

            var result = CreateCatalogSearchResult( stableId, row.Kind, title, subtitle, path, row.Icon,
                row.Payload, row.ProviderId, row.SourceId, score, row.IsFavorite, SplitAliases( row.Aliases ),
                ranking: new RankingExplanation
                {
                    FinalScore = score,
                    Contributions = contributions,
                    Matches = matches,
                    Policies = ReadIndexPolicies( row )
                } );

            if ( MatchesFilterExpression( query, result ) )
            {
                results.Add( result );
            }
        }
    }

    private static bool HasInvalidFuzzyPrefix( string firstTerm )
    {
        return firstTerm.Length == 0 || !char.IsLetterOrDigit( firstTerm[0] );
    }

    private static HashSet<string> ExistingResultIds( List<SearchResult> results )
    {
        return results
            .Select( static result => result.Id )
            .ToHashSet( StringComparer.Ordinal );
    }

    private double FuzzySubtitleScore( ParsedQuery query, string searchSubtitle )
    {
        return FuzzyMatcher.Score( query.Text, searchSubtitle ) *
               _rankingOptions.MetadataWeight /
               Math.Max( MinimumTitleWeightDivisor, _rankingOptions.TitleWeight );
    }

    private double FuzzyPathScore( ParsedQuery query, string searchPath )
    {
        return FuzzyMatcher.Score( query.Text, searchPath ) * _rankingOptions.PathWeight / Math.Max( MinimumTitleWeightDivisor, _rankingOptions.TitleWeight );
    }

    internal static SqliteCommand CreateFuzzyCandidateCommand( SqliteConnection connection, IReadOnlyList<string> sourceIds, char firstCharacter,
        bool useTitleIndex = false )
    {
        var command = connection.CreateCommand( );
        var enabledSourceParameters = string.Join( ", ", sourceIds.Select( ( _, index ) => CandidateSourceParameterName( index ) ) );

        var enabledSourceClause = SourceRestriction( sourceIds, enabledSourceParameters );

        // Each source ranges its own title index before a bounded merge. Walking
        // the global title index can scan every disabled row for sparse sources.
        var candidatePrefix = string.Empty;
        var candidateTable = useTitleIndex ? "catalog_items i INDEXED BY ix_catalog_items_title_nocase" : "catalog_items i";

        if ( NeedsPerSourceCandidates( sourceIds, useTitleIndex ) )
        {
            var sourceCandidates = BuildPerSourceCandidates( sourceIds );

            candidatePrefix = $"""
                               WITH candidate_ids AS MATERIALIZED (
                                   SELECT stable_id, title FROM ({string.Join( "\nUNION ALL\n", sourceCandidates )})
                                   ORDER BY title COLLATE NOCASE
                                   LIMIT {MaximumBoundedTitleCandidates}
                               )
                               """;

            candidateTable = "candidate_ids c JOIN catalog_items i ON i.stable_id = c.stable_id";
        }

        command.CommandText = $"""
                               {candidatePrefix}
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
                                      i.index_policy_json, i.metadata_in_search
                               FROM {candidateTable}
                               LEFT JOIN usage u ON u.stable_id = i.stable_id
                               WHERE i.title LIKE $candidate_pattern COLLATE NOCASE
                                 AND i.title_in_search = 1
                                 {enabledSourceClause}
                                 {HomeSearchClause( "i" )}
                               ORDER BY i.title COLLATE NOCASE
                               LIMIT {MaximumBoundedTitleCandidates};
                               """;

        AddHomeSearchParameters( command );
        command.Parameters.AddWithValue( "$candidate_pattern", $"{firstCharacter}%" );

        for ( var index = 0; index < sourceIds.Count; index++ )
        {
            command.Parameters.AddWithValue( CandidateSourceParameterName( index ), sourceIds[index] );
        }

        return command;
    }

    private static bool NeedsPerSourceCandidates( IReadOnlyList<string> sourceIds, bool useTitleIndex )
    {
        return sourceIds.Count > 1 && !useTitleIndex;
    }

    private static IEnumerable<string> BuildPerSourceCandidates( IReadOnlyList<string> sourceIds )
    {
        return sourceIds
            .Select( ( source, index ) => (Source: source, Index: index) )
            .DistinctBy( value => value.Source, StringComparer.Ordinal )
            .Select( value => $"""
                               SELECT stable_id, title FROM (
                                   SELECT i.stable_id, i.title
                                   FROM catalog_items i
                                   WHERE i.title_in_search = 1
                                     AND i.source_id = {CandidateSourceParameterName( value.Index )}
                                     AND i.title LIKE $candidate_pattern COLLATE NOCASE
                                     {HomeSearchClause( "i" )}
                                   ORDER BY i.title COLLATE NOCASE
                                   LIMIT {MaximumBoundedTitleCandidates}
                               )
                               """ );
    }

    private static List<RankingFieldMatch> BuildFuzzyMatches( string query, string title, string? subtitle, string? path )
    {
        List<RankingFieldMatch> matches = [];
        AddFuzzyMatch( matches, RankingFieldNames.Title, query, title );
        AddFuzzyMatch( matches, RankingFieldNames.Metadata, query, subtitle );
        AddFuzzyMatch( matches, RankingFieldNames.Path, query, path );

        return matches;
    }

    private static void AddFuzzyMatch( List<RankingFieldMatch> matches, string field, string query, string? value )
    {
        if ( string.IsNullOrWhiteSpace( value ) )
        {
            return;
        }

        var ranges = FuzzyMatcher.FindRanges( query, value );
        if ( ranges.Count > 0 )
        {
            matches.Add( new RankingFieldMatch { Field = field, Value = value, Ranges = ranges } );
        }
    }
}
