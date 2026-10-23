namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task HerdrActionExecutesFocusAsync( )
    {
        var results = await SearchHerdrAsync( "herdr: " );
        var tab = results.Single( result => result.Id == "herdr:tab:w1:t2" );
        var space = results.Single( result => result.Id == "herdr:workspace:w2" );
        HerdrFake fake = new( ) { Respond = args => RespondToHerdrCommand( args ) };
        LinuxActionExecutor executor = new( herdr: new HerdrClient( fake.Runner, static ( ) => true ) );
        var switched = await executor.ExecuteAsync( tab, tab.Actions[0], "", CancellationToken.None );
        True( switched.Succeeded, switched.Message );
        Equal( "--session default tab focus w1:t2", string.Join( ' ', fake.Calls.Single( ).Args ) );

        var switchedSpace = await executor.ExecuteAsync( space, space.Actions[0], "", CancellationToken.None );

        True( switchedSpace.Succeeded, switchedSpace.Message );
        Equal( "--session default workspace focus w2", string.Join( ' ', fake.Calls[1].Args ) );

        // Forged arguments never reach the client.
        CommandAction forged = CreateForged( tab );
        True( !(await executor.ExecuteAsync( tab, forged, "", CancellationToken.None )).Succeeded );
        CommandAction wrongKind = CreateWrongKind( );
        True( !(await executor.ExecuteAsync( tab, wrongKind, "", CancellationToken.None )).Succeeded );
        SearchResult foreign = new( tab ) { SourceId = SourceIds.Files, Kind = ResultKinds.File };

        True( !(await executor.ExecuteAsync( foreign, tab.Actions[0], "", CancellationToken.None )).Succeeded );
        True( !(await executor.ExecuteAsync( tab, new CommandAction { Id = "x", Title = "x", Kind = "herdr-focus" }, "", CancellationToken.None )).Succeeded );

        Equal( 2, fake.Calls.Count );

        True( !(await new LinuxActionExecutor( ).ExecuteAsync( tab, tab.Actions[0], "", CancellationToken.None )).Succeeded );

        HerdrFake stale = new( )
        {
            Respond = _ => HerdrResult( "", 1, """{"error":{"code":"tab_not_found","message":"tab w1:t2 not found"},"id":"cli:tab:focus"}""" )
        };

        var failed = await new LinuxActionExecutor( herdr: new HerdrClient( stale.Runner, static ( ) => true ) ).ExecuteAsync( tab, tab.Actions[0], "",
            CancellationToken.None );

        True( !failed.Succeeded );
        Equal( "That Herdr tab no longer exists. Search again.", failed.Message );
        Equal( 1, stale.Calls.Count );

        static CommandAction CreateForged( SearchResult tab )
        {
            return new CommandAction( )
            {
                Id = tab.Actions[0].Id,
                Title = tab.Actions[0].Title,
                Kind = ActionKinds.HerdrFocus,
                Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                {
                    [ActionArgumentNames.TargetKind] = HerdrTargetKinds.Tab,
                    [ActionArgumentNames.TargetId] = "w9:t9"
                }
            };
        }

        static CommandAction CreateWrongKind( )
        {
            return new CommandAction( )
            {
                Id = "x",
                Title = "x",
                Kind = ActionKinds.HerdrFocus,
                Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                {
                    [ActionArgumentNames.TargetKind] = HerdrTargetKinds.Workspace,
                    [ActionArgumentNames.TargetId] = "w1:t2"
                }
            };
        }

        static ProcessResult RespondToHerdrCommand( IReadOnlyList<string> args )
        {
            if ( args[2] == "tab" )
            {
                return HerdrResult( """{"id":"cli:tab:focus","result":{"tab":{"tab_id":"w1:t2","focused":true},"type":"tab_info"}}""" );
            }

            return HerdrResult( """{"id":"cli:workspace:focus","result":{"type":"workspace_info","workspace":{"workspace_id":"w2","focused":true}}}""" );
        }
    }

    private static void HerdrFocusRiskClassification( )
    {
        var results = SearchHerdrAsync( "herdr: " )
            .GetAwaiter( )
            .GetResult( );

        var tab = results.Single( result => result.Id == "herdr:tab:w1:t1" );
        var space = results.Single( result => result.Id == "herdr:workspace:w1" );
        Equal( "immediate", AgentLauncherTools.ClassifyRisk( tab, tab.Actions[0] ) );
        Equal( "immediate", AgentLauncherTools.ClassifyRisk( space, space.Actions[0] ) );
        SearchResult forgedFile = new( tab ) { SourceId = SourceIds.Files };
        Equal( "confirm", AgentLauncherTools.ClassifyRisk( forgedFile, tab.Actions[0] ) );
        SearchResult forgedKind = new( tab ) { Kind = ResultKinds.File };
        Equal( "confirm", AgentLauncherTools.ClassifyRisk( forgedKind, tab.Actions[0] ) );
    }

    private static async Task HerdrPreferenceMergesAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-herdr-prefs-{Guid.NewGuid( ):N}.db" );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( CancellationToken.None );

            // A stored preference set from before Herdr existed, with custom order.
            await using ( var connection = await catalog.OpenAsync( CancellationToken.None ) )
            await using ( var seed = connection.CreateCommand( ) )
            {
                seed.CommandText = """
                                   INSERT INTO source_preferences(source_id, enabled_in_search, enabled_on_home, position, updated_unix_ms)
                                   VALUES ('windows-workspaces', 0, 0, 1, 0),
                                          ('applications', 1, 1, 2, 0),
                                          ('files', 1, 0, 3, 0);
                                   """;

                await seed.ExecuteNonQueryAsync( );
            }

            var preferences = await catalog.GetPreferencesAsync( CancellationToken.None );

            // Stored toggles and relative order survive; positions are resequenced.
            var windows = preferences.Sources.Single( source => source.SourceId == "windows-workspaces" );
            True( windows is { EnabledInSearch: false, EnabledOnHome: false } );
            var applications = preferences.Sources.Single( source => source.SourceId == "applications" );
            var files = preferences.Sources.Single( source => source.SourceId == "files" );
            True( windows.Position < applications.Position && applications.Position < files.Position );
            True( applications.EnabledOnHome && !files.EnabledOnHome );
            var herdr = preferences.Sources.Single( source => source.SourceId == "herdr" );
            True( herdr is { EnabledInSearch: true, EnabledOnHome: false } );

            Equal( preferences.Sources.Count, preferences
                .Sources.Select( static source => source.Position )
                .Distinct( )
                .Count( ) );

            var defaults = LauncherPreferences.CreateDefault( );

            Equal( defaults.Sources.Count, defaults
                .Sources.Select( static source => source.Position )
                .Distinct( )
                .Count( ) );

            Equal( defaults.Sources.Single( static source => source.SourceId == "windows-workspaces" )
                       .Position +
                   1, defaults.Sources.Single( static source => source.SourceId == "herdr" ).Position );

            Equal( "Herdr", LauncherSourceRegistry.Label( "herdr" ) );
            True( SourceIds.All.Contains( "herdr" ) );

            Equal( "herdr", QueryParser
                .Parse( "herdr: x" )
                .IncludedFilters.Single( ).Id );

            Equal( "herdr-spaces", QueryParser
                .Parse( "hspaces: x" )
                .IncludedFilters.Single( ).Id );

            Equal( "herdr-tabs", QueryParser
                .Parse( "htab: x" )
                .IncludedFilters.Single( ).Id );

            Equal( "herdr-tabs", QueryParser
                .Parse( "-herdr-tab: x" )
                .ExcludedFilters.Single( ).Id );
        }
        finally
        {
            DeleteDatabase( database );
        }
    }

    private static async Task SetHerdrPreferenceAsync( SqliteCatalog catalog, bool enabledInSearch, bool enabledOnHome )
    {
        var current = await catalog.GetPreferencesAsync( CancellationToken.None );

        await catalog.ReplacePreferencesAsync( new LauncherPreferences
        {
            Sources = current
                .Sources.Select( source => CreateSourcePreference( enabledInSearch, enabledOnHome, source ) )
                .ToList( )
        }, CancellationToken.None );

        static SourcePreference CreateSourcePreference( bool enabledInSearch, bool enabledOnHome, SourcePreference source )
        {
            return new SourcePreference
            {
                SourceId = source.SourceId,
                EnabledInSearch = source.SourceId == SourceIds.Herdr ? enabledInSearch : source.EnabledInSearch,
                EnabledOnHome = source.SourceId == SourceIds.Herdr ? enabledOnHome : source.EnabledOnHome,
                Position = source.Position
            };
        }
    }
}
