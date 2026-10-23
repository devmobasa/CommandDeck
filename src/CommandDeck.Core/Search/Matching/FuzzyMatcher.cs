using System.Globalization;
using System.Text;

namespace CommandDeck;

public static partial class FuzzyMatcher
{
    private const double EmptyQueryScore = 0.15;
    private const double PrefixMatchScore = 0.94;
    private const double WordMatchScore = 0.86;
    private const double SubstringMatchScore = 0.78;
    private const double MinimumPrefixScore = 0.88;
    private const double MinimumWordScore = 0.80;
    private const double MinimumSubstringScore = 0.70;
    private const double MatchOffsetPenalty = 0.001;
    private const double MaximumPartialScore = 0.95;
    private const double MinimumAcronymScore = 0.66;
    private const double AcronymCoverageBonus = 0.04;
    private const double MaximumAcronymScore = 0.70;
    private const double SubsequenceAdjacencyBonus = 0.7;
    private const double SubsequenceConsecutiveBonus = 0.08;
    private const double SubsequenceWordBonus = 0.9;
    private const double MinimumSubsequenceScore = 0.05;
    private const double MaximumSubsequenceScore = 0.52;
    private const int LatinBasicStart = 0x0041;
    private const int LatinExtendedEnd = 0x024f;
    private const int LatinAdditionalStart = 0x1e00;
    private const int LatinAdditionalEnd = 0x1eff;
    private const int LatinExtendedCStart = 0x2c60;
    private const int LatinExtendedCEnd = 0x2c7f;
    private const int LatinExtendedDStart = 0xa720;
    private const int LatinExtendedDEnd = 0xa7ff;
    private const int LatinExtendedEStart = 0xab30;
    private const int LatinExtendedEEnd = 0xab6f;
    private const double MaximumLengthPenalty = 0.06;
    private const double LengthDifferencePenalty = 0.002;
    private const double QueryLengthScale = 2.8;
    private const double CandidateLengthScale = 0.25;

    private const int MaximumTypoTokenCharacters = 48;

    public static double Score( string query, string candidate ) =>
        Evaluate( query, candidate, includeRanges: false ).Score;

    public static double ScorePhrase( string query, string candidate ) =>
        Evaluate( query, candidate, includeRanges: false, requireContiguous: true ).Score;

    public static List<MatchedRange> FindRanges( string query, string candidate ) =>
        Evaluate( query, candidate, includeRanges: true )
            .Ranges ??
        [];

    internal static int SearchTextLength( string query ) =>
        new StringInfo( Fold( query.Trim( ) )
            .Text ).LengthInTextElements;

    // Uses exactly the same folding and word boundaries as lexical matching.
    public static bool IsStrongPrefix( string query, string candidate )
    {
        var term = Fold( query.Trim( ) ).Text;
        var text = Fold( candidate ).Text;

        if ( term.Length == 0 )
        {
            return false;
        }

        if ( text.StartsWith( term, StringComparison.OrdinalIgnoreCase ) )
        {
            return true;
        }

        for ( var index = 0; index <= text.Length - term.Length; index++ )
        {
            if ( MatchesWordPrefix( term, text, index ) )
            {
                return true;
            }
        }

        return false;
    }

    private static MatchEvaluation Evaluate( string query, string candidate, bool includeRanges, bool requireContiguous = false )
    {
        if ( string.IsNullOrWhiteSpace( query ) )
        {
            return new MatchEvaluation( EmptyQueryScore, null );
        }

        if ( string.IsNullOrEmpty( candidate ) )
        {
            return default;
        }

        var foldedQuery = Fold( query.Trim( ) );
        var foldedCandidate = Fold( candidate );

        if ( foldedQuery.Text.Length == 0 )
        {
            return default;
        }

        var match = EvaluateFolded( foldedQuery.Text, foldedCandidate.Text, includeRanges, requireContiguous );
        if ( includeRanges && match.Ranges is not null )
        {
            return match with { Ranges = foldedCandidate.MapRanges( match.Ranges ) };
        }
        else
        {
            return match;
        }
    }

    private static MatchEvaluation EvaluateFolded( string normalizedQuery, string candidate, bool includeRanges, bool requireContiguous )
    {
        var direct = candidate.IndexOf( normalizedQuery, StringComparison.OrdinalIgnoreCase );
        if ( direct >= 0 )
        {
            if ( candidate.Length == normalizedQuery.Length )
            {
                return new MatchEvaluation( 1, includeRanges ? [new MatchedRange( 0, normalizedQuery.Length )] : null );
            }

            var prefix = direct == 0;
            var boundary = !prefix && IsWordStart( candidate, direct );
            var directScore = GetDirectMatchScore( prefix, boundary );

            var lengthPenalty = Math.Min( MaximumLengthPenalty, (candidate.Length - normalizedQuery.Length) * LengthDifferencePenalty );

            var minimumScore = GetMinimumMatchScore( prefix, boundary );

            return new MatchEvaluation( Math.Clamp( directScore - lengthPenalty - (direct * MatchOffsetPenalty), minimumScore, MaximumPartialScore ),
                includeRanges ? [new MatchedRange( direct, normalizedQuery.Length )] : null );
        }

        if ( requireContiguous )
        {
            return default;
        }

        if ( TryMatchAcronym( normalizedQuery, candidate, includeRanges, out var acronym ) )
        {
            return acronym;
        }

        if ( TryMatchTypo( normalizedQuery, candidate, includeRanges, out var typo ) )
        {
            return typo;
        }

        return MatchSubsequence( normalizedQuery, candidate, includeRanges );
    }

    private static bool TryMatchAcronym( string query, string candidate, bool includeRanges, out MatchEvaluation match )
    {
        match = default;

        if ( CannotMatchInitials( query ) )
        {
            return false;
        }

        List<MatchedRange>? ranges = includeRanges ? [] : null;
        var queryIndex = 0;
        var boundaryCount = 0;
        for ( var index = 0; index < candidate.Length; index++ )
        {
            if ( !IsWordStart( candidate, index ) )
            {
                continue;
            }

            boundaryCount++;

            if ( char.ToUpperInvariant( candidate[index] ) != char.ToUpperInvariant( query[queryIndex] ) )
            {
                continue;
            }

            ranges?.Add( new MatchedRange( index, 1 ) );
            queryIndex++;

            if ( queryIndex == query.Length )
            {
                var coverage = query.Length / ( double ) Math.Max( query.Length, boundaryCount );

                match = new MatchEvaluation( Math.Clamp( MinimumAcronymScore + (coverage * AcronymCoverageBonus), MinimumAcronymScore, MaximumAcronymScore ),
                    ranges );

                return true;
            }
        }

        return false;
    }

    private static MatchEvaluation MatchSubsequence( string query, string candidate, bool includeRanges )
    {
        List<int>? positions = includeRanges ? [] : null;
        var queryIndex = 0;
        var previousMatch = -2;
        var consecutive = 0;
        double score = 0;
        for ( var candidateIndex = 0; candidateIndex < candidate.Length && queryIndex < query.Length; candidateIndex++ )
        {
            if ( char.ToUpperInvariant( candidate[candidateIndex] ) != char.ToUpperInvariant( query[queryIndex] ) )
            {
                continue;
            }

            positions?.Add( candidateIndex );
            score += 1;

            if ( candidateIndex == previousMatch + 1 )
            {
                consecutive++;
                score += SubsequenceAdjacencyBonus + (consecutive * SubsequenceConsecutiveBonus);
            }
            else
            {
                consecutive = 0;
            }

            if ( IsWordStart( candidate, candidateIndex ) )
            {
                score += SubsequenceWordBonus;
            }

            previousMatch = candidateIndex;
            queryIndex++;
        }

        if ( queryIndex != query.Length )
        {
            return default;
        }

        var normalized = score / Math.Max( query.Length * QueryLengthScale, candidate.Length * CandidateLengthScale );

        return new MatchEvaluation( Math.Clamp( normalized, MinimumSubsequenceScore, MaximumSubsequenceScore ),
            includeRanges ? CollapseRanges( positions! ) : null );
    }

    private static List<MatchedRange> CollapseRanges( List<int> positions )
    {
        List<MatchedRange> ranges = [];
        var start = positions[0];
        var length = 1;
        for ( var index = 1; index < positions.Count; index++ )
        {
            if ( positions[index] == positions[index - 1] + 1 )
            {
                length++;
            }
            else
            {
                ranges.Add( new MatchedRange( start, length ) );
                start = positions[index];
                length = 1;
            }
        }

        ranges.Add( new MatchedRange( start, length ) );

        return ranges;
    }

    private static bool IsWordStart( string value, int index )
    {
        if ( IsOutsideWord( value, index ) )
        {
            return false;
        }

        if ( index == 0 || !char.IsLetterOrDigit( value[index - 1] ) )
        {
            return true;
        }

        var current = value[index];
        var previous = value[index - 1];
        if ( char.IsDigit( current ) != char.IsDigit( previous ) )
        {
            return true;
        }

        if ( char.IsUpper( current ) && char.IsLower( previous ) )
        {
            return true;
        }

        return char.IsUpper( current ) && char.IsUpper( previous ) && index + 1 < value.Length && char.IsLower( value[index + 1] );
    }

    private static FoldedText Fold( string value )
    {
        if ( value.All( static character => char.IsAscii( character ) ) )
        {
            return new FoldedText( value, null, value.Length );
        }

        StringBuilder text = new( value.Length );
        List<int> originalIndices = new( value.Length );
        var elements = StringInfo.GetTextElementEnumerator( value );
        while ( elements.MoveNext( ) )
        {
            var originalIndex = elements.ElementIndex;
            var decomposed = elements
                .GetTextElement( )
                .Normalize( NormalizationForm.FormD );

            var foldLatinDiacritics = IsLatinBase( decomposed );
            for ( var index = 0; index < decomposed.Length; index++ )
            {
                var category = CharUnicodeInfo.GetUnicodeCategory( decomposed, index );
                var characterLength = GetCharacterLength( decomposed, index );
                if ( foldLatinDiacritics && category is UnicodeCategory.NonSpacingMark )
                {
                    index += characterLength - 1;

                    continue;
                }

                for ( var offset = 0; offset < characterLength; offset++ )
                {
                    text.Append( decomposed[index + offset] );
                    originalIndices.Add( originalIndex );
                }

                index += characterLength - 1;
            }
        }

        return new FoldedText( text.ToString( ), originalIndices.ToArray( ), value.Length );
    }

    private static bool IsLatinBase( string value )
    {
        if ( !Rune.TryGetRuneAt( value, 0, out var rune ) )
        {
            return false;
        }

        var scalar = rune.Value;

        return scalar is >= LatinBasicStart and <= LatinExtendedEnd
            or >= LatinAdditionalStart and <= LatinAdditionalEnd
            or >= LatinExtendedCStart and <= LatinExtendedCEnd
            or >= LatinExtendedDStart and <= LatinExtendedDEnd
            or >= LatinExtendedEStart and <= LatinExtendedEEnd;
    }

    private readonly record struct MatchEvaluation( double Score, List<MatchedRange>? Ranges );

    private readonly record struct FoldedText( string Text, int[]? OriginalIndices, int OriginalLength )
    {
        public List<MatchedRange> MapRanges( List<MatchedRange> ranges )
        {
            if ( OriginalIndices is null )
            {
                return [.. ranges];
            }

            List<MatchedRange> mapped = new( ranges.Count );
            foreach ( var range in ranges )
            {
                var start = OriginalIndices[range.Start];
                var foldedEnd = range.Start + range.Length;
                var end = GetOriginalEnd( OriginalIndices, OriginalLength, foldedEnd );
                mapped.Add( new MatchedRange( start, Math.Max( 1, end - start ) ) );
            }

            return mapped;
        }

        private static int GetOriginalEnd( int[] originalIndices, int originalLength, int foldedEnd )
        {
            if ( foldedEnd < originalIndices.Length )
            {
                return originalIndices[foldedEnd];
            }
            else
            {
                return originalLength;
            }
        }
    }

    private static bool MatchesWordPrefix( string term, string text, int index )
    {
        return IsWordStart( text, index ) &&
               text
                   .AsSpan( index )
                   .StartsWith( term, StringComparison.OrdinalIgnoreCase );
    }

    private static bool CannotMatchInitials( string query )
    {
        return query.Length < 2 || query.Any( static character => !char.IsLetterOrDigit( character ) );
    }

    private static bool IsOutsideWord( string value, int index )
    {
        return index < 0 || index >= value.Length || !char.IsLetterOrDigit( value[index] );
    }

    private static int GetCharacterLength( string decomposed, int index )
    {
        if ( IsDecomposedSurrogatePair( decomposed, index ) )
        {
            return 2;
        }
        else
        {
            return 1;
        }
    }

    private static bool IsDecomposedSurrogatePair( string decomposed, int index )
    {
        return char.IsHighSurrogate( decomposed[index] ) && index + 1 < decomposed.Length && char.IsLowSurrogate( decomposed[index + 1] );
    }

    private static double GetDirectMatchScore( bool prefix, bool boundary )
    {
        if ( prefix )
        {
            return PrefixMatchScore;
        }

        if ( boundary )
        {
            return WordMatchScore;
        }

        return SubstringMatchScore;
    }

    private static double GetMinimumMatchScore( bool prefix, bool boundary )
    {
        if ( prefix )
        {
            return MinimumPrefixScore;
        }

        if ( boundary )
        {
            return MinimumWordScore;
        }

        return MinimumSubstringScore;
    }
}
