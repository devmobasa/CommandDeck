using System.Text;

namespace CommandDeck;

/// <summary>Reads one bounded loopback callback. Authority comes from Host, state and the configured issuer.</summary>
internal static class OAuthLoopbackReceiver
{
    private const int MaximumHeaderBytes = 8192;
    private const int MaximumCodeCharacters = 2048;

    public static async ValueTask<string> ReadCodeAsync( Stream stream, string redirect, string state, string issuer, CancellationToken token )
    {
        var request = new byte[MaximumHeaderBytes + 1];
        var count = 0;
        while ( count < request.Length )
        {
            var read = await stream
                .ReadAsync( request.AsMemory( count, 1 ), token )
                .ConfigureAwait( false );

            if ( read == 0 )
            {
                throw new InvalidDataException( "Incomplete callback." );
            }

            count += read;

            if ( IsHeaderComplete( request, count ) )
            {
                break;
            }
        }

        if ( count > MaximumHeaderBytes )
        {
            throw new InvalidDataException( "Callback too large." );
        }

        var lines = Encoding
            .ASCII.GetString( request, 0, count )
            .Split( "\r\n" );

        var first = lines[0]
            .Split( ' ' );

        if ( IsCallbackOriginInvalid( redirect, lines, first ) )
        {
            throw new InvalidDataException( "Invalid callback origin." );
        }

        var values = new Dictionary<string, string>( StringComparer.Ordinal );

        foreach ( var pair in first[1][10..]
                     .Split( '&' ) )
        {
            var parts = pair.Split( '=', 2 );
            if ( !TryAddCallbackParameter( values, parts ) )
            {
                throw new InvalidDataException( "Invalid callback parameters." );
            }
        }

        if ( values.GetValueOrDefault( "state" ) != state ||
             values.GetValueOrDefault( "iss" ) != issuer ||
             !values.TryGetValue( "code", out var code ) ||
             code.Length is < 1 or > MaximumCodeCharacters ||
             values.ContainsKey( "error" ) )
        {
            throw new InvalidDataException( "Callback state or issuer mismatch." );
        }

        return code;
    }

    private static bool IsHeaderComplete( byte[] request, int count )
    {
        return count >= 4 &&
               request
                   .AsSpan( count - 4, 4 )
                   .SequenceEqual( "\r\n\r\n"u8 );
    }

    private static bool IsCallbackOriginInvalid( string redirect, string[] lines, string[] first )
    {
        return first is not ["GET", _, "HTTP/1.1"] ||
               !first[1]
                   .StartsWith( "/callback?", StringComparison.Ordinal ) ||
               lines.Count( line => line.StartsWith( "Host:", StringComparison.OrdinalIgnoreCase ) ) != 1 ||
               !lines.Any( line => line.Equals( "Host: " + new Uri( redirect ).Authority, StringComparison.OrdinalIgnoreCase ) );
    }

    private static bool TryAddCallbackParameter( Dictionary<string, string> values, string[] parts )
    {
        return parts.Length == 2 && values.TryAdd( Uri.UnescapeDataString( parts[0] ), Uri.UnescapeDataString( parts[1] ) );
    }
}
