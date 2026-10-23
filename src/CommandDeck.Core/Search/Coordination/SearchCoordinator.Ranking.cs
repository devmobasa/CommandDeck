namespace CommandDeck;

public sealed partial class SearchCoordinator
{
    private const double ScoreComparisonTolerance = 0.000_001;
    private const double MinimumTitleWeight = 0.01;
    private const double ApplicationKindPrior = 0.09;
    private const double OmarchyKindPrior = 0.08;
    private const double LiveKindPrior = 0.10;
    private const double ConfigurationKindPrior = 0.02;
    private const double UnrequestedAiKindPenalty = 0.02;
    private const double WorkspaceKindPrior = 0.05;
    private const double CommandKindPrior = 0.04;
    private const double AiKindPrior = 0.15;
    private const string CollapsedCountMetadataKey = "collapsedCount";

    // The final score blends the provider's score with the best displayed field.
    private const double ProviderScoreWeight = 0.82;

    private const double FieldScoreWeight = 0.18;

    // An exact alias takes this score; every other score is clamped to it.
    private const double MaximumScore = 1.5;

    private SearchResult[] AssembleSections( IReadOnlyList<SearchResult> results, ParsedQuery query, LauncherPreferences preferences, int limit )
    {
        var positions = preferences.Sources.ToDictionary( static value => value.SourceId, static value => value.Position, StringComparer.Ordinal );
        List<SearchResult> exact = [];
        List<SearchResult> remaining = [];
        foreach ( var result in results )
        {
            if ( ShouldPromoteExactMatch( query, result ) )
            {
                exact.Add( WithSection( result, ResultSections.TopMatch ) );
            }
            else
            {
                remaining.Add( result );
            }
        }

        List<SearchResult> assembled = [.. exact.OrderByDescending( static value => value.Score )];
        var groups = GroupRemainingResults( positions, remaining );
        var fairSourceCount = Math.Min( _options.Ranking.MinimumSourcesOnFirstPage, groups.Length );
        for ( var groupIndex = 0; groupIndex < groups.Length; groupIndex++ )
        {
            var group = groups[groupIndex];
            var position = positions.GetValueOrDefault( group.Key, UnlistedSourcePosition );
            ResultSection section = CreateSourceSection( group, position );
            var sourceLimit = _options.Ranking.MaximumResultsPerSource;
            sourceLimit = Math.Max( 0, sourceLimit - assembled.Count( result => result.SourceId == group.Key ) );

            if ( NeedsFirstGroupDiversity( fairSourceCount, groupIndex ) )
            {
                sourceLimit = Math.Min( sourceLimit, Math.Max( 1, _options.Ranking.FirstPageSize - (fairSourceCount - 1) ) );
            }

            assembled.AddRange( group
                .OrderByDescending( static value => value.Score )
                .ThenBy( static value => ResultTitleOrderKey( value ), StringComparer.OrdinalIgnoreCase )
                .Take( sourceLimit )
                .Select( value => WithSection( value, section ) ) );
        }

        return assembled
            .Take( limit )
            .ToArray( );
    }

    private static bool IsTopMatch( SearchResult result, ParsedQuery query )
    {
        if ( result.SourceId == SourceIds.Hey )
        {
            return false;
        }

        if ( IsExplicitIntent( result ) )
        {
            return true;
        }

        var normalized = query.Text.Trim( );
        if ( normalized.Length == 0 )
        {
            return false;
        }

        if ( string.Equals( result.Title.Trim( ), normalized, StringComparison.OrdinalIgnoreCase ) )
        {
            return true;
        }

        return result.Metadata.TryGetValue( ResultMetadataKeys.Path, out var path ) &&
               string.Equals( Path.GetFileName( path ), normalized, StringComparison.OrdinalIgnoreCase );
    }

    private static string ResultTitleOrderKey( SearchResult result )
    {
        if ( TryGetPositiveWorkspaceNumber( result, out var number ) )
        {
            return $"Workspace {number:D10}";
        }

        return result.Title;
    }

    private SearchResult ApplyFinalScore( SearchResult result, ParsedQuery query, SearchRequest request )
    {
        // HEY's own order (relevance for a search, recency for a box) is the
        // ranking: local field and context scores cannot see a mail body.
        if ( result.SourceId == SourceIds.Hey )
        {
            return result;
        }

        var prior = GetKindPrior( result, query );
        var lexical = BestFieldScore( query.Text, result );
        var sourceScore = result.Score <= 0 ? lexical : result.Score;
        var contextContributions = ContextContributions( result, request );
        var context = contextContributions.Sum( static value => value.Value );
        var baseScore = (sourceScore * ProviderScoreWeight) + lexical * FieldScoreWeight + prior + context;
        var sourceWeight = _options.Ranking.SourceWeights.GetValueOrDefault( result.SourceId, 1 );
        var sourceAdjustment = baseScore * (sourceWeight - 1);
        var final = GetFinalScore( result, baseScore, sourceAdjustment );
        var contributions = ScaleProviderContributions( result, sourceScore );

        contributions.Add( new( RankingContributionKinds.Field, "Best matching displayed field", lexical * FieldScoreWeight ) );

        if ( prior != 0 )
        {
            contributions.Add( new( RankingContributionKinds.KindPrior, "Result type prior", prior ) );
        }

        contributions.AddRange( contextContributions );

        contributions.Add( new( RankingContributionKinds.SourceWeight, $"Source weight ×{sourceWeight:0.##}", sourceAdjustment ) );

        if ( result.Metadata.ContainsKey( ResultMetadataKeys.ExactAlias ) )
        {
            contributions =
            [
                new( RankingContributionKinds.ExactAlias, "Exact alias", MaximumScore )
            ];
        }
        else
        {
            var explained = contributions.Sum( static contribution => contribution.Value );
            var policyAdjustment = final - explained;
            if ( Math.Abs( policyAdjustment ) > ScoreComparisonTolerance )
            {
                contributions.Add( new( RankingContributionKinds.Policy, "Score bounds", policyAdjustment ) );
            }
        }

        var matches = GetRankingMatches( result, query );
        var titleRanges = GetTitleRanges( result, query, matches );

        return Copy( result, score: final, ranges: titleRanges,
            ranking: new RankingExplanation
            {
                FinalScore = final,
                Contributions = contributions,
                Matches = matches,
                Policies = [.. result.Ranking.Policies]
            } );
    }

    private double BestFieldScore( string query, SearchResult result )
    {
        var ranking = _options.Ranking;
        var titleWeight = Math.Max( MinimumTitleWeight, ranking.TitleWeight );
        var score = FuzzyMatcher.Score( query, result.Title );
        score = Math.Max( score, FuzzyMatcher.Score( query, result.Subtitle ?? string.Empty ) * ranking.MetadataWeight / titleWeight );

        if ( result.Metadata.TryGetValue( ResultMetadataKeys.Path, out var path ) )
        {
            score = Math.Max( score, FuzzyMatcher.Score( query, path ) * ranking.PathWeight / titleWeight );
        }

        foreach ( var match in result.Ranking.Matches )
        {
            var weight = GetFieldWeight( ranking, match );
            score = Math.Max( score, FuzzyMatcher.Score( query, match.Value ) * weight / titleWeight );
        }

        return score;
    }

    private static List<RankingContribution> ScaleProviderContributions( SearchResult result, double sourceScore )
    {
        if ( result.Ranking.Contributions.Count == 0 )
        {
            return [new( RankingContributionKinds.Lexical, "Provider match", sourceScore * ProviderScoreWeight )];
        }

        return result
            .Ranking.Contributions.Select( static contribution => contribution with { Value = contribution.Value * ProviderScoreWeight } )
            .ToList( );
    }

    private static List<RankingFieldMatch> BuildFallbackMatches( string query, SearchResult result )
    {
        List<RankingFieldMatch> matches = [];
        AddFallbackMatch( matches, RankingFieldNames.Title, query, result.Title );
        AddFallbackMatch( matches, RankingFieldNames.Metadata, query, result.Subtitle );

        if ( result.Metadata.TryGetValue( ResultMetadataKeys.Path, out var path ) )
        {
            AddFallbackMatch( matches, RankingFieldNames.Path, query, path );
        }

        return matches;
    }

    private static void AddFallbackMatch( List<RankingFieldMatch> matches, string field, string query, string? value )
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

    private static IEnumerable<SearchResult> CollapseConfigurationResults( IEnumerable<SearchResult> ranked )
    {
        var results = ranked.ToArray( );
        var totals = CountConfigurationResults( results );
        Dictionary<string, int> retained = new( StringComparer.Ordinal );
        foreach ( var result in results )
        {
            if ( !HasConfigurationSource( result ) )
            {
                yield return result;

                continue;
            }

            var path = result.Metadata[ResultMetadataKeys.Path];
            var retainedCount = retained.GetValueOrDefault( path );
            if ( retainedCount >= 2 )
            {
                continue;
            }

            retained[path] = retainedCount + 1;
            var omitted = totals[path] - 2;
            if ( HasOmittedConfigurationMatches( retainedCount, omitted ) )
            {
                var note = omitted == 1 ? "1 more match" : $"{omitted} more matches";
                List<DetailField> details = [.. result.Details, new DetailField( "Related", $"{note} in this file" )];

                Dictionary<string, string> metadata = CreateCollapsedMetadata( result, omitted );

                yield return Copy( result, subtitle: string.IsNullOrWhiteSpace( result.Subtitle ) ? note : $"{note} · {result.Subtitle}", details: details,
                    metadata: metadata );
            }
            else
            {
                yield return result;
            }
        }
    }

    private static bool HasConfigurationSource( SearchResult result ) =>
        result.Kind == ResultKinds.Configuration && result.Metadata.TryGetValue( ResultMetadataKeys.Path, out var path ) && !string.IsNullOrWhiteSpace( path );

    private static SearchResult WithSection( SearchResult result, ResultSection section ) =>
        Copy( result, section: section );

    private static SearchResult
        Copy( SearchResult result, double? score = null, IReadOnlyList<MatchedRange>? ranges = null, string? subtitle = null,
            IReadOnlyList<DetailField>? details = null, Dictionary<string, string>? metadata = null, ResultSection? section = null,
            RankingExplanation? ranking = null ) => new( )
            {
                Id = result.Id,
                Kind = result.Kind,
                Title = result.Title,
                Subtitle = subtitle ?? result.Subtitle,
                Icon = result.Icon,
                DetailMarkdown = result.DetailMarkdown,
                Details = details is null ? result.Details : [.. details],
                ProviderId = result.ProviderId,
                SourceId = result.SourceId,
                Section = section ?? result.Section,
                IsFavorite = result.IsFavorite,
                Aliases = result.Aliases,
                CanCustomize = result.CanCustomize,
                Score = score ?? result.Score,
                MatchedRanges = ranges is null ? result.MatchedRanges : [.. ranges],
                Ranking = ranking ?? result.Ranking,
                Actions = result.Actions,
                Metadata = metadata ?? result.Metadata
            };

    private bool ShouldPromoteExactMatch( ParsedQuery query, SearchResult result )
    {
        return _history.TopMatches && IsTopMatch( result, query );
    }

    private static IGrouping<string, SearchResult>[] GroupRemainingResults( Dictionary<string, int> positions, List<SearchResult> remaining )
    {
        return remaining
            .GroupBy( static value => value.SourceId )
            .OrderBy( group => positions.GetValueOrDefault( group.Key, UnlistedSourcePosition ) )
            .ToArray( );
    }

    private ResultSection CreateSourceSection( IGrouping<string, SearchResult> group, int position )
    {
        return new( ) { Id = group.Key, Title = SourceLabel( group.Key ), Order = position };
    }

    private static bool NeedsFirstGroupDiversity( int fairSourceCount, int groupIndex )
    {
        return groupIndex == 0 && fairSourceCount > 1;
    }

    private static double GetKindPrior( SearchResult result, ParsedQuery query )
    {
        return result.Kind switch
        {
            ResultKinds.Application => ApplicationKindPrior,
            ResultKinds.Omarchy => OmarchyKindPrior,
            ResultKinds.Calculator => LiveKindPrior,
            ResultKinds.Configuration => ConfigurationKindPrior,
            ResultKinds.Window => LiveKindPrior,
            ResultKinds.Workspace => WorkspaceKindPrior,
            ResultKinds.Tab => WorkspaceKindPrior,
            ResultKinds.Quicklink => CommandKindPrior,
            ResultKinds.CustomCommand => CommandKindPrior,
            ResultKinds.AiCommand => query.IsAiMode ? AiKindPrior : -UnrequestedAiKindPenalty,
            _ => 0
        };
    }

    private static double GetFinalScore( SearchResult result, double baseScore, double sourceAdjustment )
    {
        if ( result.Metadata.ContainsKey( ResultMetadataKeys.ExactAlias ) )
        {
            return MaximumScore;
        }
        else
        {
            return Math.Clamp( baseScore + sourceAdjustment, 0, MaximumScore );
        }
    }

    private static List<RankingFieldMatch> GetRankingMatches( SearchResult result, ParsedQuery query )
    {
        if ( result.Ranking.Matches.Count > 0 )
        {
            return [.. result.Ranking.Matches];
        }
        else
        {
            return BuildFallbackMatches( query.Text, result );
        }
    }

    private static List<MatchedRange> GetTitleRanges( SearchResult result, ParsedQuery query, List<RankingFieldMatch> matches )
    {
        return matches.FirstOrDefault( static match => match.Field == RankingFieldNames.Title )
                   ?.Ranges ??
               FuzzyMatcher.FindRanges( query.Text, result.Title );
    }

    private static double GetFieldWeight( RankingOptions ranking, RankingFieldMatch match )
    {
        return match.Field switch
        {
            RankingFieldNames.Path => ranking.PathWeight,
            RankingFieldNames.Metadata => ranking.MetadataWeight,
            RankingFieldNames.Content => ranking.ContentWeight,
            RankingFieldNames.Config => ranking.ConfigWeight,
            _ => ranking.TitleWeight
        };
    }

    private static Dictionary<string, int> CountConfigurationResults( SearchResult[] results )
    {
        return results
            .Where( HasConfigurationSource )
            .GroupBy( static result => result.Metadata[ResultMetadataKeys.Path], StringComparer.Ordinal )
            .ToDictionary( static group => group.Key, static group => group.Count( ), StringComparer.Ordinal );
    }

    private static bool HasOmittedConfigurationMatches( int retainedCount, int omitted )
    {
        return retainedCount == 0 && omitted > 0;
    }

    private static Dictionary<string, string> CreateCollapsedMetadata( SearchResult result, int omitted )
    {
        return new( result.Metadata, StringComparer.Ordinal )
        {
            [CollapsedCountMetadataKey] = omitted.ToString( System.Globalization.CultureInfo.InvariantCulture )
        };
    }

    private static bool TryGetPositiveWorkspaceNumber( SearchResult result, out int number )
    {
        number = default;

        return result is { Kind: ResultKinds.Workspace, ProviderId: ProviderIds.Hyprland, SourceId: SourceIds.WindowsWorkspaces } &&
               result.Metadata.TryGetValue( ResultMetadataKeys.WorkspaceName, out var name ) &&
               int.TryParse( name, out number ) &&
               number > 0;
    }
}
