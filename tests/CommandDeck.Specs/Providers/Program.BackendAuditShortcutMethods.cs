namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditHotkeyReinstallAsync( )
    {
        using ( ShortcutExecutionFixture fixture = new( "" ) )
        {
            var menu = await fixture.RowAsync( "Menu" );
            await fixture.ChangeAsync( menu.Id, "Ctrl+Space", replace: true );
            fixture.Expect( "Ctrl+Space", "Menu" );
            var state = Path.Combine( fixture.Files.Root, "state", "commanddeck", "shortcut-overrides.json" );
            var saved = await File.ReadAllTextAsync( state );
            await InstallAsync( fixture );
            fixture.Expect( "Ctrl+Space", "Menu" );
            Equal( saved, await File.ReadAllTextAsync( state ) );
            await InstallAsync( fixture, "--chord", "Ctrl+Alt+F11" );
            fixture.Expect( "Ctrl+Space", "Menu" );
            fixture.Expect( "Ctrl+Alt+F11", "CommandDeck" );
            Equal( "CTRL + ALT + F11", (await fixture.Service.SnapshotAsync( fixture.Inventory, default )).Hotkey );
            await InstallAsync( fixture );
            fixture.Expect( "Ctrl+Space", "Menu" );
            fixture.Expect( "Ctrl+Alt+F11", "CommandDeck" );
            await InstallAsync( fixture, "--chord", "Ctrl+Space" );
            fixture.Expect( "Ctrl+Space", "CommandDeck" );
            Equal( "", (await fixture.Service.SnapshotAsync( fixture.Inventory, default )).Bindings.Single( row => row.Id == menu.Id ).Chord );
        }

        using ( ShortcutExecutionFixture fixture = new( "o.bind('SUPER + Q', 'Shared', 'shared')\n" ) )
        {
            await fixture.Service.ChangeAsync( "shortcut.hotkey", new( ) { Id = "hotkey", Chord = "Super+Q", Replace = true },
                false, fixture.Inventory, default );
            fixture.Expect( "Super+Q", "CommandDeck", "Shared" );
            await InstallAsync( fixture );
            fixture.Expect( "Super+Q", "CommandDeck", "Shared" );
        }

        using ( ShortcutExecutionFixture fixture = new( "" ) )
        {
            fixture.Files.Installer.Install( "ALT + SPACE" );
            fixture.Reload( );
            await InstallAsync( fixture );
            fixture.Expect( "Alt+Space", "CommandDeck" );
            fixture.Expect( "Ctrl+Space" );
        }

        using ( ShortcutExecutionFixture fixture = new( "" ) )
        {
            File.Delete( fixture.Files.Paths.HotkeyFile );
            File.WriteAllText( fixture.Files.Paths.MainConfig,
                HyprlandHotkeyConfig.RemoveRequire( File.ReadAllText( fixture.Files.Paths.MainConfig ) ) );
            fixture.Table.Clear( );
            fixture.Table.Execute( File.ReadAllText( fixture.Files.Paths.Bindings ), "" );
            var menu = await fixture.RowAsync( "Menu" );
            await fixture.ChangeAsync( menu.Id, "Super+F12" );
            await InstallAsync( fixture );
            True( !(await fixture.Service.SnapshotAsync( fixture.Inventory, default )).HotkeyManaged );
            fixture.Expect( "Super+F12", "Menu" );
        }

        using ( HotkeyFixture fixture = new( ) )
        {
            File.WriteAllText( fixture.Paths.MainConfig, StockHyprlandLua );
            var outcome = await RunHotkeyCliAsync( Path.GetDirectoryName( fixture.Paths.HyprDirectory )!,
                Path.Combine( fixture.Root, "state" ), "hotkey", "install" );
            Equal( 0, outcome.ExitCode );
            Equal( "CTRL + SPACE", HyprlandHotkeyConfig.ReadChord( File.ReadAllText( fixture.Paths.HotkeyFile ) ) );
        }

        static async Task InstallAsync( ShortcutExecutionFixture fixture, params string[] arguments )
        {
            var configHome = Path.GetDirectoryName( fixture.Files.Paths.HyprDirectory )!;
            var stateHome = Path.Combine( fixture.Files.Root, "state" );
            var outcome = await RunHotkeyCliAsync( configHome, stateHome, ["hotkey", "install", .. arguments] );
            Equal( 0, outcome.ExitCode );
            fixture.Reload( );
        }
    }

    private static async Task AuditShortcutModifiersAsync( )
    {
        using ShortcutExecutionFixture fixture = new( "o.bind('CTRL + Q', 'Control action', 'control')\n" );
        var menu = await fixture.RowAsync( "Menu" );
        var result = await fixture.Service.ChangeAsync( "shortcut.set", new( ) { Id = menu.Id, Chord = "Ctrl+Mod3+Q" }, false, fixture.Inventory, default );
        Equal<string?>( null, result.ConflictId );
        fixture.Expect( "Ctrl+Q", "Control action" );
        fixture.Expect( "Ctrl+Mod3+Q", "Menu" );
        Equal( "SUPER+CTRL+ALT+SHIFT+MOD2+MOD3+MOD5+Q", ShortcutLua.CanonicalChord( "Mod5+Shift+Ctrl+Mod3+Alt+Mod2+Super+Q" ) );
        Equal( "CTRL+CUSTOM+Q", ShortcutLua.CanonicalChord( "Custom+Ctrl+Q" ) );
    }
}
