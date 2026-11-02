using System.Text.RegularExpressions;

namespace CommandDeck;

public sealed record LuaBindingCall( string Chord, string Description, string Callee, string Dispatcher, string Options, bool Literal, int Offset );
public sealed record LuaRequire( string Module, int Offset, bool Ordered = true );
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
        for ( var index = 0; index < tokens.Count; index++ )
        {
            var text = tokens[index].Text;
            if ( tokens[index].Value is not null )
            {
                continue;
            }

            UpdateScope( scopes, text );
            if ( text is "(" or "{" or "[" )
            {
                nesting++;
            }

            if ( text is ")" or "}" or "]" )
            {
                nesting--;
            }

            ReadRequire( tokens, index, scopes, nesting, requires );
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
                bindings.Add( call with { Literal = call.Literal && !helpersChanged && scopes.Count == 0 && nesting == 0 && statementStart && statementEnd } );
            }
        }

        return new( bindings, requires );
    }

    private static bool ContinuesExpression( string text ) => text is "=" or "." or ":" or "+" or "-" or "*" or "/" or "%" or "^" or
        "and" or "or" or "not" or "return" or "until" or "(" or "[" or ",";

    private static void UpdateScope( Stack<string> scopes, string text )
    {
        if ( text is "for" or "while" )
        {
            scopes.Push( "loop-header" );
        }

        if ( text == "do" && scopes.TryPeek( out var header ) && header == "loop-header" )
        {
            scopes.Pop( );
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

    private static void ReadRequire( IReadOnlyList<LuaToken> tokens, int index, Stack<string> scopes, int nesting, List<LuaRequire> requires )
    {
        if ( TryRequire( tokens, index ) is not { } require )
        {
            return;
        }

        var ordered = IsOrderedRequire( tokens, index, scopes, nesting, require.Module );
        requires.Add( require with { Ordered = ordered } );
    }

    private static bool IsOrderedRequire( IReadOnlyList<LuaToken> tokens, int index, Stack<string> scopes, int nesting, string module )
    {
        if ( nesting != 0 || InUntilCondition( tokens, index ) )
        {
            return false;
        }

        if ( scopes.Count == 1 && scopes.Peek( ) == "if" && IsPresenceGuard( tokens, index, module ) )
        {
            return true;
        }

        var start = IsStatementStart( tokens, index );
        var open = tokens[index].Text == "require" ? index + 1 : index + 3;
        var last = open + (tokens[open].Text == "(" ? 2 : 0);
        var end = IsStatementEnd( tokens, last );

        return scopes.Count == 0 && start && end;
    }

    private static bool IsStatementStart( IReadOnlyList<LuaToken> tokens, int index )
    {
        if ( index == 0 || tokens[index - 1].Text == ";" )
        {
            return true;
        }

        return tokens[index - 1].Line < tokens[index].Line && !ContinuesExpression( tokens[index - 1].Text );
    }

    private static bool IsStatementEnd( IReadOnlyList<LuaToken> tokens, int last )
    {
        if ( last + 1 == tokens.Count || tokens[last + 1].Text == ";" )
        {
            return true;
        }

        return tokens[last + 1].Line > tokens[last].Line && !ContinuesExpression( tokens[last + 1].Text );
    }

    private static bool InUntilCondition( IReadOnlyList<LuaToken> tokens, int index )
    {
        for ( var cursor = index - 1; cursor >= 0; cursor-- )
        {
            var previous = tokens[cursor];
            var next = tokens[cursor + 1];
            if ( previous.Value is null && previous.Text == "until" )
            {
                return true;
            }

            if ( previous.Text is ";" or "end" or "do" )
            {
                return false;
            }

            if ( previous.Line < next.Line && !ContinuesExpression( previous.Text ) && !ContinuesExpression( next.Text ) )
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsPresenceGuard( IReadOnlyList<LuaToken> tokens, int requireIndex, string module )
    {
        var start = requireIndex - 12;
        string[] shape = ["if", "package", ".", "searchpath", "(", "", ",", "package", ".", "path", ")", "then", "require", "(", "", ")", "end"];
        if ( start < 0 || start + shape.Length > tokens.Count )
        {
            return false;
        }

        for ( var offset = 0; offset < shape.Length; offset++ )
        {
            var token = tokens[start + offset];
            if ( shape[offset].Length == 0 ? token.Value != module : token.Value is not null || token.Text != shape[offset] )
            {
                return false;
            }
        }

        return true;
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

    private static LuaRequire? TryRequire( IReadOnlyList<LuaToken> tokens, int index )
    {
        var name = tokens[index].Text;
        // Omarchy uses require_optional.module for optional default applications.
        var open = -1;
        if ( name == "require" )
        {
            open = index + 1;
        }
        else if ( name == "require_optional" && index + 3 < tokens.Count &&
                  tokens[index + 1].Text == "." && tokens[index + 2].Text == "module" )
        {
            open = index + 3;
        }

        if ( open < 0 || open >= tokens.Count )
        {
            return null;
        }

        var parenthesized = tokens[open].Text == "(";
        if ( parenthesized )
        {
            open++;
        }

        if ( open >= tokens.Count || tokens[open].Value is not { } module )
        {
            return null;
        }

        if ( parenthesized && (open + 1 >= tokens.Count || tokens[open + 1].Text != ")") )
        {
            return null;
        }

        return new( module, tokens[index].Start );
    }

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
