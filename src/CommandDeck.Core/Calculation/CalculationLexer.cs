namespace CommandDeck;

// UnitGroup and Fraction mark the parentheses the lexer adds around `km/h` and `1/2`.
internal readonly record struct CalculationToken( string Text, bool Number = false, bool UnitGroup = false, bool Fraction = false )
{
    internal string Lower => Text.ToLowerInvariant( );
}

internal static class CalculationLexer
{
    private const int MaximumTokenCount = 256;
    private const int MaximumLeadingGroupDigits = 3;
    private const int DigitGroupLength = 4;

    internal static CalculationToken[] Tokenize( string expression )
    {
        List<CalculationToken> tokens = [];
        Stack<bool> parentheses = [];
        for ( var i = 0; i < expression.Length; )
        {
            var character = expression[i];
            if ( char.IsControl( character ) )
            {
                throw new FormatException( );
            }

            if ( char.IsWhiteSpace( character ) )
            {
                i++;

                continue;
            }

            var start = i++;
            if ( expression
                .AsSpan( start )
                .StartsWith( CalculationUnitSymbols.LitresPer100Kilometres, StringComparison.Ordinal ) )
            {
                tokens.Add( new( CalculationUnitSymbols.LitresPer100Kilometres ) );
                i = start + CalculationUnitSymbols.LitresPer100Kilometres.Length;
            }
            else if ( IsNumberStart( character ) )
            {
                i = NumericEnd( expression, start, parentheses );
                var number = NormalizeNumber( expression[start..i] );
                var afterPower = tokens is [.., { Text: "^" }] or [.., { Text: "^" }, { Text: "+" or "-" }];
                var fraction = FractionEnd( expression, number, i, parentheses, afterPower );

                var rate = tokens is [.., var previous] && CalculationCurrencies.IsCurrency( previous.Text ) ? DenominatorEnd( expression, i ) : -1;
                if ( fraction > i )
                {
                    // An integer fraction before an operand is one number: 1/2 cup, 3/4 h, 1/2pi = π/2.
                    tokens.AddRange( [
                        new( "(", Fraction: true ), new( number, true ), new( "/" ),
                        new( NormalizeNumber( expression[(i + 1)..fraction] ), true ), new( ")", Fraction: true )
                    ] );

                    i = fraction;
                }
                else if ( rate > i )
                {
                    // $20/h is twenty dollars per hour, so $800 / $20/h divides by the whole rate.
                    var currency = tokens[^1];
                    tokens.RemoveAt( tokens.Count - 1 );

                    AddUnitGroup( tokens, [
                        currency, new( number, true )
                    ], expression, i, rate );

                    i = rate;
                }
                else
                {
                    tokens.Add( new( number, true ) );
                }
            }
            else if ( IsWordStart( character ) )
            {
                i = WordEnd( expression, start );
                var word = expression[start..i];
                var compound = CompoundEnd( expression, i, word );
                if ( IsFluidOunceSuffix( tokens, word ) )
                {
                    tokens[^1] = new( "floz" );
                }
                else if ( compound > i )
                {
                    // Two units written as a/b without spaces are one unit: 100 km / 50 km/h = 2 h.
                    AddUnitGroup( tokens, [new( word )], expression, i, compound );
                    i = compound;
                }
                else
                {
                    tokens.Add( new( word ) );
                }
            }
            else
            {
                var symbol = Operator( expression, start, ref i );
                var compound = CalculationCurrencies.IsCurrency( symbol ) ? DenominatorEnd( expression, i ) : -1;
                if ( compound > i )
                {
                    // A currency symbol joins a compound unit like its code does: 10 €/h.
                    AddUnitGroup( tokens, [new( symbol )], expression, i, compound );
                    i = compound;
                }
                else
                {
                    TrackParenthesis( parentheses, tokens, symbol );
                    tokens.Add( new( symbol ) );
                }
            }

            if ( tokens.Count > MaximumTokenCount )
            {
                throw new FormatException( );
            }
        }

        return tokens.ToArray( );
    }

    private static bool IsWordStart( char character ) => char.IsLetter( character ) || character is 'µ' or '°';

    private static int WordEnd( string expression, int start )
    {
        var i = start + 1;
        while ( i < expression.Length && char.IsLetterOrDigit( expression[i] ) && expression[i] is not '²' and not '³' )
        {
            i++;
        }

        return i;
    }

    private static void AddUnitGroup( List<CalculationToken> tokens, CalculationToken[] numerator, string expression, int slash, int end ) =>
        tokens.AddRange( [new( "(", UnitGroup: true ), .. numerator, new( "/" ), .. Tokenize( expression[(slash + 1)..end] ), new( ")", UnitGroup: true )] );

    // The end of `/denominator` when both sides are plain integers and an operand follows, otherwise -1.
    // Without an operand (10/2^2, 3/2!, 100/5%) or after ^ (27^1/3) normal precedence applies.
    private static int FractionEnd( string expression, string number, int end, Stack<bool> parentheses, bool afterPower )
    {
        if ( CannotReadFraction( expression, number, end, afterPower ) )
        {
            return -1;
        }

        var denominator = NumericEnd( expression, end + 1, parentheses );
        var next = denominator;
        while ( next < expression.Length && char.IsWhiteSpace( expression[next] ) )
        {
            next++;
        }

        var denominatorNumber = NormalizeNumber( expression[(end + 1)..denominator] );
        if ( !denominatorNumber.AsSpan( ).ContainsAnyExceptInRange( '0', '9' ) &&
             next < expression.Length &&
             (IsWordStart( expression[next] ) ||
              expression[next] == '(' ||
              CalculationCurrencies.IsCurrency( expression[next]
                  .ToString( ) )) )
        {
            return denominator;
        }
        else
        {
            return -1;
        }
    }

    private static int CompoundEnd( string expression, int slash, string numerator ) =>
        IsUnit( numerator ) ? DenominatorEnd( expression, slash ) : -1;

    // The end of `/unit` with an attached power when it names a unit, otherwise -1.
    private static int DenominatorEnd( string expression, int slash )
    {
        if ( CannotReadUnitDenominator( expression, slash ) )
        {
            return -1;
        }

        var end = WordEnd( expression, slash + 1 );
        if ( !IsUnit( expression[(slash + 1)..end] ) )
        {
            return -1;
        }

        if ( HasSuperscriptPower( expression, end ) )
        {
            return end + 1;
        }

        if ( HasNoExplicitPower( expression, end ) )
        {
            return end;
        }

        var digits = expression[end + 1] is '-' or '−' or '+' ? end + 2 : end + 1;
        var power = digits;
        while ( power < expression.Length && char.IsAsciiDigit( expression[power] ) )
        {
            power++;
        }

        return power > digits ? power : end;
    }

    private static bool IsUnit( string word ) =>
        CalculationUnits.Find( word ) is not null ||
        CalculationCurrencies.IsCurrency( word ) ||
        word.Length > 1 && word[^1] is '2' or '3' && CalculationUnits.Find( word[..^1] ) is not null;

    private static int NumericEnd( string expression, int start, Stack<bool> parentheses )
    {
        var grouped = GroupedNumberEnd( expression, start, parentheses );

        return grouped > 0 ? grouped : NumberEnd( expression, start );
    }

    private static string NormalizeNumber( string number ) => number
        .Replace( ",", string.Empty, StringComparison.Ordinal )
        .Replace( '−', '-' );

    private static int NumberEnd( string expression, int start )
    {
        var i = start + 1;
        if ( HasRadixPrefix( expression, start, i ) )
        {
            i++;
            var digits = i;

            while ( i < expression.Length && char.IsAsciiLetterOrDigit( expression[i] ) )
            {
                i++;
            }

            if ( i == digits )
            {
                throw new FormatException( );
            }

            return i;
        }

        while ( i < expression.Length && (char.IsAsciiDigit( expression[i] ) || expression[i] == '.') )
        {
            i++;
        }

        if ( HasExponentSuffix( expression, i ) )
        {
            i++;

            if ( expression[i] is '+' or '-' or '−' )
            {
                i++;
            }

            while ( i < expression.Length && char.IsAsciiDigit( expression[i] ) )
            {
                i++;
            }
        }

        return i;
    }

    private static string Operator( string expression, int start, ref int end )
    {
        var character = expression[start];
        if ( IsTwoCharacterOperator( expression, end, character ) )
        {
            end++;

            return character == '*' ? "^" : CalculationTokenNames.Arrow;
        }

        return character switch
        {
            '−' => "-",
            '×' or '·' => "*",
            '÷' => "/",
            _ => character.ToString( )
        };
    }

    // A comma inside a function call separates arguments: max(100,200).
    private static void TrackParenthesis( Stack<bool> parentheses, List<CalculationToken> tokens, string symbol )
    {
        if ( symbol == "(" )
        {
            parentheses.Push( tokens is [.., { Number: false } previous] && char.IsLetter( previous.Text[0] ) );
        }
        else if ( symbol == ")" )
        {
            parentheses.TryPop( out _ );
        }
    }

    // 1,000,000.5: one to three digits, then groups of exactly three. Returns
    // the number's end, or 0 when it has no such groups.
    private static int GroupedNumberEnd( string expression, int start, Stack<bool> parentheses )
    {
        var i = start;
        while ( i < expression.Length && char.IsAsciiDigit( expression[i] ) )
        {
            i++;
        }

        if ( i == start || i - start > MaximumLeadingGroupDigits || !IsDigitGroup( expression, i ) || parentheses.Contains( true ) )
        {
            return 0;
        }

        while ( IsDigitGroup( expression, i ) )
        {
            i += DigitGroupLength;
        }

        if ( i + 1 < expression.Length && expression[i] == '.' && char.IsAsciiDigit( expression[i + 1] ) )
        {
            i++;

            while ( i < expression.Length && char.IsAsciiDigit( expression[i] ) )
            {
                i++;
            }
        }

        return i;
    }

    private static bool IsDigitGroup( string expression, int comma )
    {
        return comma + DigitGroupLength <= expression.Length &&
               expression[comma] == ',' &&
               !expression.AsSpan( comma + 1, DigitGroupLength - 1 ).ContainsAnyExceptInRange( '0', '9' ) &&
               !(comma + DigitGroupLength < expression.Length && char.IsAsciiDigit( expression[comma + DigitGroupLength] ));
    }

    private static bool IsNumberStart( char character )
    {
        return char.IsAsciiDigit( character ) || character == '.';
    }

    private static bool IsFluidOunceSuffix( List<CalculationToken> tokens, string word )
    {
        return tokens.Count > 0 && (tokens[^1].Lower, word.ToLowerInvariant( )) is ("fl", "oz" ) or ("fluid", "ounce" or "ounces" );
    }

    private static bool CannotReadFraction( string expression, string number, int end, bool afterPower )
    {
        return afterPower ||
               end + 1 >= expression.Length ||
               expression[end] != '/' ||
               !char.IsAsciiDigit( expression[end + 1] ) ||
               number
                   .AsSpan( )
                   .ContainsAnyExceptInRange( '0', '9' );
    }

    private static bool CannotReadUnitDenominator( string expression, int slash )
    {
        return slash + 1 >= expression.Length || expression[slash] != '/' || !IsWordStart( expression[slash + 1] );
    }

    private static bool HasSuperscriptPower( string expression, int end )
    {
        return end < expression.Length && expression[end] is '²' or '³';
    }

    private static bool HasNoExplicitPower( string expression, int end )
    {
        return end + 1 >= expression.Length || expression[end] != '^';
    }

    private static bool HasRadixPrefix( string expression, int start, int i )
    {
        return expression[start] == '0' && i < expression.Length && "xXbBoO".Contains( expression[i] );
    }

    private static bool HasExponentSuffix( string expression, int i )
    {
        return i + 1 < expression.Length && expression[i] is 'e' or 'E' && (char.IsAsciiDigit( expression[i + 1] ) || expression[i + 1] is '+' or '-' or '−');
    }

    private static bool IsTwoCharacterOperator( string expression, int end, char character )
    {
        return end < expression.Length && (character == '*' && expression[end] == '*' || character == '-' && expression[end] == '>');
    }
}
