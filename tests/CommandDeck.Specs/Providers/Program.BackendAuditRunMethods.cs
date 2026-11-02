namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditRunShortcutArgumentsAsync( )
    {
        const string query = """> /usr/bin/true "two words" "" it's a"b" "$HOME" """;
        RunCommandProvider provider = new( );
        var row = (await provider.SearchAsync( QueryParser.Parse( query ), new SearchRequest { Query = query }, default )).Single( );
        KeybindingHints hints = new( static _ => ValueTask.FromResult<string?>( "[]" ), static ( ) => true );
        var decorated = hints.Decorate( [row] ).Single( );
        var add = decorated.Actions.Single( static action => action.Id == KeybindingHints.ChangeShortcutActionId );
        Equal( """/usr/bin/true 'two words' '' 'it'\''s' 'a"b"' '$HOME'""", add.Arguments["shortcutCommand"] );
        var copy = decorated.Actions.Single( static action => action.Id == KeybindingHints.CopyBindingActionId );
        var text = TextActionPayload.Read( copy.Arguments ).Text;
        True( text.Contains( """hl.dsp.exec_cmd("/usr/bin/true 'two words' '' 'it'\\''s' 'a\"b\"' '$HOME'")""", StringComparison.Ordinal ) );
        await hints.Refresh;
    }
}
