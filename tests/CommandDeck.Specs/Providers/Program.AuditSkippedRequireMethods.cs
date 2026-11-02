namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditSkippedRequireAsync( )
    {
        using ShortcutExecutionFixture fixture = new( "" );
        var original = File.ReadAllText( fixture.Files.Paths.MainConfig );
        var withoutModule = HyprlandHotkeyConfig.RemoveRequire( original );
        foreach ( var expression in new[] { "false and require('hypr.commanddeck')", "true or require('hypr.commanddeck')", "require('hypr.commanddeck')" } )
        {
            File.WriteAllText( fixture.Files.Paths.MainConfig, withoutModule + "\nlocal unused = " + expression + "\n" );
            var snapshot = await fixture.Service.SnapshotAsync( fixture.Inventory, default );
            True( !snapshot.ModuleInstalled, expression );
            True( snapshot.Bindings.All( static binding => !binding.Changeable ), expression );
        }

        File.WriteAllText( fixture.Files.Paths.MainConfig, original );
        True( (await fixture.Service.SnapshotAsync( fixture.Inventory, default )).ModuleInstalled );
    }
}
