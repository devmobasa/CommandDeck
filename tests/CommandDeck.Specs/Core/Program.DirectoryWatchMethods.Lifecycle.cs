namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task NewAndMovedDirectoriesAreWatchedByPolicyAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return;
        }

        var sandbox = CreateIncrementalSpecRoot( );
        var root = Path.Combine( sandbox, "root" );
        var notes = Path.Combine( root, "notes" );
        var web = Path.Combine( root, "web" );
        Directory.CreateDirectory( notes );
        Directory.CreateDirectory( Path.Combine( web, "dist" ) );
        Directory.CreateDirectory( Path.Combine( web, "node_modules" ) );
        await File.WriteAllTextAsync( Path.Combine( web, ProjectMarkerNames.PackageJson ), "{}" );

        try
        {
            var options = CreateIncrementalIndexOptions( Path.Combine( sandbox, "catalog.db" ), root );
            await using SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( CancellationToken.None );
            await using IndexCoordinator indexer = new( catalog, catalog, options );
            await indexer.StartAsync( CancellationToken.None );
            await indexer.ReindexAsync( progress: null, CancellationToken.None );
            Equal( "notes, root, web", WatchedList( indexer, root ) );

            // A new admitted directory is watched from its creation on.
            var fresh = Path.Combine( notes, "fresh" );
            Directory.CreateDirectory( fresh );
            Directory.CreateDirectory( Path.Combine( web, "node_modules", "pkg" ) );

            True( await WaitUntilAsync( ( ) => Task.FromResult( WatchedList( indexer, root ) == "notes, notes/fresh, root, web" ),
                TimeSpan.FromSeconds( 3 ) ) );

            await File.WriteAllTextAsync( Path.Combine( fresh, "one.txt" ), "watchfresh marker" );

            True( await WaitForCatalogResultAsync( catalog, "watchfresh", result => result.Title == "one.txt", TimeSpan.FromSeconds( 3 ) ) );

            // Moved into a generated tree it is neither indexed nor watched;
            // moved back it is both again.
            var buried = Path.Combine( web, "dist", "fresh" );
            Directory.Move( fresh, buried );

            True( await WaitUntilAsync(
                async ( ) => (await SearchAsync( catalog, "watchfresh" )).Count == 0 && WatchedList( indexer, root ) == "notes, root, web",
                TimeSpan.FromSeconds( 3 ) ) );

            Directory.Move( buried, fresh );
            await File.WriteAllTextAsync( Path.Combine( fresh, "two.txt" ), "watchreturned marker" );

            True( await WaitForCatalogResultAsync( catalog, "watchreturned", result => result.Title == "two.txt", TimeSpan.FromSeconds( 3 ) ) );
            True( await WaitForCatalogResultAsync( catalog, "watchfresh", result => result.Title == "one.txt", TimeSpan.FromSeconds( 3 ) ) );

            Equal( "notes, notes/fresh, root, web", WatchedList( indexer, root ) );

            // Removing the project marker admits dist, which is then watched.
            File.Delete( Path.Combine( web, ProjectMarkerNames.PackageJson ) );

            True( await WaitUntilAsync( ( ) => Task.FromResult( WatchedList( indexer, root ) == "notes, notes/fresh, root, web, web/dist" ),
                TimeSpan.FromSeconds( 3 ) ) );

            await File.WriteAllTextAsync( Path.Combine( web, "dist", "bundle.txt" ), "watchadmitted marker" );

            True( await WaitForCatalogResultAsync( catalog, "watchadmitted", result => result.Title == "bundle.txt", TimeSpan.FromSeconds( 3 ) ) );
        }
        finally
        {
            DeleteIncrementalSpecRoot( sandbox );
        }

        static string WatchedList( IndexCoordinator indexer, string root ) => string.Join( ", ", indexer
            .SnapshotWatchedDirectories( )
            .Select( path => path == root ? "root" : Path.GetRelativePath( root, path ) )
            .Order( StringComparer.Ordinal )
            .Distinct( ) );
    }
}
