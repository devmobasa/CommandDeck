namespace CommandDeck.ExtensionProtocol;

public static class ExtensionStorageLimits
{
    public const int MaximumEntries = 64;
    public const int MaximumValueBytes = 16 * ByteUnits.BytesPerKibibyte;
    public const int MaximumInstallationBytes = 64 * ByteUnits.BytesPerKibibyte;
}
