namespace CommandDeck.Specs;

public static partial class Program
{
    private static readonly string[] LocationVariables =
    [
        EnvironmentVariableNames.Home, "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_CONFIG_HOME", EnvironmentVariableNames.CodexHome,
        EnvironmentVariableNames.ClaudeConfigDirectory, EnvironmentVariableNames.CargoHome, EnvironmentVariableNames.GoPath,
        EnvironmentVariableNames.GoModuleCache, EnvironmentVariableNames.CondaPackageDirectories
    ];

    private static async Task RunExcludedLocationRegistrationsAsync( )
    {
        Run( "every built-in exclusion can be switched against its default", EveryBuiltInCanBeSwitched );
        Run( "a named folder rule matches whole folders below the indexed folder", NamedFolderRuleMatchesWholeFolders );
        Run( "tool variables add folders and never the home folder", ToolVariablesAddFolders );
        await RunAsync( "built-ins cover Flatpak and Snap installs and keep your browser files", BuiltInsCoverOtherInstallsAsync );
        await RunAsync( "journals and LevelDB files are skipped only beside the real database", TransientFilesNeedTheirDatabaseAsync );
        await RunAsync( "folders tagged as caches are skipped, also when tagged later", TaggedCacheFoldersAreSkippedAsync );
        await RunAsync( "an app cache scope that resolves above home is ignored", AppCacheScopeAboveHomeIsIgnoredAsync );
    }

    private static void EveryBuiltInCanBeSwitched( )
    {
        IndexRootPreferences preferences = new( )
        {
            ExcludedFolders = [],
            BuiltInExclusionOverrides =
            [
                .. BuiltInExclusions.All.Select( static value => new BuiltInExclusionOverride { Id = value.Id, Enabled = !value.EnabledByDefault } )
            ]
        };

        // Validation used to cap the settings below the number of built-ins.
        True( BuiltInExclusions.All.Count > 64 );
        preferences.Validate( );
    }

    private static void NamedFolderRuleMatchesWholeFolders( )
    {
        ExcludedNameRule rule = new( "claude-worktrees", "**/.claude/worktrees", [".claude", "worktrees"] );

        True( rule.Matches( "/r/p/.claude/worktrees/f", "/r", "/r", directory: true ) );
        // a file with the name is not a worktree folder.
        True( !rule.Matches( "/r/p/.claude/worktrees", "/r", "/r", directory: false ) );
        True( !rule.Matches( "/r/p/x.claude/worktrees/f", "/r", "/r", directory: true ) );
        // an indexed .claude folder still skips its worktrees.
        True( rule.Matches( "/r/p/.claude/worktrees/f", "/r/p/.claude", "/r/p/.claude", directory: true ) );
        // an indexed worktrees folder, or a worktree, is indexed.
        True( !rule.Matches( "/r/p/.claude/worktrees", "/r/p/.claude/worktrees", "/r/p/.claude/worktrees", directory: true ) );
        True( !rule.Matches( "/r/p/.claude/worktrees/f/a", "/r/p/.claude/worktrees/f", "/r/p/.claude/worktrees/f", directory: true ) );
        True( rule.Matches( "/.claude/worktrees/f", "/", "/", directory: true ) );
    }

    private static void ToolVariablesAddFolders( )
    {
        const string home = "/home/spec";
        var saved = SaveVariables( );

        try
        {
            Environment.SetEnvironmentVariable( EnvironmentVariableNames.GoModuleCache, "/srv/gomod" );
            Environment.SetEnvironmentVariable( EnvironmentVariableNames.GoPath, "/a/go:relative/go:/b/go" );
            Environment.SetEnvironmentVariable( EnvironmentVariableNames.CondaPackageDirectories, "/x/pkgs,/y/pkgs" );
            Environment.SetEnvironmentVariable( EnvironmentVariableNames.CargoHome, home );

            var go = BuiltInExclusions.ResolveAll( BuiltInExclusions.Find( BuiltInExclusionIds.GoModules )!, home );
            Equal( "/home/spec/go/pkg/mod|/srv/gomod|/a/go/pkg/mod|/b/go/pkg/mod", string.Join( '|', go ) );

            var conda = BuiltInExclusions.ResolveAll( BuiltInExclusions.Find( BuiltInExclusionIds.CondaPackages )!, home );
            True( conda.Contains( "/x/pkgs" ) && conda.Contains( "/y/pkgs" ) && conda.Contains( "/home/spec/miniconda3/pkgs" ) );

            // CARGO_HOME=home gives home/registry, a folder below home.
            var cargo = BuiltInExclusions.ResolveAll( BuiltInExclusions.Find( BuiltInExclusionIds.CargoRegistry )!, home );
            Equal( "/home/spec/.cargo/registry|/home/spec/registry", string.Join( '|', cargo ) );

            Environment.SetEnvironmentVariable( EnvironmentVariableNames.GoModuleCache, "/home" );
            go = BuiltInExclusions.ResolveAll( BuiltInExclusions.Find( BuiltInExclusionIds.GoModules )!, home );
            True( !go.Contains( "/home" ) );
        }
        finally
        {
            RestoreVariables( saved );
        }
    }

    private static async Task BuiltInsCoverOtherInstallsAsync( )
    {
        var root = CreateCliSpecRoot( );
        var saved = SaveVariables( );

        try
        {
            var home = Directory.CreateDirectory( Path.Combine( root, "home" ) ).FullName;
            ClearLocationVariables( home );
            Environment.SetEnvironmentVariable( EnvironmentVariableNames.CargoHome, Path.Combine( home, "tools", "cargo" ) );

            await WriteMarkerAsync( home, ".var/app/com.example.App/cache/flatcachemarker.bin" );
            await WriteMarkerAsync( home, ".var/app/com.example.App/data/notes/cache/flatnotemarker.md" );
            await WriteMarkerAsync( home, ".var/app/com.example.App/config/Electron/GPUCache/flatgpumarker.bin" );
            await WriteMarkerAsync( home, ".var/app/com.slack.Slack/config/Slack/flatslackmarker.json" );
            await WriteMarkerAsync( home, "snap/firefox/common/.mozilla/firefox/abc.default/chrome/snapchromemarker.css" );
            await WriteMarkerAsync( home, "snap/firefox/common/.mozilla/firefox/abc.default/snapplacesmarker.sqlite" );
            await WriteMarkerAsync( home, "snap/someapp/12/.cache/snapcachemarker.bin" );
            await WriteMarkerAsync( home, ".mozilla/firefox/abc.default/user.js" );
            await WriteMarkerAsync( home, ".mozilla/firefox/abc.default/storage/profilemarker.bin" );
            await WriteMarkerAsync( home, ".mozilla/firefox/profiles.ini" );
            await WriteMarkerAsync( home, "tools/cargo/registry/cratemarker.rs" );
            await WriteMarkerAsync( home, ".local/share/flatpak/app/flatpakappmarker.bin" );
            await WriteMarkerAsync( home, ".local/share/flatpak/overrides/flatpakoverridemarker" );
            await WriteMarkerAsync( home, ".claude/themes/claudethememarker.json" );

            var options = DeveloperIndexOptions( Path.Combine( root, "catalog.db" ), home, incremental: false );
            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ReindexAsync( null, default );

            foreach ( var hidden in new[] { "flatcachemarker", "flatgpumarker", "flatslackmarker", "snapplacesmarker", "snapcachemarker", "profilemarker",
                         "profiles.ini", "cratemarker", "flatpakappmarker" } )
            {
                Equal( 0, (await SearchAsync( catalog, hidden )).Count );
            }

            foreach ( var shown in new[] { "flatnotemarker", "snapchromemarker", "user.js", "flatpakoverridemarker", "claudethememarker" } )
            {
                True( (await SearchAsync( catalog, shown )).Count >= 1 );
            }

            var listed = (await indexer.ManageRootsAsync( ProtocolOperations.IndexRoots, null, null, null, default )).Exclusions;
            True( listed.All( value => value.Id != "nuget-v3-cache" ) );
            True( listed.Single( value => value.Id == BuiltInExclusionIds.AppCaches ).Description!.Contains( "GCM Store", StringComparison.Ordinal ) );
            True( listed.Single( value => value.Id == BuiltInExclusionIds.Flatpak ).Enabled );
            var slack = listed.Single( value => value.Id == BuiltInExclusionIds.SlackData );
            // the Flatpak install is the one on this computer.
            Equal( Path.Combine( home, ".var/app/com.slack.Slack" ), slack.Path );
            True( slack.Exists );
        }
        finally
        {
            RestoreVariables( saved );
            Directory.Delete( root, true );
        }
    }

    private static async Task TransientFilesNeedTheirDatabaseAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            await File.WriteAllTextAsync( Path.Combine( files, "notesmarker" ), "my notes" );
            await File.WriteAllTextAsync( Path.Combine( files, "notesmarker-journal" ), "my journal" );
            await WriteMarkerAsync( files, "level/CURRENT", "MANIFEST-000004\n" );
            await WriteMarkerAsync( files, "level/MANIFEST-000004", "m" );
            await WriteMarkerAsync( files, "level/000005.log" );
            await WriteMarkerAsync( files, "other/CURRENT", "the current plan" );
            await WriteMarkerAsync( files, "other/MANIFEST-000001", "m" );
            await WriteMarkerAsync( files, "other/000007.ldb" );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );
            options.Indexing.Roots = [new IndexRootOptions { Path = files, IncludeHidden = true, IncludeContentInSearch = true }];

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ReindexAsync( null, default );

            // notesmarker is not a database, so its -journal is your file.
            Equal( 2, (await SearchAsync( catalog, "notesmarker" )).Count );
            Equal( 0, (await SearchAsync( catalog, "000005.log" )).Count );
            // CURRENT must name a manifest for the folder to be LevelDB.
            Equal( 1, (await SearchAsync( catalog, "000007.ldb" )).Count );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task TaggedCacheFoldersAreSkippedAsync( )
    {
        var root = CreateIncrementalSpecRoot( );
        const string tag = "Signature: 8a477f597d28d172789f06886806bc55\n# This file is a cache directory tag.\n";

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            await WriteMarkerAsync( files, "tool/cache/CACHEDIR.TAG", tag );
            await WriteMarkerAsync( files, "tool/cache/blobs/tagcachemarker.bin" );
            await WriteMarkerAsync( files, "tool/fake/CACHEDIR.TAG", "not a tag" );
            await WriteMarkerAsync( files, "tool/fake/faketagmarker.bin" );
            await WriteMarkerAsync( files, "later/latetagmarker.txt" );
            var options = DeveloperIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ReindexAsync( null, default );

            Equal( 0, (await SearchAsync( catalog, "tagcachemarker" )).Count );
            Equal( 1, (await SearchAsync( catalog, "faketagmarker" )).Count );
            Equal( 1, (await SearchAsync( catalog, "latetagmarker" )).Count );

            await File.WriteAllTextAsync( Path.Combine( files, "later", "CACHEDIR.TAG" ), tag );
            True( await WaitUntilAsync( async ( ) => (await SearchAsync( catalog, "latetagmarker" )).Count == 0, TimeSpan.FromSeconds( 5 ) ) );

            await indexer.ManageExclusionsAsync( ProtocolOperations.IndexExclusionDisable, null, BuiltInExclusionIds.CacheDirectoryTags, default );
            await indexer.BackgroundReindex.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Equal( 1, (await SearchAsync( catalog, "tagcachemarker" )).Count );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task AppCacheScopeAboveHomeIsIgnoredAsync( )
    {
        var root = CreateCliSpecRoot( );
        var saved = SaveVariables( );

        try
        {
            var files = Directory.CreateDirectory( Path.Combine( root, "files" ) ).FullName;
            await WriteMarkerAsync( files, "notes/Cache/linkednotemarker.md" );
            var link = Path.Combine( root, "config-link" );
            File.CreateSymbolicLink( link, "/" );
            Environment.SetEnvironmentVariable( "XDG_CONFIG_HOME", link );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), files );

            await using SqliteCatalog catalog = new( options.Storage );
            await using IndexCoordinator indexer = new( catalog, catalog, options, rootPreferences: catalog.LauncherPreferences );
            await catalog.InitializeAsync( default );
            await indexer.ReindexAsync( null, default );

            // ~/.config resolves to /, so every folder would be an app folder.
            Equal( 1, (await SearchAsync( catalog, "linkednotemarker" )).Count );
        }
        finally
        {
            RestoreVariables( saved );
            Directory.Delete( root, true );
        }
    }

    private static async Task WriteMarkerAsync( string root, string relativePath, string? content = null )
    {
        var path = Path.Combine( root, relativePath );
        Directory.CreateDirectory( Path.GetDirectoryName( path )! );
        await File.WriteAllTextAsync( path, content ?? "marker" );
    }

    private static void ClearLocationVariables( string home )
    {
        foreach ( var name in LocationVariables )
        {
            Environment.SetEnvironmentVariable( name, null );
        }

        Environment.SetEnvironmentVariable( EnvironmentVariableNames.Home, home );
    }

    private static Dictionary<string, string?> SaveVariables( ) =>
        LocationVariables.ToDictionary( static name => name, static name => Environment.GetEnvironmentVariable( name ) );

    private static void RestoreVariables( Dictionary<string, string?> saved )
    {
        foreach ( var (name, value) in saved )
        {
            Environment.SetEnvironmentVariable( name, value );
        }
    }
}
