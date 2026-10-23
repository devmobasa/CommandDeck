namespace CommandDeck.ExtensionProtocol;

public static class ExtensionPackageLimits
{
    public const long MaximumExpandedBytes = 500L * ByteUnits.BytesPerMebibyte;
    public const int MaximumArchiveEntries = 10000;
}
