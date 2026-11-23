namespace CommandDeck.Specs;

public static partial class Program
{
    // A desktop listing whose IDs equal the terminal fixture's w1, w1:t1, and w1:p1.
    private const string HerdrAppCollidingJson = """
                                                 {"spaces":[{"id":"w1","name":"Desk","active":false,"tabs":[
                                                   {"id":"w1:t1","name":"desk","active":true,"terminals":[
                                                     {"terminal":"t1","pane":"w1:p1","status":"blocked","app":"codex"}]}]}]}
                                                 """;

    private static async Task HerdrAppsWithEqualIdsStayApartAsync( )
    {
        HerdrFake terminal = new( )
        {
            Respond = static args => args[2] switch
            {
                "workspace" => HerdrResult( HerdrWorkspaceListJson.Replace( "\"w3\"", "\"app-space:w3\"", StringComparison.Ordinal ) ),
                "tab" => HerdrResult( HerdrTabListJson ),
                _ => HerdrResult( """{"result":{"type":"agent_list","agents":[{"pane_id":"w1:p1","tab_id":"w1:t1","workspace_id":"w1","agent":"claude",""" +
                                  "\"agent_status\":\"idle\"}]}}" )
            }
        };

        HerdrAppFake desktop = new( ) { Respond = static args => HerdrResult( args[0] == "ls" ? HerdrAppCollidingJson : """{"pane":"p9"}""" ) };
        HerdrProvider provider = new( new HerdrClient( terminal.Runner, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        // A terminal space whose ID looks like a desktop scope is not listed.
        Equal( "herdr:app-space:w1|herdr:workspace:w1|herdr:workspace:w2", string.Join( '|', (await SearchMergedHerdrAsync( provider, "hspace: " ))
            .Select( static row => row.Id )
            .Order( StringComparer.Ordinal ) ) );

        // With both apps listed, every row names its app.
        True( (await SearchMergedHerdrAsync( provider, "herdr: " )).All( static row => row.Id.Contains( ":app-", StringComparison.Ordinal )
            ? row.Subtitle!.Contains( " · Herdr app · ", StringComparison.Ordinal )
            : row.Subtitle!.StartsWith( "Herdr terminal · ", StringComparison.Ordinal ) ) );

        // A browsed space lists only its own app's tabs and offers to create one there.
        Equal( "herdr:app-tab:w1:t1", string.Join( '|', (await SearchScopedHerdrAsync( provider, "", "app-space:w1" )).Select( static row => row.Id ) ) );
        Equal( "herdr:tab:w1:t1|herdr:tab:w1:t2", string.Join( '|', (await SearchScopedHerdrAsync( provider, "", "w1" )).Select( static row => row.Id ) ) );

        var offer = (await SearchScopedHerdrAsync( provider, "fresh", "app-space:w1" )).Single( );
        Equal( "herdr:new-tab:app-space:w1", offer.Id );
        Equal( HerdrTargetKinds.AppSpace, offer.Metadata[HerdrProvider.TargetKindMetadata] );
        True( HerdrSearch.MatchesScope( offer, "app-space:w1" ) && !HerdrSearch.MatchesScope( offer, "w1" ) );
        Equal( "herdr:new-tab:w1", (await SearchScopedHerdrAsync( provider, "fresh", "w1" )).Single( ).Id );

        // The launcher browses a space through the Herdr tabs filter, which keeps the offer.
        SearchCoordinator coordinator = new( [provider], new SearchOptions( ) );
        foreach ( var scope in new[] { "w1", "app-space:w1" } )
        {
            var view = await coordinator.SearchAsync( new SearchRequest { Query = "htab: fresh", HerdrWorkspaceId = scope, Limit = 20 }, default );
            Equal( "herdr:new-tab:" + scope, view[^1].Id );
        }

        // A browsed space asks only its own app.
        var asked = (Terminal: terminal.Calls.Count, Desktop: desktop.Calls.Count);
        await SearchScopedHerdrAsync( provider, "", "app-space:w1" );
        Equal( asked.Terminal, terminal.Calls.Count );
        await SearchScopedHerdrAsync( provider, "", "w1" );
        Equal( asked.Desktop + 1, desktop.Calls.Count );

        // Creating from the desktop offer reaches only the desktop app.
        var terminalCalls = terminal.Calls.Count;
        LinuxActionExecutor executor = new( herdr: new HerdrClient( terminal.Runner, static ( ) => true ),
            herdrApp: new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        var created = await executor.ExecuteAsync( offer, ActionInputValues.Bind( offer.Actions[0], new Dictionary<string, string> { ["Name"] = "fresh" } ),
            "", CancellationToken.None );

        True( created.Succeeded, created.Message );
        Equal( terminalCalls, terminal.Calls.Count );
        Equal( """do|tab.new|{"space":"w1","name":"fresh","focus":false}""", Commands( desktop ).Split( '#' )[^1] );

        // With both apps listed, the edit dialogs name the tab's app and space.
        var both = await SearchMergedHerdrAsync( provider, "herdr: " );
        Equal( "Rename tab: Space 1 · Tab 1 · Herdr app · Desk", both.Single( static row => row.Id == "herdr:app-tab:w1:t1" ).Actions[1].Title );
        Equal( "Rename tab: Herdr terminal · CommandDeck", both.Single( static row => row.Id == "herdr:tab:w1:t1" ).Actions[1].Title );
        Equal( "New tab: Space 1 · Herdr app · Desk", both.Single( static row => row.Id == "herdr:app-space:w1" ).Actions[2].Title );

        // A browsed space reads one app, but its dialogs still name it while both apps are installed.
        var browsed = await SearchScopedHerdrAsync( provider, "", "app-space:w1" );
        Equal( "Rename tab: Space 1 · Tab 1 · Herdr app · Desk", browsed.Single( ).Actions[1].Title );
        Equal( "Create tab: Space 1 · Herdr app · Desk", (await SearchScopedHerdrAsync( provider, "fresh", "app-space:w1" )).Single( ).Actions[0].Title );

        // Each app names its own agents, and their rows stay distinct.
        var agents = await SearchMergedHerdrAsync( provider, "agents:" );
        Equal( "herdr:agent:app-pane:w1:p1|herdr:agent:w1:p1", string.Join( '|', agents.Select( static row => row.Id ) ) );
        Equal( "Space 1 · Tab 1 · desk|Herdr app · codex · Needs attention · Desk", $"{agents[0].Title}|{agents[0].Subtitle}" );
        Equal( "tests|Herdr terminal · claude · Idle · CommandDeck", $"{agents[1].Title}|{agents[1].Subtitle}" );
        Equal( "herdr:agent:app-pane:w1:p1", (await SearchScopedHerdrAsync( provider, "agents:", "app-space:w1" )).Single( ).Id );
    }

    private static async Task HerdrAppLabelsCannotBreakSearchAsync( )
    {
        // A label one short of the limit, then a character made of two UTF-16 units.
        var longLabel = new string( 'a', HerdrClient.MaximumLabelCharacters - 1 ) + "\U0001F600";
        var json = $$"""{"spaces":[{"id":"sp1","name":"\ud800","active":true,"tabs":[]},{"id":"sp2","name":"{{longLabel}}","active":false,"tabs":[]}]}""";
        HerdrAppFake desktop = new( ) { Respond = _ => HerdrResult( json ) };
        HerdrFake terminal = new( )
        {
            Respond = args => HerdrResult( args[2] == "workspace"
                ? HerdrWorkspaceListJson.Replace( "\"label\":\"Notes\"", $"\"label\":\"{longLabel}\"", StringComparison.Ordinal )
                : HerdrTabListJson )
        };

        HerdrProvider provider = new( new HerdrClient( terminal.Runner, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        // Neither label throws, and both apps' rows stay listed.
        var rows = await SearchMergedHerdrAsync( provider, "herdr: " );
        True( rows.Any( static row => row.Id == "herdr:workspace:w1" ) );
        Equal( "Space 1", rows.Single( static row => row.Id == "herdr:app-space:sp1" ).Title );
        Equal( HerdrClient.MaximumLabelCharacters - 1, rows.Single( static row => row.Id == "herdr:app-space:sp2" ).Title.Length );
        Equal( HerdrClient.MaximumLabelCharacters - 1, rows.Single( static row => row.Id == "herdr:workspace:w2" ).Title.Length );
        Equal( "herdr:app-space:sp2|herdr:workspace:w2|herdr:workspace:w3", string.Join( '|', (await SearchMergedHerdrAsync( provider, "hspace: aaaa" ))
            .Select( static row => row.Id )
            .Order( StringComparer.Ordinal ) ) );

        // A space on another machine names it, so same-named spaces stay apart.
        HerdrAppFake remote = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[{"id":"s1","name":"Project","machine":null,"active":true,"tabs":[]},""" +
                                               """{"id":"s2","name":"Project","machine":"devbox","active":false,"tabs":[]},""" +
                                               """{"id":"s3","name":"","machine":"devbox","active":false,"tabs":[]}]}""" )
        };

        var spaces = await new HerdrAppClient( remote.Runner, static ( ) => true ).ListAsync( CancellationToken.None );
        Equal( "Project:|Project:devbox|:devbox", string.Join( '|', spaces!.Inventory.Workspaces.Select( static space => $"{space.Label}:{space.Machine}" ) ) );

        // The machine comes before the name, so a local space named after a remote one cannot pass for it.
        HerdrAppFake lookalike = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[{"id":"s1","name":"Project on devbox","machine":null,"active":true,"tabs":[]},""" +
                                               """{"id":"s2","name":"Project","machine":"devbox","active":false,"tabs":[]}]}""" )
        };

        HerdrProvider machines = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( lookalike.Runner, static ( ) => true ) );
        Equal( "Space 1 · This computer · 0 tabs · 0 terminals · focused|Space 2 · Remote devbox · 0 tabs · 0 terminals", string.Join( '|',
            (await SearchMergedHerdrAsync( machines, "hspace: " )).Select( static row => row.Subtitle ) ) );

        // A remote machine aliased "local" still reads as remote, and a machine's name finds its spaces and tabs.
        HerdrAppFake aliased = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[{"id":"s1","name":"Project","machine":null,"active":true,"tabs":[""" +
                                               """{"id":"t1","name":"server","active":true,"terminals":[]}]},""" +
                                               """{"id":"s2","name":"Project","machine":"local","active":false,"tabs":[""" +
                                               """{"id":"t2","name":"server","active":true,"terminals":[]}]}]}""" )
        };

        HerdrProvider aliasProvider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( aliased.Runner, static ( ) => true ) );

        var servers = await SearchMergedHerdrAsync( aliasProvider, "htab: server" );
        Equal( "Rename tab: Space 1 · Tab 1 · This computer · Project|Rename tab: Space 2 · Tab 1 · Remote local · Project", string.Join( '|', servers
            .OrderBy( static row => row.Id, StringComparer.Ordinal )
            .Select( static row => row.Actions[1].Title ) ) );
        Equal( "herdr:app-tab:t2", (await SearchMergedHerdrAsync( aliasProvider, "htab: local" )).Single( ).Id );

        // A terminal agent's long name is cut whole too, so desktop agents stay listed.
        var agentName = new string( 'c', 63 ) + "\U0001F600";
        HerdrFake agentTerminal = new( )
        {
            Respond = args => args[2] switch
            {
                "workspace" => HerdrResult( HerdrWorkspaceListJson ),
                "tab" => HerdrResult( HerdrTabListJson ),
                _ => HerdrResult( "{\"result\":{\"type\":\"agent_list\",\"agents\":[{\"pane_id\":\"w1:p1\",\"tab_id\":\"w1:t1\",\"workspace_id\":\"w1\"," +
                                  $"\"agent\":\"{agentName}\",\"agent_status\":\"idle\"}}]}}}}" )
            }
        };

        HerdrProvider agentsProvider = new( new HerdrClient( agentTerminal.Runner, static ( ) => true ),
            new HerdrAppClient( new HerdrAppFake( ).Runner, static ( ) => true ) );

        var agents = await SearchMergedHerdrAsync( agentsProvider, "agents: c" );
        True( agents.Any( static row => row.Id == "herdr:agent:w1:p1" ) && agents.Any( static row => row.Id == "herdr:agent:app-pane:p3" ) );
        Equal( 63, agents.Single( static row => row.Id == "herdr:agent:w1:p1" ).Metadata[HerdrProvider.AgentNameMetadata].Length );
    }

    private static async Task HerdrAppRepliesMustAcknowledgeTheChangeAsync( )
    {
        const string unconfirmed = "Herdr did not confirm the change; it may already have happened. Refresh before trying again.";
        var rows = await HerdrAppRowsAsync( );
        var tab = rows.Single( static row => row.Id == "herdr:app-tab:tb4" );
        var space = rows.Single( static row => row.Id == "herdr:app-space:sp2" );
        var values = new Dictionary<string, string> { ["Name"] = "renamed" };

        (bool Create, ProcessResult Reply, bool Succeeded, string Message)[] cases =
        [
            (false, HerdrResult( """{"ok":true}""" ), true, "Renamed Herdr tab."),
            (false, HerdrResult( "{}" ), false, unconfirmed),
            (false, HerdrResult( """{"ok":false}""" ), false, unconfirmed),
            (false, HerdrResult( "done" ), false, unconfirmed),
            (false, HerdrResult( "", 137, "killed" ), false, unconfirmed),
            (true, HerdrResult( "", 2, "herdr-app: the window hung up" ), false, unconfirmed),
            (false, HerdrResult( "", 2, "herdr-app: no Herdr window at /run/h.sock: refused" ), false, "The Herdr app did not answer. Is its window open?"),
            (false, HerdrResult( "crash", 1 ), false, "Herdr could not change this tab. Refresh to check that the space or tab still exists."),
            (false, HerdrResult( """{"error":"no tab \u0007tb4"}""", 1 ), false, "Herdr refused the change: no tab tb4"),
            (true, HerdrResult( """{"pane":"p9","terminal":"t9"}""" ), true, "Created Herdr tab."),
            (true, HerdrResult( """{"ok":true}""" ), true, "Created Herdr tab."),
            (true, HerdrResult( """{"pane":""}""" ), false, unconfirmed),
            (true, HerdrResult( """{"ok":false,"pane":"p9"}""" ), false, unconfirmed),
            (false, HerdrResult( """{"error":"late"}""" ), false, unconfirmed),
            (false, HerdrResult( """{"error":null}""", 137 ), false, unconfirmed),
            (false, HerdrResult( """{"error":null}""", 1 ), false, "Herdr could not change this tab. Refresh to check that the space or tab still exists."),

            // The runner reports a process that never started as exit -1.
            (false, new ProcessResult { ExitCode = -1, StandardOutput = "", StandardError = "Unable to start 'herdr-app': gone" }, false,
                "Herdr could not change this tab. Refresh to check that the space or tab still exists.")
        ];

        foreach ( (var create, var reply, var succeeded, var message) in cases )
        {
            HerdrAppFake desktop = new( ) { Respond = _ => reply };
            var row = create ? space : tab;
            var action = ActionInputValues.Bind( row.Actions[create ? 2 : 1], values );

            var outcome = await new LinuxActionExecutor( herdrApp: new HerdrAppClient( desktop.Runner, static ( ) => true ) ).ExecuteAsync( row, action, "",
                CancellationToken.None );

            Equal( succeeded, outcome.Succeeded );
            Equal( message, outcome.Message );
            Equal( 1, desktop.Calls.Count );
        }

        // A refusal arrives on stdout, so its error text is what is logged.
        List<string> lines = [];
        HerdrAppFake refusing = new( ) { Respond = static _ => HerdrResult( """{"error":"no such tab"}""", 1 ) };
        await new HerdrAppClient( refusing.Runner, static ( ) => true, log: lines.Add ).RenameTabAsync( "tb4", "x", CancellationToken.None );

        Equal( "event=herdr_app.command_failed command=\"do tab.rename\" outcome=exit exit_code=1 error=\"no such tab\"", lines.Single( ) );
    }

    private static async Task HerdrProviderListsWhatAnsweredByTheDeadlineAsync( )
    {
        HerdrAppFake desktop = new( );
        TaskCompletionSource stopped = new( TaskCreationOptions.RunContinuationsAsynchronously );
        BoundedProcessRunner late = async ( file, args, timeout, limits, cancellationToken ) =>
        {
            try
            {
                await Task.Delay( TimeSpan.FromSeconds( 3 ), cancellationToken );
            }
            catch ( OperationCanceledException )
            {
                stopped.TrySetResult( );

                throw;
            }

            return await desktop.Runner( file, args, timeout, limits, cancellationToken );
        };

        HerdrProvider provider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => true ), new HerdrAppClient( late, static ( ) => true ) );

        // The terminal app answered at once; the late app adds nothing and holds nothing up.
        var watch = System.Diagnostics.Stopwatch.StartNew( );
        var rows = await SearchHerdrWithinAsync( provider, "herdr: ", TimeSpan.FromMilliseconds( 200 ) );
        watch.Stop( );

        True( rows.Any( static row => row.Id == "herdr:workspace:w1" ) );
        True( rows.All( static row => !row.Id.Contains( "app-", StringComparison.Ordinal ) ) );
        True( watch.Elapsed < TimeSpan.FromSeconds( 2 ), watch.Elapsed.ToString( ) );

        // The late list is stopped rather than left running.
        await stopped.Task.WaitAsync( TimeSpan.FromSeconds( 2 ) );

        // The coordinator's own deadline reaches the provider.
        SearchCoordinator coordinator = new( [provider], new SearchOptions { ProviderTimeoutMilliseconds = 400 } );
        var collected = await coordinator.SearchAsync( new SearchRequest { Query = "herdr: ", Limit = 20 }, default );
        True( collected.Any( static row => row.Id == "herdr:workspace:w1" ), string.Join( '|', collected.Select( static row => row.Id ) ) );

        // With nothing answered at the cutoff, the wait goes on: an early return would show nothing.
        HerdrFake slowFake = new( );
        BoundedProcessRunner slow = async ( file, args, timeout, limits, cancellationToken ) =>
        {
            await Task.Delay( TimeSpan.FromMilliseconds( 400 ), cancellationToken );

            return await slowFake.Runner( file, args, timeout, limits, cancellationToken );
        };

        HerdrProvider alone = new( new HerdrClient( slow, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => false ) );
        True( (await SearchHerdrWithinAsync( alone, "herdr: ", TimeSpan.FromMilliseconds( 200 ) )).Any( static row => row.Id == "herdr:workspace:w1" ) );

        // After the cutoff, the first app to answer is listed without waiting for the other.
        HerdrProvider racing = new( new HerdrClient( slow, static ( ) => true ), new HerdrAppClient( late, static ( ) => true ) );

        var raced = System.Diagnostics.Stopwatch.StartNew( );
        var first = await SearchHerdrWithinAsync( racing, "herdr: ", TimeSpan.FromMilliseconds( 200 ) );
        raced.Stop( );

        True( first.Any( static row => row.Id == "herdr:workspace:w1" ) && first.All( static row => !row.Id.Contains( "app-", StringComparison.Ordinal ) ) );
        True( raced.Elapsed < TimeSpan.FromSeconds( 2 ), raced.Elapsed.ToString( ) );

        // An app that answers early with nothing to show does not end the wait.
        HerdrAppFake empty = new( ) { Respond = static _ => HerdrResult( """{"spaces":[]}""" ) };
        HerdrProvider emptyApp = new( new HerdrClient( slow, static ( ) => true ), new HerdrAppClient( empty.Runner, static ( ) => true ) );
        True( (await SearchHerdrWithinAsync( emptyApp, "herdr: ", TimeSpan.FromMilliseconds( 200 ) )).Any( static row => row.Id == "herdr:workspace:w1" ) );

        // Nor does one whose rows do not match the query.
        HerdrProvider unmatched = new( new HerdrClient( slow, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );
        Equal( "herdr:workspace:w2|herdr:workspace:w3", string.Join( '|', (await SearchHerdrWithinAsync( unmatched, "hspace: notes",
                TimeSpan.FromMilliseconds( 200 ) ))
            .Select( static row => row.Id )
            .Order( StringComparer.Ordinal ) ) );

        // Nor does an agent list with no agents.
        HerdrFake slowAgentsFake = new( )
        {
            Respond = static args => args[2] switch
            {
                "workspace" => HerdrResult( HerdrWorkspaceListJson ),
                "tab" => HerdrResult( HerdrTabListJson ),
                _ => HerdrResult( """{"result":{"type":"agent_list","agents":[{"pane_id":"w1:p1","tab_id":"w1:t1","workspace_id":"w1","agent":"claude",""" +
                                  "\"agent_status\":\"idle\"}]}}" )
            }
        };

        BoundedProcessRunner slowAgents = async ( file, args, timeout, limits, cancellationToken ) =>
        {
            await Task.Delay( TimeSpan.FromMilliseconds( 400 ), cancellationToken );

            return await slowAgentsFake.Runner( file, args, timeout, limits, cancellationToken );
        };

        HerdrProvider agentless = new( new HerdrClient( slowAgents, static ( ) => true ), new HerdrAppClient( empty.Runner, static ( ) => true ) );
        Equal( "herdr:agent:w1:p1", (await SearchHerdrWithinAsync( agentless, "agents:", TimeSpan.FromMilliseconds( 200 ) )).Single( ).Id );

        // An early agent that the query excludes does not end the wait either.
        HerdrAppFake blockedOnly = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[{"id":"sp9","name":"Only","active":true,"tabs":[""" +
                                               """{"id":"tb9","name":"t","active":true,"terminals":""" +
                                               """[{"terminal":"t1","pane":"p9","status":"blocked","app":"codex"}]}]}]}""" )
        };

        HerdrProvider excluded = new( new HerdrClient( slowAgents, static ( ) => true ), new HerdrAppClient( blockedOnly.Runner, static ( ) => true ) );
        Equal( "herdr:agent:w1:p1", (await SearchHerdrWithinAsync( excluded, "agents: -hattention:", TimeSpan.FromMilliseconds( 200 ) )).Single( ).Id );

        // An early answer whose only match the context prefix excludes does not end the wait.
        HerdrAppFake localTests = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[{"id":"s1","name":"Here","machine":null,"active":true,"tabs":[""" +
                                               """{"id":"t1","name":"tests","active":true,"terminals":[]}]},""" +
                                               """{"id":"s2","name":"Far","machine":"devbox","active":false,"tabs":[]}]}""" )
        };

        HerdrProvider prefixed = new( new HerdrClient( slow, static ( ) => true ), new HerdrAppClient( localTests.Runner, static ( ) => true ) );
        Equal( "herdr:tab:w1:t1", (await SearchHerdrWithinAsync( prefixed, "htab: tests -computer", TimeSpan.FromMilliseconds( 200 ) )).Single( ).Id );

        // An infinite coordinator timeout sets no deadline, so both apps are waited for.
        HerdrProvider unbounded = new( new HerdrClient( slow, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );
        var waited = await new SearchCoordinator( [unbounded], new SearchOptions { ProviderTimeoutMilliseconds = Timeout.Infinite } )
            .SearchAsync( new SearchRequest { Query = "herdr: ", Limit = 50 }, default );
        True( waited.Any( static row => row.Id == "herdr:workspace:w1" ) && waited.Any( static row => row.Id == "herdr:app-space:sp1" ),
            string.Join( '|', waited.Select( static row => row.Id ) ) );

        // Within the deadline both apps are listed.
        HerdrProvider patient = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => true ), new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        var both = await SearchHerdrWithinAsync( patient, "herdr: ", TimeSpan.FromSeconds( 5 ) );
        True( both.Any( static row => row.Id == "herdr:workspace:w1" ) && both.Any( static row => row.Id == "herdr:app-space:sp1" ) );

        // An app that fails in an unexpected way hides none of the other's rows.
        HerdrAppFake broken = new( ) { Respond = static _ => throw new ArgumentException( "unexpected" ) };
        HerdrProvider survivor = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => true ), new HerdrAppClient( broken.Runner, static ( ) => true ) );

        True( (await SearchMergedHerdrAsync( survivor, "herdr: " )).Any( static row => row.Id == "herdr:workspace:w1" ) );
    }

    private static async Task HerdrAppFocusSendsNothingOnceCancelledAsync( )
    {
        using CancellationTokenSource cancellation = new( );
        HerdrAppFake desktop = new( );

        // Cancelled just before the command starts: the runner is never called.
        HerdrAppClient client = new( desktop.Runner, static ( ) => true, listener: ( ) =>
        {
            cancellation.Cancel( );

            return 1;
        } );

        await ThrowsAsync<OperationCanceledException>( async ( ) => await client.FocusAsync( HerdrTargetKinds.AppTab, "tb4", cancellation.Token ) );
        Equal( 0, desktop.Calls.Count );

        // The focus gate is free again afterwards.
        True( (await client.FocusAsync( HerdrTargetKinds.AppSpace, "sp2", CancellationToken.None )).Succeeded );
    }

    private static async Task<IReadOnlyList<SearchResult>> SearchHerdrWithinAsync( HerdrProvider provider, string query, TimeSpan deadline )
    {
        SearchRequest request = new( ) { Query = query, Limit = 50, EnabledSearchSources = [SourceIds.Herdr] };
        request.ProviderDeadlineTimestamp = System.Diagnostics.Stopwatch.GetTimestamp( ) +
                                            ( long ) (deadline.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        return await provider.SearchAsync( QueryParser.Parse( query ), request, CancellationToken.None );
    }

    private static async Task<IReadOnlyList<SearchResult>> SearchScopedHerdrAsync( HerdrProvider provider, string query, string scope )
    {
        return await provider.SearchAsync( QueryParser.Parse( query ),
            new SearchRequest { Query = query, HerdrWorkspaceId = scope, Limit = 50, EnabledSearchSources = [SourceIds.Herdr] }, CancellationToken.None );
    }
}
