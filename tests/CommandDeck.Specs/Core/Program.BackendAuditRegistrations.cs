namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task RunBackendAuditRegistrationsAsync( )
    {
        Run( "backend audit CLI flags preserve search positionals and option terminator", AuditCliPositionals );

        Run( "backend audit grouped fractions and currency rates preserve precedence", AuditGroupedCalculation );

        await RunAsync( "backend audit hotkey reinstall preserves Settings and applies explicit chords", AuditHotkeyReinstallAsync );
        await RunAsync( "backend audit editors detach with literal argv and terminal preference", AuditDetachedEditorsAsync );
        await RunAsync( "backend audit index controls reach the daemon owner", AuditIndexDaemonControlsAsync );
        await RunAsync( "backend audit moved subtree replaced by a file removes descendants", AuditIncrementalDescendantsAsync );
        await RunAsync( "backend audit socket startup preserves non-sockets and active listeners", AuditSocketPathsAsync );
        await RunAsync( "backend audit quoted phrases require contiguous catalog matches", AuditQuotedPhraseAsync );
        await RunAsync( "backend audit Run shortcut decodes JSON argv with literal shell words", AuditRunShortcutArgumentsAsync );
        await RunAsync( "backend audit null shortcut fields keep the connection usable", AuditNullShortcutFieldsAsync );
        await RunAsync( "backend audit shortcut modifiers do not create false conflicts", AuditShortcutModifiersAsync );
    }
}
