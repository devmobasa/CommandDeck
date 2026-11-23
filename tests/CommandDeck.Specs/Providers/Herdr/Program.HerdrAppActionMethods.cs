namespace CommandDeck.Specs;

public static partial class Program
{
    private const string HerdrAppWindowsJson = """
                                               [{"address":"0x10","class":"kitty","initialClass":"kitty","focusHistoryID":0,"mapped":true},
                                                {"address":"0x55d9","class":"herdr-app","initialClass":"herdr-app","pid":4242,"focusHistoryID":3,"mapped":true}]
                                               """;

    private static async Task HerdrAppFocusShowsTheTabThroughItsSpaceAsync( )
    {
        var rows = await HerdrAppRowsAsync( );
        var tab = rows.Single( static row => row.Id == "herdr:app-tab:tb4" );
        var space = rows.Single( static row => row.Id == "herdr:app-space:sp2" );
        List<string> events = [];
        HerdrAppFake desktop = new( );

        HerdrWindowActivator activator = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ), ( _, _, _ ) =>
        {
            events.Add( "title" );

            return ValueTask.FromResult( HerdrWindowTitleResult.Success( ) );
        }, ( _, args, _, _ ) =>
        {
            events.Add( string.Join( ' ', args ) );

            return ValueTask.FromResult( HerdrResult( args[0] == "-j" ? HerdrAppWindowsJson : "ok\n" ) );
        }, static ( ) => true, static ( ) => "unused", static ( ) => 4242 );

        LinuxActionExecutor executor = new( herdrWindowActivator: activator,
            herdrApp: new HerdrAppClient( desktop.Runner, static ( ) => true, listener: static ( ) => 4242 ) );

        var switched = await executor.ExecuteAsync( tab, tab.Actions[0], "", CancellationToken.None );
        True( switched.Succeeded, switched.Message );
        Equal( "Switched Herdr tab.", switched.Message );
        // Herdr's tab.select shows the tab's own space, so one command does it.
        Equal( """do|tab.select|{"id":"tb4"}""", Commands( desktop ) );
        True( desktop.Calls.All( static call => call.Timeout == HerdrAppClient.CommandTimeout && call.Limits.ClearEnvironment ) );

        // The app draws its own window: no terminal title is marked, and the
        // most recently focused herdr-app window is raised.
        Equal( "-j clients|dispatch hl.dsp.focus({ window = \"address:0x55d9\" })", string.Join( '|', events ) );

        var shown = await executor.ExecuteAsync( space, space.Actions[0], "", CancellationToken.None );
        True( shown.Succeeded, shown.Message );
        Equal( """do|space.select|{"id":"sp2"}""", Commands( desktop ).Split( '#' )[^1] );

        // An app window Hyprland cannot find is a partial failure.
        HerdrWindowActivator windowless = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            static ( _, _, _ ) => ValueTask.FromResult( HerdrWindowTitleResult.Success( ) ),
            static ( _, _, _, _ ) => ValueTask.FromResult( HerdrResult( """[{"address":"0x10","class":"kitty"}]""" ) ), static ( ) => true,
            static ( ) => "unused", static ( ) => null );

        var partial = await new LinuxActionExecutor( herdrWindowActivator: windowless, herdrApp: new HerdrAppClient( new HerdrAppFake( ).Runner,
            static ( ) => true ) ).ExecuteAsync( tab, tab.Actions[0], "", CancellationToken.None );

        True( !partial.Succeeded );
        Equal( "Switched Herdr tab, but could not focus its window.", partial.Message );
    }

    private static async Task HerdrAppFocusReportsStaleAndClosedWindowsAsync( )
    {
        var rows = await HerdrAppRowsAsync( );
        var tab = rows.Single( static row => row.Id == "herdr:app-tab:tb4" );

        (ProcessResult Reply, string Message)[] cases =
        [
            (HerdrResult( """{"error":"no such tab"}""", 1 ), "That Herdr tab no longer exists. Search again."),
            (HerdrResult( "", 2, "herdr-app: no Herdr window at /run/h.sock" ), "The Herdr app did not answer. Is its window open?"),

            // A hangup after the command was sent may hide a switch that happened.
            (HerdrResult( "", 2, "herdr-app: the window hung up" ), "Herdr did not confirm the switch; it may already have happened."),
            (new ProcessResult { ExitCode = -1, StandardOutput = "", StandardError = "", TimedOut = true },
                "Herdr did not confirm the switch; it may already have happened."),
            (HerdrResult( "warning" ), "Herdr did not confirm the switch; it may already have happened."),
            (HerdrResult( "{}" ), "Herdr did not confirm the switch; it may already have happened."),
            (HerdrResult( "crash", 1 ), "Herdr could not switch to this result."),

            // The window queued the command but did not answer in time.
            (HerdrResult( """{"error":"the window did not answer"}""", 1 ), "Herdr did not confirm the switch; it may already have happened.")
        ];

        foreach ( (var reply, var message) in cases )
        {
            // One command, sent once: a stale tab is refused by Herdr and changes nothing.
            HerdrAppFake desktop = new( ) { Respond = _ => reply };

            var outcome = await new LinuxActionExecutor( herdrApp: new HerdrAppClient( desktop.Runner, static ( ) => true ) ).ExecuteAsync( tab,
                tab.Actions[0], "", CancellationToken.None );

            True( !outcome.Succeeded );
            Equal( message, outcome.Message );
            Equal( """do|tab.select|{"id":"tb4"}""", Commands( desktop ) );

            // Herdr's own reason for a refusal reaches the detail.
            if ( reply.StandardOutput.Contains( "no such", StringComparison.Ordinal ) )
            {
                True( outcome.DetailMarkdown?.Contains( "Herdr said: `no such tab`", StringComparison.Ordinal ) == true, outcome.DetailMarkdown );
            }
        }

        True( !(await new LinuxActionExecutor( ).ExecuteAsync( tab, tab.Actions[0], "", CancellationToken.None )).Succeeded );
        Equal( "The Herdr app is unavailable.", (await new LinuxActionExecutor( herdr: new HerdrClient( new HerdrFake( ).Runner, static ( ) => true ) )
            .ExecuteAsync( tab, tab.Actions[0], "", CancellationToken.None )).Message );
    }

    private static async Task HerdrAppFocusRejectsForgedTargetsAsync( )
    {
        var rows = await HerdrAppRowsAsync( );
        var tab = rows.Single( static row => row.Id == "herdr:app-tab:tb4" );
        var terminalTab = (await SearchHerdrAsync( "herdr: " )).Single( static row => row.Id == "herdr:tab:w1:t2" );
        HerdrAppFake desktop = new( );
        HerdrFake terminal = new( );
        LinuxActionExecutor executor = new( herdr: new HerdrClient( terminal.Runner, static ( ) => true ),
            herdrApp: new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        // A row from one app never drives the other, and no forged ID runs.
        True( !(await executor.ExecuteAsync( terminalTab, tab.Actions[0], "", CancellationToken.None )).Succeeded );
        True( !(await executor.ExecuteAsync( tab, terminalTab.Actions[0], "", CancellationToken.None )).Succeeded );
        True( !(await executor.ExecuteAsync( tab, Forge( tab.Actions[0], "tb9" ), "", CancellationToken.None )).Succeeded );
        True( !(await executor.ExecuteAsync( new SearchResult( tab ) { SourceId = SourceIds.Files }, tab.Actions[0], "", CancellationToken.None ))
            .Succeeded );

        Equal( 0, desktop.Calls.Count + terminal.Calls.Count );
        Equal( "immediate", AgentLauncherTools.ClassifyRisk( tab, tab.Actions[0] ) );

        static CommandAction Forge( CommandAction action, string targetId ) => new( )
        {
            Id = action.Id,
            Title = action.Title,
            Kind = action.Kind,
            Arguments = new Dictionary<string, string>( action.Arguments, StringComparer.Ordinal ) { [ActionArgumentNames.TargetId] = targetId }
        };
    }

    private static async Task HerdrAppTabEditsSendOneJsonObjectAsync( )
    {
        var rows = await HerdrAppRowsAsync( );
        var tab = rows.Single( static row => row.Id == "herdr:app-tab:tb4" );
        var space = rows.Single( static row => row.Id == "herdr:app-space:sp2" );
        const string literal = "--literal $(never) ; 7 \"true\"";
        var values = new Dictionary<string, string> { ["Name"] = literal };
        HerdrAppFake desktop = new( )
        {
            Respond = static args => HerdrResult( args[1] == "tab.new" ? """{"pane":"p9","terminal":"t9"}""" : """{"ok":true}""" )
        };
        LinuxActionExecutor executor = new( herdrApp: new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        var created = await executor.ExecuteAsync( space, ActionInputValues.Bind( space.Actions[2], values ), "", CancellationToken.None );
        True( created.Succeeded, created.Message );
        Equal( "Created Herdr tab.", created.Message );

        var renamed = await executor.ExecuteAsync( tab, ActionInputValues.Bind( tab.Actions[1], values ), "", CancellationToken.None );
        True( renamed.Succeeded, renamed.Message );

        // Each name stays one JSON string, never an option or a JSON value.
        Equal( """do|tab.new|{"space":"sp2","name":"--literal $(never) ; 7 \u0022true\u0022","focus":false}""" +
               """#do|tab.rename|{"id":"tb4","name":"--literal $(never) ; 7 \u0022true\u0022"}""", Commands( desktop ) );

        // An unconfirmed creation is reported once, never retried.
        HerdrAppFake lost = new( ) { Respond = static _ => new ProcessResult { ExitCode = -1, StandardOutput = "", StandardError = "", TimedOut = true } };
        var unconfirmed = await new LinuxActionExecutor( herdrApp: new HerdrAppClient( lost.Runner, static ( ) => true ) ).ExecuteAsync( space,
            ActionInputValues.Bind( space.Actions[2], values ), "", CancellationToken.None );

        True( !unconfirmed.Succeeded );
        True( unconfirmed.Message!.Contains( "may already have happened", StringComparison.Ordinal ) );
        Equal( 1, lost.Calls.Count );

        HerdrAppFake refused = new( ) { Respond = static _ => HerdrResult( """{"error":"no such tab"}""", 1 ) };
        True( !(await new LinuxActionExecutor( herdrApp: new HerdrAppClient( refused.Runner, static ( ) => true ) ).ExecuteAsync( tab,
            ActionInputValues.Bind( tab.Actions[1], values ), "", CancellationToken.None )).Succeeded );
    }

    private static async Task HerdrAppAgentFocusesItsOwnPaneAsync( )
    {
        HerdrProvider provider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( new HerdrAppFake( ).Runner, static ( ) => true ) );
        var agent = (await SearchMergedHerdrAsync( provider, "agents:" )).Single( static row => row.Id == "herdr:agent:app-pane:p3" );

        // pane.focus shows the agent's own pane, its tab, and its space; a zoomed tab shows that pane.
        HerdrAppFake desktop = new( );
        var focused = await new LinuxActionExecutor( herdrApp: new HerdrAppClient( desktop.Runner, static ( ) => true ) ).ExecuteAsync( agent,
            agent.Actions[0], "", CancellationToken.None );

        True( focused.Succeeded, focused.Message );
        Equal( "Switched to the Herdr agent.", focused.Message );
        Equal( """do|pane.focus|{"pane":"p3"}""", Commands( desktop ) );

        // Two sessions of one agent in a tab differ by terminal name and position.
        HerdrAppFake twins = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[{"id":"s1","name":"Work","active":true,"tabs":[""" +
                                               """{"id":"t1","name":"pair","active":true,"terminals":""" +
                                               """[{"terminal":"a","pane":"pa","status":"blocked","app":"codex","name":"billing"},""" +
                                               """{"terminal":"b","pane":"pb","status":"blocked","app":"codex","name":null}]}]}]}""" )
        };

        HerdrProvider pair = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ), new HerdrAppClient( twins.Runner, static ( ) => true ) );
        // The distinction leads the title, so a compact row that elides the subtitle still shows it.
        Equal( "Space 1 · Tab 1 · Terminal 1 · pair:codex (billing)|Space 1 · Tab 1 · Terminal 2 · pair:codex",
            string.Join( '|',
            (await SearchMergedHerdrAsync( pair, "agents:" )).Select( static row => $"{row.Title}:{row.Subtitle!.Split( " · " )[0]}" ) ) );
        Equal( "2 in its tab", (await SearchMergedHerdrAsync( pair, "agents:" ))[1].Details.Single( static detail => detail.Label == "Terminal" ).Value );

        // The leading numbers travel as metadata, so a compact row can keep room for them.
        Equal( "Space 1 · Tab 1 · Terminal 1>pa|Space 1 · Tab 1 · Terminal 2>pb", string.Join( '|', (await SearchMergedHerdrAsync( pair, "agents:" ))
            .Select( static row => $"{row.Metadata[HerdrProvider.TitlePositionMetadata]}>{row.Actions[0].Arguments[ActionArgumentNames.TargetId]}" ) ) );

        // Agents in tabs with the same name in one space are told apart by tab number.
        HerdrAppFake sameNames = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[{"id":"s1","name":"Work","active":true,"tabs":[""" +
                                               """{"id":"t1","name":"Codex","active":true,"terminals":""" +
                                               """[{"terminal":"a","pane":"pa","status":"blocked","app":"codex"}]},""" +
                                               """{"id":"t2","name":"Codex","active":false,"terminals":""" +
                                               """[{"terminal":"b","pane":"pb","status":"blocked","app":"codex"}]},""" +
                                               """{"id":"t3","name":"Docs","active":false,"terminals":""" +
                                               """[{"terminal":"c","pane":"pc","status":"idle","app":"codex"}]}]}]}""" )
        };

        HerdrProvider named = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( sameNames.Runner, static ( ) => true ) );
        Equal( "Space 1 · Tab 1 · Codex|Space 1 · Tab 2 · Codex|Space 1 · Tab 3 · Docs",
            string.Join( '|', (await SearchMergedHerdrAsync( named, "agents:" )).Select( static row => row.Title ) ) );
        Equal( "herdr:agent:app-pane:pa", (await SearchMergedHerdrAsync( pair, "agents: billing" )).Single( ).Id );

        // A pane that has closed is refused and changes nothing.
        HerdrAppFake gone = new( ) { Respond = static _ => HerdrResult( """{"error":"no such pane"}""", 1 ) };
        Equal( "That Herdr agent's pane no longer exists. Search again.", (await new LinuxActionExecutor(
            herdrApp: new HerdrAppClient( gone.Runner, static ( ) => true ) ).ExecuteAsync( agent, agent.Actions[0], "", CancellationToken.None )).Message );

        // The pane target is only valid on an agent row.
        var tab = (await HerdrAppRowsAsync( )).Single( static row => row.Id == "herdr:app-tab:tb2" );
        SearchResult forged = new( tab )
        {
            Metadata = new Dictionary<string, string>( tab.Metadata )
            {
                [HerdrProvider.TargetKindMetadata] = HerdrTargetKinds.AppPane,
                [HerdrProvider.TargetIdMetadata] = "p3"
            }
        };

        HerdrAppFake unused = new( );
        True( !(await new LinuxActionExecutor( herdrApp: new HerdrAppClient( unused.Runner, static ( ) => true ) ).ExecuteAsync( forged,
            agent.Actions[0], "", CancellationToken.None )).Succeeded );
        Equal( 0, unused.Calls.Count );

        // tab.new may wait for its terminal: it gets the window's whole reply time.
        var space = (await HerdrAppRowsAsync( )).Single( static row => row.Id == "herdr:app-space:sp2" );
        HerdrAppFake creating = new( ) { Respond = static _ => HerdrResult( """{"pane":"p9"}""" ) };
        await new LinuxActionExecutor( herdrApp: new HerdrAppClient( creating.Runner, static ( ) => true ) ).ExecuteAsync( space,
            ActionInputValues.Bind( space.Actions[2], new Dictionary<string, string> { ["Name"] = "build" } ), "", CancellationToken.None );

        Equal( HerdrAppClient.CreateTimeout, creating.Calls.Single( ).Timeout );

        // A window that queued the change but did not answer may still make it.
        HerdrAppFake late = new( ) { Respond = static _ => HerdrResult( """{"error":"the window did not answer"}""", 1 ) };
        var unconfirmed = await new LinuxActionExecutor( herdrApp: new HerdrAppClient( late.Runner, static ( ) => true ) ).ExecuteAsync( space,
            ActionInputValues.Bind( space.Actions[2], new Dictionary<string, string> { ["Name"] = "build" } ), "", CancellationToken.None );

        True( unconfirmed.Message!.Contains( "may already have happened", StringComparison.Ordinal ), unconfirmed.Message );
    }

    private static async Task<IReadOnlyList<SearchResult>> HerdrAppRowsAsync( )
    {
        HerdrProvider provider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( new HerdrAppFake( ).Runner, static ( ) => true ) );

        return await SearchMergedHerdrAsync( provider, "herdr: " );
    }

    private static string Commands( HerdrAppFake fake ) =>
        string.Join( '#', fake.Calls.Select( static call => string.Join( '|', call.Args ) ) );
}
