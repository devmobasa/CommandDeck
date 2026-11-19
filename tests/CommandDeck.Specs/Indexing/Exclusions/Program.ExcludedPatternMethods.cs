namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task RunExcludedPatternRegistrationsAsync( )
    {
        await RunAsync( "a built-in agent folder keeps your config and skills and skips its state", AgentFolderKeepsYourFilesAsync );
        await RunAsync( "agent worktrees are skipped in any project unless indexed explicitly", AgentWorktreesAreSkippedAsync );
        await RunAsync( "app cache folders are skipped inside config app folders only", AppCacheFoldersAreSkippedAsync );
        await RunAsync( "transient files are skipped only beside the data they belong to", TransientFilesAreSkippedAsync );
    }

    private static async Task AgentFolderKeepsYourFilesAsync( )
    {
        var root = CreateCliSpecRoot( );
        var previous = Environment.GetEnvironmentVariable( "CODEX_HOME" );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var codex = Directory.CreateDirectory( Path.Combine( files, "codex" ) ).FullName;
            Directory.CreateDirectory( Path.Combine( codex, "skills", "myskill" ) );
            Directory.CreateDirectory( Path.Combine( codex, "sessions", "2026" ) );
            Directory.CreateDirectory( Path.Combine( codex, ".tmp", "plugins" ) );
            await File.WriteAllTextAsync( Path.Combine( codex, "config.toml" ), "model = \"x\"" );
            await File.WriteAllTextAsync( Path.Combine( codex, "skills", "myskill", "skillmarker.md" ), "skill" );
            await File.WriteAllTextAsync( Path.Combine( codex, "sessions", "2026", "sessionmarker.jsonl" ), "{}" );
            await File.WriteAllTextAsync( Path.Combine( codex, ".tmp", "plugins", "tmpmarker.md" ), "tmp" );
            await File.WriteAllTextAsync( Path.Combine( codex, "historymarker.jsonl" ), "{}" );
            Environment.SetEnvironmentVariable( "CODEX_HOME", codex );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ReindexAsync( null, default );

            Equal( 1, (await SearchAsync( catalog, "skillmarker" )).Count );
            True( (await SearchAsync( catalog, "config.toml" )).Count >= 1 );
            Equal( 0, (await SearchAsync( catalog, "sessionmarker" )).Count );
            Equal( 0, (await SearchAsync( catalog, "tmpmarker" )).Count );
            Equal( 0, (await SearchAsync( catalog, "historymarker" )).Count );

            var listed = await indexer.ManageRootsAsync( ProtocolOperations.IndexRoots, null, null, null, default );
            var codexData = listed.Exclusions.Single( value => value.Id == "codex-data" );

            Equal( codex, codexData.Path );
            True( codexData.Description!.Contains( "keeps config.toml", StringComparison.Ordinal ) );

            await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionAdd, codex, null, default );
            // your own exclusion of the folder hides its kept files too.
            Equal( 0, (await SearchAsync( catalog, "skillmarker" )).Count );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
        }
        finally
        {
            Environment.SetEnvironmentVariable( "CODEX_HOME", previous );
            Directory.Delete( root, true );
        }
    }

    private static async Task AgentWorktreesAreSkippedAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var worktree = Directory.CreateDirectory( Path.Combine( files, "project", ".claude", "worktrees", "feature" ) ).FullName;
            Directory.CreateDirectory( Path.Combine( files, "project", "src" ) );
            await File.WriteAllTextAsync( Path.Combine( worktree, "worktreemarker.txt" ), "copy" );
            await File.WriteAllTextAsync( Path.Combine( files, "project", ".claude", "settingsmarker.json" ), "{}" );
            await File.WriteAllTextAsync( Path.Combine( files, "project", "src", "projectmarker.txt" ), "real" );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );
            options.Indexing.Roots = [new IndexRootOptions { Path = files, IncludeHidden = true }];

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ReindexAsync( null, default );

            Equal( 0, (await SearchAsync( catalog, "worktreemarker" )).Count );
            Equal( 1, (await SearchAsync( catalog, "settingsmarker" )).Count );
            Equal( 1, (await SearchAsync( catalog, "projectmarker" )).Count );

            await indexer.ManageRootsAsync( ProtocolOperations.IndexAdd, worktree, null, null, default );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            // a worktree you index yourself is indexed.
            Equal( 1, (await SearchAsync( catalog, "worktreemarker" )).Count );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task AppCacheFoldersAreSkippedAsync( )
    {
        var root = CreateCliSpecRoot( );
        var previous = Environment.GetEnvironmentVariable( "XDG_CONFIG_HOME" );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var config = Directory.CreateDirectory( Path.Combine( files, "config" ) ).FullName;
            var app = Directory.CreateDirectory( Path.Combine( config, "someapp" ) ).FullName;
            Directory.CreateDirectory( Path.Combine( app, "Cache" ) );
            Directory.CreateDirectory( Path.Combine( app, "Default", "Service Worker" ) );
            Directory.CreateDirectory( Path.Combine( files, "notes", "Cache" ) );
            await File.WriteAllTextAsync( Path.Combine( app, "Cache", "cachemarker.bin" ), "x" );
            await File.WriteAllTextAsync( Path.Combine( app, "Default", "Service Worker", "workermarker.js" ), "x" );
            await File.WriteAllTextAsync( Path.Combine( app, "appsettingsmarker.json" ), "{}" );
            await File.WriteAllTextAsync( Path.Combine( files, "notes", "Cache", "notecachemarker.md" ), "mine" );
            Environment.SetEnvironmentVariable( "XDG_CONFIG_HOME", config );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ReindexAsync( null, default );

            Equal( 0, (await SearchAsync( catalog, "cachemarker" )).Count );
            Equal( 0, (await SearchAsync( catalog, "workermarker" )).Count );
            Equal( 1, (await SearchAsync( catalog, "appsettingsmarker" )).Count );
            // a Cache folder outside config app folders stays.
            Equal( 1, (await SearchAsync( catalog, "notecachemarker" )).Count );

            await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionDisable, null, "app-caches", default );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 1, (await SearchAsync( catalog, "cachemarker" )).Count );
        }
        finally
        {
            Environment.SetEnvironmentVariable( "XDG_CONFIG_HOME", previous );
            Directory.Delete( root, true );
        }
    }

    private static async Task TransientFilesAreSkippedAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            var level = Directory.CreateDirectory( Path.Combine( files, "leveldb" ) ).FullName;
            await File.WriteAllTextAsync( Path.Combine( files, "walbase.sqlite" ), "SQLite format 3\0 a database" );
            await File.WriteAllTextAsync( Path.Combine( files, "walbase.sqlite-wal" ), "wal" );
            await File.WriteAllTextAsync( Path.Combine( files, "walbase.sqlite-shm" ), "shm" );
            await File.WriteAllTextAsync( Path.Combine( files, "standalonemarker-journal" ), "a journal of mine" );
            await File.WriteAllTextAsync( Path.Combine( level, "CURRENT" ), "MANIFEST-000001" );
            await File.WriteAllTextAsync( Path.Combine( level, "MANIFEST-000001" ), "m" );
            await File.WriteAllTextAsync( Path.Combine( level, "000003.ldb" ), "t" );
            await File.WriteAllTextAsync( Path.Combine( files, ".swapmarker.txt.swp" ), "s" );
            await File.WriteAllTextAsync( Path.Combine( files, "pidmarker.pid" ), "1" );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );
            options.Indexing.Roots = [new IndexRootOptions { Path = files, IncludeHidden = true, IncludeContentInSearch = false }];

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ReindexAsync( null, default );

            // the database stays and its journals go.
            Equal( 1, (await SearchAsync( catalog, "walbase" )).Count );
            // a -journal name without its database stays.
            Equal( 1, (await SearchAsync( catalog, "standalonemarker" )).Count );
            Equal( 0, (await SearchAsync( catalog, "000003.ldb" )).Count );
            Equal( 0, (await SearchAsync( catalog, "swapmarker" )).Count );
            Equal( 0, (await SearchAsync( catalog, "pidmarker" )).Count );

            await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionDisable, null, "transient-files", default );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 3, (await SearchAsync( catalog, "walbase" )).Count );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }
}
