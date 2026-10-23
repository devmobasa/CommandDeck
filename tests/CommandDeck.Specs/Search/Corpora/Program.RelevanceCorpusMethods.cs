using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private sealed record RelevanceCorpusMeasurement( int CaseCount, int Passed, double TopOneAccuracy, double MeanReciprocalRank, double VisibleRecall );

    private sealed record RelevanceHit( string Id, int Rank, string Section, double Score, string Explanation );

    private sealed record RelevanceStepOutcome( string Query, bool Passed, int AcceptableRank, IReadOnlyList<RelevanceHit> Visible, string Detail );

    private sealed record RelevanceCaseOutcome( RelevanceCase Case, bool Passed, IReadOnlyList<RelevanceStepOutcome> Steps );

    private static async Task<RelevanceCorpusMeasurement> MeasureRelevanceForGateAsync( )
    {
        var path = Path.Combine( AppContext.BaseDirectory, "fixtures", "ranking-corpus-v1.json" );
        using var document = JsonDocument.Parse( File.ReadAllText( path ) );
        var root = document.RootElement;
        var relevance = root.GetProperty( "relevanceCases" );

        var measurement = await MeasureRelevanceCorpusAsync( relevance, root
            .GetProperty( "version" )
            .GetInt32( ), writeReport: true );

        if ( FallsBelowRelevanceBaseline( relevance, measurement ) )
        {
            throw new InvalidOperationException( "Relevance corpus aggregate fell below its measured baseline." );
        }

        return measurement;

        static bool FallsBelowRelevanceBaseline( JsonElement relevance, RelevanceCorpusMeasurement measurement )
        {
            return measurement.TopOneAccuracy <
                   relevance
                       .GetProperty( "minimumTopOneAccuracy" )
                       .GetDouble( ) ||
                   measurement.MeanReciprocalRank <
                   relevance
                       .GetProperty( "minimumMeanReciprocalRank" )
                       .GetDouble( ) ||
                   measurement.VisibleRecall <
                   relevance
                       .GetProperty( "minimumVisibleRecall" )
                       .GetDouble( );
        }
    }

    private static async Task<RelevanceCorpusMeasurement> MeasureRelevanceCorpusAsync( JsonElement relevance, int fixtureVersion,
        IReadOnlySet<string>? omitIndexedIds = null, bool writeReport = false )
    {
        var fixture = ParseRelevanceFixture( relevance );
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-relevance-corpus-{Guid.NewGuid( ):N}.db" );
        FixedTimeProvider clock = new( PromotionNow );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database }, clock );
            await catalog.InitializeAsync( CancellationToken.None );

            await catalog.ReplaceAllAsync( RelevanceItems( fixture.Items, omitIndexedIds ), null, CancellationToken.None );

            SearchCoordinator coordinator = new( [new CatalogSearchProvider( catalog, catalog )], new SearchOptions( ), catalog, catalog, catalog.UsageHistory,
                clock );

            await ApplyRelevanceAliasesAsync( coordinator, catalog, fixture.Items, fixture.Aliases );
            await ApplyRelevanceUsageAsync( catalog, fixture.Usage );
            var outcomes = new List<RelevanceCaseOutcome>( fixture.Cases.Count );
            var topOneMatches = 0;
            var scoredSteps = 0;
            double reciprocalRankTotal = 0;
            var recalledSteps = 0;
            var recalled = 0;
            foreach ( var testCase in fixture.Cases )
            {
                var steps = new List<RelevanceStepOutcome>( testCase.Steps.Count );
                foreach ( var step in testCase.Steps )
                {
                    var results = await coordinator.SearchAsync( new SearchRequest { Query = step.Query, Limit = RelevanceProbeLimit },
                        CancellationToken.None );

                    var outcome = JudgeRelevanceStep( testCase, step, results );
                    steps.Add( outcome );

                    if ( step.Acceptable.Count > 0 )
                    {
                        scoredSteps++;

                        if ( outcome.AcceptableRank == 1 )
                        {
                            topOneMatches++;
                        }

                        reciprocalRankTotal += ReciprocalRank( outcome );
                    }

                    if ( step.Required.Count > 0 )
                    {
                        recalledSteps++;

                        if ( step.Required.All( id => outcome.Visible.Any( hit => hit.Id == id ) ) )
                        {
                            recalled++;
                        }
                    }
                }

                outcomes.Add( new RelevanceCaseOutcome( testCase, steps.All( static step => step.Passed ), steps ) );
            }

            var measurement = new RelevanceCorpusMeasurement( outcomes.Count, outcomes.Count( static outcome => outcome.Passed ),
                scoredSteps == 0 ? 0 : topOneMatches / ( double ) scoredSteps, scoredSteps == 0 ? 0 : reciprocalRankTotal / scoredSteps,
                recalledSteps == 0 ? 0 : recalled / ( double ) recalledSteps );

            if ( writeReport )
            {
                WriteRelevanceReport( fixtureVersion, measurement, outcomes );
            }

            if ( measurement.Passed != measurement.CaseCount )
            {
                throw new InvalidOperationException( FormatRelevanceFailures( outcomes ) );
            }

            return measurement;
        }
        finally
        {
            DeleteDatabase( database );
        }

        static double ReciprocalRank( RelevanceStepOutcome outcome )
        {
            if ( outcome.AcceptableRank == 0 )
            {
                return 0;
            }

            return 1d / outcome.AcceptableRank;
        }
    }

    private static async IAsyncEnumerable<IndexedItem> RelevanceItems( JsonElement items, IReadOnlySet<string>? omitIndexedIds )
    {
        await foreach ( var item in CorpusItems( items ) )
        {
            if ( omitIndexedIds is null || !omitIndexedIds.Contains( item.StableId ) )
            {
                yield return item;
            }
        }
    }

    private static async Task ApplyRelevanceAliasesAsync( SearchCoordinator coordinator, SqliteCatalog catalog, JsonElement items,
        IReadOnlyList<RelevanceAlias> aliases )
    {
        Dictionary<string, string> titles = new( StringComparer.Ordinal );
        foreach ( var item in items.EnumerateArray( ) )
        {
            var id = item
                .GetProperty( "id" )
                .GetString( );

            var title = item
                .GetProperty( "title" )
                .GetString( );

            if ( id is null )
            {
                continue;
            }

            if ( title is null )
            {
                continue;
            }

            titles[id] = title;
        }

        foreach ( var alias in aliases )
        {
            if ( !titles.TryGetValue( alias.Id, out var title ) )
            {
                throw new InvalidOperationException( $"Relevance alias '{alias.Alias}' could not find '{alias.Id}'." );
            }

            var results = await coordinator.SearchAsync( new SearchRequest { Query = title, Limit = RelevanceProbeLimit }, CancellationToken.None );

            var result = results.FirstOrDefault( candidate => candidate.Id == alias.Id );
            if ( result is null )
            {
                throw new InvalidOperationException( $"Relevance alias '{alias.Alias}' could not find '{alias.Id}'." );
            }

            await catalog.SetAliasAsync( result, alias.Alias, CancellationToken.None );
        }
    }

    private static async Task ApplyRelevanceUsageAsync( SqliteCatalog catalog, IReadOnlyList<RelevanceUsage> usage )
    {
        foreach ( var entry in usage )
        {
            for ( var count = 0; count < entry.Count; count++ )
            {
                await catalog.UsageHistory.RecordInvocationAsync( entry.Id, "launch", null, CancellationToken.None );
            }
        }
    }

    private static RelevanceStepOutcome JudgeRelevanceStep( RelevanceCase testCase, RelevanceStep step, IReadOnlyList<SearchResult> results )
    {
        var visible = results
            .Take( testCase.VisibleWindow )
            .Select( ( result, index ) => new RelevanceHit(
                result.Id, index + 1, result.Section?.Id ?? string.Empty, result.Score, ExplainRelevanceHit( result ) ) )
            .ToArray( );

        var visibleIds = visible
            .Select( static hit => hit.Id )
            .ToHashSet( StringComparer.Ordinal );

        List<string> problems = [];
        AddCountProblems( problems, step, results.Count );
        AddMembershipProblems( problems, testCase, step, results, visibleIds );
        AddSectionProblems( problems, step, visible );
        var acceptableRank = 0;
        for ( var index = 0; index < visible.Length; index++ )
        {
            if ( step.Acceptable.Contains( visible[index].Id, StringComparer.Ordinal ) )
            {
                acceptableRank = index + 1;

                break;
            }
        }

        var detail = problems.Count == 0 ? "passed" : string.Join( "; ", problems );

        return new RelevanceStepOutcome( step.Query, problems.Count == 0, acceptableRank, visible, detail );
    }

    private static void AddCountProblems( List<string> problems, RelevanceStep step, int count )
    {
        if ( step.ExpectEmpty && count != 0 )
        {
            problems.Add( $"expected no rows, saw {count}" );
        }

        if ( step.MaximumCount is { } maximum && count > maximum )
        {
            problems.Add( $"count {count} exceeds {step.MaximumCount}" );
        }
    }

    private static void AddMembershipProblems( List<string> problems, RelevanceCase testCase, RelevanceStep step, IReadOnlyList<SearchResult> results,
        HashSet<string> visibleIds )
    {
        foreach ( var id in step.Required )
        {
            if ( visibleIds.Contains( id ) )
            {
                continue;
            }

            var rank = -1;
            for ( var index = 0; index < results.Count; index++ )
            {
                if ( results[index].Id == id )
                {
                    rank = index;

                    break;
                }
            }

            problems.Add( DescribeMissingResult( testCase, id, rank ) );
        }

        foreach ( var id in step.Forbidden )
        {
            if ( visibleIds.Contains( id ) )
            {
                problems.Add( $"forbidden '{id}' is visible" );
            }
        }

        if ( step.Acceptable.Count > 0 && !step.Acceptable.Any( visibleIds.Contains ) )
        {
            problems.Add( "no acceptable id is visible" );
        }

        static string DescribeMissingResult( RelevanceCase testCase, string id, int rank )
        {
            if ( rank < 0 )
            {
                return $"required '{id}' was not retrieved";
            }

            return $"required '{id}' is rank {rank + 1}, outside window {testCase.VisibleWindow}";
        }
    }

    private static void AddSectionProblems( List<string> problems, RelevanceStep step, IReadOnlyList<RelevanceHit> visible )
    {
        if ( step.Section is null )
        {
            return;
        }

        foreach ( var id in step.Required )
        {
            RelevanceHit? hit = null;
            foreach ( var candidate in visible )
            {
                if ( candidate.Id == id )
                {
                    hit = candidate;

                    break;
                }
            }

            if ( hit is null )
            {
                continue;
            }

            if ( !string.Equals( hit.Section, step.Section, StringComparison.Ordinal ) )
            {
                problems.Add( $"'{id}' is in '{hit.Section}' rather than '{step.Section}'" );
            }
        }
    }
}
