namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ShortcutManualHotkeyGetsSupportWithoutRebindingAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        File.Delete( fixture.Paths.HotkeyFile );
        File.WriteAllText( fixture.Paths.MainConfig, HyprlandHotkeyConfig.RemoveRequire( File.ReadAllText( fixture.Paths.MainConfig ) ) );
        var personal = File.ReadAllText( fixture.Paths.Bindings );
        await using var service = ShortcutService( fixture );
        var snapshot = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        True( !snapshot.ModuleInstalled );
        var menu = snapshot.Bindings.Single( static row => row.Description == "Menu" );
        True( menu.Changeable );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id, Chord = "Super+J" }, false,
             ShortcutInventoryFixture, default );
        True( snapshot.ModuleInstalled );
        True( !snapshot.HotkeyManaged );
        var module = File.ReadAllText( fixture.Paths.HotkeyFile );
        True( !module.Contains( "commanddeck:toggle", StringComparison.Ordinal ) );
        Equal( 1, HyprlandHotkeyConfig.CountRequires( File.ReadAllText( fixture.Paths.MainConfig ) ) );
        Equal( personal, File.ReadAllText( fixture.Paths.Bindings ) );
        await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ChangeAsync( ProtocolOperations.ShortcutHotkey,
            new( ) { Id = "hotkey", Chord = "Super+F3" }, false, ShortcutInventoryFixture, default ) );
    }

    private static async Task ShortcutConfigurationErrorRollsBackAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        var previous = File.ReadAllText( fixture.Paths.HotkeyFile );
        await using var service = ShortcutService( fixture, ( _, arguments, _, _ ) =>
            ValueTask.FromResult( ShortcutProcessResult( arguments[0] == "configerrors" ? "Lua error" : "ok" ) ) );
        await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ChangeAsync( ProtocolOperations.ShortcutHotkey,
            new( ) { Id = "hotkey", Chord = "Super+F3" }, false, ShortcutInventoryFixture, default ) );
        Equal( previous, File.ReadAllText( fixture.Paths.HotkeyFile ) );
        Equal( 0, (await service.SnapshotAsync( ShortcutInventoryFixture, default )).ChangeCount );
    }

    private static async Task ShortcutReplaceGlobalHotkeyAndRestoreAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        await using var service = ShortcutService( fixture );
        Keybinding[] inventory = [.. ShortcutInventoryFixture, new( ) { Chord = "Ctrl+Space", Description = "CommandDeck" }];
        var snapshot = await service.SnapshotAsync( inventory, default );
        var id = snapshot.Bindings[0].Id;
        var conflict = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = id, Chord = "Ctrl+Space" }, false, inventory, default );
        Equal( "Open CommandDeck", conflict.ConflictDescription );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = id, Chord = "Ctrl+Space", Replace = true }, false,
             inventory, default );
        Equal( "", snapshot.Hotkey );
        True( snapshot.Bindings.Single( static row => row.LauncherHotkey ).Changed );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = id }, true, inventory, default );
        Equal( "CTRL + SPACE", snapshot.Hotkey );
        True( !File.Exists( service.ModuleFile ) );

        await service.ChangeAsync( ProtocolOperations.ShortcutHotkey, new( ) { Id = "hotkey", Chord = "Super+F3" }, false, ShortcutInventoryFixture, default );
        Keybinding[] current = [.. ShortcutInventoryFixture, new( ) { Chord = "Super+F3", Description = "CommandDeck" }];
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = id, Chord = "Super+F3", Replace = true }, false, current, default );
        Equal( "", snapshot.Hotkey );
        True( !snapshot.Lua.Contains( "hl.dsp.global(\"commanddeck:toggle\")", StringComparison.Ordinal ),
             "export must not reinstall a suppressed global binding" );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = id }, true, current, default );
        Equal( "SUPER + F3", snapshot.Hotkey );
        Equal( 1, snapshot.ChangeCount );
        await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = id, Chord = "Super+F3", Replace = true }, false, current, default );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = "hotkey" }, true, current, default );
        Equal( "CTRL + SPACE", snapshot.Hotkey );
        Equal( 1, snapshot.ChangeCount );
        True( snapshot.Bindings.All( static row => !row.LauncherHotkey ) );
        Equal( "SUPER + F3", snapshot.Bindings.Single( static row => row.Description == "Menu" && row.Changed ).Chord );
    }
}
