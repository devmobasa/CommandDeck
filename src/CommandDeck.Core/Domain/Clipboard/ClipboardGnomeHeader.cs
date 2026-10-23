namespace CommandDeck;

public enum ClipboardGnomeHeaderResult
{
    Copy, Cut, Invalid
}

public readonly record struct ClipboardGnomeHeader( ClipboardGnomeHeaderResult Result, ReadOnlyMemory<byte> Bytes )
{
    internal const int MaximumBytes = 8;

    /// <summary>Consumes through the first LF only, without buffering or prefetching body bytes.</summary>
    public static async Task<ClipboardGnomeHeader> ReadAsync( Stream offer, CancellationToken cancellationToken )
    {
        var bytes = new byte[MaximumBytes];
        for ( var length = 0; length < bytes.Length; length++ )
        {
            if ( await offer
                     .ReadAsync( bytes.AsMemory( length, 1 ), cancellationToken )
                     .ConfigureAwait( false ) ==
                 0 )
            {
                return new( ClipboardGnomeHeaderResult.Invalid, bytes.AsMemory( 0, length ) );
            }

            if ( bytes[length] != ( byte ) '\n' )
            {
                continue;
            }

            var header = bytes.AsMemory( 0, length + 1 );
            var result = ClassifyGnomeHeader( header );

            return new( result, header );
        }

        return new( ClipboardGnomeHeaderResult.Invalid, bytes );
    }

    private static ClipboardGnomeHeaderResult ClassifyGnomeHeader( Memory<byte> header )
    {
        if ( IsCopyHeader( header.Span ) )
        {
            return ClipboardGnomeHeaderResult.Copy;
        }
        else
        {
            if ( IsCutHeader( header ) )
            {
                return ClipboardGnomeHeaderResult.Cut;
            }
            else
            {
                return ClipboardGnomeHeaderResult.Invalid;
            }
        }
    }

    public static bool IsCopyHeader( ReadOnlySpan<byte> header )
    {
        return header.SequenceEqual( "copy\n"u8 ) || header.SequenceEqual( "copy\r\n"u8 );
    }

    private static bool IsCutHeader( Memory<byte> header )
    {
        return header.Span.SequenceEqual( "cut\n"u8 ) || header.Span.SequenceEqual( "cut\r\n"u8 );
    }
}
