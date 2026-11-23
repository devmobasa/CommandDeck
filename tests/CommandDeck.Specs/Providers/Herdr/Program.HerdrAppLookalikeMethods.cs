namespace CommandDeck.Specs;

public static partial class Program
{
    // Rows that Enter would send to different targets never look the same,
    // even when a narrow row cuts its subtitle.
    private static async Task HerdrAppLookalikeRowsStayApartAsync( )
    {
        // Two tabs named "Tab Review" in one space are numbered; a literal "Tab 1"
        // in second place is numbered against the unnamed first tab, which is not.
        var reviews = await AgentRowsAsync( """{"spaces":[{"id":"s1","name":"Work","active":true,"tabs":[""" +
                                            Tab( "t1", "Tab Review", "pa" ) + "," + Tab( "t2", "Tab Review", "pb" ) + "]}," +
                                            """{"id":"s2","name":"Play","active":false,"tabs":[""" +
                                            Tab( "t3", "", "pc" ) + "," + Tab( "t4", "Tab 1", "pd" ) + "]}]}" );

        Equal( "Space 1 · Tab 1 · Tab Review>pa|Space 1 · Tab 2 · Tab Review>pb|Space 2 · Tab 1>pc|Space 2 · Tab 2 · Tab 1>pd",
            string.Join( '|', reviews.Select( static row =>
            $"{row.Title}>{row.Actions[0].Arguments[ActionArgumentNames.TargetId]}" ) ) );

        // The same tab name in two spaces on one machine leads with each space's number.
        const string tabName = "customer-billing-reconciliation-worktrees";
        var spaces = await AgentRowsAsync( """{"spaces":[""" +
                                           """{"id":"s1","name":"Payments","machine":"devbox","active":true,"tabs":[""" +
                                           Tab( "t1", tabName, "pa" ) + "]}," +
                                           """{"id":"s2","name":"Reports","machine":"devbox","active":false,"tabs":[""" +
                                           Tab( "t2", tabName, "pb" ) + "]}]}" );

        Equal( $"Space 1 · Tab 1 · {tabName}>pa|Space 2 · Tab 1 · {tabName}>pb", string.Join( '|', spaces.Select( static row =>
            $"{row.Title}>{row.Actions[0].Arguments[ActionArgumentNames.TargetId]}" ) ) );

        // Two same-named tabs in a long remote space: the tab number comes before
        // the space name in the subtitle, and the rename heading names it.
        const string longSpace = "customer-billing-reconciliation-worktrees-and-other-long-names";
        HerdrAppFake remote = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[{"id":"s1","name":"Here","machine":null,"active":true,"tabs":[]},""" +
                                               $$"""{"id":"s2","name":"{{longSpace}}","machine":"devbox","active":false,"tabs":[""" +
                                               Tab( "t1", "Codex", "pa" ) + "," + Tab( "t2", "Codex", "pb" ) + "]}]}" )
        };

        HerdrProvider provider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( remote.Runner, static ( ) => true ) );
        var tabs = (await SearchMergedHerdrAsync( provider, "htab: codex" ))
            .OrderBy( static row => row.Id, StringComparer.Ordinal )
            .ToArray( );

        Equal( "herdr:app-tab:t1|herdr:app-tab:t2", string.Join( '|', tabs.Select( static row => row.Id ) ) );
        True( tabs[0].Subtitle!.StartsWith( "Space 2 · Tab 1 · Remote devbox · In ", StringComparison.Ordinal ), tabs[0].Subtitle );
        True( tabs[1].Subtitle!.StartsWith( "Space 2 · Tab 2 · Remote devbox · In ", StringComparison.Ordinal ), tabs[1].Subtitle );
        Equal( $"Rename tab: Space 2 · Tab 1 · Remote devbox · {longSpace}|Rename tab: Space 2 · Tab 2 · Remote devbox · {longSpace}",
            string.Join( '|', tabs.Select( static row => row.Actions[1].Title ) ) );

        // Two spaces Herdr named "service" on one machine: their tabs, rename
        // headings, and new-tab headings lead with each space's number.
        HerdrAppFake services = new( )
        {
            Respond = static _ => HerdrResult( """{"spaces":[""" +
                                               """{"id":"s1","name":"service","machine":"devbox","active":false,"tabs":[""" +
                                               Tab( "t1", "Codex", "pa" ) + "]}," +
                                               """{"id":"s2","name":"service","machine":"devbox","active":false,"tabs":[""" +
                                               Tab( "t2", "Codex", "pb" ) + "]}," +
                                               """{"id":"s3","name":"Here","machine":null,"active":true,"tabs":[]}]}""" )
        };

        HerdrProvider serviceProvider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( services.Runner, static ( ) => true ) );
        var serviceTabs = (await SearchMergedHerdrAsync( serviceProvider, "htab: codex" ))
            .OrderBy( static row => row.Id, StringComparer.Ordinal )
            .ToArray( );

        Equal( "herdr:app-tab:t1>Rename tab: Space 1 · Tab 1 · Remote devbox · service|herdr:app-tab:t2>Rename tab: Space 2 · Tab 1 · Remote devbox · service",
            string.Join( '|', serviceTabs.Select( static row => $"{row.Id}>{row.Actions[1].Title}" ) ) );
        True( serviceTabs[0].Subtitle != serviceTabs[1].Subtitle, serviceTabs[0].Subtitle );

        var serviceSpaces = (await SearchMergedHerdrAsync( serviceProvider, "hspace: service" ))
            .OrderBy( static row => row.Id, StringComparer.Ordinal )
            .ToArray( );
        Equal( "New tab: Space 1 · Remote devbox · service|New tab: Space 2 · Remote devbox · service",
            string.Join( '|', serviceSpaces.Select( static row => row.Actions[2].Title ) ) );

        var offers = new List<string>( );
        foreach ( var scope in new[] { "app-space:s1", "app-space:s2" } )
        {
            var offer = (await SearchScopedHerdrAsync( serviceProvider, "deploy", scope )).Single( );
            offers.Add( $"{offer.Actions[0].Arguments[ActionArgumentNames.TargetId]}>{offer.Actions[0].Title}" );
        }

        Equal( "s1>Create tab: Space 1 · Remote devbox · service|s2>Create tab: Space 2 · Remote devbox · service", string.Join( '|', offers ) );

        static string Tab( string id, string name, string pane ) =>
            $$"""{"id":"{{id}}","name":"{{name}}","active":false,"terminals":[{"terminal":"x{{pane}}","pane":"{{pane}}","status":"blocked","app":"codex"}]}""";
    }

    // The rename dialog opens with the tab's shown name. Submitting it unchanged
    // sends nothing, so an automatic number or a shortened long name is never
    // stored as the tab's own name; a new name is sent once.
    private static async Task HerdrAppUnchangedRenameSendsNothingAsync( )
    {
        // A long name whose shown 512 characters end in a space.
        var longName = new string( 'n', 511 ) + " " + new string( 'm', 88 );
        HerdrAppFake desktop = new( )
        {
            Respond = args => HerdrResult( args[0] == "ls"
                ? """{"spaces":[{"id":"s1","name":"Work","active":true,"tabs":[""" +
                  """{"id":"t1","name":"1","active":true,"terminals":[]},""" +
                  $$"""{"id":"t2","name":"{{longName}}","active":false,"terminals":[]}]}]}"""
                : """{"ok":true}""" )
        };

        HerdrProvider provider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( desktop.Runner, static ( ) => true ) );
        var rows = (await SearchMergedHerdrAsync( provider, "htab: " ))
            .OrderBy( static row => row.Id, StringComparer.Ordinal )
            .ToArray( );
        LinuxActionExecutor executor = new( herdrApp: new HerdrAppClient( desktop.Runner, static ( ) => true ) );
        var listed = desktop.Calls.Count;

        foreach ( var row in rows )
        {
            var rename = row.Actions[1];
            var shown = rename.Inputs.Single( ).DefaultValue!;
            var unchanged = await executor.ExecuteAsync( row, ActionInputValues.Bind( rename, new Dictionary<string, string> { ["Name"] = shown } ), "",
                CancellationToken.None );

            True( unchanged.Succeeded, unchanged.Message );
            Equal( "Herdr tab name unchanged.", unchanged.Message );
        }

        Equal( listed, desktop.Calls.Count );
        Equal( HerdrClient.MaximumLabelCharacters, rows[1].Actions[1].Inputs.Single( ).DefaultValue!.Length );

        // Spaces Herdr would trim make no change either: "1 " and the long name
        // without its trailing space.
        foreach ( (var row, var edited) in new[] { (rows[0], "1 "), (rows[1], new string( 'n', 511 )) } )
        {
            var trimmed = await executor.ExecuteAsync( row, ActionInputValues.Bind( row.Actions[1], new Dictionary<string, string> { ["Name"] = edited } ),
                "", CancellationToken.None );

            Equal( "Herdr tab name unchanged.", trimmed.Message );
        }

        Equal( listed, desktop.Calls.Count );

        var renamed = await executor.ExecuteAsync( rows[0], ActionInputValues.Bind( rows[0].Actions[1],
            new Dictionary<string, string> { ["Name"] = "build" } ), "", CancellationToken.None );

        True( renamed.Succeeded, renamed.Message );
        Equal( """do|tab.rename|{"id":"t1","name":"build"}""", string.Join( '|', desktop.Calls[^1].Args ) );
        Equal( listed + 1, desktop.Calls.Count );
    }

    private static async Task<IReadOnlyList<SearchResult>> AgentRowsAsync( string listing )
    {
        HerdrAppFake desktop = new( ) { Respond = _ => HerdrResult( listing ) };
        HerdrProvider provider = new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            new HerdrAppClient( desktop.Runner, static ( ) => true ) );

        return await SearchMergedHerdrAsync( provider, "agents:" );
    }
}
