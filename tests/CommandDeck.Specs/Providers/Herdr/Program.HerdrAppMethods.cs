namespace CommandDeck.Specs;

public static partial class Program
{
    // The shape `herdr-app ls` prints: spaces in order, tabs in order, and each
    // tab's terminals. The third space is invalid and the fourth is malformed.
    private const string HerdrAppListJson = """
                                            {"spaces":[
                                              {"id":"sp1","name":"Vision","folder":"/home/u","machine":null,"active":true,"tabs":[
                                                {"id":"tb1","name":"PDF catalogue","active":false,"settled":false,"terminals":[
                                                  {"terminal":"t2","pane":"p1","status":"idle","app":"claude-code","name":null,"sub_agents":[]}]},
                                                {"id":"tb2","name":"2","active":true,"settled":false,"terminals":[
                                                  {"terminal":"t3","pane":"p2","status":null,"app":null,"name":null,"sub_agents":[]},
                                                  {"terminal":"t4","pane":"p3","status":"blocked","app":"codex","name":null,"sub_agents":[]}]}]},
                                              {"id":"sp2","name":"sharpmarker-next","folder":"/home/u/code","machine":null,"active":false,"tabs":[
                                                {"id":"tb3","name":"1","active":true,"settled":false,"terminals":[
                                                  {"terminal":"t9","pane":"p4","status":"working","app":"claude-code","name":null,"sub_agents":[]}]},
                                                {"id":"tb4","name":"server","active":false,"settled":true,"terminals":[
                                                  {"terminal":"t10","pane":"p5","status":null,"app":null,"name":null,"sub_agents":[]}]}]},
                                              {"id":"","name":"broken","tabs":[]},
                                              {"id":"sp3","name":"bad\u0007name","active":false,"tabs":"nope"}
                                            ],"recent":[{"name":"","folder":"/tmp"}]}
                                            """;

    private static async Task HerdrAppClientListsTheOpenWindowAsync( )
    {
        HerdrAppFake fake = new( );
        Dictionary<string, string> environment = new( StringComparer.Ordinal )
        {
            ["PATH"] = "/usr/bin:/home/u/.local/bin",
            ["HOME"] = "/home/u",
            ["XDG_CONFIG_HOME"] = "/home/u/.config",
            ["XDG_RUNTIME_DIR"] = "/run/user/1000",
            ["HERDR_APP_HOME"] = "/elsewhere",
            ["WAYLAND_DISPLAY"] = "wayland-1"
        };

        HerdrAppClient client = new( fake.Runner, static ( ) => true, environment.GetValueOrDefault );
        var listing = await client.ListAsync( CancellationToken.None );

        True( listing is not null );
        var call = fake.Calls.Single( );
        Equal( "herdr-app", call.File );
        Equal( "ls", string.Join( ' ', call.Args ) );
        Equal( HerdrAppClient.ListTimeout, call.Timeout );
        Equal( HerdrAppClient.ListStandardOutputBytes, call.Limits.MaximumStandardOutputBytes );
        Equal( HerdrAppClient.StandardErrorBytes, call.Limits.MaximumStandardErrorBytes );
        Equal( HerdrAppClient.MaximumMemoryBytes, call.Limits.MaximumMemoryBytes );
        True( call.Limits.ClearEnvironment );

        // HERDR_APP_HOME stays behind: the daemon always reads the default window.
        Equal( "HOME=/home/u|LANG=C.UTF-8|LC_ALL=C.UTF-8|PATH=/usr/bin:/home/u/.local/bin|XDG_CONFIG_HOME=/home/u/.config|XDG_RUNTIME_DIR=/run/user/1000",
            string.Join( '|', call
                .Limits.EnvironmentVariables!.OrderBy( static pair => pair.Key, StringComparer.Ordinal )
                .Select( static pair => $"{pair.Key}={pair.Value}" ) ) );

        var inventory = listing!.Inventory;

        Equal( "sp1:1:Vision:True:2:3:blocked|sp2:2:sharpmarker-next:False:2:2:working|sp3:4:badname:False:0:0:", string.Join( '|', inventory
            .Workspaces.Select( static space =>
                $"{space.Id}:{space.Number}:{space.Label}:{space.Focused}:{space.TabCount}:{space.PaneCount}:{space.AgentStatus}" ) ) );

        // Only the shown space's selected tab is focused.
        Equal( "tb1:sp1:1:False:1:idle|tb2:sp1:2:True:2:blocked|tb3:sp2:1:False:1:working|tb4:sp2:2:False:1:", string.Join( '|', inventory
            .Tabs.Select( static tab => $"{tab.Id}:{tab.WorkspaceId}:{tab.Number}:{tab.Focused}:{tab.PaneCount}:{tab.AgentStatus}" ) ) );

        Equal( "p1:tb1:sp1:claude-code:idle:False|p3:tb2:sp1:codex:blocked:False|p4:tb3:sp2:claude-code:working:False", string.Join( '|', listing
            .Agents.Select( static agent => $"{agent.PaneId}:{agent.TabId}:{agent.WorkspaceId}:{agent.Name}:{agent.AgentStatus}:{agent.Focused}" ) ) );

        True( inventory.Workspaces.All( static space => space.App ) && inventory.Tabs.All( static tab => tab.App ) &&
              listing.Agents.All( static agent => agent.App ) );
    }

    private static async Task HerdrAppClientYieldsNothingWithoutAWindowAsync( )
    {
        HerdrAppFake absent = new( );
        HerdrAppClient missing = new( absent.Runner, static ( ) => false );

        True( (await missing.ListAsync( CancellationToken.None )) is null );
        True( !(await missing.FocusAsync( HerdrTargetKinds.AppSpace, "sp1", CancellationToken.None )).Succeeded );
        True( !(await missing.RenameTabAsync( "tb1", "name", CancellationToken.None )).Succeeded );
        Equal( 0, absent.Calls.Count );

        ProcessResult[] failures =
        [
            HerdrResult( "", 2, "herdr-app: no Herdr window at /home/u/.config/herdr-app/herdr-app.sock: No such file or directory (os error 2)" ),
            new( ) { ExitCode = -1, StandardOutput = "", StandardError = "", TimedOut = true },
            new( ) { ExitCode = -1, StandardOutput = "", StandardError = "", OutputLimitExceeded = true },
            HerdrResult( "not json" ), HerdrResult( "[]" ), HerdrResult( """{"spaces":{}}""" ), HerdrResult( HerdrAppListJson, 1 )
        ];

        foreach ( var failure in failures )
        {
            HerdrAppFake fake = new( ) { Respond = _ => failure };

            True( (await new HerdrAppClient( fake.Runner, static ( ) => true ).ListAsync( CancellationToken.None )) is null );
        }

        // A closed window logs once a minute, with only the command word.
        List<string> lines = [];
        HerdrAppFake closed = new( ) { Respond = _ => failures[0] };
        HerdrAppClient logging = new( closed.Runner, static ( ) => true, log: lines.Add );
        await logging.ListAsync( CancellationToken.None );
        await logging.ListAsync( CancellationToken.None );

        Equal( "event=herdr_app.command_failed command=\"ls\" outcome=exit exit_code=2 error=\"herdr-app: no Herdr window at " +
               "/home/u/.config/herdr-app/herdr-app.sock: No such file or directory (os error 2)\"", lines.Single( ) );

        HerdrAppFake throwing = new( ) { Respond = _ => throw new IOException( "spawn failed" ) };

        True( (await new HerdrAppClient( throwing.Runner, static ( ) => true ).ListAsync( CancellationToken.None )) is null );
    }

    private static async Task HerdrProviderMergesTheAppWithTheTerminalAppAsync( )
    {
        HerdrFake terminal = new( );
        HerdrAppFake desktop = new( );
        HerdrProvider provider = new( new HerdrClient( terminal.Runner, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        // The space matches by name and its tabs through it, as terminal rows do.
        var sharp = await SearchMergedHerdrAsync( provider, "herdr: sharp" );
        Equal( "herdr:app-space:sp2", sharp[0].Id );
        True( sharp.Any( static row => row.Id == "herdr:app-tab:tb3" ) && sharp.Any( static row => row.Id == "herdr:app-tab:tb4" ) );
        Equal( HerdrTargetKinds.AppSpace, sharp[0].Actions[0].Arguments[ActionArgumentNames.TargetKind] );
        Equal( HerdrTargetKinds.AppSpace, sharp[0].Metadata[HerdrProvider.TargetKindMetadata] );
        Equal( "Space 2 · Herdr app · 2 tabs · 2 terminals", sharp[0].Subtitle );

        var tab = sharp.Single( static row => row.Id == "herdr:app-tab:tb4" );
        Equal( "Space 2 · Tab 2 · Herdr app · In sharpmarker-next · 1 terminal", tab.Subtitle );
        Equal( "app-space:sp2", tab.Metadata[HerdrSearch.WorkspaceIdMetadata] );
        Equal( HerdrTargetKinds.AppTab, tab.Actions[0].Arguments[ActionArgumentNames.TargetKind] );

        // Browsing lists both apps; the attention filter sees app statuses.
        var all = await SearchMergedHerdrAsync( provider, "herdr: " );
        True( all.Any( static row => row.Id == "herdr:workspace:w1" ) && all.Any( static row => row.Id == "herdr:app-space:sp1" ) );
        Equal( "herdr:app-tab:tb2", (await SearchMergedHerdrAsync( provider, "hattention:" )).Single( ).Id );

        // Either app failing leaves the other's rows.
        HerdrFake stopped = new( ) { Respond = _ => HerdrResult( "", 1, "server_not_running" ) };
        HerdrProvider appOnly = new( new HerdrClient( stopped.Runner, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );
        Equal( "herdr:app-space:sp2", (await SearchMergedHerdrAsync( appOnly, "hspace: sharp" )).Single( ).Id );

        HerdrAppFake closed = new( ) { Respond = _ => HerdrResult( "", 2, "no Herdr window" ) };
        HerdrProvider terminalOnly = new( new HerdrClient( terminal.Runner, static ( ) => true ), new HerdrAppClient( closed.Runner, static ( ) => true ) );
        True( (await SearchMergedHerdrAsync( terminalOnly, "herdr: " )).All( static row => !row.Id.Contains( "app-", StringComparison.Ordinal ) ) );

        // Without the terminal app, nothing runs it; without either, nothing runs.
        HerdrFake absent = new( );
        HerdrProvider installedApp = new( new HerdrClient( absent.Runner, static ( ) => false ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );
        True( (await SearchMergedHerdrAsync( installedApp, "herdr: " )).Count > 0 );
        Equal( 0, absent.Calls.Count );

        HerdrAppFake unused = new( );
        HerdrProvider neither = new( new HerdrClient( absent.Runner, static ( ) => false ), new HerdrAppClient( unused.Runner, static ( ) => false ) );
        Equal( 0, (await SearchMergedHerdrAsync( neither, "herdr: " )).Count );
        Equal( 0, absent.Calls.Count + unused.Calls.Count );
    }

    private static async Task HerdrAppAgentsJoinTheAgentListAsync( )
    {
        HerdrAppFake desktop = new( );
        HerdrProvider provider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        var agents = await SearchMergedHerdrAsync( provider, "agents:" );

        Equal( "herdr:agent:app-pane:p3|herdr:agent:app-pane:p4|herdr:agent:app-pane:p1", string.Join( '|', agents.Select( static row => row.Id ) ) );
        // The listing does not say which pane has focus, and the row focuses the agent's own pane.
        Equal( "codex · Needs attention · Vision", agents[0].Subtitle );
        Equal( "Space 1 · Tab 2 · Terminal 2", agents[0].Title );
        Equal( HerdrTargetKinds.AppPane, agents[0].Actions[0].Arguments[ActionArgumentNames.TargetKind] );
        Equal( "p3", agents[0].Actions[0].Arguments[ActionArgumentNames.TargetId] );
        Equal( "herdr:agent:app-pane:p4", (await SearchMergedHerdrAsync( provider, "agents: sharp" )).Single( ).Id );

        // The terminal app's agents join when both of its lists answer.
        HerdrFake terminal = new( )
        {
            Respond = static args => args[2] switch
            {
                "workspace" => HerdrResult( HerdrWorkspaceListJson ),
                "tab" => HerdrResult( HerdrTabListJson ),
                _ => HerdrResult( """{"result":{"type":"agent_list","agents":[{"pane_id":"w1:p1","tab_id":"w1:t1","workspace_id":"w1","agent":"claude",""" +
                                  "\"agent_status\":\"blocked\"}]}}" )
            }
        };

        HerdrProvider both = new( new HerdrClient( terminal.Runner, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        Equal( "herdr:agent:w1:p1|herdr:agent:app-pane:p3|herdr:agent:app-pane:p4|herdr:agent:app-pane:p1",
            string.Join( '|', (await SearchMergedHerdrAsync( both, "agents:" )).Select( static row => row.Id ) ) );
    }

    private static async Task<IReadOnlyList<SearchResult>> SearchMergedHerdrAsync( HerdrProvider provider, string query )
    {
        return await provider.SearchAsync( QueryParser.Parse( query ),
            new SearchRequest { Query = query, Limit = 50, EnabledSearchSources = [SourceIds.Herdr] }, CancellationToken.None );
    }

    private sealed class HerdrAppFake
    {
        public List<(string File, IReadOnlyList<string> Args, TimeSpan Timeout, ProcessExecutionLimits Limits)> Calls { get; } = [];

        public Func<IReadOnlyList<string>, ProcessResult> Respond { get; init; } = static args => args[0] switch
        {
            "ls" => HerdrResult( HerdrAppListJson ),
            "do" => HerdrResult( """{"ok":true}""" ),
            _ => HerdrResult( "", 2, "usage" )
        };

        public BoundedProcessRunner Runner => ( file, args, timeout, limits, _ ) =>
        {
            lock ( Calls )
            {
                Calls.Add( (file, args, timeout, limits) );
            }

            return ValueTask.FromResult( Respond( args ) );
        };
    }
}
