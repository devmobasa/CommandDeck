namespace CommandDeck;

public sealed partial class SearchCoordinator
{
    private const int UsageSignalBatchSize = 500;

    private const int MaximumTopMatches = 3;
    private const int MinimumPromotionUses = 2;

    private const int PromotionRecencyDays = 21;

    // A source without a saved position sorts after every positioned source.
    private const int UnlistedSourcePosition = 100;

    private enum PromotionTier
    {
        Explicit, Exact, Learned, Grouped
    }

    private bool LearningAvailable =>
        _usageHistory is not null && _history.LearnFromUse && _options is { Ranking.Adaptive.Enabled: true, Ranking.TimeAware.Enabled: true };

    private static string UsageId( SearchResult result ) =>
        result.Metadata.GetValueOrDefault( ResultMetadataKeys.CatalogResultId, result.Id );

    // Exact aliases, calculator results, and rows a provider places explicitly
    // (a typed URL) answer the query itself rather than match it.
    private static bool IsExplicitIntent( SearchResult result ) =>
        result.Metadata.ContainsKey( ResultMetadataKeys.ExactAlias ) || result.Kind == ResultKinds.Calculator || ResultPlacements.IsExplicit( result );

    private static bool StrongTitleMatch( SearchResult result, ParsedQuery query )
    {
        var positive = GetPositiveClauses( query );
        if ( positive.Any( static clause => clause.Field is not null and not QueryFieldIds.Title ) )
        {
            return false;
        }

        // Explicit provider/command modes retain their parser-owned text. Ordinary
        // clauses (including quoted phrases) are never flattened and reparsed.
        if ( NeedsModeClause( query, positive ) )
        {
            positive = [new QueryClause { Text = query.Text }];
        }

        if ( positive.Sum( static clause => FuzzyMatcher.SearchTextLength( clause.Text ) ) < 2 )
        {
            return false;
        }

        var basename = result.Metadata.TryGetValue( ResultMetadataKeys.Path, out var path ) ? Path.GetFileName( path ) : "";

        return positive.Length > 0 &&
               positive.All( clause =>
                   FuzzyMatcher.IsStrongPrefix( clause.Text, result.Title ) || clause.Field is null && FuzzyMatcher.IsStrongPrefix( clause.Text, basename ) );
    }

    private bool Learnable( SearchResult result, ParsedQuery query ) =>
        LearnableAction( result, query ) && StrongTitleMatch( result, query );

    private bool LearnableAction( SearchResult result, ParsedQuery query ) =>
        LearningAvailable &&
        query is { IsAiMode: false, IsCalculator: false } &&
        result is { ProviderId: not ProviderIds.Fallback, SourceId: not SourceIds.Folders, Actions.Count: > 0 } &&
        TimeAwareRankingPolicy.Multiplier( result.Kind, _options.Ranking.TimeAware ) > 0 &&
        IsLearningAction( result.Actions[0] );

    // Learned queries need opt-in query history on top of every learning gate,
    // and apply only to the user's own interactive searches.
    private async ValueTask<IReadOnlyDictionary<string, LearnedQuery>> ReadLearnedQueriesAsync( SearchRequest request, SearchPurpose purpose,
        DateTimeOffset now, CancellationToken cancellationToken )
    {
        if ( !TryGetLearnedQueryStore( purpose, out var store ) )
        {
            return new Dictionary<string, LearnedQuery>( StringComparer.Ordinal );
        }

        try
        {
            var since = now.ToUnixTimeMilliseconds( ) - PromotionRecencyDays * ( long ) FrecencyPolicy.MillisecondsPerDay;

            return await store
                .GetLearnedQueriesAsync( request.Query ?? string.Empty, since, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
        {
            throw;
        }
        catch ( Exception )
        {
            return new Dictionary<string, LearnedQuery>( StringComparer.Ordinal );
        } // Unavailable history learns nothing, like missing usage.
    }

    private bool LearnedFromQuery( SearchResult result, ParsedQuery query, IReadOnlyDictionary<string, LearnedQuery> learnedQueries ) =>
        learnedQueries.ContainsKey( UsageId( result ) ) && LearnableAction( result, query );

    private static bool IsLearningAction( CommandAction action ) => action.Kind is ActionKinds.LaunchDesktop
        or ActionKinds.OpenPath
        or ActionKinds.OpenUrl
        or ActionKinds.RevealPath
        or ActionKinds.EditAtLine
        or ActionKinds.RunArgv
        or ActionKinds.RunTrustedShell
        or ActionKinds.RunScript
        or ActionKinds.HyprlandDispatch
        or ActionKinds.HerdrFocus;

    private async ValueTask<IReadOnlyDictionary<string, UsageSignal>> ReadSearchUsageAsync( IReadOnlyList<SearchResult> results, ParsedQuery query,
        bool browsing, IReadOnlyDictionary<string, LearnedQuery> learnedQueries, CancellationToken cancellationToken )
    {
        Dictionary<string, UsageSignal> usage = new( StringComparer.Ordinal );
        if ( !LearningAvailable )
        {
            return usage;
        }

        var ids = GetSearchUsageIds( results, query, browsing, learnedQueries );

        try
        {
            foreach ( var batch in ids.Chunk( UsageSignalBatchSize ) )
            {
                foreach ( var pair in await _usageHistory!
                             .GetUsageSignalsAsync( batch, cancellationToken )
                             .ConfigureAwait( false ) )
                {
                    usage[pair.Key] = pair.Value;
                }
            }
        }
        catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
        {
            throw;
        }
        catch ( Exception )
        {
            usage.Clear( );
        } // Unavailable history is exact-only, never partial evidence.

        return usage;
    }

    private sealed record PromotionCandidate( SearchResult Result, PromotionTier Tier, string Reason, double Tie, double Age );

    private PromotionCandidate ClassifyPromotion( SearchResult result, ParsedQuery query, IReadOnlyDictionary<string, UsageSignal> usage,
        IReadOnlyDictionary<string, LearnedQuery> learnedQueries, DateTimeOffset now )
    {
        var tier = PromotionTier.Grouped;
        var reason = "No strong title match or learning disabled";
        var signal = usage.GetValueOrDefault( UsageId( result ) );
        var age = GetLastUseAgeDays( now, signal );
        if ( IsExplicitIntent( result ) )
        {
            tier = PromotionTier.Explicit;
            reason = "Explicit intent";
        }
        else if ( IsTopMatch( result, query ) )
        {
            tier = PromotionTier.Exact;
            reason = "Exact title or basename";
        }

        // The learned query itself is private history, so the reason never quotes it.
        else if ( HasRecentLearnedQuery( result, query, learnedQueries, signal, age ) )
        {
            tier = PromotionTier.Learned;

            if ( learnedQueries[UsageId( result )].Kind == LearnedQueryKind.Exact )
            {
                reason = "Accepted after this same search";
            }
            else
            {
                reason = "Accepted after a longer search that starts with this one";
            }
        }
        else if ( Learnable( result, query ) )
        {
            reason = "Requires two accepted uses and last use within 21 days";

            if ( HasRecentRepeatedUse( signal, age ) )
            {
                tier = PromotionTier.Learned;
                reason = "Repeated recent use and strong title match";
            }
        }

        var tie = GetPromotionTieScore( result, now, signal );

        return new( result, tier, reason, tie, age );
    }

    private SearchResult[] AssembleTopMatches( IReadOnlyList<SearchResult> results, ParsedQuery query, LauncherPreferences preferences, int limit,
        IReadOnlyDictionary<string, UsageSignal> usage, IReadOnlyDictionary<string, LearnedQuery> learnedQueries, DateTimeOffset now )
    {
        var positions = preferences.Sources.ToDictionary( static source => source.SourceId, static source => source.Position );
        var sourceCap = _options.Ranking.MaximumResultsPerSource;
        var pageSize = Math.Min( limit, _options.Ranking.FirstPageSize );
        var target = GetDiversityTarget( results, pageSize );
        var entries = GetEntries( results, query, usage, learnedQueries, now );

        // An exact alias leads the explicit tier even when a source weight lifts
        // another explicit row, such as a typed URL, to the same capped score.
        var ordered = GetOrderedEntries( entries );
        HashSet<string> promoted = new( StringComparer.Ordinal );
        Dictionary<string, int> counts = new( StringComparer.Ordinal );
        foreach ( var entry in ordered.Where( static entry => entry.Tier != PromotionTier.Grouped ) )
        {
            var source = entry.Result.SourceId;
            if ( HasReachedPromotionLimit( limit, sourceCap, promoted, counts, source ) )
            {
                continue;
            }

            var distinct = counts.Count + (counts.ContainsKey( source ) ? 0 : 1);
            if ( WouldDisplaceReservedSources( pageSize, target, promoted, entry, distinct ) )
            {
                continue;
            }

            promoted.Add( entry.Result.Id );
            counts[source] = counts.GetValueOrDefault( source ) + 1;
        }

        var entriesById = entries.ToDictionary( static entry => entry.Result.Id, StringComparer.Ordinal );
        var assembled = GetPromotedResults( ordered, promoted, entriesById );
        var seen = counts.Keys.ToHashSet( StringComparer.Ordinal );
        var groups = GroupUnpromotedResults( results, positions, promoted );
        for ( var index = 0; index < groups.Length; index++ )
        {
            var group = groups[index];
            var allowance = Math.Min( sourceCap - counts.GetValueOrDefault( group.Key ), limit - assembled.Count );
            if ( allowance <= 0 )
            {
                continue;
            }

            var prospective = seen.Count + (seen.Contains( group.Key ) ? 0 : 1);
            var availableLater = CountAvailableSources( sourceCap, counts, seen, groups, index, group );
            var reserve = Math.Min( Math.Max( 0, target - prospective ), availableLater );

            // Allocate before emitting: no later duplicate group can push a
            // reserved source off the first page.
            if ( ShouldReserveFirstPageSlots( pageSize, assembled, reserve ) )
            {
                allowance = Math.Min( allowance, Math.Max( 0, pageSize - assembled.Count - reserve ) );
            }

            if ( allowance <= 0 )
            {
                continue;
            }

            seen.Add( group.Key );

            var section = CreateSourceSection( group, positions.GetValueOrDefault( group.Key, UnlistedSourcePosition ) );

            assembled.AddRange( OrderPromotionGroup( group, entriesById, promoted, section, allowance ) );
        }

        return assembled.ToArray( );
    }

    private PromotionCandidate[] GetEntries( IReadOnlyList<SearchResult> results, ParsedQuery query, IReadOnlyDictionary<string, UsageSignal> usage,
        IReadOnlyDictionary<string, LearnedQuery> learnedQueries, DateTimeOffset now ) => results
        .Select( result => ClassifyPromotion( result, query, usage, learnedQueries, now ) )
        .ToArray( );

    private static PromotionCandidate[] GetOrderedEntries( PromotionCandidate[] entries ) =>
        entries
            .OrderBy( static entry => entry.Tier )
            .ThenByDescending( static entry => entry.Result.Metadata.ContainsKey( ResultMetadataKeys.ExactAlias ) )
            .ThenByDescending( static entry => entry.Result.Score )
            .ThenByDescending( static entry => entry.Tie )
            .ThenBy( static entry => ResultTitleOrderKey( entry.Result ), StringComparer.OrdinalIgnoreCase )
            .ThenBy( static entry => entry.Result.Id, StringComparer.Ordinal )
            .ToArray( );

    private static QueryClause[] GetPositiveClauses( ParsedQuery query )
    {
        return query
            .Expression.Clauses.Where( static clause => !clause.Excluded )
            .ToArray( );
    }

    private static bool NeedsModeClause( ParsedQuery query, QueryClause[] positive )
    {
        return positive.Length == 0 && (query.IsCommandMode || query.ProviderFilter is not null);
    }

    private string[] GetSearchUsageIds( IReadOnlyList<SearchResult> results, ParsedQuery query, bool browsing,
        IReadOnlyDictionary<string, LearnedQuery> learnedQueries )
    {
        return results
            .Where( result => result.Kind is ResultKinds.Window or ResultKinds.Workspace or ResultKinds.Tab ||
                              _history.TopMatches && !browsing && (Learnable( result, query ) || LearnedFromQuery( result, query, learnedQueries )) )
            .Select( UsageId )
            .Distinct( StringComparer.Ordinal )
            .Order( StringComparer.Ordinal )
            .ToArray( );
    }

    private static double GetLastUseAgeDays( DateTimeOffset now, UsageSignal signal )
    {
        if ( signal.LastUsedUnixMilliseconds > 0 )
        {
            return Math.Max( 0, (now.ToUnixTimeMilliseconds( ) - signal.LastUsedUnixMilliseconds) / FrecencyPolicy.MillisecondsPerDay );
        }
        else
        {
            return double.PositiveInfinity;
        }
    }

    private bool HasRecentLearnedQuery( SearchResult result, ParsedQuery query, IReadOnlyDictionary<string, LearnedQuery> learnedQueries, UsageSignal signal,
        double age )
    {
        return LearnedFromQuery( result, query, learnedQueries ) &&
               signal.UseCount >= (learnedQueries[UsageId( result )].Kind == LearnedQueryKind.Exact ? 1 : MinimumPromotionUses) &&
               age <= PromotionRecencyDays;
    }

    private static bool HasRecentRepeatedUse( UsageSignal signal, double age )
    {
        return signal.UseCount >= MinimumPromotionUses && age <= PromotionRecencyDays;
    }

    private double GetPromotionTieScore( SearchResult result, DateTimeOffset now, UsageSignal signal )
    {
        if ( LearningAvailable )
        {
            return TimeAwareRankingPolicy.Score( result.Kind, signal.UseCount, signal.LastUsedUnixMilliseconds, now, _options.Ranking.TimeAware );
        }
        else
        {
            return 0;
        }
    }

    private int GetDiversityTarget( IReadOnlyList<SearchResult> results, int pageSize )
    {
        return Math.Min( pageSize, Math.Min( _options.Ranking.MinimumSourcesOnFirstPage, results
            .Select( static result => result.SourceId )
            .Distinct( )
            .Count( ) ) );
    }

    private static IGrouping<string, SearchResult>[] GroupUnpromotedResults( IReadOnlyList<SearchResult> results, Dictionary<string, int> positions,
        HashSet<string> promoted )
    {
        return results
            .Where( result => !promoted.Contains( result.Id ) )
            .GroupBy( static result => result.SourceId )
            .OrderBy( group => positions.GetValueOrDefault( group.Key, UnlistedSourcePosition ) )
            .ThenBy( static group => group.Key, StringComparer.Ordinal )
            .ToArray( );
    }

    private static int CountAvailableSources( int sourceCap, Dictionary<string, int> counts, HashSet<string> seen, IGrouping<string, SearchResult>[] groups,
        int index, IGrouping<string, SearchResult> group )
    {
        return groups
            .Skip( index + 1 )
            .Count( next => !seen.Contains( next.Key ) && next.Key != group.Key && counts.GetValueOrDefault( next.Key ) < sourceCap );
    }

    private static bool ShouldReserveFirstPageSlots( int pageSize, List<SearchResult> assembled, int reserve )
    {
        return reserve > 0 && assembled.Count < pageSize;
    }

    private static bool HasReachedPromotionLimit( int limit, int sourceCap, HashSet<string> promoted, Dictionary<string, int> counts, string source )
    {
        return promoted.Count >= Math.Min( MaximumTopMatches, limit ) || counts.GetValueOrDefault( source ) >= sourceCap;
    }

    private static bool WouldDisplaceReservedSources( int pageSize, int target, HashSet<string> promoted, PromotionCandidate entry, int distinct )
    {
        return entry.Tier == PromotionTier.Learned && promoted.Count + 1 + Math.Max( 0, target - distinct ) > pageSize;
    }

    private static List<SearchResult> GetPromotedResults( PromotionCandidate[] ordered, HashSet<string> promoted,
        Dictionary<string, PromotionCandidate> entriesById ) => ordered
        .Where( entry => promoted.Contains( entry.Result.Id ) )
        .Select( entry => WithSection( ExplainPromotionResult( entry.Result, entriesById, promoted ), ResultSections.TopMatches ) )
        .ToList( );

    private static IEnumerable<SearchResult> OrderPromotionGroup( IGrouping<string, SearchResult> group, Dictionary<string, PromotionCandidate> entriesById,
        HashSet<string> promoted, ResultSection section, int allowance ) => group
        .OrderByDescending( static result => result.Score )
        .ThenByDescending( result => entriesById[result.Id].Tie )
        .ThenBy( static result => ResultTitleOrderKey( result ), StringComparer.OrdinalIgnoreCase )
        .ThenBy( static result => result.Id, StringComparer.Ordinal )
        .Take( allowance )
        .Select( result => WithSection( ExplainPromotionResult( result, entriesById, promoted ), section ) );

    private static SearchResult ExplainPromotionResult( SearchResult result, Dictionary<string, PromotionCandidate> entriesById, HashSet<string> promoted )
    {
        var entry = entriesById[result.Id];
        var selected = promoted.Contains( result.Id );
        var ranking = CreatePromotionRanking( result, entry, selected );

        return Copy( result, ranking: ranking );
    }

    private static RankingExplanation CreatePromotionRanking( SearchResult result, PromotionCandidate entry, bool selected ) => new( )
    {
        FinalScore = result.Ranking.FinalScore,
        Contributions = result.Ranking.Contributions,
        Matches = result.Ranking.Matches,
        Policies = result.Ranking.Policies,
        Placement = CreatePromotionPlacement( result, entry, selected )
    };

    private static RankingPlacement CreatePromotionPlacement( SearchResult result, PromotionCandidate entry, bool selected ) => new( )
    {
        Tier = entry.Tier switch
        {
            PromotionTier.Explicit => ExplicitPlacementTier,
            PromotionTier.Exact => ExactPlacementTier,
            PromotionTier.Learned => LearnedPlacementTier,
            _ => GroupedPlacementTier
        },
        Promoted = selected,
        Reason = GetPromotionReason( entry, selected ),
        UsageId = entry.Tier == PromotionTier.Learned ? UsageId( result ) : null,
        LastUseAgeDays = entry.Tier == PromotionTier.Learned ? entry.Age : null
    };

    private static string GetPromotionReason( PromotionCandidate entry, bool selected )
    {
        if ( IsUnselectedPromotion( entry, selected ) )
        {
            return entry.Reason + "; section/source cap or first-page diversity";
        }

        return entry.Reason;
    }

    private static bool IsUnselectedPromotion( PromotionCandidate entry, bool selected ) =>
        entry.Tier != PromotionTier.Grouped && !selected;

    private const string ExplicitPlacementTier = "explicit";
    private const string ExactPlacementTier = "exact";
    private const string LearnedPlacementTier = "learned";
    private const string GroupedPlacementTier = "grouped";

    private bool TryGetLearnedQueryStore( SearchPurpose purpose, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out ILearnedQueryStore? store )
    {
        store = null;

        if ( purpose != SearchPurpose.Interactive )
        {
            return false;
        }

        if ( !LearningAvailable )
        {
            return false;
        }

        if ( !_history.RememberSearches )
        {
            return false;
        }

        store = _usageHistory as ILearnedQueryStore;

        return store is not null;
    }
}
