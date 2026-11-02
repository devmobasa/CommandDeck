namespace CommandDeck.Specs;

public static partial class Program
{
    private const string ShortcutDefaultsFixture = """
                                                   -- Fictional Omarchy-style defaults.
                                                   o.bind("SUPER + Q", "Menu", hl.dsp.exec_cmd([[fictional-menu --root]]), { repeatable = false })
                                                   o.bind("SUPER + T", "Terminal", { omarchy = "terminal" })
                                                   for workspace = 1, 10 do
                                                     o.bind("SUPER + " .. workspace, "Workspace", hl.dsp.workspace(workspace))
                                                   end
                                                   hl.define_submap("capture", function()
                                                     hl.bind("F9", hl.dsp.exec_cmd("capture"), { description = "Capture" })
                                                   end)
                                                   """;

    private static void ShortcutLuaReaderKeepsLiteralCallsAndRejectsCode( )
    {
        var valid = LuaBindingReader.Read( """
                                          -- o.bind("SUPER + X", "Comment", "never")
                                          --[=[ o.bind("SUPER + Y", "Long comment", "never") ]=]
                                          hl.bind("CTRL + F1", hl.dsp.exec_cmd("say \\\"hello\\\""), {
                                            description = [=[Quoted "title" -- not comment]=], nested = { true, 1.5, -2, .5, 0x10, 0x1.fp2, { key = "value" } }
                                          })
                                          o.bind('SUPER + T', 'Terminal', { omarchy = [[terminal]] })
                                          o.bind('SUPER + W', 'Close', hl.dsp.window.close())
                                          hl.bind('SUPER + F', hl.dsp.window.fullscreen({ mode = 'fullscreen' }), {description = 'Fullscreen'})
                                          """ );
        Equal( 4, valid.Bindings.Count );
        True( valid.Bindings.All( static bind => bind.Literal ) );
        Equal( "Quoted \"title\" -- not comment", valid.Bindings[0].Description );
        Equal( "Terminal", valid.Bindings[1].Description );
        foreach ( var expression in new[] { "command", "\"a\" .. \"b\"", "function() end", "hl.dsp.exec_cmd(command)",
             "{ launch = o.launch(\"x\") }", "{ n = 1 + 2 }" } )
        {
            var source = LuaBindingReader.Read( $"o.bind(\"SUPER + X\", \"Unsafe\", {expression})" );
            Equal( 1, source.Bindings.Count );
            True( !source.Bindings[0].Literal, expression );
        }

        var blocks = LuaBindingReader.Read( ShortcutDefaultsFixture );
        True( !blocks.Bindings.Single( static bind => bind.Description == "Workspace" ).Literal );
        True( !blocks.Bindings.Single( static bind => bind.Description == "Capture" ).Literal );
        Equal( 0, LuaBindingReader.Read( "o.bind([[unfinished" ).Bindings.Count );
        Equal( 0, LuaBindingReader.Read( "o.bind('unfinished" ).Bindings.Count );
        Equal( "A\nB", LuaTokens.Read( "\"\\x41\\010B\"" ).Single( ).Value );
        Equal( "A🙂", LuaTokens.Read( "\"A\\u{1f642}\"" ).Single( ).Value );
        True( !LuaBindingReader.Read( "function deferred() require(\"hypr.bindings\") end" ).Requires.Single( ).Ordered );
        Equal( 0, LuaBindingReader.Read( "require(\"hypr.bindings\" .. suffix)" ).Requires.Count );
    }

    private static void PrepareShortcutSources( HotkeyFixture fixture )
    {
        var defaults = Path.Combine( fixture.Root, "omarchy", "default", "hypr" );
        Directory.CreateDirectory( defaults );
        File.WriteAllText( Path.Combine( defaults, "omarchy.lua" ), "require(\"default.hypr.bindings\")\n" );
        File.WriteAllText( Path.Combine( defaults, "bindings.lua" ), ShortcutDefaultsFixture );
        File.WriteAllText( Path.Combine( defaults, "toggles.lua" ), "o.bind(\"SUPER + F8\", \"Late\", \"fictional-late\")\n" );
        File.WriteAllText( fixture.Paths.MainConfig, """
                                                   require("default.hypr.omarchy")
                                                   require("hypr.bindings")
                                                   if package.searchpath("hypr.commanddeck", package.path) then require("hypr.commanddeck") end
                                                   require("default.hypr.toggles")
                                                   """ );
        File.WriteAllText( fixture.Paths.Bindings, "hl.bind(\"SUPER + B\", hl.dsp.exec_cmd(\"browser\"), { description = \"Browser\" })\n" );
        File.WriteAllText( fixture.Paths.HotkeyFile, HyprlandHotkeyConfig.GenerateFile( "CTRL+SPACE" ) );
    }

    private const string ShortcutFixtureInventoryJson = """
        [{"modmask":64,"key":"Q","description":"Menu"},{"modmask":64,"key":"T","description":"Terminal"},
         {"modmask":64,"key":"B","description":"Browser"},{"modmask":64,"key":"F8","description":"Late"},
         {"modmask":64,"key":"1","description":"Workspace"},{"modmask":0,"key":"F9","description":"Capture","submap":"capture"}]
        """;

    private static readonly Keybinding[] ShortcutInventoryFixture =
    [
        new( ) { Chord = "Super+Q", Description = "Menu" }, new( ) { Chord = "Super+T", Description = "Terminal" },
        new( ) { Chord = "Super+B", Description = "Browser" }, new( ) { Chord = "Super+F8", Description = "Late" },
        new( ) { Chord = "Super+1", Description = "Workspace" }, new( ) { Chord = "F9", Description = "Capture", Submap = "capture" }
    ];

    private static ShortcutEditorService ShortcutService( HotkeyFixture fixture, ExternalProcessRunner? runner = null, TimeProvider? clock = null ) =>
        new( fixture.Paths, Path.Combine( fixture.Root, "omarchy" ), runner ?? (static ( _, args, _, _ ) =>
             ValueTask.FromResult( ShortcutProcessResult( ShortcutFixtureOutput( args[0] ) ) )),
            static ( ) => true, clock );

    private static string ShortcutFixtureOutput( string command )
    {
        return command switch
        {
            "configerrors" => "",
            "-j" => ShortcutFixtureInventoryJson,
            _ => "ok"
        };
    }

    private static ProcessResult ShortcutProcessResult( string output, int exitCode = 0 ) => new( )
    {
        ExitCode = exitCode,
        StandardOutput = output,
        StandardError = ""
    };

    private static async Task ShortcutSourcePolicyAndCacheAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        await using var service = ShortcutService( fixture );
        var snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        Equal( "Menu|Terminal|Browser", string.Join( '|', snapshot.Bindings.Where( static row => row.Changeable ).Select( static row => row.Description ) ) );
        // Recording's own Escape is plumbing, not a shortcut row; other submaps still list.
        var withRecording = await service.SnapshotAsync(
            [.. ShortcutInventoryFixture, new( ) { Chord = "Escape", Description = "", Submap = ShortcutLua.RecordingSubmap }], default );
        True( withRecording.Bindings.All( static row => row.Submap != ShortcutLua.RecordingSubmap ) );
        True( withRecording.Bindings.Any( static row => row.Submap == "capture" ) );
        True( snapshot.Bindings.Single( static row => row.Description == "Workspace" ).SourceFile.EndsWith( "bindings.lua", StringComparison.Ordinal ) );
        True( !snapshot.Bindings.Single( static row => row.Description == "Late" ).Changeable );
        File.WriteAllText( fixture.Paths.Bindings, "hl.bind(\"SUPER + B\", handler, { description = \"Browser\" })\n" );
        snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        True( !snapshot.Bindings.Single( static row => row.Description == "Browser" ).Changeable );
        File.WriteAllText( fixture.Paths.Bindings, "hl.bind('SUPER + B', hl.dsp.exec_cmd('browser'), {description='Browser'})\n" );
        var orderedMain = File.ReadAllText( fixture.Paths.MainConfig );
        File.WriteAllText( fixture.Paths.MainConfig, orderedMain.Replace( "require(\"hypr.bindings\")",
             "if enabled then require(\"hypr.bindings\") end", StringComparison.Ordinal ) );
        snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        True( !snapshot.Bindings.Single( static row => row.Description == "Browser" ).Changeable );
        File.WriteAllText( fixture.Paths.MainConfig, orderedMain.Replace( "require(\"hypr.bindings\")",
             "if package.searchpath(\"hypr.bindings\", package.path) then require(\"hypr.bindings\") end", StringComparison.Ordinal ) );
        snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        True( snapshot.Bindings.Single( static row => row.Description == "Browser" ).Changeable );
        File.AppendAllText( fixture.Paths.Bindings, "local hl = {}\n" );
        snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        True( !snapshot.Bindings.Single( static row => row.Description == "Browser" ).Changeable );
        File.WriteAllText( fixture.Paths.MainConfig, "require(\"hypr.bindings\")\n" );
        snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        True( snapshot.Bindings.All( static row => !row.Changeable ) );
    }

    private static async Task ShortcutMoveRemoveAddReplaceAndResetAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        var originalBindings = File.ReadAllText( fixture.Paths.Bindings );
        var originalMain = File.ReadAllText( fixture.Paths.MainConfig );
        await using var service = ShortcutService( fixture );
        var snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        var menu = snapshot.Bindings.Single( static row => row.Description == "Menu" );
        var terminal = snapshot.Bindings.Single( static row => row.Description == "Terminal" );
        var conflict = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id, Chord = "Super+T" }, false,
             ShortcutInventoryFixture, default );
        Equal( "Terminal", conflict.ConflictDescription );
        True( !File.Exists( service.ModuleFile ) );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id, Chord = "Super+T", Replace = true }, false,
             ShortcutInventoryFixture, default );
        Equal( 2, snapshot.ChangeCount );
        var lua = File.ReadAllText( service.ModuleFile );
        True( lua.Contains( "hl.unbind(\"SUPER + Q\")", StringComparison.Ordinal ) );
        True( lua.Contains( "hl.unbind(\"SUPER + T\")", StringComparison.Ordinal ) );
        True( lua.Contains( "o.bind(\"SUPER + T\", \"Menu\", hl.dsp.exec_cmd([[fictional-menu --root]]), { repeatable = false })", StringComparison.Ordinal ) );
        Equal( "", snapshot.Bindings.Single( row => row.Id == terminal.Id ).Chord );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = menu.Id }, true, ShortcutInventoryFixture, default );
        Equal( 0, snapshot.ChangeCount );
        True( !File.Exists( service.ModuleFile ) );
        await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ChangeAsync( ProtocolOperations.ShortcutSet,
             new( ) { Id = menu.Id }, false, ShortcutInventoryFixture, default ) );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id }, true, ShortcutInventoryFixture, default );
        Equal( 1, snapshot.ChangeCount );
        True( !File.ReadAllText( service.ModuleFile ).Contains( "o.bind(", StringComparison.Ordinal ) );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( )
        {
            Id = "added:fixture",
            Description = "A \"title\"",
            Command = "echo 'quote'\nnext\\path",
            Chord = "Super+F12"
        }, false, ShortcutInventoryFixture, default );
        var calls = LuaBindingReader.Read( File.ReadAllText( service.ModuleFile ) ).Bindings;
        Equal( "A \"title\"", calls.Single( ).Description );
        True( calls.Single( ).Literal );
        True( File.Exists( service.ModuleFile + ".bak" ) );
        await using ( var reopened = ShortcutService( fixture ) )
        {
            Equal( 2, (await reopened.SnapshotAsync( ShortcutInventoryFixture, default )).ChangeCount );
        }

        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutResetAll, new( ) { Id = "all" }, true, ShortcutInventoryFixture, default );
        Equal( 0, snapshot.ChangeCount );
        Equal( originalBindings, File.ReadAllText( fixture.Paths.Bindings ) );
        Equal( originalMain, File.ReadAllText( fixture.Paths.MainConfig ) );
    }

    private static async Task ShortcutReloadFailureRestoresFilesAsync( )
    {
        if ( OperatingSystem.IsWindows( ) )
        {
            throw new SpecSkippedException( "This fixture requires Unix symbolic links and modes." );
        }

        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        var fails = false;
        var reloads = 0;
        await using var service = ShortcutService( fixture, ( _, arguments, _, _ ) =>
        {
            if ( arguments[0] == "-j" )
            {
                return ValueTask.FromResult( ShortcutProcessResult( ShortcutFixtureInventoryJson ) );
            }

            if ( arguments[0] == "configerrors" )
            {
                return ValueTask.FromResult( ShortcutProcessResult( "" ) );
            }

            Equal( "reload", arguments[0] );
            reloads++;

            return ValueTask.FromResult( ShortcutProcessResult( fails ? "broken" : "ok", fails ? 1 : 0 ) );
        } );
        var snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        var id = snapshot.Bindings[0].Id;
        await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = id, Chord = "Super+J" }, false, ShortcutInventoryFixture, default );
        var old = File.ReadAllText( service.ModuleFile );
        fails = true;
        await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( )
        {
            Id = id,
            Chord = "Super+K"
        }, false, ShortcutInventoryFixture, default ) );
        Equal( old, File.ReadAllText( service.ModuleFile ) );
        Equal( old, File.ReadAllText( service.ModuleFile + ".bak" ) );
        Equal( "SUPER + J", (await service.SnapshotAsync( ShortcutInventoryFixture, default )).Bindings.Single( row => row.Id == id ).Chord );
        Equal( 3, reloads );

        var target = Path.Combine( fixture.Root, "linked-keys.lua" );
        File.Move( service.ModuleFile, target );
        File.SetUnixFileMode( target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead );
        File.CreateSymbolicLink( service.ModuleFile, Path.GetRelativePath( Path.GetDirectoryName( service.ModuleFile )!, target ) );
        var link = new FileInfo( service.ModuleFile ).LinkTarget;
        await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ChangeAsync(
            ProtocolOperations.ShortcutResetAll, new( ) { Id = "all" }, true, ShortcutInventoryFixture, default ) );
        Equal( link, new FileInfo( service.ModuleFile ).LinkTarget );
        Equal( old, File.ReadAllText( target ) );
        Equal( UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode( target ) );
        Equal( "SUPER + J", (await service.SnapshotAsync( ShortcutInventoryFixture, default )).Bindings.Single( row => row.Id == id ).Chord );
    }

    private static async Task ShortcutWriteFailureKeepsModuleAsync( )
    {
        using HotkeyFixture fixture = new( );
        var module = Path.Combine( fixture.Root, "keys.lua" );
        var blocked = Path.Combine( fixture.Root, "directory" );
        File.WriteAllText( module, "old" );
        Directory.CreateDirectory( blocked );
        await ThrowsAsync<IOException>( async ( ) =>
             await ShortcutFileTransaction.ApplyAsync( new Dictionary<string, string?>( StringComparer.Ordinal ) { [module] = "new", [blocked] = "bad" },
            static _ => ValueTask.CompletedTask, default ) );
        Equal( "old", File.ReadAllText( module ) );
        Equal( "old", File.ReadAllText( module + ".bak" ) );
    }
}
