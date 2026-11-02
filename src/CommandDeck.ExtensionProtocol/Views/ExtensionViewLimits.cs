namespace CommandDeck.Extensions.Views;

public static class ExtensionViewLimits
{
    public const int MaximumItemIdBytes = CommandDeck.ExtensionProtocol.ExtensionItemIdLimits.MaximumBytes;
    public const int MaximumEncodedViewBytes = 240 * CommandDeck.ExtensionProtocol.ByteUnits.BytesPerKibibyte;
    public const int MaximumParentViews = 7;
    public const int MaximumPngAssetBytes = 32768;
}
