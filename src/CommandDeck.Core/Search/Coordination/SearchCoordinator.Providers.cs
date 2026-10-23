namespace CommandDeck;

using System.Diagnostics;

public sealed partial class SearchCoordinator
{
    private const int ExtensionSearchTimeoutMilliseconds = 1800;

    private static bool IsBareHome( ParsedQuery query, SearchRequest request ) =>
        request is { HerdrWorkspaceId: null, OmarchyMenuId: null } &&
        query is { Text.Length: 0, HasExplicitFilter: false, IsCalculator: false, IsCommandMode: false, IsAiMode: false };

    // A search scoped to one slow source waits longer: an extension search, or
    // HEY, which answers over the network.
    private int ProviderDeadline( ParsedQuery query )
    {
        if ( IsExtensionScopedQuery( query ) )
        {
            return Math.Max( _options.ProviderTimeoutMilliseconds, ExtensionSearchTimeoutMilliseconds );
        }

        if ( HeySearch.IsRequested( query ) )
        {
            return Math.Max( _options.ProviderTimeoutMilliseconds, HeySearch.ScopedDeadlineMilliseconds );
        }
        else
        {
            return _options.ProviderTimeoutMilliseconds;
        }
    }

    private static bool FilterMatches( FilterDescriptor filter, SearchResult result ) =>
        string.Equals( filter.SourceId, result.SourceId, StringComparison.Ordinal ) &&
        (filter.Id != HerdrSearch.AttentionFilterId || HerdrSearch.NeedsAttention( result )) &&
        (!filter.Id.StartsWith( ExtensionStableIds.SearchProviderPrefix, StringComparison.Ordinal ) ||
         result.Metadata.GetValueOrDefault( ResultMetadataKeys.ExtensionSearchProvider ) == filter.Id) &&
        (filter.Kind is null || string.Equals( filter.Kind, result.Kind, StringComparison.Ordinal ));

    private static bool ClausesMatch( IReadOnlyList<QueryClause> clauses, SearchResult result )
    {
        foreach ( var clause in clauses )
        {
            var matches = ClauseMatches( clause, result );
            if ( DoesNotMatchClause( clause, matches ) )
            {
                return false;
            }
        }

        return true;
    }

    private static bool ClauseMatches( QueryClause clause, SearchResult result )
    {
        // A positive unqualified clause has already been matched by the
        // provider's lexical/fuzzy engine. Requiring a literal substring here
        // would discard accepted typo and acronym matches.
        if ( IsUnqualifiedPositiveClause( clause ) )
        {
            return true;
        }

        var values = GetClauseValues( clause, result );

        return values.Any( value => value?.Contains( clause.Text, StringComparison.OrdinalIgnoreCase ) == true );
    }

    // Home's live sections need the same deadline and late-work ownership as
    // ordinary searches, even when a provider ignores its cancellation token.
    private async Task<SearchOutcome> RunProviderAsync( ICommandProvider provider, ParsedQuery parsed, SearchRequest request,
        CancellationToken requestCancellation )
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource( requestCancellation );
        timeout.CancelAfter( ProviderDeadline( parsed ) );
        var run = ProviderRun.Start( this, provider, parsed, request, timeout.Token, requestCancellation );

        var collection = await CollectWithinDeadlineAsync( [run], timeout, requestCancellation )
            .ConfigureAwait( false );

        return new SearchOutcome( collection
            .Batches.SelectMany( static batch => batch )
            .ToArray( ), collection.IncompleteProviderCount );
    }

    // Waits for every provider until the deadline fires, then returns whatever has
    // completed. A provider that ignores cancellation, such as a live provider
    // blocked on a slow process, keeps running in the background and is reported
    // as timed out exactly once. Catalog queries interrupt SQLite instead.
    private static async Task<ProviderCollection> CollectWithinDeadlineAsync( List<ProviderRun> runs, CancellationTokenSource timeout,
        CancellationToken requestCancellation )
    {
        var all = Task.WhenAll( runs.Select( static run => run.Completion ) );
        var deadline = Task.Delay( Timeout.InfiniteTimeSpan, timeout.Token );

        var first = await Task
            .WhenAny( all, deadline )
            .ConfigureAwait( false );

        if ( first == all )
        {
            timeout.Dispose( );
            requestCancellation.ThrowIfCancellationRequested( );
            var completedBatches = await all.ConfigureAwait( false );

            return new( completedBatches, runs.Count( static run => run.Outcome != SearchProviderOutcomes.Completed ) );
        }

        // Late providers still hold the token; release the source once they unwind.
        _ = all.ContinueWith( _ => timeout.Dispose( ), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default );
        List<IReadOnlyList<SearchResult>> batches = [];
        var incompleteProviderCount = 0;
        foreach ( var run in runs )
        {
            if ( run.Completion.IsCompletedSuccessfully )
            {
                batches.Add( run.Completion.Result );

                if ( run.Outcome != SearchProviderOutcomes.Completed )
                {
                    incompleteProviderCount++;
                }

                continue;
            }

            run.Abandon( );
            incompleteProviderCount++;
        }

        requestCancellation.ThrowIfCancellationRequested( );

        return new( [.. batches], incompleteProviderCount );
    }

    private sealed record ProviderCollection( IReadOnlyList<SearchResult>[] Batches, int IncompleteProviderCount );

    private sealed class ProviderRun
    {
        private readonly ICommandProvider _provider;
        private readonly CancellationToken _requestCancellation;
        private readonly SearchCoordinator _owner;
        private readonly bool _privateQuery;
        private readonly long _started = Stopwatch.GetTimestamp( );
        private int _reported;
        private string? _outcome;

        private ProviderRun( SearchCoordinator owner, ICommandProvider provider, bool privateQuery, CancellationToken requestCancellation )
        {
            _provider = provider;
            _requestCancellation = requestCancellation;
            _owner = owner;
            _privateQuery = privateQuery;
        }

        public Task<IReadOnlyList<SearchResult>> Completion { get; private set; } = null!;

        public string? Outcome => Volatile.Read( ref _outcome );

        public static ProviderRun Start( SearchCoordinator owner, ICommandProvider provider, ParsedQuery parsed, SearchRequest request,
            CancellationToken providerDeadline, CancellationToken requestCancellation )
        {
            lock ( owner._providerLifetimeGate )
            {
                ProviderRun run = new( owner, provider, PrivateSources.ReferencesPrivateSource( parsed ), requestCancellation );
                var slots = owner._providerSlots[provider];
                if ( owner._stopping || providerDeadline.IsCancellationRequested || !slots.Wait( 0, CancellationToken.None ) )
                {
                    run.Report( providerDeadline.IsCancellationRequested ? SearchProviderOutcomes.Cancelled : SearchProviderOutcomes.Saturated, 0 );

                    run.Completion = Task.FromResult<IReadOnlyList<SearchResult>>( [] );

                    return run;
                }

                run.Completion = Task.Run( async ( ) =>
                {
                    try
                    {
                        return await run
                            .ExecuteAsync( parsed, request, providerDeadline )
                            .ConfigureAwait( false );
                    }
                    finally
                    {
                        slots.Release( );
                    }
                }, CancellationToken.None );

                owner.TrackProvider( run.Completion );

                return run;
            }
        }

        public void Abandon( ) =>
            Report( _requestCancellation.IsCancellationRequested ? SearchProviderOutcomes.Cancelled : SearchProviderOutcomes.TimedOut, 0 );

        private async Task<IReadOnlyList<SearchResult>> ExecuteAsync( ParsedQuery parsed, SearchRequest request, CancellationToken providerDeadline )
        {
            try
            {
                providerDeadline.ThrowIfCancellationRequested( );

                var results = await _provider
                    .SearchAsync( parsed, request, providerDeadline )
                    .ConfigureAwait( false );

                Report( SearchProviderOutcomes.Completed, results.Count );

                return results;
            }
            catch ( OperationCanceledException ) when ( providerDeadline.IsCancellationRequested )
            {
                Abandon( );

                return [];
            }
            catch ( Exception exception )
            {
                Report( SearchProviderOutcomes.Failed, 0, exception.GetType( ).Name );

                return [];
            }
        }

        private void Report( string outcome, int resultCount, string? errorType = null )
        {
            if ( Interlocked.Exchange( ref _reported, 1 ) != 0 )
            {
                return;
            }

            Volatile.Write( ref _outcome, outcome );

            if ( _privateQuery )
            {
                return;
            }

            _owner._providerDiagnostic?.Invoke( new SearchProviderDiagnostic
            {
                ProviderId = _provider.Id,
                Outcome = outcome,
                DurationMilliseconds = ( long ) Stopwatch.GetElapsedTime( _started ).TotalMilliseconds,
                ResultCount = resultCount,
                ErrorType = errorType
            } );
        }
    }

    private static bool IsExtensionScopedQuery( ParsedQuery query )
    {
        return query.IncludedFilters.Count == 1 &&
               query
                   .IncludedFilters[0]
                   .Id.StartsWith( ExtensionStableIds.SearchProviderPrefix, StringComparison.Ordinal );
    }

    private static bool DoesNotMatchClause( QueryClause clause, bool matches )
    {
        return !clause.Excluded && !matches || clause.Excluded && matches;
    }

    private static bool IsUnqualifiedPositiveClause( QueryClause clause )
    {
        return clause is { Excluded: false, Field: null };
    }

    private static IEnumerable<string?> GetClauseValues( QueryClause clause, SearchResult result )
    {
        return clause.Field switch
        {
            QueryFieldIds.Title => [result.Title],
            QueryFieldIds.Metadata => [result.Subtitle, .. result.Metadata.Values],
            QueryFieldIds.Path => [result.Metadata.GetValueOrDefault( ResultMetadataKeys.Path )],
            QueryFieldIds.Content => result
                .Ranking.Matches.Where( static match => match.Field == RankingFieldNames.Content )
                .Select( static match => match.Value ),
            QueryFieldIds.Config => result
                .Ranking.Matches.Where( static match => match.Field == RankingFieldNames.Config )
                .Select( static match => match.Value ),
            _ => new[] { result.Title, result.Subtitle }
                .Concat( result.Metadata.Values )
                .Concat( result.Ranking.Matches.Select( static match => match.Value ) )
        };
    }
}
