namespace CommandDeck.ExtensionProtocol;

// Shared source keeps Core independent of the extension assembly while both
// sides of the activation contract compile the same byte limit.
internal static class ExtensionItemIdLimits
{
    public const int MaximumBytes = 256;
}
