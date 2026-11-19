namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task RunExcludedFolderRegistrationsAsync( )
    {
        Run( "excluded folders compare whole path components", ExcludedFoldersCompareWholeComponents );
        Run( "excluded folder paths reject relative, dot, home and ancestor paths", ExcludedFolderPathsRejectUnsafeInput );
        await RunAsync( "a user excluded folder leaves search at once, survives restart, and returns when removed", UserExcludedFolderLifecycleAsync );
        await RunAsync( "a user excluded folder hides a nested indexed folder and previews", UserExclusionHidesNestedRootAsync );
        await RunAsync( "a built-in exclusion yields to an indexed folder inside it and can be turned off", BuiltInExclusionYieldsToExplicitRootAsync );
        await RunAsync( "excluded folder changes reject files, indexed folders, duplicates and unknown ids", ExcludedFolderValidationAsync );
        await RunAsync( "an import without exclusion lists keeps them and empty lists clear them", ExcludedFolderImportCompatibilityAsync );
        await RunAsync( "revealing an index issue refuses an unknown issue", RevealRefusesUnknownIssueAsync );
        await RunAsync( "an import refuses the filesystem root, home, files and expanded tokens", ExcludedFolderImportRejectsUnsafePathsAsync );
        await RunAsync( "a user excluded folder hides a linked indexed folder in its resolved form", UserExclusionHidesLinkedRootAsync );
    }

    private static void ExcludedFoldersCompareWholeComponents( )
    {
        True( ExcludedFolderPaths.IsSameOrDescendant( "/a/b", "/a/b" ) );
        True( ExcludedFolderPaths.IsSameOrDescendant( "/a/b", "/a/b/c" ) );
        True( !ExcludedFolderPaths.IsSameOrDescendant( "/a/b", "/a/bc" ) );
        True( !ExcludedFolderPaths.IsSameOrDescendant( "/a/b", "/a" ) );
        True( ExcludedFolderPaths.IsSameOrDescendant( "/", "/a" ) );
    }

    private static void ExcludedFolderPathsRejectUnsafeInput( )
    {
        const string home = "/home/spec";

        Equal( "/home/spec/.cache", ExcludedFolderPaths.Normalize( "~/.cache/", home ) );
        Equal( "/srv/[data]*", ExcludedFolderPaths.Normalize( "/srv/[data]*", home ) );

        foreach ( var value in new[] { "", "relative/path", "~", "/home/spec/../other", "/home/spec/./x", "/", "/home", "/home/spec", "~/" } )
        {
            Throws<InvalidOperationException>( ( ) => ExcludedFolderPaths.Normalize( value, home ) );
        }

        Throws<InvalidOperationException>( ( ) => ExcludedFolderPaths.Normalize( "/" + new string( 'a', 600 ), home ) );
    }

    private static async Task UserExcludedFolderLifecycleAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var skipped = Directory.CreateDirectory( Path.Combine( files, "skipped" ) ).FullName;
            var kept = Directory.CreateDirectory( Path.Combine( files, "kept" ) ).FullName;
            await File.WriteAllTextAsync( Path.Combine( skipped, "skippedrow.txt" ), "skippedsignal" );
            await File.WriteAllTextAsync( Path.Combine( kept, "keptrow.txt" ), "keptsignal" );
            await File.WriteAllTextAsync( Path.Combine( files, "skippedish.txt" ), "neighbour" );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );
            string id;

            await using ( SqliteCatalog catalog = new( options.Storage ) )
            await using ( IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences ) )
            {
                await catalog.InitializeAsync( default );
                await indexer.ReindexAsync( null, default );
                Equal( 1, (await SearchAsync( catalog, "skippedrow" )).Count );

                var added = await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionAdd, skipped, null, default );

                id = added.Exclusions.Single( value => value.Source == "user" ).Id;
                Equal( 0, (await SearchAsync( catalog, "skippedrow" )).Count );
                await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
                Equal( 0, (await SearchAsync( catalog, "skippedrow" )).Count );
                Equal( 1, (await SearchAsync( catalog, "keptrow" )).Count );
                Equal( 1, (await SearchAsync( catalog, "skippedish" )).Count );

                var inspection = await indexer.InspectAsync( default );
                True( inspection.ExclusionDiagnostics.Any( value => value.Reason == "excluded-folder" && value.Rule == skipped ) );

                var edited = await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionUpdate, kept, id, default );

                Equal( id, edited.Exclusions.Single( value => value.Source == "user" ).Id );
                await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
                Equal( 1, (await SearchAsync( catalog, "skippedrow" )).Count );
                Equal( 0, (await SearchAsync( catalog, "keptrow" )).Count );
            }

            await using SqliteCatalog reopened = new( options.Storage );
            await reopened.InitializeAsync( default );
            await using IndexCoordinator restarted = new( reopened, reopened, options, rootPreferences: reopened.LauncherPreferences );

            var listed = await restarted.ManageRootsAsync( ProtocolOperations.IndexRoots, null, null, null, default );

            Equal( kept, listed.Exclusions.Single( value => value.Source == "user" ).Path );
            await restarted.ManageExclusionsAsync( ProtocolOperations.IndexExclusionRemove, null, id, default );
            await restarted.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 1, (await SearchAsync( reopened, "keptrow" )).Count );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task UserExclusionHidesNestedRootAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var work = Directory.CreateDirectory( Path.Combine( files, "work" ) ).FullName;
            var project = Directory.CreateDirectory( Path.Combine( work, "project" ) ).FullName;
            var file = Path.Combine( project, "projectrow.txt" );
            await File.WriteAllTextAsync( file, "projectsignal" );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            FilePreviewService previews = new( options.Indexing, options.Privacy, folders: indexer.FolderRules );

            await indexer.ManageRootsAsync( ProtocolOperations.IndexAdd, project, null, null, default );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 1, (await SearchAsync( catalog, "projectrow" )).Count );
            True( (await previews.ReadAsync( file, default )).Succeeded );

            var result = await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionAdd, work, null, default );

            Equal( 0, (await SearchAsync( catalog, "projectrow" )).Count );
            True( !(await previews.ReadAsync( file, default )).Succeeded );
            True( result.Exclusions.Single( value => value.Source == "user" ).Issue!.Contains( project, StringComparison.Ordinal ) );
            True( result.Roots.Single( value => value.Path == project ).Issue!.StartsWith( "Inside excluded folder", StringComparison.Ordinal ) );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 0, (await SearchAsync( catalog, "projectrow" )).Count );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task BuiltInExclusionYieldsToExplicitRootAsync( )
    {
        var root = CreateCliSpecRoot( );
        var previous = Environment.GetEnvironmentVariable( "XDG_CACHE_HOME" );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var cache = Directory.CreateDirectory( Path.Combine( files, "cache" ) ).FullName;
            var tool = Directory.CreateDirectory( Path.Combine( cache, "tool" ) ).FullName;
            var chosen = Directory.CreateDirectory( Path.Combine( cache, "chosen" ) ).FullName;
            await File.WriteAllTextAsync( Path.Combine( tool, "cacherow.txt" ), "cachesignal" );
            await File.WriteAllTextAsync( Path.Combine( chosen, "chosenrow.txt" ), "chosensignal" );
            Environment.SetEnvironmentVariable( "XDG_CACHE_HOME", cache );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );

            await indexer.ManageRootsAsync( ProtocolOperations.IndexAdd, chosen, null, null, default );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 0, (await SearchAsync( catalog, "cacherow" )).Count );
            Equal( 1, (await SearchAsync( catalog, "chosenrow" )).Count );

            var disabled = await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionDisable, null, "xdg-cache", default );

            True( !disabled.Exclusions.Single( value => value.Id == "xdg-cache" ).Enabled );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 1, (await SearchAsync( catalog, "cacherow" )).Count );

            await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionEnable, null, "xdg-cache", default );
            Equal( 0, (await SearchAsync( catalog, "cacherow" )).Count );
            Equal( 1, (await SearchAsync( catalog, "chosenrow" )).Count );
        }
        finally
        {
            Environment.SetEnvironmentVariable( "XDG_CACHE_HOME", previous );
            Directory.Delete( root, true );
        }
    }

    private static async Task ExcludedFolderValidationAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var folder = Directory.CreateDirectory( Path.Combine( files, "folder" ) ).FullName;
            var file = Path.Combine( files, "file.txt" );
            await File.WriteAllTextAsync( file, "x" );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );

            Task Change( string operation, string? path, string? id ) => indexer.ManageExclusionsAsync( operation, path, id, default ).AsTask( );

            await ThrowsAsync<InvalidOperationException>( ( ) => Change( ProtocolOperations.IndexExclusionAdd, file, null ) );
            await ThrowsAsync<InvalidOperationException>( ( ) => Change( ProtocolOperations.IndexExclusionAdd, files, null ) );
            await ThrowsAsync<InvalidOperationException>( ( ) => Change( ProtocolOperations.IndexExclusionAdd, "folder", null ) );
            await ThrowsAsync<InvalidOperationException>( ( ) => Change( ProtocolOperations.IndexExclusionRemove, null, "nope" ) );
            await ThrowsAsync<InvalidOperationException>( ( ) => Change( ProtocolOperations.IndexExclusionEnable, null, "nope" ) );

            var missing = Path.Combine( files, "later" );
            var added = await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionAdd, folder, null, default );
            await ThrowsAsync<InvalidOperationException>( ( ) => Change( ProtocolOperations.IndexExclusionAdd, folder, null ) );
            var later = await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionAdd, missing, null, default );

            Equal( 1, added.Exclusions.Count( value => value.Source == "user" ) );
            True( !later.Exclusions.Single( value => value.Path == missing ).Exists );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task ExcludedFolderImportCompatibilityAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var folder = Directory.CreateDirectory( Path.Combine( files, "folder" ) ).FullName;
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionAdd, folder, null, default );
            await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionDisable, null, "xdg-cache", default );

            var kept = await indexer.ManageRootsAsync( ProtocolOperations.IndexRootsImport, null, null, new IndexRootPreferences( ), default );

            Equal( folder, kept.Exclusions.Single( value => value.Source == "user" ).Path );
            True( !kept.Exclusions.Single( value => value.Id == "xdg-cache" ).Enabled );

            var cleared = await indexer.ManageRootsAsync( ProtocolOperations.IndexRootsImport, null, null,
                new IndexRootPreferences { ExcludedFolders = [], BuiltInExclusionOverrides = [] }, default );

            Equal( 0, cleared.Exclusions.Count( value => value.Source == "user" ) );
            True( cleared.Exclusions.Single( value => value.Id == "xdg-cache" ).Enabled );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task ExcludedFolderImportRejectsUnsafePathsAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var file = Path.Combine( files, "file.txt" );
            await File.WriteAllTextAsync( file, "x" );
            var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );

            foreach ( var path in new[] { "/", home, file, files + "/../files", files + "/$HOME" } )
            {
                IndexRootPreferences import = new( ) { ExcludedFolders = [new ExcludedFolder { Id = "imported", Path = path }] };

                if ( path.EndsWith( "$HOME", StringComparison.Ordinal ) )
                {
                    var kept = await indexer.ManageRootsAsync( ProtocolOperations.IndexRootsImport, null, null, import, default );

                    Equal( path, kept.Exclusions.Single( value => value.Source == "user" ).Path );
                    continue;
                }

                await ThrowsAsync<InvalidOperationException>( ( ) => indexer
                    .ManageRootsAsync( ProtocolOperations.IndexRootsImport, null, null, import, default )
                    .AsTask( ) );
            }

            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task UserExclusionHidesLinkedRootAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var hidden = Directory.CreateDirectory( Path.Combine( files, "private" ) ).FullName;
            var target = Directory.CreateDirectory( Path.Combine( root, "elsewhere" ) ).FullName;
            var file = Path.Combine( target, "linkedrow.txt" );
            await File.WriteAllTextAsync( file, "linkedsignal" );
            var link = Path.Combine( hidden, "link" );
            Directory.CreateSymbolicLink( link, target );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            FilePreviewService previews = new( options.Indexing, options.Privacy, folders: indexer.FolderRules );

            await indexer.ManageRootsAsync( ProtocolOperations.IndexAdd, link, null, null, default );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 1, (await SearchAsync( catalog, "linkedrow" )).Count );
            True( (await previews.ReadAsync( file, default )).Succeeded );

            await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionAdd, hidden, null, default );

            Equal( 0, (await SearchAsync( catalog, "linkedrow" )).Count );
            True( !(await previews.ReadAsync( file, default )).Succeeded, "the resolved path is hidden too" );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 0, (await SearchAsync( catalog, "linkedrow" )).Count );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task RevealRefusesUnknownIssueAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await indexer.RevealIndexIssueAsync( "index-work:000000000000000000000000", default ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }
}
