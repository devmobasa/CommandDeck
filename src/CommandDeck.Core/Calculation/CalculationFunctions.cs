using System.Globalization;

namespace CommandDeck;

internal static class CalculationFunctions
{
    private const string SqrtToken = "sqrt";
    private const string CbrtToken = "cbrt";
    private const string SinToken = "sin";
    private const string CosToken = "cos";
    private const string TanToken = "tan";
    private const string AsinToken = "asin";
    private const string AcosToken = "acos";
    private const string AtanToken = "atan";
    private const string LogToken = "log";
    private const string Log10Token = "log10";
    private const string Log2Token = "log2";
    private const string LnToken = "ln";
    private const string ExpToken = "exp";
    private const string AbsToken = "abs";
    private const string RoundToken = "round";
    private const string FloorToken = "floor";
    private const string CeilToken = "ceil";
    private const string GcdToken = "gcd";
    private const string LcmToken = "lcm";
    private const string MinToken = "min";
    private const string MaxToken = "max";

    private const double MinimumSnapMagnitude = 1e-12;
    private const int DegreesPerHalfTurn = 180;
    private const decimal MaximumRoundingDigits = 28m;

    private static readonly Dictionary<string, (int Minimum, int Maximum, bool RequiresParentheses)> Functions = new( StringComparer.Ordinal )
    {
        [SqrtToken] = (1, 1, false),
        [CbrtToken] = (1, 1, false),
        [SinToken] = (1, 1, false),
        [CosToken] = (1, 1, false),
        [TanToken] = (1, 1, false),
        [AsinToken] = (1, 1, false),
        [AcosToken] = (1, 1, false),
        [AtanToken] = (1, 1, false),
        [LogToken] = (1, 2, false),
        [Log10Token] = (1, 1, false),
        [Log2Token] = (1, 1, false),
        [LnToken] = (1, 1, false),
        [ExpToken] = (1, 1, false),
        [AbsToken] = (1, 1, false),
        [RoundToken] = (1, 2, false),
        [FloorToken] = (1, 1, false),
        [CeilToken] = (1, 1, false),
        [GcdToken] = (2, int.MaxValue, true),
        [LcmToken] = (2, int.MaxValue, true),
        [MinToken] = (2, int.MaxValue, true),
        [MaxToken] = (2, int.MaxValue, true),
    };

    internal static bool IsFunction( string name ) => Functions.ContainsKey( name );

    internal static bool RequiresParentheses( string name ) => Functions[name].RequiresParentheses;

    internal static CalculationQuantity? Constant( string name ) => name switch
    {
        "pi" or "π" => CalculationQuantity.Scalar( 3.1415926535897932384626433833m ),
        "tau" or "τ" => CalculationQuantity.Scalar( 6.2831853071795864769252867666m ),
        "e" => CalculationQuantity.Scalar( 2.7182818284590452353602874714m ),
        "phi" or "φ" => CalculationQuantity.Scalar( 1.6180339887498948482045868344m ),
        "c" => new( new( 299792458m ), [new( CalculationUnits.Canonical( "m" ), 1 ), new( CalculationUnits.Canonical( "s" ), -1 )] ),
        "g" => new( new( 9.80665m ), [new( CalculationUnits.Canonical( "m" ), 1 ), new( CalculationUnits.Canonical( "s" ), -2 )] ),
        _ => null
    };

    internal static CalculationQuantity Call( string name, List<CalculationQuantity> arguments )
    {
        if ( !Functions.TryGetValue( name, out var function ) || arguments.Count < function.Minimum || arguments.Count > function.Maximum )
        {
            throw new FormatException( );
        }

        var first = arguments[0];
        first.RejectOffset( );

        if ( name is MinToken or MaxToken )
        {
            var chosen = first;
            foreach ( var argument in arguments.Skip( 1 ) )
            {
                argument.RejectOffset( );

                var compared = argument
                    .ConvertTo( chosen )
                    .Value.CompareTo( chosen.Value );

                if ( name == MinToken ? compared < 0 : compared > 0 )
                {
                    chosen = argument;
                }
            }

            return chosen with { BarePercent = false, UnitOnly = false };
        }

        if ( name is GcdToken or LcmToken )
        {
            return IntegerFunction( name, arguments );
        }

        if ( name is SqrtToken or CbrtToken )
        {
            return first.Root( name == SqrtToken ? 2 : 3 );
        }

        if ( name is AbsToken or RoundToken or FloorToken or CeilToken )
        {
            return Round( name, arguments );
        }

        if ( name is SinToken or CosToken or TanToken )
        {
            var radians = GetRadians( first );
            var sin = Snap( Math.Sin( radians ) );
            var cos = Snap( Math.Cos( radians ) );
            if ( IsUndefinedTangent( name, cos ) )
            {
                throw new ArithmeticException( );
            }

            return new( new CalculationNumber( name switch
            {
                SinToken => sin,
                CosToken => cos,
                _ => sin / cos
            } ), [] );
        }

        var x = ScalarValue( first );
        var answer = EvaluateTranscendental( name, arguments, x );
        if ( HasExponentialUnderflow( name, answer ) )
        {
            throw new ArithmeticException( );
        }

        return new( new CalculationNumber( answer ), name is AsinToken or AcosToken or AtanToken ? [new( CalculationUnits.Canonical( "°" ), 1 )] : [] );
    }

    private static CalculationQuantity Round( string name, List<CalculationQuantity> arguments )
    {
        var first = arguments[0];
        var digits = 0;
        if ( arguments.Count == 2 )
        {
            arguments[1]
                .RequireScalar( );

            if ( IsInvalidRoundingPrecision( arguments ) )
            {
                throw new ArithmeticException( );
            }

            digits = ( int ) arguments[1].Value.Double;
        }

        var value = first.Value;
        if ( TryGetDisplayedRoundingValue( name, value, out var displayed ) )
        {
            value = new( displayed );
        }

        var scale = Math.Pow( 10, digits );
        var scaled = GetScaledRoundingValue( name, value, scale );
        var result = RoundNumber( name, digits, value, scale, scaled );

        return first with { Value = result, BarePercent = false, UnitOnly = false };
    }

    private static double Snap( double value ) => Math.Abs( value ) < MinimumSnapMagnitude ? 0 : value;

    private static double InverseTrig( string name, CalculationNumber value )
    {
        if ( IsOutsideInverseTrigDomain( value ) )
        {
            throw new ArithmeticException( );
        }

        return (name == AsinToken ? Math.Asin( value.Double ) : Math.Acos( value.Double )) * DegreesPerHalfTurn / Math.PI;
    }

    private static double Logarithm( string name, CalculationNumber value, List<CalculationQuantity> arguments )
    {
        if ( value.CompareTo( new( 0m ) ) <= 0 )
        {
            throw new ArithmeticException( );
        }

        if ( UsesDefaultLogarithmBase( name, arguments ) )
        {
            return name switch
            {
                Log2Token => Math.Log2( value.Double ),
                LnToken => Math.Log( value.Double ),
                _ => Math.Log10( value.Double )
            };
        }

        var numberBase = ScalarValue( arguments[1] );
        if ( IsInvalidLogarithmBase( numberBase ) )
        {
            throw new ArithmeticException( );
        }

        // When an exact base rounds to 1, ln(base) is base - 1 to double precision.
        var logarithm = numberBase.Double == 1 ? ( double ) (numberBase.Exact - 1m) : Math.Log( numberBase.Double );

        return Math.Log( value.Double ) / logarithm;
    }

    private static CalculationNumber ScalarValue( CalculationQuantity quantity )
    {
        quantity.RequireScalar( );

        return quantity.Value;
    }

    private static CalculationQuantity IntegerFunction( string name, List<CalculationQuantity> arguments )
    {
        var result = new CalculationNumber( 0m );
        var first = true;
        foreach ( var argument in arguments )
        {
            argument.RequireScalar( );

            if ( !argument.Value.IsInteger )
            {
                throw new ArithmeticException( );
            }

            var number = argument.Value.CompareTo( new( 0m ) ) < 0 ? argument.Value.Negate( ) : argument.Value;
            if ( IsFirstLeastCommonMultipleArgument( name, first ) )
            {
                result = number;
                first = false;

                continue;
            }

            var a = result;
            var b = number;
            while ( !b.IsZero )
            {
                (a, b) = (b, CalculationNumber.Binary( a, b, '%' ));
            }

            if ( name == GcdToken )
            {
                result = a;
            }
            else
            {
                if ( a.IsZero )
                {
                    result = new( 0m );
                }
                else
                {
                    result = CalculationNumber.Binary( CalculationNumber.Binary( result, a, '/' ), number, '*' );
                }
            }
        }

        return new( result, [] );
    }

    private static double GetRadians( CalculationQuantity first )
    {
        if ( first.Dimension == new CalculationDimension( Angle: 1 ) )
        {
            return first.BaseValue.Double;
        }
        else
        {
            return ScalarValue( first )
                       .Double *
                   Math.PI /
                   DegreesPerHalfTurn;
        }
    }

    private static bool IsUndefinedTangent( string name, double cos )
    {
        return name == TanToken && cos == 0;
    }

    private static double EvaluateTranscendental( string name, List<CalculationQuantity> arguments, CalculationNumber x )
    {
        return name switch
        {
            AsinToken or AcosToken => InverseTrig( name, x ),
            AtanToken => Math.Atan( x.Double ) * DegreesPerHalfTurn / Math.PI,
            LogToken or Log10Token or Log2Token or LnToken => Logarithm( name, x, arguments ),
            ExpToken => Math.Exp( x.Double ),
            _ => throw new FormatException( )
        };
    }

    private static bool HasExponentialUnderflow( string name, double answer )
    {
        return name == ExpToken && answer == 0;
    }

    private static bool IsInvalidRoundingPrecision( List<CalculationQuantity> arguments )
    {
        return !arguments[1].Value.IsInteger ||
               arguments[1]
                   .Value.CompareTo( new( 0m ) ) <
               0 ||
               arguments[1]
                   .Value.CompareTo( new( MaximumRoundingDigits ) ) >
               0;
    }

    private static double GetScaledRoundingValue( string name, CalculationNumber value, double scale )
    {
        if ( NeedsFloatingRounding( name, value ) )
        {
            return double.Parse( (value.Double * scale).ToString( "G15", CultureInfo.InvariantCulture ), CultureInfo.InvariantCulture );
        }
        else
        {
            return 0;
        }
    }

    private static CalculationNumber RoundNumber( string name, int digits, CalculationNumber value, double scale, double scaled )
    {
        if ( value.IsExact )
        {
            return new CalculationNumber( name switch
            {
                AbsToken => decimal.Abs( value.Exact ),
                FloorToken => decimal.Floor( value.Exact ),
                CeilToken => decimal.Ceiling( value.Exact ),
                _ => decimal.Round( value.Exact, digits, MidpointRounding.AwayFromZero )
            } );
        }
        else
        {
            return new CalculationNumber( name switch
            {
                AbsToken => Math.Abs( value.Double ),
                FloorToken => Math.Floor( value.Double ),
                CeilToken => Math.Ceiling( value.Double ),
                _ => double.IsFinite( scaled ) ? Math.Round( scaled, MidpointRounding.AwayFromZero ) / scale : value.Double
            } );
        }
    }

    private static bool IsOutsideInverseTrigDomain( CalculationNumber value )
    {
        return value.CompareTo( new( -1m ) ) < 0 || value.CompareTo( new( 1m ) ) > 0;
    }

    private static bool UsesDefaultLogarithmBase( string name, List<CalculationQuantity> arguments )
    {
        return name != LogToken || arguments.Count == 1;
    }

    private static bool IsInvalidLogarithmBase( CalculationNumber numberBase )
    {
        return numberBase.CompareTo( new( 0m ) ) <= 0 || numberBase.CompareTo( new( 1m ) ) == 0;
    }

    private static bool IsFirstLeastCommonMultipleArgument( string name, bool first )
    {
        return first && name == LcmToken;
    }

    private static bool TryGetDisplayedRoundingValue( string name, CalculationNumber value, out decimal displayed )
    {
        displayed = default;

        return name == RoundToken &&
               !value.IsExact &&
               Math.Abs( value.Double ) >= CalculationNumber.MinimumDecimalMagnitude &&
               decimal.TryParse( CalculationFormatter.Number( value ), NumberStyles.Float, CultureInfo.InvariantCulture, out displayed );
    }

    private static bool NeedsFloatingRounding( string name, CalculationNumber value )
    {
        return name == RoundToken && !value.IsExact;
    }
}
