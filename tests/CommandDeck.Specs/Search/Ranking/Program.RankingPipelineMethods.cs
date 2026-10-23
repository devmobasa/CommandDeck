namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task CatalogRankingExposesContributionsFieldsCapsAndFairnessAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-ranking-pipeline-{Guid.NewGuid( ):N}.db" );

        try
        {
            SearchOptions searchOptions = CreateSearchOptions( );
            ConfigurationValidator.Validate( new CommandDeckOptions { Search = searchOptions } );

            Throws<InvalidOperationException>( ( ) => ConfigurationValidator.Validate( new CommandDeckOptions
            {
                Search = new SearchOptions { Ranking = new RankingOptions { TitleWeight = 0 } }
            } ) );

            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database }, searchOptions: searchOptions );

            await catalog.InitializeAsync( CancellationToken.None );
            await catalog.ReplaceAllAsync( RankingPipelineItems( ), null, CancellationToken.None );
            SearchCoordinator coordinator = new( [new CatalogSearchProvider( catalog )], searchOptions );

            var alpha = await coordinator.SearchAsync( new SearchRequest { Query = "alpha", Limit = 8, ActiveApplication = "alpha.desktop" },
                CancellationToken.None );

            RankingTrue( alpha.Count >= 4, "alpha query returned fewer than four rows" );

            RankingTrue( alpha
                .GroupBy( static result => result.SourceId )
                .All( static group => group.Count( ) <= 3 ), "per-source cap was not applied" );

            RankingTrue( alpha
                             .Take( 4 )
                             .Select( static result => result.SourceId )
                             .Distinct( )
                             .Count( ) >=
                         2, "first page did not contain two sources" );

            var explained = alpha.Single( static result => result.Title == "Alpha Browser" );

            RankingTrue( explained.Ranking.Contributions.Any( static contribution => contribution.Kind == "lexical" ), "lexical contribution missing" );
            RankingTrue( explained.Ranking.Contributions.Any( static contribution => contribution.Kind == "source-weight" ), "source contribution missing" );
            RankingTrue( explained.Ranking.Contributions.Any( static contribution => contribution.Kind == "context" ), "context contribution missing" );
            RankingTrue( explained.Ranking.FinalScore == explained.Score, "final explanation score drifted" );

            RankingTrue( Math.Abs( explained.Ranking.Contributions.Sum( static value => value.Value ) - explained.Score ) < 0.000_001,
                "ranking contributions do not sum to the final score" );

            await AssertFieldMatchAsync( coordinator, "browser", "title" );
            await AssertFieldMatchAsync( coordinator, "Projects", "path" );
            await AssertFieldMatchAsync( coordinator, "desktop", "metadata" );
            await AssertFieldMatchAsync( coordinator, "quartz", "content" );
            await AssertFieldMatchAsync( coordinator, "mainMod", "config" );

            var typo = await coordinator.SearchAsync( new SearchRequest { Query = "alhpa", Limit = 8 }, CancellationToken.None );

            RankingTrue( typo.Any( static result => result.Title == "Alpha" ), "transposed typo did not reach the catalog result" );

            var acronym = await coordinator.SearchAsync( new SearchRequest { Query = "ab", Limit = 8 }, CancellationToken.None );

            RankingTrue( acronym.Any( static result => result.Title == "Alpha Browser" ), "acronym did not reach the catalog result" );

            var falsePositive = await coordinator.SearchAsync( new SearchRequest { Query = "zzzzunrelated", Limit = 8 }, CancellationToken.None );

            Equal( 0, falsePositive.Count );
        }
        finally
        {
            DeleteDatabase( database );
        }

        static SearchOptions CreateSearchOptions( )
        {
            return new SearchOptions( )
            {
                Ranking = new RankingOptions
                {
                    MaximumResultsPerSource = 3,
                    FirstPageSize = 4,
                    MinimumSourcesOnFirstPage = 2,
                    SourceWeights = new Dictionary<string, double>( StringComparer.Ordinal ) { [SourceIds.Applications] = 1.1, [SourceIds.Files] = 0.95 }
                }
            };
        }
    }

    private static void RankingTrue( bool condition, string message )
    {
        if ( !condition )
        {
            throw new InvalidOperationException( message );
        }
    }

    private static async Task AssertFieldMatchAsync( SearchCoordinator coordinator, string query, string field )
    {
        var results = await coordinator.SearchAsync( new SearchRequest { Query = query, Limit = 8 }, CancellationToken.None );

        var result = results.First( static candidate => candidate.Ranking.Matches.Count > 0 );
        var match = result.Ranking.Matches.First( candidate => candidate.Field == field );
        True( match.Ranges.Count > 0 );
        True( match.Ranges.All( range => range is { Start: >= 0, Length: > 0 } && range.Start + range.Length <= match.Value.Length ) );
    }

    private static async IAsyncEnumerable<IndexedItem> RankingPipelineItems( )
    {
        for ( var index = 0; index < 4; index++ )
        {
            yield return CreateIndexedItem( index );
        }

        for ( var index = 0; index < 4; index++ )
        {
            yield return CreateStableIdIndexedItem( index );
        }

        yield return CreateHyprlandBindingItem( );

        await Task.CompletedTask;

        static IndexedItem CreateIndexedItem( int index )
        {
            return new IndexedItem
            {
                StableId = $"app:alpha-{index}.desktop",
                Kind = ResultKinds.Application,
                Title = index switch
                {
                    0 => "Alpha Browser",
                    3 => "Alpha",
                    _ => $"Alpha Application {index}"
                },
                Subtitle = "Fast desktop application",
                PayloadJson = CreateApplicationPayload( index ),
                ProviderId = "spec",
                SourceId = SourceIds.Applications
            };
        }

        static IndexedItem CreateStableIdIndexedItem( int index )
        {
            return new IndexedItem
            {
                StableId = $"file:alpha-{index}",
                Kind = ResultKinds.File,
                Title = $"alpha-notes-{index}.md",
                Path = $"/home/spec/Projects/alpha-notes-{index}.md",
                Body = index == 0 ? "quartz deployment notes" : "alpha project notes",
                ProviderId = "spec",
                SourceId = SourceIds.Files
            };
        }

        static IndexedItem CreateHyprlandBindingItem( )
        {
            return new IndexedItem
            {
                StableId = "configuration:mainmod",
                Kind = ResultKinds.Configuration,
                Title = "Hyprland binding",
                Path = "/home/spec/.config/hypr/hyprland.conf",
                ConfigKeys = "mainMod RETURN exec ghostty",
                PayloadJson = PayloadWriter.Create( ("key", "mainMod"), ("value", "SUPER") ),
                ProviderId = "spec",
                SourceId = SourceIds.Configuration
            };
        }

        static string? CreateApplicationPayload( int index )
        {
            if ( index == 0 )
            {
                return PayloadWriter.Create( (ResultMetadataKeys.Application.DesktopId, "alpha.desktop") );
            }

            return null;
        }
    }
}
