namespace CommandDeck;

public static class AppLog
{
    private const string DebugName = "debug";
    private const string InfoName = "info";
    private const string WarningName = "warning";
    private const string ErrorName = "error";
    private const int LevelColumnWidthCharacters = 7;

    public static bool Verbose { get; set; }

    public static void Debug( string message )
    {
        if ( Verbose )
        {
            Write( DebugName, message );
        }
    }

    public static void Info( string message ) => Write( InfoName, message );
    public static void Warning( string message ) => Write( WarningName, message );

    public static void Error( string message, Exception? exception = null ) =>
        Write( ErrorName, exception is null ? message : $"{message} {exception}" );

    // Specs capture lines here instead of scraping stderr.
    internal static Action<string, string>? Sink { get; set; }

    private static void Write( string level, string message )
    {
        if ( Sink is { } sink )
        {
            sink( level, message );

            return;
        }

        Console.Error.WriteLine( $"{DateTimeOffset.UtcNow:O} {level.ToUpperInvariant( ),-LevelColumnWidthCharacters} {message}" );
    }
}
