using System.Collections.Frozen;

namespace CommandDeck;

internal static class CalculationCurrencies
{
    internal const string RateBaseCode = "EUR";

    private static readonly (string Code, string? Symbol, string[] Aliases)[] Currencies =
    [
        new( "USD", "$", ["dollar", "dollars"] ),
        new( RateBaseCode, "€", ["euro", "euros"] ),
        new( "GBP", "£", [] ), new( "JPY", "¥", ["yen"] ),
        new( "INR", "₹", ["rupee", "rupees"] ), new( "KRW", "₩", [] ), new( "ILS", "₪", [] ),
        new( "CNY", null, ["yuan"] ),
        .. "CZK DKK HUF PLN RON SEK CHF ISK NOK TRY AUD BRL CAD HKD IDR MXN MYR NZD PHP SGD THB ZAR"
            .Split( ' ' )
            .Select( code => (code, ( string? ) null, Array.Empty<string>( )) )
    ];

    private static readonly FrozenSet<string> Supported = Currencies
        .Select( row => row.Code )
        .ToFrozenSet( StringComparer.OrdinalIgnoreCase );

    private static readonly FrozenSet<string> Unsupported = "btc bitcoin bitcoins eth ethereum rub ₽ ₿ aed bam twd"
        .Split( ' ' )
        .ToFrozenSet( StringComparer.OrdinalIgnoreCase );

    private static readonly FrozenDictionary<string, string> Aliases = Currencies
        .SelectMany( row => (row.Symbol is null ? row.Aliases : row.Aliases.Append( row.Symbol )).Select( alias => KeyValuePair.Create( alias, row.Code ) ) )
        .ToFrozenDictionary( StringComparer.OrdinalIgnoreCase );

    private static readonly FrozenDictionary<string, string?> Symbols =
        Currencies.ToFrozenDictionary( row => row.Code, row => row.Symbol, StringComparer.Ordinal );

    internal static readonly CurrencyRateSnapshot PlaceholderRates = new(
        DateOnly.MinValue, Supported.ToFrozenDictionary( code => code, _ => 1m, StringComparer.OrdinalIgnoreCase ) );

    internal static bool IsCurrency( string spelling ) => Aliases.ContainsKey( spelling ) || Supported.Contains( spelling );

    internal static string? Resolve( string spelling )
    {
        if ( Unsupported.Contains( spelling ) )
        {
            throw new FormatException( );
        }

        if ( Aliases.TryGetValue( spelling, out var code ) )
        {
            return code;
        }
        else
        {
            if ( Supported.Contains( spelling ) )
            {
                return spelling.ToUpperInvariant( );
            }
            else
            {
                return null;
            }
        }
    }

    internal static string? Symbol( string code ) => Symbols.GetValueOrDefault( code );
}
