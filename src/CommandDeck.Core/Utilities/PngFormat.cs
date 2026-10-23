namespace CommandDeck;

public static class PngFormat
{
    public const string PngDataUrlPrefix = "data:image/png;base64,";
    public static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    public static ReadOnlySpan<byte> HeaderChunkType => "IHDR"u8;
}
