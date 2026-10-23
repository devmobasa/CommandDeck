namespace CommandDeck;

public static class ProtocolLimits
{
    public const int CurrentVersion = 3;
    public const int MaximumRequestBytes = 256 * ByteUnits.BytesPerKibibyte;
    public const int MaximumResponseBytes = 2 * ByteUnits.BytesPerMebibyte;
    public const int MaximumCustomizationExportBytes = 256 * ByteUnits.BytesPerKibibyte;
    public const int MaximumCustomizationSummaries = 256;
    public const int MaximumQueryCharacters = 4 * 1024;
    public const int MaximumIdentifierCharacters = 512;
    public const int MaximumConcurrentRequestsPerClient = 4;
    public const int MaximumPreviewBytes = 8 * ByteUnits.BytesPerKibibyte;
    public const int MaximumPreviewLines = 200;
}
