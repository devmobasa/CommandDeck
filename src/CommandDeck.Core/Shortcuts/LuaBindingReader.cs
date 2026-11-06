using System.Text.RegularExpressions;

namespace CommandDeck;

public sealed record LuaBindingCall( string Chord, string Description, string Callee, string Dispatcher, string Options, bool Literal, int Offset );
// Ordered: runs here, every time the file runs. InPlace: runs here if it runs at
// all, as at file level inside a condition; only a function can run it later.
// InFunction: inside a function, which may run at any time. InLoop: inside a loop,
// so it may run after code that follows it.
public sealed record LuaRequire( string Module, int Offset, bool Ordered = true, bool InPlace = true, bool InFunction = false,
    bool InLoop = false );
public sealed record LuaSource( IReadOnlyList<LuaBindingCall> Bindings, IReadOnlyList<LuaRequire> Requires );

public static partial class LuaBindingReader
{
    public static LuaSource Read( string source )
    {
        try
        {
            return ReadTokens( source, LuaTokens.Read( source ) );
        }
        catch ( FormatException )
        {
            return new( [], [] );
        }
    }

    private static LuaSource ReadTokens( string source, IReadOnlyList<LuaToken> tokens )
    {
        List<LuaBindingCall> bindings = [];
        List<LuaRequire> requires = [];
        Stack<string> scopes = new( );
        var nesting = 0;
        var helpersChanged = HelpersChanged( tokens );
        // A goto can repeat or skip any statement, and a return inside a block
        // skips the rest of the file, so what follows runs only maybe.
        var jumps = tokens.Any( static token => token is { Value: null, Text: "goto" } );
        var returned = false;
        UntilCondition until = new( );
        for ( var index = 0; index < tokens.Count; index++ )
        {
            var text = tokens[index].Text;
            if ( tokens[index].Value is not null )
            {
                continue;
            }

            until.Before( tokens, index, nesting, scopes.Count );
            UpdateScope( scopes, text );
            until.After( text, nesting, scopes.Count );

            returned |= text == "return" && scopes.Count > 0 && !scopes.Contains( "function" );
            if ( text is "(" or "{" or "[" )
            {
                nesting++;
            }

            if ( text is ")" or "}" or "]" )
            {
                nesting--;
            }

            ReadRequire( tokens, index, scopes, nesting, requires, new( returned || jumps, jumps, until.Active ) );
            if ( !IsBind( tokens, index ) )
            {
                continue;
            }

            var arguments = Arguments( tokens, index + 3, out var last );
            if ( arguments is null )
            {
                continue;
            }

            var call = ReadBinding( source, tokens, index, arguments );
            if ( call is not null )
            {
                var statementStart = IsStatementStart( tokens, index );
                var statementEnd = IsStatementEnd( tokens, last );
                bindings.Add( call with
                {
                    Literal = call.Literal && !helpersChanged && !returned && !jumps && scopes.Count == 0 && nesting == 0 && statementStart && statementEnd
                } );
            }
        }

        return new( bindings, requires );
    }

    // Symbols are single characters, so == is "=" "=" and >= is ">" "=".
    private static bool ContinuesExpression( string text ) => text is "=" or "." or ":" or "+" or "-" or "*" or "/" or "%" or "^" or
        "<" or ">" or "~" or "&" or "|" or "#" or "and" or "or" or "not" or "return" or "until" or "(" or "[" or "{" or ",";

    private static void UpdateScope( Stack<string> scopes, string text )
    {
        if ( text is "for" or "while" )
        {
            scopes.Push( "loop-header" );
        }

        if ( text == "do" && scopes.TryPeek( out var header ) && header == "loop-header" )
        {
            scopes.Pop( );
            scopes.Push( "loop" );

            return;
        }

        if ( text is "function" or "if" or "do" or "repeat" )
        {
            scopes.Push( text );
        }

        if ( text is "end" or "until" && scopes.Count > 0 )
        {
            scopes.Pop( );
        }
    }

    private static bool IsWholeStatement( IReadOnlyList<LuaToken> tokens, int index )
    {
        var open = tokens[index].Text == "require" ? index + 1 : index + 3;
        var last = open + (tokens[open].Text == "(" ? 2 : 0);

        return IsStatementStart( tokens, index ) && IsStatementEnd( tokens, last );
    }

    private static bool IsStatementStart( IReadOnlyList<LuaToken> tokens, int index )
    {
        // A block keyword ends the statement before it, as a semicolon does.
        if ( index == 0 || tokens[index - 1] is { Text: ";" } or { Value: null, Text: "then" or "do" or "else" } )
        {
            return true;
        }

        return tokens[index - 1].Line < tokens[index].Line && !ContinuesExpression( tokens[index - 1].Text );
    }

    private static bool IsStatementEnd( IReadOnlyList<LuaToken> tokens, int last )
    {
        if ( last + 1 == tokens.Count || tokens[last + 1] is { Text: ";" } or { Value: null, Text: "end" or "else" or "elseif" } )
        {
            return true;
        }

        return tokens[last + 1].Line > tokens[last].Line && !ContinuesExpression( tokens[last + 1].Text );
    }

    private static bool HelpersChanged( IReadOnlyList<LuaToken> tokens )
    {
        var braces = 0;
        for ( var index = 0; index < tokens.Count; index++ )
        {
            if ( tokens[index].Value is not null )
            {
                continue;
            }

            var text = tokens[index].Text;
            if ( text == "{" )
            {
                braces++;
            }

            if ( text == "}" )
            {
                braces--;
            }

            if ( text == "local" && LocalHelpers( tokens, index + 1 ) )
            {
                return true;
            }

            if ( braces == 0 && text is "o" or "hl" && HelperAssignment( tokens, index ) )
            {
                return true;
            }
        }

        return false;
    }

    private static bool LocalHelpers( IReadOnlyList<LuaToken> tokens, int index )
    {
        if ( index < tokens.Count && tokens[index].Text == "function" )
        {
            index++;
        }

        while ( index < tokens.Count )
        {
            var name = tokens[index++];
            if ( name.Value is not null )
            {
                return false;
            }

            if ( name.Text is "o" or "hl" )
            {
                return true;
            }

            if ( index + 2 < tokens.Count && tokens[index].Text == "<" && tokens[index + 2].Text == ">" )
            {
                index += 3;
            }

            if ( index >= tokens.Count || tokens[index++].Text != "," )
            {
                return false;
            }
        }

        return false;
    }

    private static bool HelperAssignment( IReadOnlyList<LuaToken> tokens, int index )
    {
        if ( index > 0 && tokens[index - 1].Text == "function" )
        {
            return true;
        }

        var cursor = index + 1;
        while ( cursor < tokens.Count && tokens[cursor].Text is "." or ":" )
        {
            cursor += 2;
        }

        if ( cursor < tokens.Count && tokens[cursor].Text == "[" )
        {
            while ( cursor < tokens.Count && tokens[cursor].Text != "]" )
            {
                cursor++;
            }

            cursor++;
        }

        while ( cursor < tokens.Count && tokens[cursor].Text == "," )
        {
            cursor += 2;
        }

        return cursor < tokens.Count && tokens[cursor].Text == "=";
    }

    private static bool IsBind( IReadOnlyList<LuaToken> tokens, int index ) => index + 3 < tokens.Count &&
        tokens[index].Text is "o" or "hl" && tokens[index + 1].Text == "." && tokens[index + 2].Text == "bind" && tokens[index + 3].Text == "(";

    private static LuaBindingCall? ReadBinding( string source, IReadOnlyList<LuaToken> tokens, int start, List<List<LuaToken>> arguments )
    {
        var omarchy = tokens[start].Text == "o";
        var expected = omarchy ? 3 : 2;
        if ( arguments.Count < expected || arguments.Count > expected + 1 || arguments[0].Count == 0 )
        {
            return null;
        }

        var chord = arguments[0] is [{ Value: { } literalChord }] ? literalChord : "";
        var description = omarchy && arguments[1] is [{ Value: { } label }] ? label : Description( arguments.Last( ) );
        var dispatcher = arguments[expected - 1];
        var options = arguments.Count == expected + 1 ? arguments[^1] : [];
        var literal = chord.Length > 0 && dispatcher.Count > 0 && (dispatcher[0].Value is not null || dispatcher[0].Text is "{" or "hl") &&
             IsLiteral( dispatcher ) && (options.Count == 0 || IsLiteral( options )) &&
            (!omarchy || arguments[1] is [{ Value: not null }]);

        return new( chord, description, omarchy ? "o.bind" : "hl.bind", Slice( source, dispatcher ),
            options.Count == 0 ? "{}" : Slice( source, options ), literal, tokens[start].Start );
    }

    private static string Description( List<LuaToken> tokens )
    {
        for ( var index = 0; index + 2 < tokens.Count; index++ )
        {
            if ( tokens[index].Text == "description" && tokens[index + 1].Text == "=" && tokens[index + 2].Value is { } value )
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string Slice( string source, List<LuaToken> tokens ) => tokens.Count == 0 ? "" : source[tokens[0].Start..tokens[^1].End];

    private static List<List<LuaToken>>? Arguments( IReadOnlyList<LuaToken> tokens, int open, out int end )
    {
        List<List<LuaToken>> values = [];
        List<LuaToken> value = [];
        var depth = 0;
        end = open;
        for ( var index = open + 1; index < tokens.Count; index++ )
        {
            var token = tokens[index];
            if ( token.Value is null )
            {
                if ( token.Text == ")" && depth == 0 )
                {
                    if ( value.Count > 0 )
                    {
                        values.Add( value );
                    }

                    end = index;

                    return values;
                }

                if ( token.Text == "," && depth == 0 )
                {
                    values.Add( value );
                    value = [];
                    continue;
                }

                if ( token.Text is "(" or "{" or "[" )
                {
                    depth++;
                }

                if ( token.Text is ")" or "}" or "]" )
                {
                    depth--;
                }

                if ( depth < 0 )
                {
                    return null;
                }
            }

            value.Add( token );
        }

        return null;
    }

    public static bool IsLiteral( IReadOnlyList<LuaToken> tokens )
    {
        var index = 0;

        return ReadValue( tokens, ref index, 0 ) && index == tokens.Count;
    }

    private static bool ReadValue( IReadOnlyList<LuaToken> tokens, ref int index, int depth )
    {
        if ( index >= tokens.Count || depth > 32 )
        {
            return false;
        }

        var token = tokens[index++];
        if ( token.Value is not null || token.Text is "true" or "false" or "nil" )
        {
            return true;
        }

        if ( token.Text == "-" )
        {
            return index < tokens.Count && Number( tokens[index++].Text );
        }

        if ( Number( token.Text ) )
        {
            return true;
        }

        if ( token.Text == "{" )
        {
            return ReadTable( tokens, ref index, depth + 1 );
        }

        if ( token.Text != "hl" || index + 4 >= tokens.Count )
        {
            return false;
        }

        if ( tokens[index++].Text != "." || tokens[index++].Text != "dsp" || tokens[index++].Text != "." )
        {
            return false;
        }

        if ( !ReadDispatcherPath( tokens, ref index ) )
        {
            return false;
        }

        return ReadList( tokens, ref index, ")", depth + 1 );
    }

    private static bool ReadDispatcherPath( IReadOnlyList<LuaToken> tokens, ref int index )
    {
        while ( index < tokens.Count )
        {
            var name = tokens[index++];
            if ( name.Value is not null || name.Text.Length == 0 || !(char.IsAsciiLetter( name.Text[0] ) || name.Text[0] == '_') ||
                !name.Text.All( static character => char.IsAsciiLetterOrDigit( character ) || character == '_' ) )
            {
                return false;
            }

            if ( index >= tokens.Count )
            {
                return false;
            }

            var separator = tokens[index++].Text;
            if ( separator == "(" )
            {
                return true;
            }

            if ( separator != "." )
            {
                return false;
            }
        }

        return false;
    }

    private static bool Number( string value ) => LuaNumberPattern( ).IsMatch( value );

    [GeneratedRegex( @"^(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$|^0[xX][0-9a-fA-F]+(?:\.[0-9a-fA-F]*)?(?:[pP][+-]?[0-9]+)?$",
         RegexOptions.CultureInvariant )]
    private static partial Regex LuaNumberPattern( );

    private static bool ReadTable( IReadOnlyList<LuaToken> tokens, ref int index, int depth )
    {
        while ( index < tokens.Count && tokens[index].Text != "}" )
        {
            if ( index + 1 < tokens.Count && tokens[index + 1].Text == "=" )
            {
                if ( !tokens[index].Text.All( static character => char.IsAsciiLetterOrDigit( character ) || character == '_' ) )
                {
                    return false;
                }

                index += 2;
            }
            else if ( tokens[index].Text == "[" )
            {
                index++;
                if ( !ReadValue( tokens, ref index, depth ) || !Take( tokens, ref index, "]" ) || !Take( tokens, ref index, "=" ) )
                {
                    return false;
                }
            }

            if ( !ReadValue( tokens, ref index, depth ) )
            {
                return false;
            }

            if ( index < tokens.Count && tokens[index].Text is "," or ";" )
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return Take( tokens, ref index, "}" );
    }

    private static bool ReadList( IReadOnlyList<LuaToken> tokens, ref int index, string close, int depth )
    {
        while ( index < tokens.Count && tokens[index].Text != close )
        {
            if ( !ReadValue( tokens, ref index, depth ) )
            {
                return false;
            }

            if ( index < tokens.Count && tokens[index].Text == "," )
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return Take( tokens, ref index, close );
    }

    private static bool Take( IReadOnlyList<LuaToken> tokens, ref int index, string text ) => index < tokens.Count && tokens[index++].Text == text;
}
