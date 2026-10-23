using System.Globalization;

namespace CommandDeck;

internal readonly struct CalculationNumber
{
    private const int HexadecimalRadix = 16;
    private const int OctalRadix = 8;
    private const int DecimalRadix = 10;
    internal const double MinimumDecimalMagnitude = 1e-12;
    private const decimal MinimumExactMagnitude = 0.000000000001m;
    private const double MinimumComparisonMagnitude = 1e-28;
    private const decimal MaximumExactExponent = 4096m;
    private const decimal MaximumFactorialArgument = 170m;

    // Keep decimal's 28-29 significant digits while values fit, otherwise use double.
    // Literals beyond 28 significant digits round on input; cancellation beyond that
    // precision (for example (1e28 + 0.1 - 1e28) * 10) loses rounded digits: a known limit.
    // Display results to 15 significant digits (exact integers keep all digits).
    // Nonzero decimal results below 1e-12 move to double; tiny or underflowed
    // decimal products and quotients recompute in double.
    // Transcendental functions (sin, cos, tan, asin, acos, atan, ln, log, log2, log10, exp),
    // roots and non-integer powers compute in double after rounding inputs to about 16
    // significant digits. Results involving an approximate value have double precision.
    // Inputs differing only beyond that precision can coincide (ln(1.00000000000000000001) = 0).
    // Domain checks still use the exact value.
    internal decimal Exact { get; }
    internal double Approximate { get; }
    internal bool IsExact { get; }
    internal double Double => IsExact ? ( double ) Exact : Approximate;
    internal bool IsZero => IsExact ? Exact == 0 : Approximate == 0;
    internal bool IsInteger => IsExact ? decimal.Truncate( Exact ) == Exact : Math.Truncate( Approximate ) == Approximate;

    internal CalculationNumber( decimal value )
    {
        Exact = value;
        IsExact = true;
    }

    internal CalculationNumber( double value )
    {
        if ( IsInvalidFloatingPointValue( value ) )
        {
            throw new ArithmeticException( );
        }

        Approximate = value == 0 ? 0 : value;
    }

    internal static CalculationNumber Parse( string text )
    {
        if ( HasIntegerRadixPrefix( text ) )
        {
            var radix = char.ToLowerInvariant( text[1] ) switch
            {
                'x' => HexadecimalRadix,
                'b' => 2,
                _ => OctalRadix
            };

            var value = new CalculationNumber( 0m );
            foreach ( var character in text.AsSpan( 2 ) )
            {
                var digit = character is >= '0' and <= '9' ? character - '0' : char.ToLowerInvariant( character ) - 'a' + DecimalRadix;
                if ( IsInvalidRadixDigit( radix, digit ) )
                {
                    throw new FormatException( );
                }

                value = Binary( Binary( value, new( ( decimal ) radix ), '*' ), new( ( decimal ) digit ), '+' );
            }

            return value;
        }

        var floating = double.Parse( text, NumberStyles.Float, CultureInfo.InvariantCulture );
        if ( HasFloatingPointUnderflow( text, floating ) )
        {
            throw new ArithmeticException( );
        }

        if ( TryParseExactDecimal( text, floating, out var exact ) )
        {
            return new( exact );
        }

        return new( floating );
    }

    internal static CalculationNumber Binary( CalculationNumber left, CalculationNumber right, char operation )
    {
        if ( IsZeroDivisor( right, operation ) )
        {
            throw new ArithmeticException( );
        }

        if ( left.IsExact && right.IsExact )
        {
            try
            {
                var result = EvaluateExactBinary( left, right, operation );

                // Keep the decimal cancellation result; recompute only tiny products and quotients.
                if ( HasExactProductUnderflow( left, right, operation, result ) )
                {
                    return Floating( left, right, operation );
                }

                if ( IsSmallNonzeroResult( result ) )
                {
                    return new( ( double ) result );
                }

                return new( result );
            }
            catch ( OverflowException ) { }
        }

        return Floating( left, right, operation );
    }

    private static CalculationNumber Floating( CalculationNumber a, CalculationNumber b, char op )
    {
        var result = EvaluateFloatingBinary( a, b, op );
        if ( HasFloatingProductUnderflow( a, b, op, result ) )
        {
            throw new ArithmeticException( );
        }

        return new( result );
    }

    internal CalculationNumber Negate( ) => IsExact ? new( -Exact ) : new( -Approximate );

    internal int CompareTo( CalculationNumber other )
    {
        if ( IsExact && other.IsExact )
        {
            return Exact.CompareTo( other.Exact );
        }

        var approximate = IsExact ? other.Approximate : Approximate;
        if ( CanCompareAsDecimal( other, approximate ) )
        {
            return IsExact ? Exact.CompareTo( ( decimal ) approximate ) : (( decimal ) approximate).CompareTo( other.Exact );
        }

        return Double.CompareTo( other.Double );
    }

    internal CalculationNumber Power( CalculationNumber exponent )
    {
        if ( IsInvalidPower( exponent ) )
        {
            throw new ArithmeticException( );
        }

        if ( CanRaiseExactly( exponent ) )
        {
            var power = ( int ) exponent.Double;
            var factor = power < 0 ? Binary( new( 1m ), this, '/' ) : this;
            var result = new CalculationNumber( 1m );
            for ( var remaining = Math.Abs( power ); remaining > 0; remaining >>= 1 )
            {
                if ( (remaining & 1) != 0 )
                {
                    result = Binary( result, factor, '*' );
                }

                if ( remaining > 1 )
                {
                    factor = Binary( factor, factor, '*' );
                }
            }

            return result;
        }

        var approximate = Math.Pow( Double, exponent.Double );
        if ( HasPowerUnderflow( approximate ) )
        {
            throw new ArithmeticException( );
        }

        return new( approximate );
    }

    internal CalculationNumber Factorial( )
    {
        if ( IsInvalidFactorialArgument( ) )
        {
            throw new ArithmeticException( );
        }

        var result = 1m;
        for ( var i = 2; i <= Double; i++ )
        {
            try
            {
                result *= i;
            }
            catch ( OverflowException )
            {
                return FloatingFactorial( ( int ) Double );
            }
        }

        return new( result );
    }

    private static CalculationNumber FloatingFactorial( int limit )
    {
        var value = 1d;
        var correction = 0d;
        for ( var i = 2; i <= limit; i++ )
        {
            var product = value * i;
            correction = correction * i + Math.FusedMultiplyAdd( value, i, -product );
            value = product;
        }

        return new( value + correction );
    }

    private static bool IsInvalidFloatingPointValue( double value )
    {
        return !double.IsFinite( value ) || double.IsSubnormal( value );
    }

    private static bool HasIntegerRadixPrefix( string text )
    {
        return text.Length > 2 && text[0] == '0' && "xbo".Contains( char.ToLowerInvariant( text[1] ) );
    }

    private static bool IsInvalidRadixDigit( int radix, int digit )
    {
        return digit < 0 || digit >= radix;
    }

    private static bool HasFloatingPointUnderflow( string text, double floating )
    {
        return floating == 0 &&
               text
                   .Split( 'e', 'E' )[0]
                   .Any( character => character is >= '1' and <= '9' );
    }

    private static bool IsZeroDivisor( CalculationNumber right, char operation )
    {
        return operation is '/' or '%' && right.IsZero;
    }

    private static decimal EvaluateExactBinary( CalculationNumber left, CalculationNumber right, char operation )
    {
        return operation switch
        {
            '+' => left.Exact + right.Exact,
            '-' => left.Exact - right.Exact,
            '*' => left.Exact * right.Exact,
            '/' => left.Exact / right.Exact,
            '%' => left.Exact % right.Exact,
            _ => throw new FormatException( )
        };
    }

    private static bool HasExactProductUnderflow( CalculationNumber left, CalculationNumber right, char operation, decimal result )
    {
        return operation is '*' or '/' && decimal.Abs( result ) < MinimumExactMagnitude && !left.IsZero && !right.IsZero;
    }

    private static bool IsSmallNonzeroResult( decimal result )
    {
        return result != 0 && decimal.Abs( result ) < MinimumExactMagnitude;
    }

    private static double EvaluateFloatingBinary( CalculationNumber a, CalculationNumber b, char op )
    {
        return op switch
        {
            '+' => a.Double + b.Double,
            '-' => a.Double - b.Double,
            '*' => a.Double * b.Double,
            '/' => a.Double / b.Double,
            '%' => a.Double % b.Double,
            _ => throw new FormatException( )
        };
    }

    private static bool HasFloatingProductUnderflow( CalculationNumber a, CalculationNumber b, char op, double result )
    {
        return op is '*' or '/' && result == 0 && !a.IsZero && !b.IsZero;
    }

    private bool CanCompareAsDecimal( CalculationNumber other, double approximate )
    {
        return IsExact != other.IsExact &&
               (approximate == 0 || Math.Abs( approximate ) >= MinimumComparisonMagnitude) &&
               Math.Abs( approximate ) < ( double ) decimal.MaxValue;
    }

    private bool IsInvalidPower( CalculationNumber exponent )
    {
        return IsZero && exponent.CompareTo( new( 0m ) ) < 0 ||
               exponent.IsInteger && (exponent.CompareTo( new( -MaximumExactExponent ) ) < 0 || exponent.CompareTo( new( MaximumExactExponent ) ) > 0) ||
               CompareTo( new( 0m ) ) < 0 && !exponent.IsInteger;
    }

    private bool CanRaiseExactly( CalculationNumber exponent )
    {
        return IsExact && exponent.IsInteger;
    }

    private bool HasPowerUnderflow( double approximate )
    {
        return approximate == 0 && !IsZero;
    }

    private bool IsInvalidFactorialArgument( )
    {
        return !IsInteger || CompareTo( new( 0m ) ) < 0 || CompareTo( new( MaximumFactorialArgument ) ) > 0;
    }

    private static bool TryParseExactDecimal( string text, double floating, out decimal exact )
    {
        exact = default;

        return decimal.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out exact ) &&
               (floating == 0 || Math.Abs( floating ) >= MinimumDecimalMagnitude);
    }
}
