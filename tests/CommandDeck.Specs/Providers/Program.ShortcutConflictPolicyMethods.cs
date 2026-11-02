namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ShortcutCannotReplaceBindingsLoadedAfterItsModuleAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        await using var service = ShortcutService( fixture );
        var menu = (await service.SnapshotAsync( ShortcutInventoryFixture, default )).Bindings.Single( static row => row.Description == "Menu" );
        var conflict = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id, Chord = "Super+F8" }, false,
             ShortcutInventoryFixture, default );
        Equal( "Late", conflict.ConflictDescription );
        await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ChangeAsync( ProtocolOperations.ShortcutSet,
            new( ) { Id = menu.Id, Chord = "Super+F8", Replace = true }, false, ShortcutInventoryFixture, default ) );
        True( !File.Exists( service.ModuleFile ) );
        Equal( 0, (await service.SnapshotAsync( ShortcutInventoryFixture, default )).ChangeCount );
    }

    private static async Task ShortcutGeneratedKeysKeepDistinctRemovalIdentitiesAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        var json = ShortcutFixtureInventoryJson.TrimEnd( ).TrimEnd( ']' ) + ",{\"modmask\":64,\"key\":\"2\",\"description\":\"Workspace\"}]";
        var inventory = HyprlandShortcutInspector.ParseKeybindings( json );
        await using var service = ShortcutService( fixture, ( _, arguments, _, _ ) => ValueTask.FromResult( ShortcutProcessResult(
            arguments[0] switch { "configerrors" => "", "-j" => json, _ => "ok" } ) ) );
        var before = await service.SnapshotAsync( inventory, default );
        var generated = before.Bindings.Where( static row => row.Description == "Workspace" ).ToList( );
        Equal( 2, generated.Select( static row => row.Id ).Distinct( StringComparer.Ordinal ).Count( ) );
        var menu = before.Bindings.Single( static row => row.Description == "Menu" );
        var first = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id, Chord = "Super+1", Replace = true }, false,
             inventory, default );
        True( first.Bindings.Any( static row => row.Description == "Workspace" && row.Chord == "Super+2" ) );
        var second = await service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = menu.Id, Chord = "Super+2", Replace = true },
             false, inventory, default );
        Equal( 2, second.Bindings.Count( static row => row.Description == "Workspace" && row.Chord.Length == 0 ) );
        var lua = File.ReadAllText( service.ModuleFile );
        True( lua.Contains( "hl.unbind(\"Super + 1\")", StringComparison.Ordinal ) );
        True( lua.Contains( "hl.unbind(\"Super + 2\")", StringComparison.Ordinal ) );
    }
}
