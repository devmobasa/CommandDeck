namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task RunUnconfirmedAuditRegistrationsAsync( )
    {
        Run( "unconfirmed audit highlights keep complete graphemes", AuditHighlightGraphemes );

        await RunAsync( "unconfirmed audit structured title fields retain restrictions", AuditStructuredTitleAsync );
        await RunAsync( "unconfirmed audit short circuited requires stay read only", AuditSkippedRequireAsync );
        await RunAsync( "unconfirmed audit skipped tar data counts toward extraction bounds", AuditSkippedTarAsync );
        await RunAsync( "unconfirmed audit package output pipes obey the deadline", AuditPackagePipeDeadlineAsync );
        await RunAsync( "unconfirmed audit package operation history is bounded", AuditPackageRetentionAsync );
        Run( "unconfirmed audit extension item ids share a UTF8 byte limit", AuditExtensionItemIds );
        await RunAsync( "unconfirmed audit cancelled codex startup reaps children", ( ) => AuditAssistantStartupAsync( false ) );
        await RunAsync( "unconfirmed audit cancelled CLI startup reaps children", ( ) => AuditAssistantStartupAsync( true ) );
        await RunAsync( "unconfirmed audit cancelled unit submission stops the unit", AuditUnitStartupAsync );
        Run( "unconfirmed audit fallback socket directory is private", AuditFallbackSocketMode );
        Run( "unconfirmed audit fallback socket rejects symbolic links", AuditFallbackSocketLink );
        await RunAsync( "unconfirmed audit socket peer identity rejects a different UID", AuditSocketPeerAsync );
    }
}
