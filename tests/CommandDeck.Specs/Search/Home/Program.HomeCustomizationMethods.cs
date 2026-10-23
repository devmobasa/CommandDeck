namespace CommandDeck.Specs;

using Microsoft.Data.Sqlite;

public static partial class Program
{
    private static async Task FavoritesRejectOverflowInsteadOfTruncatingExportAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-favorite-limit-spec-{Guid.NewGuid( ):N}.db" );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( CancellationToken.None );

            for ( var index = 0; index < CustomizationLimits.MaximumFavorites; index++ )
            {
                await catalog.SetFavoriteAsync( new SearchResult
                {
                    Id = $"file:favorite-{index:D3}",
                    Kind = ResultKinds.File,
                    Title = $"Favorite {index:D3}",
                    ProviderId = ProviderIds.Files,
                    SourceId = SourceIds.Files,
                    CanCustomize = true
                }, true, CancellationToken.None );
            }

            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.SetFavoriteAsync( CreateSearchResult( ), true, CancellationToken.None ) );

            var snapshot = await catalog.GetSnapshotAsync( false, CancellationToken.None );
            var export = await catalog.ExportAsync( CancellationToken.None );
            Equal( 256, snapshot.Customizations.Count );
            Equal( 256, export.Customizations.Count );

            await using ( SqliteConnection connection = new( $"Data Source={database}" ) )
            {
                await connection.OpenAsync( );
                await using var insert = connection.CreateCommand( );

                insert.CommandText = """
                                     INSERT INTO favorites(
                                         stable_id, position, title_snapshot, source_id,
                                         created_unix_ms, updated_unix_ms)
                                     VALUES(
                                         'file:legacy-overflow', 257, 'Legacy overflow',
                                         'files', 1, 1);
                                     """;

                await insert.ExecuteNonQueryAsync( );
            }

            snapshot = await catalog.GetSnapshotAsync( false, CancellationToken.None );
            export = await catalog.ExportAsync( CancellationToken.None );
            Equal( 257, snapshot.Customizations.Count );
            Equal( 257, export.Customizations.Count );

            await catalog.ReorderFavoritesAsync( snapshot
                .Customizations.Select( value => value.StableId )
                .Reverse( )
                .ToList( ), CancellationToken.None );

            var reordered = await catalog.GetSnapshotAsync( false, CancellationToken.None );
            Equal( "file:legacy-overflow", reordered.Customizations[0].StableId );
        }
        finally
        {
            DeleteDatabase( database );
        }

        static SearchResult CreateSearchResult( )
        {
            return new SearchResult
            {
                Id = "file:favorite-overflow",
                Kind = ResultKinds.File,
                Title = "Favorite overflow",
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Files,
                CanCustomize = true
            };
        }
    }

    private static async Task HomePreservesBookmarkOrderOverScoreAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-home-order-spec-{Guid.NewGuid( ):N}.db" );

        try
        {
            SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( CancellationToken.None );

            await catalog.ReplaceAllAsync( CustomizationItems( includeFile: true ), null, CancellationToken.None );

            var application =
                (await catalog.SearchAsync( QueryParser.Parse( "Brave" ), 10, CancellationToken.None )).Single( value => value.Kind == "application" );

            var file = (await catalog.SearchAsync( QueryParser.Parse( "notes" ), 10, CancellationToken.None )).Single( value => value.Kind == "file" );

            await catalog.SetFavoriteAsync( application, true, CancellationToken.None );
            await catalog.SetFavoriteAsync( file, true, CancellationToken.None );
            await catalog.ReorderFavoritesAsync( [file.Id, application.Id], CancellationToken.None );

            await catalog.UsageHistory.RecordInvocationAsync( application.Id, application.Actions[0].Id, "Brave", CancellationToken.None );

            SearchCoordinator coordinator = new( [new CatalogSearchProvider( catalog, catalog )], new SearchOptions( ), catalog, catalog );

            var home = await coordinator.SearchAsync( new SearchRequest { Query = string.Empty, Limit = 20 }, CancellationToken.None );

            Equal( file.Id, home[0].Id );
            Equal( application.Id, home[1].Id );
            await catalog.DisposeAsync( );
        }
        finally
        {
            DeleteDatabase( database );
        }
    }

    private static async Task HomeFrecencyConsidersOlderFrequentResultsAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-home-frecency-spec-{Guid.NewGuid( ):N}.db" );

        DateTimeOffset now = new( 2026, 8, 30, 12, 0, 0,
            TimeSpan.Zero );

        try
        {
            SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database }, new FixedTimeProvider( now ) );

            await catalog.InitializeAsync( CancellationToken.None );

            async IAsyncEnumerable<IndexedItem> Items( )
            {
                for ( var index = 0; index < 300; index++ )
                {
                    yield return ApplicationItem( $"app-{index:D3}.desktop", $"Application {index:D3}", $"app-{index:D3}" );
                }

                await Task.CompletedTask;
            }

            await catalog.ReplaceAllAsync( Items( ), null, CancellationToken.None );

            await using ( SqliteConnection connection = new( $"Data Source={database}" ) )
            {
                await connection.OpenAsync( );
                await using var command = connection.CreateCommand( );

                command.CommandText = """
                                      INSERT INTO usage(stable_id, use_count, last_used_unix_ms)
                                      VALUES($id, $uses, $last_used);
                                      """;

                var id = command.Parameters.Add( "$id", SqliteType.Text );
                var uses = command.Parameters.Add( "$uses", SqliteType.Integer );
                var lastUsed = command.Parameters.Add( "$last_used", SqliteType.Integer );
                id.Value = "app:app-000.desktop";
                uses.Value = 100;

                lastUsed.Value = now
                    .AddDays( -365 )
                    .ToUnixTimeMilliseconds( );

                await command.ExecuteNonQueryAsync( );

                for ( var index = 1; index < 300; index++ )
                {
                    id.Value = $"app:app-{index:D3}.desktop";
                    uses.Value = 1;

                    lastUsed.Value = now
                        .AddSeconds( -index )
                        .ToUnixTimeMilliseconds( );

                    await command.ExecuteNonQueryAsync( );
                }
            }

            var home = await catalog.GetHomeResultsAsync( LauncherPreferences.CreateDefault( ), 20, CancellationToken.None );

            True( home.Any( result => result.Id == "app:app-000.desktop" ) );
            Equal( "app:app-000.desktop", home[0].Id );
            await catalog.DisposeAsync( );
        }
        finally
        {
            DeleteDatabase( database );
        }
    }
}
