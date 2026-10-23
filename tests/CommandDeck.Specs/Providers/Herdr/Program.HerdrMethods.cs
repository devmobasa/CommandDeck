namespace CommandDeck.Specs;

public static partial class Program
{
    // Fixtures are Herdr 0.9.0 shaped: extra and unknown fields, one record with
    // a control character in its id, one non-object entry, an orphan tab, and a
    // tab without an id. Labels are invented.
    private const string HerdrStatusRunningJson = """
                                                  {"status":"running","running":true,"version":"0.9.0","protocol":22,"capabilities":{"live_handoff":true},
                                                   "compatible":true,"endpoint_compatible":true,"socket":"/tmp/herdr.sock","session":null,"restart_needed":false}
                                                  """;

    private const string HerdrStatusStoppedJson = """
                                                  {"status":"not_running","running":false,"version":null,"protocol":null,"compatible":null,"endpoint_compatible":null,"socket":"/tmp/herdr.sock"}
                                                  """;

    private const string HerdrWorkspaceListJson = """
                                                  {"id":"cli:workspace:list","result":{"type":"workspace_list","workspaces":[
                                                    {"workspace_id":"w1","number":1,"label":"CommandDeck","focused":true,"pane_count":5,"tab_count":2,"active_tab_id":"w1:t1","agent_status":"working","worktree":{"repo_name":"commanddeck"}},
                                                    {"workspace_id":"w2","number":2,"label":"Notes","focused":false,"pane_count":1,"tab_count":2,"agent_status":"idle"},
                                                    {"workspace_id":"w3","number":3,"label":"Notes","focused":false,"pane_count":1,"tab_count":0},
                                                    {"workspace_id":"bad\u0001id","number":4,"label":"Broken"},
                                                    "not-an-object"
                                                  ]}}
                                                  """;

    private const string HerdrTabListJson = """
                                            {"id":"cli:tab:list","result":{"tabs":[
                                              {"tab_id":"w1:t1","workspace_id":"w1","number":1,"label":"tests","focused":true,"pane_count":2,"agent_status":"idle"},
                                              {"tab_id":"w1:t2","workspace_id":"w1","number":2,"label":"release","focused":false,"pane_count":1},
                                              {"tab_id":"w2:t1","workspace_id":"w2","number":1,"label":"scratch","focused":false,"pane_count":1},
                                              {"tab_id":"w2:t2","workspace_id":"w2","number":2,"label":"commanddeck","focused":false,"pane_count":1},
                                              {"tab_id":"gone:t1","workspace_id":"gone","number":1,"label":"orphan","focused":false,"pane_count":3},
                                              {"tab_id":"","workspace_id":"w1","number":9,"label":"invalid"}
                                            ],"type":"tab_list"}}
                                            """;

    private static async Task HerdrProviderIsGatedAsync( )
    {
        HerdrFake fake = new( );
        HerdrProvider provider = new( new HerdrClient( fake.Runner, static ( ) => true ) );
        SearchRequest disabled = new( ) { Query = "tests", Limit = 20, EnabledSearchSources = [SourceIds.Files] };
        SearchRequest enabled = new( ) { Query = "tests", Limit = 20, EnabledSearchSources = [SourceIds.Herdr] };

        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "tests" ), disabled, CancellationToken.None )).Count );
        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "herdr: tests" ), disabled, CancellationToken.None )).Count );
        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "file: tests" ), enabled, CancellationToken.None )).Count );
        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "-herdr: tests" ), enabled, CancellationToken.None )).Count );
        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "-hspace: -htab: tests" ), enabled, CancellationToken.None )).Count );
        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "-ws: " ), enabled, CancellationToken.None )).Count );
        Equal( 0, fake.Calls.Count );
        HerdrProvider missing = new( new HerdrClient( fake.Runner, static ( ) => false ) );

        Equal( 0, (await missing.SearchAsync( QueryParser.Parse( "tests" ), enabled, CancellationToken.None )).Count );
        Equal( 0, fake.Calls.Count );
        using CancellationTokenSource cancelled = new( );
        cancelled.Cancel( );

        await ThrowsAsync<OperationCanceledException>( async ( ) => await provider.SearchAsync( QueryParser.Parse( "tests" ), enabled, cancelled.Token ) );

        Equal( 0, fake.Calls.Count );
    }

    private static async Task HerdrProviderListsConcurrentlyAsync( )
    {
        TaskCompletionSource<ProcessResult> workspaces = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource<ProcessResult> tabs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        var started = 0;

        BoundedProcessRunner runner = ( _, args, _, _, _ ) =>
        {
            Interlocked.Increment( ref started );

            return new ValueTask<ProcessResult>( args[2] == "workspace" ? workspaces.Task : tabs.Task );
        };

        HerdrProvider provider = new( new HerdrClient( runner, static ( ) => true ) );

        var search = provider
            .SearchAsync( QueryParser.Parse( "tests" ), new SearchRequest { Query = "tests", Limit = 20, EnabledSearchSources = [SourceIds.Herdr] },
                CancellationToken.None )
            .AsTask( );

        await Task.Delay( 50 );
        Equal( 2, started );
        True( !search.IsCompleted );
        workspaces.SetResult( HerdrResult( HerdrWorkspaceListJson ) );
        tabs.SetResult( HerdrResult( HerdrTabListJson ) );
        var results = await search.WaitAsync( TimeSpan.FromSeconds( 5 ) );
        Equal( "herdr:tab:w1:t1", results.Single( ).Id );
    }

    private static async Task HerdrProviderProjectsInventoryAsync( )
    {
        var results = await SearchHerdrAsync( "herdr: " );
        var space = results.Single( result => result.Id == "herdr:workspace:w1" );
        Equal( "workspace", space.Kind );
        Equal( "CommandDeck", space.Title );
        Equal( "Space 1 · 2 tabs · 5 panes · focused", space.Subtitle );
        Equal( "utilities-terminal", space.Icon );
        Equal( "herdr", space.SourceId );
        Equal( "herdr", space.ProviderId );

        Equal( "Number=1|Tabs=2|Panes=5|Agent status=working|Herdr ID=w1|tests=2 panes · idle|release=1 pane",
            string.Join( '|', space.Details.Select( detail => $"{detail.Label}={detail.Value}" ) ) );

        Equal( "switch-space", space.Actions[0].Id );
        Equal( 3, space.Actions.Count );
        Equal( "herdr-focus", space.Actions[0].Kind );

        Equal( "workspace", space
            .Actions[0]
            .Arguments["targetKind"] );

        Equal( "w1", space
            .Actions[0]
            .Arguments["targetId"] );

        Equal( "workspace", space.Metadata["herdrTargetKind"] );
        Equal( "w1", space.Metadata["herdrTargetId"] );
        Equal( "true", space.Metadata["herdrFocused"] );
        True( !space.Metadata.ContainsKey( "workspace" ) );
        var tab = results.Single( result => result.Id == "herdr:tab:w1:t2" );
        Equal( "tab", tab.Kind );
        Equal( "release", tab.Title );
        Equal( "In CommandDeck · Tab 2 · 1 pane", tab.Subtitle );
        Equal( "Space=CommandDeck|Number=2|Panes=1|Herdr ID=w1:t2", string.Join( '|', tab.Details.Select( detail => $"{detail.Label}={detail.Value}" ) ) );
        Equal( "switch-tab", tab.Actions[0].Id );
        Equal( 2, tab.Actions.Count );

        Equal( "tab", tab
            .Actions[0]
            .Arguments["targetKind"] );

        Equal( "w1:t2", tab
            .Actions[0]
            .Arguments["targetId"] );

        Equal( "w1", tab.Metadata["herdrWorkspaceId"] );
        Equal( "CommandDeck", tab.Metadata["herdrWorkspaceLabel"] );
        var orphan = results.Single( result => result.Id == "herdr:tab:gone:t1" );
        Equal( "In Unknown space · Tab 1 · 3 panes", orphan.Subtitle );

        // Duplicate labels stay distinct by id; the sanitized records are absent.
        Equal( 2, results.Count( result => result.Title == "Notes" ) );
        True( results.All( result => !result.Id.Contains( '' ) && result.Id != "herdr:tab:" ) );

        foreach ( var result in results )
        {
            True( result.Details.All( static detail => detail.Value.Length <= 16 * 1024 && !detail.Value.Any( char.IsControl ) ) );
        }
    }

    private static async Task HerdrProviderFiltersAndRanksAsync( )
    {
        var byParent = await SearchHerdrAsync( "commanddeck" );
        Equal( "herdr:workspace:w1", byParent[0].Id );
        var direct = byParent.Single( result => result.Id == "herdr:tab:w2:t2" );
        var viaParent = byParent.Single( result => result.Id == "herdr:tab:w1:t2" );
        True( direct.Score > viaParent.Score, "an exact tab label outranks a tab found through its space" );
        True( byParent.Any( result => result.Id == "herdr:tab:w1:t1" ) );
        True( !byParent.Any( result => result.Id == "herdr:tab:w2:t1" ) );
        var focusedFirst = await SearchHerdrAsync( "notes" );

        True( focusedFirst.Any( result => result.Id == "herdr:workspace:w2" ) && focusedFirst.Any( result => result.Id == "herdr:workspace:w3" ) );

        var spaces = await SearchHerdrAsync( "hspace: notes" );
        True( spaces.Count > 0 && spaces.All( result => result.Kind == "workspace" ) );
        var tabs = await SearchHerdrAsync( "htab: tests" );
        Equal( "herdr:tab:w1:t1", tabs.Single( ).Id );
        var withoutTabs = await SearchHerdrAsync( "-htab: commanddeck" );
        True( withoutTabs.Count > 0 && withoutTabs.All( result => result.Kind == "workspace" ) );
        var withoutSpaces = await SearchHerdrAsync( "-hspace: commanddeck" );
        True( withoutSpaces.Count > 0 && withoutSpaces.All( result => result.Kind == "tab" ) );
        Equal( 0, (await SearchHerdrAsync( "zzz-no-match" )).Count );
    }

    private static async Task HerdrProviderBrowsesDeterministicallyAsync( )
    {
        var browse = await SearchHerdrAsync( "herdr: " );

        Equal( "herdr:workspace:w1|herdr:tab:w1:t1|herdr:workspace:w2|herdr:workspace:w3|herdr:tab:w1:t2|herdr:tab:w2:t1|herdr:tab:w2:t2|herdr:tab:gone:t1",
            string.Join( '|', browse.Select( static result => result.Id ) ) );

        var limited = await SearchHerdrAsync( "herdr: ", limit: 2 );
        Equal( 2, limited.Count );

        // Oversized inventories are cut at the record caps before projection.
        var manyTabs = string.Join( ',', Enumerable
            .Range( 0, HerdrClient.MaximumTabs + 50 )
            .Select( index => $$"""{"tab_id":"w1:t{{index}}","workspace_id":"w1","number":{{index}},"label":"tab {{index}}","pane_count":1}""" ) );

        HerdrFake fake = new( ) { Respond = args => RespondToHerdrCommand( manyTabs, args ) };
        HerdrProvider provider = new( new HerdrClient( fake.Runner, static ( ) => true ) );

        var capped = await provider.SearchAsync( QueryParser.Parse( "htab: " ),
            new SearchRequest { Query = "htab: ", Limit = 2000, EnabledSearchSources = [SourceIds.Herdr] }, CancellationToken.None );

        Equal( HerdrClient.MaximumTabs, capped.Count );

        static ProcessResult RespondToHerdrCommand( string manyTabs, IReadOnlyList<string> args )
        {
            if ( args[2] == "tab" )
            {
                return HerdrResult( "{\"result\":{\"type\":\"tab_list\",\"tabs\":[" + manyTabs + "]}}" );
            }

            return HerdrResult( HerdrWorkspaceListJson );
        }
    }

    private static async Task HerdrProviderStaysSilentOnFailureAsync( )
    {
        HerdrFake tabsFail = new( ) { Respond = args => RespondToHerdrCommand( args ) };
        Equal( 0, (await SearchHerdrAsync( "commanddeck", tabsFail )).Count );

        HerdrFake malformed = new( ) { Respond = args => args[2] == "tab" ? HerdrResult( "{not json" ) : HerdrResult( HerdrWorkspaceListJson ) };

        Equal( 0, (await SearchHerdrAsync( "commanddeck", malformed )).Count );
        HerdrFake timedOut = new( ) { Respond = args => TimedOutWorkspaceResponse( args ) };
        Equal( 0, (await SearchHerdrAsync( "commanddeck", timedOut )).Count );

        static ProcessResult RespondToHerdrCommand( IReadOnlyList<string> args )
        {
            if ( args[2] == "tab" )
            {
                return HerdrResult( "", 1, """{"id":"cli:tab:list","error":{"code":"server_not_running","message":"x"}}""" );
            }

            return HerdrResult( HerdrWorkspaceListJson );
        }

        static ProcessResult TimedOutWorkspaceResponse( IReadOnlyList<string> args )
        {
            if ( args[2] == "workspace" )
            {
                return new ProcessResult
                {
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = "",
                    TimedOut = true
                };
            }

            return HerdrResult( HerdrTabListJson );
        }
    }
}
