using System.Text.RegularExpressions;

namespace CommandDeck;

// Bare search only opts into a bounded calculation grammar. Explicit '=' input
// uses the full engine grammar for constants, functions and compound units.
public static partial class CalculatorInput
{
    private const int MaximumCandidateCharacters = 512;

    public static bool IsInlineCandidate( ParsedQuery query ) =>
        query is { ProviderFilter: null, IsCommandMode: false, IncludedFilters.Count: 0, ExcludedFilters.Count: 0 } &&
        !query.Expression.Clauses.Any( static clause => clause.Field is not null ) &&
        IsInlineCandidate( query.Original );

    private static bool IsInlineCandidate( string text )
    {
        if ( text.Length is 0 or > MaximumCandidateCharacters )
        {
            return false;
        }

        return DateCounting.IsCandidate( text ) || IsArithmetic( text ) || IsConversion( text );
    }

    private static bool IsConversion( string text )
    {
        var leading = text
            .AsSpan( )
            .TrimStart( );

        return !leading.IsEmpty &&
               (char.IsAsciiDigit( leading[0] ) || ".+-−$€£¥₹₩₪".Contains( leading[0] )) &&
               ConversionTokenRegex( )
                   .IsMatch( text ) &&
               CalculatorEngine.IsConversion( text );
    }

    [GeneratedRegex( @"\b(?:to|in|as)\b|->|→", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant )]
    private static partial Regex ConversionTokenRegex( );

    private static bool IsArithmetic( string text )
    {
        if ( HasNonArithmeticInput( text ) )
        {
            return false;
        }

        return BinaryOperatorRegex( )
            .IsMatch( text );
    }

    [GeneratedRegex( @"\A[+-]?[0-9]+(?:[-/][0-9]+)+\z" )]
    private static partial Regex DigitGroupsRegex( );

    [GeneratedRegex( @"[0-9)]\s*[-+*/%^]\s*(?:[-+]\s*)*[0-9.(]" )]
    private static partial Regex BinaryOperatorRegex( );

    private static bool HasNonArithmeticInput( string text )
    {
        return text.Any( static c => !char.IsAsciiDigit( c ) && !char.IsWhiteSpace( c ) && !".+-*/%^()".Contains( c ) ) ||
               DigitGroupsRegex( )
                   .IsMatch( text );
    }
}
