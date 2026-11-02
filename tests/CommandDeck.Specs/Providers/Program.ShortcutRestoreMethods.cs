namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ShortcutRestoreFreesAChordTakenByTheLauncherAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        await using var service = ShortcutService( fixture );
        var before = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        var menu = before.Bindings.Single( static row => row.Description == "Menu" );
        var conflict = await service.ChangeAsync( ProtocolOperations.ShortcutHotkey, new( ) { Id = "hotkey", Chord = "Super+Q" }, false,
             ShortcutInventoryFixture, default );
        Equal( "Menu", conflict.ConflictDescription );
        var changed = await service.ChangeAsync( ProtocolOperations.ShortcutHotkey, new( ) { Id = "hotkey", Chord = "Super+Q", Replace = true },
             false, ShortcutInventoryFixture, default );
        Equal( "SUPER + Q", changed.Hotkey );
        Equal( "", changed.Bindings.Single( row => row.Id == menu.Id ).Chord );
        var restored = await service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = menu.Id }, true, ShortcutInventoryFixture, default );
        Equal( "CTRL + SPACE", restored.Hotkey );
        Equal( 0, restored.ChangeCount );
        True( !File.Exists( service.ModuleFile ) );
    }

    private static async Task ShortcutRestoreFreesAChordTakenByAnotherDesktopActionAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        await using var service = ShortcutService( fixture );
        var before = await service.SnapshotAsync( ShortcutInventoryFixture, default );
        var menu = before.Bindings.Single( static row => row.Description == "Menu" );
        var terminal = before.Bindings.Single( static row => row.Description == "Terminal" );
        await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id, Chord = "Super+T", Replace = true }, false,
             ShortcutInventoryFixture, default );
        var restored = await service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = terminal.Id }, true, ShortcutInventoryFixture, default );
        Equal( 0, restored.ChangeCount );
        True( !File.Exists( service.ModuleFile ) );
        Equal( "Super+T", restored.Bindings.Single( static row => row.Description == "Terminal" ).Chord );
    }

    private static async Task ShortcutRestoreReadsTheReloadedInventoryAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        List<string> commands = [];
        var response = ShortcutFixtureInventoryJson.Replace( "\"key\":\"Q\"", "\"key\":\"J\"", StringComparison.Ordinal );
        await using var service = ShortcutService( fixture, ( _, arguments, _, _ ) =>
        {
            commands.Add( string.Join( ' ', arguments ) );

            return ValueTask.FromResult( ShortcutProcessResult( arguments[0] switch { "configerrors" => "", "-j" => response, _ => "ok" } ) );
        } );
        var menu = (await service.SnapshotAsync( ShortcutInventoryFixture, default )).Bindings.Single( static row => row.Description == "Menu" );
        await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id, Chord = "Super+J" }, false, ShortcutInventoryFixture, default );
        response = ShortcutFixtureInventoryJson;
        var restored = await service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = menu.Id }, true,
            [new( ) { Description = "Menu", Chord = "Super+J" }], default );
        var row = restored.Bindings.Single( static binding => binding.Description == "Menu" );
        Equal( "Super+Q", row.Chord );
        True( row.Changeable );
        Equal( "reload|configerrors|-j binds|reload|configerrors|-j binds", string.Join( '|', commands ) );
    }
}
