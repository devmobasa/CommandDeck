namespace CommandDeck;

public static class CalculatorEngine
{
    public const int MaximumExpressionLength = 512;

    public static bool IsConversion( string expression )
    {
        // Placeholder rates validate compatibility only; their values never become search results.
        var result = Evaluate( expression, static ( ) => CalculationCurrencies.PlaceholderRates );

        return result is { Status: CalculationStatus.Success, IsConversion: true };
    }

    public static CalculationResult Evaluate( string expression, Func<CurrencyRateSnapshot?>? rates = null )
    {
        try
        {
            if ( IsInvalidExpression( expression ) )
            {
                return new( ) { Status = CalculationStatus.Invalid };
            }

            var tokens = CalculationLexer.Tokenize( expression );
            NormalizeTemperatureShorthand( tokens );
            var context = new CalculationContext( rates );
            CalculationQuantity? target = null;
            string? numberBase = null;
            var split = -1;
            var candidates = FindConversionKeywords( tokens );

            // The rightmost complete target wins; the parser restricts remaining in to inches.
            foreach ( var candidate in candidates
                         .AsEnumerable( )
                         .Reverse( ) )
            {
                var remainder = tokens[(candidate + 1)..];
                if ( IsBaseConversion( remainder ) )
                {
                    split = candidate;
                    numberBase = remainder[0].Lower;

                    break;
                }

                try
                {
                    target = new CalculationParser( remainder, context, target: true ).Parse( );
                    split = candidate;

                    break;
                }
                catch ( Exception exception ) when ( exception is FormatException or ArithmeticException )
                {
                    // Not a complete target; the next keyword to the left may be.
                }
            }

            if ( split >= 0 )
            {
                tokens = tokens[..split];
            }

            var quantity = new CalculationParser( tokens, context ).Parse( );
            if ( target is not null )
            {
                if ( IsFuelEconomyConversion( target, quantity ) )
                {
                    var reciprocal = CalculationNumber.Binary( new( 1m ), quantity.BaseValue, '/' );
                    quantity = target with { Value = CalculationNumber.Binary( reciprocal, target.Factor, '/' ), UnitOnly = false };
                }
                else
                {
                    quantity = quantity.ConvertTo( target );
                }
            }

            var text = numberBase is not null ? CalculationFormatter.Base( quantity, numberBase ) : CalculationFormatter.Format( quantity );

            return new( )
            {
                Status = CalculationStatus.Success,
                Text = text,
                PreciseText = FormatPreciseCurrency( quantity ),
                RatesDate = context.Snapshot?.Date,
                IsConversion = split >= 0
            };
        }
        catch ( CalculationRatesUnavailableException )
        {
            return new( ) { Status = CalculationStatus.RatesUnavailable };
        }
        catch ( Exception )
        {
            return new( ) { Status = CalculationStatus.Invalid };
        }
    }

    private static List<int> FindConversionKeywords( CalculationToken[] tokens )
    {
        List<int> candidates = [];
        var depth = 0;
        for ( var index = 0; index < tokens.Length; index++ )
        {
            if ( tokens[index].Text == "(" )
            {
                depth++;
            }
            else if ( tokens[index].Text == ")" )
            {
                depth--;
            }

            if ( IsTopLevelConversion( tokens, depth, index ) )
            {
                candidates.Add( index );
            }
        }

        return candidates;
    }

    private static void NormalizeTemperatureShorthand( CalculationToken[] tokens )
    {
        var offset = tokens.Length > 0 && tokens[0].Text is "+" or "-" ? 1 : 0;
        if ( HasNoTemperatureShorthandShape( tokens, offset ) )
        {
            return;
        }

        static string? Temperature( string text ) => text
                .TrimStart( '°' )
                .ToLowerInvariant( ) switch
        {
            "c" => CalculationUnitSymbols.Celsius,
            "f" => CalculationUnitSymbols.Fahrenheit,
            "k" => CalculationUnitSymbols.Kelvin,
            _ => null
        };

        var source = Temperature( tokens[offset + 1].Text );
        var target = Temperature( tokens[offset + 3].Text );
        if ( source is not null && target is not null )
        {
            tokens[offset + 1] = new( source );
            tokens[offset + 3] = new( target );
        }
    }

    private static bool IsInvalidExpression( string expression )
    {
        return string.IsNullOrWhiteSpace( expression ) || expression.Length > MaximumExpressionLength;
    }

    private static bool IsTopLevelConversion( CalculationToken[] tokens, int depth, int i )
    {
        return depth == 0 &&
               tokens[i].Lower is CalculationTokenNames.To
                   or CalculationTokenNames.In
                   or CalculationTokenNames.As
                   or CalculationTokenNames.Arrow
                   or CalculationTokenNames.UnicodeArrow;
    }

    private static bool IsBaseConversion( CalculationToken[] remainder )
    {
        return remainder.Length == 1 && CalculationFormatter.IsBase( remainder[0].Lower );
    }

    private static bool IsFuelEconomyConversion( CalculationQuantity target, CalculationQuantity quantity )
    {
        return quantity.Units is [{ Unit.Symbol: CalculationUnitSymbols.MilesPerGallon, Power: 1 }] &&
               target.Units is [{ Unit.Symbol: CalculationUnitSymbols.LitresPer100Kilometres, Power: 1 }] ||
               quantity.Units is [{ Unit.Symbol: CalculationUnitSymbols.LitresPer100Kilometres, Power: 1 }] &&
               target.Units is [{ Unit.Symbol: CalculationUnitSymbols.MilesPerGallon, Power: 1 }];
    }

    private static bool HasNoTemperatureShorthandShape( CalculationToken[] tokens, int offset )
    {
        return tokens.Length != offset + 4 || !tokens[offset].Number || tokens[offset + 2].Lower is not (CalculationTokenNames.To or CalculationTokenNames.In);
    }

    private static string? FormatPreciseCurrency( CalculationQuantity quantity )
    {
        var code = CalculationFormatter.CurrencyCode( quantity );
        if ( code is null )
        {
            return null;
        }

        return CalculationFormatter.Number( quantity.Value, preserveInteger: false ) + " " + code;
    }
}

public enum CalculationStatus { Success, Invalid, RatesUnavailable }

public sealed record CalculationResult
{
    public required CalculationStatus Status { get; init; }
    public string? Text { get; init; }
    public string? PreciseText { get; init; }

    public DateOnly? RatesDate { get; init; }

    // True for a successful to/in/as conversion; inline detection promotes only conversions.
    public bool IsConversion { get; init; }
}

public sealed record CurrencyRateSnapshot( DateOnly Date, IReadOnlyDictionary<string, decimal> PerEuro );
