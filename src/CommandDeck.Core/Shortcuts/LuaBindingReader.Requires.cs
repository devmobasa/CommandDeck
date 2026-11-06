namespace CommandDeck;

// Which requires a Lua file makes, and where they run.
public static partial class LuaBindingReader
{
    // An until condition runs on every pass of its loop, from until to the end
    // of its expression, which may span lines inside brackets or blocks. A block
    // inside the condition, such as an anonymous function, can hold a loop of
    // its own, so conditions nest.
    private sealed class UntilCondition
    {
        private readonly Stack<(int Nesting, int Scopes)> _open = new( );

        public bool Active => _open.Count > 0;

        public void Before( IReadOnlyList<LuaToken> tokens, int index, int nesting, int scopes )
        {
            if ( _open.TryPeek( out var level ) && level == (nesting, scopes) && EndsUntilCondition( tokens, index ) )
            {
                _open.Pop( );
            }
        }

        public void After( string text, int nesting, int scopes )
        {
            if ( text == "until" )
            {
                _open.Push( (nesting, scopes) );
            }
        }
    }

    // A statement keyword, a semicolon, or a line break that neither side
    // continues ends the expression.
    private static bool EndsUntilCondition( IReadOnlyList<LuaToken> tokens, int index )
    {
        var token = tokens[index];
        if ( token.Text is ";" or "end" or "else" or "elseif" or "until" or "local" or "return" or "break" or "goto" or "do" or "for" or "while" or
             "repeat" or "if" )
        {
            return true;
        }

        var previous = tokens[index - 1];

        return previous.Line < token.Line && !ContinuesExpression( previous.Text ) && !ContinuesExpression( token.Text );
    }

    // Where the reader is: Maybe after a return inside a block or in a file with
    // a goto, Jumps in such a file, and InUntil inside an until condition.
    private readonly record struct RequireContext( bool Maybe, bool Jumps, bool InUntil );

    private static void ReadRequire( IReadOnlyList<LuaToken> tokens, int index, Stack<string> scopes, int nesting, List<LuaRequire> requires,
        RequireContext context )
    {
        if ( TryRequire( tokens, index ) is not { } require )
        {
            return;
        }

        var ordered = !context.Maybe && !context.InUntil && IsOrderedRequire( tokens, index, scopes, nesting, require.Module );
        var inPlace = ordered || !context.InUntil && IsInPlaceRequire( tokens, index, scopes, nesting );
        var inLoop = context.Jumps || context.InUntil || scopes.Any( static scope => scope is "loop" or "loop-header" or "repeat" );
        requires.Add( require with { Ordered = ordered, InPlace = inPlace, InFunction = scopes.Contains( "function" ), InLoop = inLoop } );
    }

    private static bool IsInPlaceRequire( IReadOnlyList<LuaToken> tokens, int index, Stack<string> scopes, int nesting )
    {
        return nesting == 0 && !scopes.Contains( "function" ) && IsWholeStatement( tokens, index );
    }

    private static bool IsOrderedRequire( IReadOnlyList<LuaToken> tokens, int index, Stack<string> scopes, int nesting, string module )
    {
        if ( nesting != 0 )
        {
            return false;
        }

        if ( scopes.Count == 1 && scopes.Peek( ) == "if" && IsPresenceGuard( tokens, index, module ) )
        {
            return true;
        }

        return scopes.Count == 0 && IsWholeStatement( tokens, index );
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
}
