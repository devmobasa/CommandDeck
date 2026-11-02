namespace CommandDeck.Specs;

public static partial class Program
{
    // Herdr 0.9.1 shaped: agent list as `herdr agent list` prints it, with a
    // record whose pane id has a control character, a non-object entry, and an
    // unnamed agent with no status. Labels are invented.
    private const string HerdrAgentListJson = """
                                              {"id":"cli:agent:list","result":{"type":"agent_list","agents":[
                                                {"agent":"claude","agent_status":"working","focused":true,"pane_id":"w1:p1","revision":3,"state_change_seq":4,"tab_id":"w1:t1","terminal_id":"term_a","workspace_id":"w1"},
                                                {"agent":"codex","display_agent":"Codex","agent_status":"blocked","focused":false,"pane_id":"w1:p3","revision":1,"state_change_seq":2,"tab_id":"w1:t2","terminal_id":"term_b","workspace_id":"w1"},
                                                {"agent":"claude","agent_status":"idle","focused":false,"pane_id":"w2:p1","revision":1,"state_change_seq":1,"tab_id":"w2:t1","terminal_id":"term_c","workspace_id":"w2"},
                                                {"agent":"pi","agent_status":"done","focused":false,"pane_id":"w2:p2","revision":1,"state_change_seq":5,"tab_id":"w2:t2","terminal_id":"term_d","workspace_id":"w2"},
                                                {"pane_id":"w1:p4","tab_id":"w1:t1","workspace_id":"w1","focused":false,"revision":0},
                                                {"agent":"claude","agent_status":"working","pane_id":"bad\u0001pane","tab_id":"w1:t1","workspace_id":"w1"},
                                                "not-an-object"
                                              ]}}
                                              """;

    private static HerdrFake AgentFake( string agents = HerdrAgentListJson ) => new( )
    {
        Respond = args => args[2] switch
        {
            "workspace" => HerdrResult( HerdrWorkspaceListJson ),
            "tab" => HerdrResult( HerdrTabListJson ),
            "agent" => HerdrResult( agents ),
            _ => HerdrResult( "", 2, "usage" )
        }
    };

    private static async Task HerdrAgentsListEveryAgentByUrgencyAsync( )
    {
        var fake = AgentFake( );
        var agents = await SearchHerdrAsync( "agents:", fake );

        // Blocked first, then working, done, idle, and an unknown status last.
        Equal( "herdr:agent:w1:p3|herdr:agent:w1:p1|herdr:agent:w2:p2|herdr:agent:w2:p1|herdr:agent:w1:p4",
            string.Join( '|', agents.Select( static result => result.Id ) ) );

        True( agents.Zip( agents.Skip( 1 ) ).All( static pair => pair.First.Score > pair.Second.Score ) );
        Equal( "--session default agent list", string.Join( ' ', fake.Calls.Single( static call => call.Args[2] == "agent" ).Args ) );
        Equal( 3, fake.Calls.Count );

        var blocked = agents[0];
        Equal( "tab", blocked.Kind );
        Equal( "release", blocked.Title );
        Equal( "Codex · Needs attention · CommandDeck", blocked.Subtitle );
        Equal( "Agents", blocked.Section!.Title );
        Equal( "blocked", blocked.Metadata[HerdrProvider.AgentStatusMetadata] );
        Equal( "Codex", blocked.Metadata[HerdrProvider.AgentNameMetadata] );
        Equal( "switch-tab", blocked.Actions.Single( ).Id );
        Equal( "w1:t2", blocked.Actions.Single( ).Arguments["targetId"] );
        True( HerdrSearch.NeedsAttention( blocked ) );

        Equal( "Agent=Codex|Status=Needs attention|Space=CommandDeck|Tab=release|Herdr pane ID=w1:p3",
            string.Join( '|', blocked.Details.Select( static detail => $"{detail.Label}={detail.Value}" ) ) );

        Equal( "claude · Working · CommandDeck · focused", agents[1].Subtitle );
        Equal( "Agent · Status unknown · CommandDeck", agents[4].Subtitle );

        // Text narrows by agent, tab, or space name and keeps the urgency order.
        Equal( "herdr:agent:w1:p1|herdr:agent:w2:p1", string.Join( '|', (await SearchHerdrAsync( "agents: claude",
             AgentFake( ) )).Select( static result => result.Id ) ) );
        Equal( "herdr:agent:w2:p2|herdr:agent:w2:p1", string.Join( '|', (await SearchHerdrAsync( "agents: notes",
             AgentFake( ) )).Select( static result => result.Id ) ) );
        Equal( "herdr:agent:w1:p3", (await SearchHerdrAsync( "agents: release", AgentFake( ) )).Single( ).Id );
        Equal( 0, (await SearchHerdrAsync( "agents: zzzz", AgentFake( ) )).Count );

        // Browsing a space lists only its agents.
        HerdrProvider provider = new( new HerdrClient( AgentFake( ).Runner, static ( ) => true ) );

        var scoped = await provider.SearchAsync( QueryParser.Parse( "agents:" ),
            new SearchRequest { Query = "agents:", HerdrWorkspaceId = "w2", Limit = 50, EnabledSearchSources = [SourceIds.Herdr] }, CancellationToken.None );

        Equal( "herdr:agent:w2:p2|herdr:agent:w2:p1", string.Join( '|', scoped.Select( static result => result.Id ) ) );

        // A failed or malformed agent list shows nothing rather than an error row.
        Equal( 0, (await SearchHerdrAsync( "agents:", AgentFake( "{not json" ) )).Count );
        Equal( 0, (await SearchHerdrAsync( "agents:", new HerdrFake( ) )).Count );
    }

    private static async Task HerdrAgentRowsPassTheCoordinatorAndSwitchTabsAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-herdr-agents-{Guid.NewGuid( ):N}.db" );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( CancellationToken.None );
            HerdrProvider provider = new( new HerdrClient( AgentFake( ).Runner, static ( ) => true ) );
            SearchCoordinator coordinator = new( [provider], new SearchOptions( ), catalog, catalog );

            var listed = await coordinator.SearchAsync( new SearchRequest { Query = "agents:", Limit = 20 }, CancellationToken.None );
            Equal( "herdr:agent:w1:p3|herdr:agent:w1:p1|herdr:agent:w2:p2|herdr:agent:w2:p1|herdr:agent:w1:p4",
                string.Join( '|', listed.Select( static result => result.Id ) ) );

            // The alias works, and the filter admits no ordinary tab row.
            True( (await coordinator.SearchAsync( new SearchRequest { Query = "hagents: codex", Limit = 20 },
                 CancellationToken.None )).All( HerdrSearch.IsAgentRow ) );

            var tabs = await coordinator.SearchAsync( new SearchRequest { Query = "htab: release", Limit = 20 }, CancellationToken.None );
            True( tabs.Count > 0 && !tabs.Any( HerdrSearch.IsAgentRow ) );

            // Enter switches to the agent's tab through the ordinary focus action.
            HerdrFake focus = new( )
            {
                Respond = static _ => HerdrResult( """{"id":"cli:tab:focus","result":{"tab":{"tab_id":"w1:t2","focused":true},"type":"tab_info"}}""" )
            };

            LinuxActionExecutor executor = new( herdr: new HerdrClient( focus.Runner, static ( ) => true ) );
            var row = listed[0];
            var switched = await executor.ExecuteAsync( row, row.Actions.Single( ), "", CancellationToken.None );
            True( switched.Succeeded, switched.Message );
            Equal( "--session default tab focus w1:t2", string.Join( ' ', focus.Calls.Single( ).Args ) );
        }
        finally
        {
            DeleteDatabase( database );
        }
    }
}
