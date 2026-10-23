using System.Globalization;

namespace CommandDeck;

internal static class CalculationFormatter
{
    private const int MinimumFixedNotationExponent = -6;
    private const int MaximumFixedNotationExponent = 15;
    private const uint HexadecimalRadix = 16u;
    private const uint OctalRadix = 8u;
    private const decimal FixedCurrencyNotationLimit = 1000000000000000m;

    internal static string? CurrencyCode( CalculationQuantity quantity )
    {
        if ( quantity.Units is [{ Unit.Dimension.Currency: 1, Power: 1 }] )
        {
            return quantity.Units[0].Unit.Symbol;
        }
        else
        {
            return null;
        }
    }

    internal static string Format( CalculationQuantity quantity )
    {
        var code = CurrencyCode( quantity );
        if ( code is not null )
        {
            var value = quantity.Value;
            var negative = value.Double < 0;
            var magnitude = negative ? value.Negate( ) : value;
            var displayed = Number( magnitude, preserveInteger: false );
            var amount = FormatCurrencyAmount( magnitude, displayed );
            var symbol = CalculationCurrencies.Symbol( code );
            var sign = negative && amount != "0.00" ? "-" : "";

            return sign + (symbol is not null ? symbol + amount : code + " " + amount);
        }

        var unit = Units( quantity.Units );

        return Number( quantity.Value ) + (unit.Length == 0 || unit == "°" || unit.StartsWith( '/' ) ? "" : " ") + unit;
    }

    internal static string Number( CalculationNumber number, bool preserveInteger = true )
    {
        if ( number.IsZero )
        {
            return "0";
        }

        if ( CanPreserveInteger( number, preserveInteger ) )
        {
            return number.Exact.ToString( "0", CultureInfo.InvariantCulture );
        }

        string formatted;
        if ( number.IsExact )
        {
            formatted = number.Exact.ToString( CalculationNumberFormats.DisplayPrecision, CultureInfo.InvariantCulture );
        }
        else
        {
            formatted = number.Double.ToString( CalculationNumberFormats.DisplayPrecision, CultureInfo.InvariantCulture );
        }

        if ( formatted.Contains( 'E' ) )
        {
            var pieces = formatted.Split( 'E' );
            var exponent = int.Parse( pieces[1], CultureInfo.InvariantCulture );
            if ( exponent is >= MinimumFixedNotationExponent and < MaximumFixedNotationExponent )
            {
                return double
                    .Parse( formatted, CultureInfo.InvariantCulture )
                    .ToString( "0.####################", CultureInfo.InvariantCulture );
            }

            return pieces[0] + "e" + exponent.ToString( CultureInfo.InvariantCulture );
        }

        return formatted == "-0" ? "0" : formatted;
    }

    private static string Units( CalculationUnitPower[] terms )
    {
        static string Term( CalculationUnitPower term, int index )
        {
            var power = Math.Abs( term.Power );
            var symbol = term.Unit.Symbol;
            if ( index > 0 || term.Power < 0 )
            {
                symbol = symbol switch
                {
                    "g" => "gram",
                    "in" => "inch",
                    _ => symbol
                };
            }

            if ( NeedsUnitParentheses( term, power, symbol ) )
            {
                symbol = term
                    .Unit.Symbols.Split( '|' )[0];

                if ( symbol.Contains( '/' ) )
                {
                    symbol = "(" + symbol + ")";
                }
            }

            return symbol +
                   (power switch
                   {
                       1 => "",
                       2 => "²",
                       3 => "³",
                       _ => "^" + power.ToString( CultureInfo.InvariantCulture )
                   });
        }

        var numerator = string.Join( '·', terms
            .Where( term => term.Power > 0 )
            .Select( Term ) );

        var denominator = GetDenominatorTerms( terms );
        if ( denominator.Length == 0 )
        {
            return numerator;
        }

        return numerator + "/" + (denominator.Length > 1 ? "(" : "") + string.Join( '·', denominator.Select( Term ) ) + (denominator.Length > 1 ? ")" : "");
    }

    private static uint ResolveRadix( string name ) => name switch
    {
        "hex" or "hexadecimal" => HexadecimalRadix,
        "bin" or "binary" => 2u,
        "oct" or "octal" => OctalRadix,
        "dec" or "decimal" => 10u,
        _ => 0u
    };

    internal static bool IsBase( string name ) => ResolveRadix( name ) != 0;

    internal static string Base( CalculationQuantity quantity, string name )
    {
        quantity.RequireScalar( );

        var integer = quantity.Value.IsExact ? quantity.Value.Exact : checked(( decimal ) quantity.Value.Double);
        if ( IsOutsideSignedIntegerRange( quantity, integer ) )
        {
            throw new ArithmeticException( );
        }

        var value = ( long ) integer;
        var radix = ResolveRadix( name );
        if ( radix == 10u )
        {
            return value.ToString( CultureInfo.InvariantCulture );
        }

        if ( radix == 0 )
        {
            radix = OctalRadix;
        }

        string prefix;
        if ( radix == HexadecimalRadix )
        {
            prefix = "0x";
        }
        else
        {
            if ( radix == 2 )
            {
                prefix = "0b";
            }
            else
            {
                prefix = "0o";
            }
        }

        var remaining = ( ulong ) decimal.Abs( integer );
        var digits = "";

        do
        {
            digits = "0123456789ABCDEF"[( int ) (remaining % radix)] + digits;
            remaining /= radix;
        } while ( remaining > 0 );

        return (value < 0 ? "-" : "") + prefix + digits;
    }

    private static string FormatCurrencyAmount( CalculationNumber magnitude, string displayed )
    {
        if ( magnitude.CompareTo( new( FixedCurrencyNotationLimit ) ) >= 0 )
        {
            return displayed;
        }
        else
        {
            return decimal
                .Round( decimal.Parse( displayed, NumberStyles.Float, CultureInfo.InvariantCulture ), 2, MidpointRounding.AwayFromZero )
                .ToString( "F2", CultureInfo.InvariantCulture );
        }
    }

    private static bool CanPreserveInteger( CalculationNumber number, bool preserveInteger )
    {
        return preserveInteger && number is { IsExact: true, IsInteger: true };
    }

    private static bool NeedsUnitParentheses( CalculationUnitPower term, int power, string symbol )
    {
        return symbol.Contains( '/' ) && (power > 1 || term.Power < 0);
    }

    private static CalculationUnitPower[] GetDenominatorTerms( CalculationUnitPower[] terms )
    {
        return terms
            .Where( term => term.Power < 0 )
            .ToArray( );
    }

    private static bool IsOutsideSignedIntegerRange( CalculationQuantity quantity, decimal integer )
    {
        return !quantity.Value.IsInteger || integer < long.MinValue || integer > long.MaxValue;
    }
}

internal static class CalculationNumberFormats
{
    internal const string DisplayPrecision = "G15";
}
