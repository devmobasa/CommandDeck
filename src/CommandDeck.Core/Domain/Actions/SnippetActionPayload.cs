namespace CommandDeck;

// A snippet that reads clipboard history carries a marker so its row is treated as
// private clipboard data: never on Home, in suggestions, or given to the assistant.
public sealed record SnippetActionPayload( string Template, bool ReadsClipboardHistory = false )
{
    private const string EnabledArgumentValue = "true";
    private const string ClipboardHistoryKey = "clipboardHistory";

    public Dictionary<string, string> ToArguments( )
    {
        Dictionary<string, string> arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Template] = Template };
        if ( ReadsClipboardHistory )
        {
            arguments[ClipboardHistoryKey] = EnabledArgumentValue;
        }

        return arguments;
    }

    public static SnippetActionPayload Read( IReadOnlyDictionary<string, string> arguments )
    {
        var template = arguments.GetValueOrDefault( ActionArgumentNames.Template );
        if ( string.IsNullOrWhiteSpace( template ) )
        {
            throw new InvalidOperationException( "Action requires 'template'." );
        }

        return new SnippetActionPayload( template, arguments.GetValueOrDefault( ClipboardHistoryKey ) == EnabledArgumentValue );
    }

    public static bool IsHistoryReader( CommandAction action ) =>
        action.Kind is ActionKinds.SnippetCopy or ActionKinds.SnippetPreview &&
        action.Arguments.GetValueOrDefault( ClipboardHistoryKey ) == EnabledArgumentValue;
}
