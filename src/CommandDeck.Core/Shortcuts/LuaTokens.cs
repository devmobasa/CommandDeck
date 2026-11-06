using System.Globalization;
using System.Text;

namespace CommandDeck;

public sealed record LuaToken( string Text, string? Value, int Start, int End, int Line );

// A deliberately small lexer, not a Lua evaluator. Invalid or unfinished input
// fails closed, so it can never become a dispatcher in a generated module.
public static class LuaTokens
{
    public static IReadOnlyList<LuaToken> Read( string source )
    {
        List<LuaToken> tokens = [];
        var offset = 0;
        var line = 1;
        while ( offset < source.Length )
        {
            var start = offset;
            var character = source[offset];
            if ( char.IsWhiteSpace( character ) )
            {
                if ( character == '\n' )
                {
                    line++;
                }

                offset++;
                continue;
            }

            var comment = source.AsSpan( offset ).StartsWith( "--", StringComparison.Ordinal );
            if ( comment )
            {
                offset += 2;
            }

            string? value = null;
            if ( TryLongString( source, ref offset, out var longValue ) )
            {
                value = longValue;
            }
            else if ( comment )
            {
                while ( offset < source.Length && source[offset] != '\n' )
                {
                    offset++;
                }
            }
            else if ( character is '\'' or '"' )
            {
                value = ReadString( source, ref offset );
            }
            else
            {
                ReadBareToken( source, ref offset );
            }

            var text = source[start..offset];
            if ( !comment )
            {
                tokens.Add( new( text, value, start, offset, line ) );
            }

            line += text.Count( static item => item == '\n' );
        }

        return tokens;
    }

    private static void ReadBareToken( string source, ref int offset )
    {
        var character = source[offset];
        if ( char.IsAsciiDigit( character ) || character == '.' && offset + 1 < source.Length && char.IsAsciiDigit( source[offset + 1] ) )
        {
            // A sign belongs to a number only after its exponent marker: e in
            // decimal, p in hexadecimal, where e is a digit. Otherwise, as in
            // 0+1, it is an operator.
            var hexadecimal = character == '0' && offset + 1 < source.Length && source[offset + 1] is 'x' or 'X';
            while ( offset < source.Length && (char.IsAsciiLetterOrDigit( source[offset] ) || source[offset] is '.' or '_' ||
                    source[offset] is '+' or '-' && (hexadecimal ? source[offset - 1] is 'p' or 'P' : source[offset - 1] is 'e' or 'E')) )
            {
                offset++;
            }
        }
        else if ( char.IsAsciiLetter( character ) || character == '_' )
        {
            while ( offset < source.Length && (char.IsAsciiLetterOrDigit( source[offset] ) || source[offset] == '_') )
            {
                offset++;
            }
        }
        else
        {
            offset++;
        }
    }

    private static bool TryLongString( string source, ref int offset, out string? value )
    {
        value = null;
        if ( offset >= source.Length || source[offset] != '[' )
        {
            return false;
        }

        var end = offset + 1;
        while ( end < source.Length && source[end] == '=' )
        {
            end++;
        }

        if ( end >= source.Length || source[end] != '[' )
        {
            return false;
        }

        var close = "]" + new string( '=', end - offset - 1 ) + "]";
        var finish = source.IndexOf( close, end + 1, StringComparison.Ordinal );
        if ( finish < 0 )
        {
            throw new FormatException( "Unfinished Lua long string." );
        }

        value = source[(end + 1)..finish];
        if ( value.StartsWith( '\n' ) )
        {
            value = value[1..];
        }

        offset = finish + close.Length;

        return true;
    }

    private static string ReadString( string source, ref int offset )
    {
        var quote = source[offset++];
        StringBuilder value = new( );
        while ( offset < source.Length )
        {
            var character = source[offset++];
            if ( character == quote )
            {
                return value.ToString( );
            }

            if ( character is '\n' or '\r' )
            {
                throw new FormatException( "Unfinished Lua string." );
            }

            if ( character == '\\' )
            {
                value.Append( ReadEscape( source, ref offset ) );
            }
            else
            {
                value.Append( character );
            }
        }

        throw new FormatException( "Unfinished Lua string." );
    }

    private static string ReadEscape( string source, ref int offset )
    {
        if ( offset >= source.Length )
        {
            throw new FormatException( "Unfinished Lua escape." );
        }

        var character = source[offset++];
        if ( char.IsAsciiDigit( character ) )
        {
            var start = offset - 1;
            while ( offset < source.Length && offset - start < 3 && char.IsAsciiDigit( source[offset] ) )
            {
                offset++;
            }

            var code = int.Parse( source.AsSpan( start, offset - start ), CultureInfo.InvariantCulture );
            if ( code > 255 )
            {
                throw new FormatException( "Invalid Lua escape." );
            }

            return (( char ) code).ToString( );
        }

        if ( character == 'z' )
        {
            while ( offset < source.Length && char.IsWhiteSpace( source[offset] ) )
            {
                offset++;
            }

            return "";
        }

        if ( character == 'x' )
        {
            if ( offset + 2 > source.Length || !byte.TryParse( source.AsSpan( offset, 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                 out var code ) )
            {
                throw new FormatException( "Invalid Lua hexadecimal escape." );
            }

            offset += 2;

            return (( char ) code).ToString( );
        }

        if ( character == 'u' )
        {
            return ReadUnicodeEscape( source, ref offset );
        }

        if ( character == '\r' && offset < source.Length && source[offset] == '\n' )
        {
            offset++;
        }

        return character switch
        {
            'n' => "\n",
            'r' => "\r",
            't' => "\t",
            'a' => "\a",
            'b' => "\b",
            'f' => "\f",
            'v' => "\v",
            '\n' or '\r' => "\n",
            '\\' or '\'' or '"' => character.ToString( ),
            _ => throw new FormatException( "Unsupported Lua escape." )
        };
    }

    private static string ReadUnicodeEscape( string source, ref int offset )
    {
        if ( offset >= source.Length || source[offset++] != '{' )
        {
            throw new FormatException( "Invalid Lua Unicode escape." );
        }

        var end = source.IndexOf( '}', offset );
        if ( end < 0 || !int.TryParse( source.AsSpan( offset, end - offset ), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code ) ||
            !System.Text.Rune.IsValid( code ) )
        {
            throw new FormatException( "Invalid Lua Unicode escape." );
        }

        offset = end + 1;

        return char.ConvertFromUtf32( code );
    }

}
