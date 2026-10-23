using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CommandDeck;

public enum ClipboardUrlRejection
{
    None, Empty, TooLarge, InvalidCharacter, UnsupportedScheme, InvalidAuthority,
    InvalidPort, InvalidPercentEncoding, EncodedControl, InvalidComponent
}

/// <summary>An exact validated literal and a lexical comparison key; never a repaired URI.</summary>
public sealed class ClipboardHttpUrl
{
    private const int MaximumUrlCharacters = 8192;
    private const int MaximumPort = 65535;
    private const int MaximumHostCharacters = 253;
    private const int MaximumHostLabelCharacters = 63;
    private const int DefaultHttpPort = 80;
    private const int DefaultHttpsPort = 443;

    private static readonly UTF8Encoding StrictUtf8 = new( false, true );
    public string Literal { get; }
    public string Key { get; }
    public string Scheme { get; }

    private ClipboardHttpUrl( string literal, string key, string scheme )
    {
        Literal = literal;
        Key = key;
        Scheme = scheme;
    }

    public static bool TryParseSingleHttpUrl( string? text, out ClipboardHttpUrl? value, out ClipboardUrlRejection rejection )
    {
        value = null;
        rejection = ValidateLiteral( text );

        if ( rejection != ClipboardUrlRejection.None )
        {
            return false;
        }

        var literal = text!;
        var delimiter = literal.IndexOf( Uri.SchemeDelimiter, StringComparison.Ordinal );
        if ( delimiter < 0 )
        {
            rejection = ClipboardUrlRejection.UnsupportedScheme;

            return false;
        }

        var scheme = literal[..delimiter]
            .ToLowerInvariant( );

        if ( scheme is not (UrlSchemes.Http or UrlSchemes.Https) )
        {
            rejection = ClipboardUrlRejection.UnsupportedScheme;

            return false;
        }

        var authorityStart = delimiter + 3;
        var authorityEnd = literal.IndexOfAny( ['/', '?', '#'], authorityStart );
        if ( authorityEnd < 0 )
        {
            authorityEnd = literal.Length;
        }

        rejection = ParseAuthority( literal[authorityStart..authorityEnd], scheme, out var authorityKey );

        if ( rejection != ClipboardUrlRejection.None )
        {
            return false;
        }

        var tail = literal[authorityEnd..];
        rejection = ValidateTail( tail );

        if ( rejection != ClipboardUrlRejection.None )
        {
            return false;
        }

        var keyTail = NormalizePercentEncoding( tail );
        if ( NeedsRootPath( keyTail ) )
        {
            keyTail = "/" + keyTail;
        }

        value = new( literal, scheme + Uri.SchemeDelimiter + authorityKey + keyTail, scheme );

        return true;
    }

    internal static ClipboardUrlRejection ValidateLiteral( string? text )
    {
        if ( string.IsNullOrEmpty( text ) )
        {
            return ClipboardUrlRejection.Empty;
        }

        if ( text.Length > MaximumUrlCharacters )
        {
            return ClipboardUrlRejection.TooLarge;
        }

        if ( ContainsInvalidUrlCharacter( text ) )
        {
            return ClipboardUrlRejection.InvalidCharacter;
        }

        return ValidatePercentEncoding( text );
    }

    private static ClipboardUrlRejection ParseAuthority( string authority, string scheme, out string key )
    {
        key = "";
        string host;
        string portSuffix;
        if ( authority.StartsWith( '[' ) )
        {
            var bracket = authority.IndexOf( ']' );
            if ( IsInvalidIpv6Authority( authority, bracket ) )
            {
                return ClipboardUrlRejection.InvalidAuthority;
            }

            host = authority[..(bracket + 1)];
            portSuffix = authority[(bracket + 1)..];
        }
        else
        {
            var colon = authority.IndexOf( ':' );
            host = colon < 0 ? authority : authority[..colon];
            portSuffix = colon < 0 ? "" : authority[colon..];

            if ( !ValidDnsHost( host ) )
            {
                return ClipboardUrlRejection.InvalidAuthority;
            }
        }

        if ( portSuffix.Length > 0 )
        {
            if ( HasInvalidPort( portSuffix, out var port ) )
            {
                return ClipboardUrlRejection.InvalidPort;
            }

            if ( IsDefaultPort( scheme, port ) )
            {
                portSuffix = "";
            }
        }

        key = host.ToLowerInvariant( ) + portSuffix;

        return ClipboardUrlRejection.None;
    }

    private static bool ValidDnsHost( string host )
    {
        if ( host.Length is 0 or > MaximumHostCharacters )
        {
            return false;
        }

        var labels = GetHostLabels( host );

        return !host.EndsWith( "..", StringComparison.Ordinal ) &&
               labels.All( static label => label.Length is > 0 and <= MaximumHostLabelCharacters &&
                                           char.IsAsciiLetterOrDigit( label[0] ) &&
                                           char.IsAsciiLetterOrDigit( label[^1] ) &&
                                           label.All( static character => char.IsAsciiLetterOrDigit( character ) || character == '-' ) );
    }

    private static ClipboardUrlRejection ValidateTail( string tail )
    {
        // '[' and ']' are authority delimiters, not raw path/query/fragment data.
        if ( tail.Contains( '[', StringComparison.Ordinal ) ||
             tail.Contains( ']', StringComparison.Ordinal ) ||
             tail.Count( static character => character == '#' ) > 1 )
        {
            return ClipboardUrlRejection.InvalidComponent;
        }
        else
        {
            return ClipboardUrlRejection.None;
        }
    }

    private static ClipboardUrlRejection ValidatePercentEncoding( string text )
    {
        for ( var index = 0; index < text.Length; index++ )
        {
            if ( text[index] != '%' )
            {
                continue;
            }

            List<byte> bytes = [];

            do
            {
                if ( HasInvalidEscapeByte( text, index, out var value ) )
                {
                    return ClipboardUrlRejection.InvalidPercentEncoding;
                }

                bytes.Add( value );
                index += 3;
            } while ( index < text.Length && text[index] == '%' );

            index--;

            try
            {
                var decoded = StrictUtf8.GetString( bytes.ToArray( ) );
                if ( ContainsUnsafeDecodedCharacter( decoded ) )
                {
                    return ClipboardUrlRejection.EncodedControl;
                }
            }
            catch ( DecoderFallbackException )
            {
                return ClipboardUrlRejection.InvalidPercentEncoding;
            }
        }

        return ClipboardUrlRejection.None;
    }

    private static string NormalizePercentEncoding( string text )
    {
        StringBuilder key = new( text.Length );
        for ( var index = 0; index < text.Length; index++ )
        {
            if ( text[index] != '%' )
            {
                key.Append( text[index] );

                continue;
            }

            var decoded = ( char ) byte.Parse( text.AsSpan( index + 1, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture );
            if ( IsUnreserved( decoded ) )
            {
                key.Append( decoded );
            }
            else
            {
                key
                    .Append( '%' )
                    .Append( char.ToUpperInvariant( text[index + 1] ) )
                    .Append( char.ToUpperInvariant( text[index + 2] ) );
            }

            index += 2;
        }

        return key.ToString( );
    }

    private static bool IsUnreserved( char value ) => char.IsAsciiLetterOrDigit( value ) || value is '-' or '.' or '_' or '~';

    private static bool NeedsRootPath( string keyTail )
    {
        return keyTail.Length == 0 || keyTail[0] != '/';
    }

    private static bool ContainsInvalidUrlCharacter( string text )
    {
        return text.Any( static character => !IsUnreserved( character ) && ":/?#[]@!$&'()*+,;=%".IndexOf( character, StringComparison.Ordinal ) < 0 );
    }

    private static bool IsInvalidIpv6Authority( string authority, int bracket )
    {
        return bracket < 0 ||
               authority.Contains( '%', StringComparison.Ordinal ) ||
               !IPAddress.TryParse( authority[1..bracket], out var address ) ||
               address.AddressFamily != AddressFamily.InterNetworkV6;
    }

    private static bool IsDefaultPort( string scheme, int port )
    {
        return (scheme, port) is (UrlSchemes.Http, DefaultHttpPort ) or (UrlSchemes.Https, DefaultHttpsPort );
    }

    private static string[] GetHostLabels( string host )
    {
        return host
            .TrimEnd( '.' )
            .Split( '.' );
    }

    private static bool ContainsUnsafeDecodedCharacter( string decoded )
    {
        return decoded.Any( static character => char.IsControl( character ) || character is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069' );
    }

    private static bool HasInvalidPort( string portSuffix, out int port )
    {
        port = default;

        return portSuffix[0] != ':' ||
               portSuffix.Length == 1 ||
               portSuffix
                   .AsSpan( 1 )
                   .ContainsAnyExceptInRange( '0', '9' ) ||
               !int.TryParse( portSuffix.AsSpan( 1 ), NumberStyles.None, CultureInfo.InvariantCulture, out port ) ||
               port > MaximumPort;
    }

    private static bool HasInvalidEscapeByte( string text, int index, out byte value )
    {
        value = default;

        return index + 2 >= text.Length || !byte.TryParse( text.AsSpan( index + 1, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value );
    }
}
