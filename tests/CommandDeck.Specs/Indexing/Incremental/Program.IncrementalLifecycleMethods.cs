namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task IncrementalIndexAppliesFileLifecycleAsync( )
    {
        var root = CreateIncrementalSpecRoot( );
        var documents = Path.Combine( root, "documents" );
        Directory.CreateDirectory( documents );

        try
        {
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), documents );
            SqliteCatalog inner = new( options.Storage );
            await inner.InitializeAsync( CancellationToken.None );
            await using TrackingSearchCatalog catalog = new( inner, inner );
            await using var indexer = new IndexCoordinator( catalog, catalog, options );
            await indexer.StartAsync( CancellationToken.None );
            await indexer.ReindexAsync( progress: null, CancellationToken.None );
            var path = Path.Combine( documents, "created-note.txt" );
            await File.WriteAllTextAsync( path, "incremental alpha marker" );

            True( await WaitForCatalogResultAsync( catalog, "incremental alpha", result => result.Title == "created-note.txt", TimeSpan.FromSeconds( 1 ) ) );

            var appliesBeforeBurst = catalog.ApplyPathChangesCount;

            for ( var index = 0; index < 20; index++ )
            {
                await File.WriteAllTextAsync( path, $"incremental beta marker revision {index}" );
            }

            True( await WaitForCatalogResultAsync( catalog, "revision 19", result => result.Title == "created-note.txt", TimeSpan.FromSeconds( 1 ) ) );
            True( catalog.ApplyPathChangesCount - appliesBeforeBurst <= 2 );
            True( await WaitUntilAsync( async ( ) => (await SearchAsync( catalog, "incremental alpha" )).Count == 0, TimeSpan.FromSeconds( 1 ) ) );

            var renamed = Path.Combine( documents, "renamed-note.txt" );
            File.Move( path, renamed );

            True( await WaitForCatalogResultAsync( catalog, "revision 19", result => result.Title == "renamed-note.txt", TimeSpan.FromSeconds( 1 ) ) );

            var temporary = Path.Combine( documents, ".atomic-save.tmp" );
            await File.WriteAllTextAsync( temporary, "incremental gamma marker" );
            File.Move( temporary, renamed, overwrite: true );

            True( await WaitForCatalogResultAsync( catalog, "incremental gamma", result => result.Title == "renamed-note.txt", TimeSpan.FromSeconds( 1 ) ) );
            True( await WaitUntilAsync( async ( ) => (await SearchAsync( catalog, "incremental beta" )).Count == 0, TimeSpan.FromSeconds( 1 ) ) );

            File.Delete( renamed );

            True( await WaitUntilAsync( async ( ) => (await SearchAsync( catalog, "incremental gamma" )).Count == 0, TimeSpan.FromSeconds( 1 ) ) );
        }
        finally
        {
            DeleteIncrementalSpecRoot( root );
        }
    }

    private static async Task IncrementalJournalReplaysAfterRestartAsync( )
    {
        var root = CreateIncrementalSpecRoot( );
        var documents = Path.Combine( root, "documents" );
        Directory.CreateDirectory( documents );
        var path = Path.Combine( documents, "restart-note.txt" );
        await File.WriteAllTextAsync( path, "durable before marker" );

        try
        {
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), documents, debounceMilliseconds: 500,
                maximumLatencyMilliseconds: 900 );

            await using SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( CancellationToken.None );

            await using ( var first = new IndexCoordinator( catalog, catalog, options ) )
            {
                await first.StartAsync( CancellationToken.None );
                await first.ReindexAsync( progress: null, CancellationToken.None );

                // Same size and timestamp: only the journaled event can reveal this edit.
                await RewritePreservingMetadataAsync( path, "durable replay marker" );

                True( await WaitUntilAsync( async ( ) => (await first.GetIncrementalStatusAsync( CancellationToken.None )).PendingEventCount > 0,
                    TimeSpan.FromSeconds( 1 ) ) );
            }

            Equal( 0, (await SearchAsync( catalog, "durable replay" )).Count );

            await using ( var second = new IndexCoordinator( catalog, catalog, options ) )
            {
                await second.StartAsync( CancellationToken.None );

                True( await WaitForCatalogResultAsync( catalog, "durable replay", result => result.Title == "restart-note.txt", TimeSpan.FromSeconds( 2 ) ) );

                True( await WaitUntilAsync( async ( ) => (await second.GetIncrementalStatusAsync( CancellationToken.None )).PendingEventCount == 0,
                    TimeSpan.FromSeconds( 1 ) ) );
            }
        }
        finally
        {
            DeleteIncrementalSpecRoot( root );
        }
    }

    private static async Task WatcherClosesInitialScanWindowAsync( )
    {
        var root = CreateIncrementalSpecRoot( );
        var documents = Path.Combine( root, "documents" );
        Directory.CreateDirectory( documents );
        var path = Path.Combine( documents, "window-note.txt" );
        await File.WriteAllTextAsync( path, "scan window old marker" );

        try
        {
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), documents );
            SqliteCatalog inner = new( options.Storage );
            await inner.InitializeAsync( CancellationToken.None );
            await using TrackingSearchCatalog catalog = new( inner, inner );
            await using var indexer = new IndexCoordinator( catalog, catalog, options );

            (var firstItem, var release) = catalog.PauseNextReplacement( item => item.Path == path && item.Kind == ResultKinds.File );

            var scan = indexer
                .ReindexAsync( progress: null, CancellationToken.None )
                .AsTask( );

            try
            {
                await firstItem.WaitAsync( TimeSpan.FromSeconds( 30 ) );
                var scanning = await indexer.GetIncrementalStatusAsync( CancellationToken.None );

                True( scanning.ReconciliationRequired, "the staged generation has not completed startup reconciliation" );

                Equal( 1, scanning.WatchedRootCount );
                await File.WriteAllTextAsync( path, "scan window final marker" );

                True( await WaitUntilAsync( async ( ) => (await indexer.GetIncrementalStatusAsync( CancellationToken.None )).PendingEventCount > 0,
                    TimeSpan.FromSeconds( 30 ) ) );
            }
            finally
            {
                release( );
            }

            await scan;

            True( await WaitForCatalogResultAsync( catalog, "window final", result => result.Title == "window-note.txt", TimeSpan.FromSeconds( 30 ) ) );

            await File.WriteAllTextAsync( path, "scan window ordinary marker" );

            True( await WaitForCatalogResultAsync( catalog, "window ordinary", result => result.Title == "window-note.txt", TimeSpan.FromSeconds( 30 ) ) );

            True( await WaitUntilAsync( async ( ) => (await indexer.GetIncrementalStatusAsync( CancellationToken.None )).PendingEventCount == 0,
                TimeSpan.FromSeconds( 30 ) ) );

            Equal( 1, catalog.ReplaceAllCount );
            True( !(await indexer.GetIncrementalStatusAsync( CancellationToken.None )).Reconciling );
            True( catalog.ApplyPathChangesCount > 0 );
            True( catalog.LastPathChanges.All( change => string.Equals( change.Path, path, StringComparison.Ordinal ) ) );
        }
        finally
        {
            DeleteIncrementalSpecRoot( root );
        }
    }

    private static async Task IncrementalStatusReportsRootCoverageAsync( )
    {
        var root = CreateIncrementalSpecRoot( );
        var watched = Path.Combine( root, "watched" );
        var missing = Path.Combine( root, "missing" );
        Directory.CreateDirectory( watched );

        try
        {
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), watched, missing );
            await using SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( CancellationToken.None );
            await using var indexer = new IndexCoordinator( catalog, catalog, options );
            await indexer.StartAsync( CancellationToken.None );
            var status = await indexer.GetIncrementalStatusAsync( CancellationToken.None );
            True( status.Started );
            Equal( 1, status.WatchedRootCount );
            True( status.UnwatchedRoots.Contains( missing, StringComparer.Ordinal ) );
            Equal( "4", status.ExtractorVersion );

            var missingRoot = status.Roots.Single( value => string.Equals( value.Path, missing, StringComparison.Ordinal ) );

            True( !missingRoot.Watched );
            True( missingRoot.LastError?.Contains( "does not exist", StringComparison.Ordinal ) == true );
            True( !string.IsNullOrWhiteSpace( missingRoot.Remediation ) );
        }
        finally
        {
            DeleteIncrementalSpecRoot( root );
        }
    }

    private static async Task ExtractorVersionChangeReconcilesAsync( )
    {
        var root = CreateIncrementalSpecRoot( );
        var documents = Path.Combine( root, "documents" );
        Directory.CreateDirectory( documents );
        var database = Path.Combine( root, "catalog.db" );
        var path = Path.Combine( documents, "versioned-note.txt" );
        var unaffectedPath = Path.Combine( documents, "unaffected-note.txt" );
        await File.WriteAllTextAsync( path, "extractor old marker" );
        await File.WriteAllTextAsync( unaffectedPath, "unaffected old marker" );

        try
        {
            var options = CreateIncrementalIndexOptions( database, documents );
            await using SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( CancellationToken.None );

            await using ( var first = new IndexCoordinator( catalog, catalog, options ) )
            {
                await first.ReindexAsync( progress: null, CancellationToken.None );
            }

            var statePath = database + ".index-state";
            var state = (await File.ReadAllTextAsync( statePath )).Split( '|' );
            Equal( 3, state.Length );
            state[2] = Convert.ToBase64String( System.Text.Encoding.UTF8.GetBytes( "outdated-extractor" ) );
            await File.WriteAllTextAsync( statePath, string.Join( '|', state ) );

            await using ( Microsoft.Data.Sqlite.SqliteConnection connection = new( $"Data Source={database}" ) )
            {
                await connection.OpenAsync( );
                await using var command = connection.CreateCommand( );

                command.CommandText = """
                                      UPDATE catalog_items
                                      SET extractor_version = 'outdated-extractor'
                                      WHERE path = $path;
                                      """;

                command.Parameters.AddWithValue( "$path", path );
                Equal( 1, await command.ExecuteNonQueryAsync( ) );
            }

            // Same sizes and timestamps keep restart change detection out of this
            // spec: only the extractor-version pass can reveal the new content.
            await RewritePreservingMetadataAsync( path, "extractor new marker" );
            await RewritePreservingMetadataAsync( unaffectedPath, "unaffected new marker" );
            TrackingSearchCatalog tracking = new( catalog, catalog );
            await using ( var second = new IndexCoordinator( tracking, tracking, options ) )
            {
                await second.StartAsync( CancellationToken.None );

                True( await WaitForCatalogResultAsync( catalog, "extractor new", result => result.Title == "versioned-note.txt", TimeSpan.FromSeconds( 2 ) ) );

                // Search rows publish before the reconciliation checkpoint. Wait
                // for its public completion state before asserting that state.
                True( await WaitUntilAsync( async ( ) =>
                {
                    var current = await second.GetIncrementalStatusAsync( CancellationToken.None );

                    return current is { ReconciliationRequired: false, ExtractorVersion: "4" };
                }, TimeSpan.FromSeconds( 2 ) ) );

                var status = await second.GetIncrementalStatusAsync( CancellationToken.None );
                True( !status.ReconciliationRequired );
                Equal( "4", status.ExtractorVersion );
                Equal( 1, tracking.ApplyPathChangesCount );
                Equal( 1, tracking.LastPathChanges.Count );
                Equal( path, tracking.LastPathChanges[0].Path );

                True( !await WaitForCatalogResultAsync( catalog, "unaffected new", result => result.Title == "unaffected-note.txt",
                    TimeSpan.FromMilliseconds( 150 ) ) );

                True( await WaitForCatalogResultAsync( catalog, "unaffected old", result => result.Title == "unaffected-note.txt",
                    TimeSpan.FromSeconds( 1 ) ) );
            }
        }
        finally
        {
            DeleteIncrementalSpecRoot( root );
        }
    }
}
