namespace CommandDeck;

// Query-free commands that copy clipboard history one entry at a time. Their
// actions are clipboard-history actions, so the rows are private clipboard rows:
// never on Home or customizable, and never recorded, suggested, attached to AI
// requests, or offered to assistant tools.
public static class ClipboardSequenceCommands
{
    private const string NextTitle = "Copy next clipboard entry";
    private const string RestartTitle = "Restart clipboard sequence";

    public const string NextId = "clipboard-sequence:next";
    public const string RestartId = "clipboard-sequence:restart";
    public const string NextAction = "sequence-next";
    public const string RestartAction = "sequence-restart";

    public static bool IsCommand( string stableId ) => stableId is NextId or RestartId;

    public static IEnumerable<IndexedItem> CreateItems( ) =>
    [
        Item( NextId, NextTitle, "Copy the next entry of the clipboard sequence, oldest first" ),
        Item( RestartId, RestartTitle, "Copy next clipboard entry starts with your next copy" )
    ];

    public static CommandAction CreateAction( string stableId ) => new( )
    {
        Id = stableId == NextId ? NextAction : RestartAction,
        Title = stableId == NextId ? NextTitle : RestartTitle,
        Kind = ActionKinds.ClipboardHistory,
        Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Entry] = "" }
    };

    public static string Started( int count )
    {
        if ( count == 1 )
        {
            return "Clipboard sequence starts here with 1 entry.";
        }
        else
        {
            return $"Clipboard sequence starts here with {count} entries.";
        }
    }

    private static IndexedItem Item( string id, string title, string subtitle ) => new( )
    {
        StableId = id,
        Kind = ResultKinds.CustomCommand,
        Title = title,
        Subtitle = subtitle,
        Icon = "edit-paste",
        ProviderId = ProviderIds.Commands,
        SourceId = SourceIds.CommandsQuicklinks
    };
}
