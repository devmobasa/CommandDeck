namespace CommandDeck;

public static partial class FuzzyMatcher
{
    private const int LongTypoQueryCharacters = 8;
    private const double MinimumTypoScore = 0.53;
    private const double MaximumTypoScore = 0.59;
    private const int MinimumTypoQueryCharacters = 4;
    private const double TypoDistancePenalty = 0.04;
    private const double TypoBaseScore = 0.63;
    private const double TypoLengthPenalty = 0.01;

    private static bool TryMatchTypo( string query, string candidate, bool includeRanges, out MatchEvaluation match )
    {
        match = default;

        if ( CannotMatchTypo( query ) )
        {
            return false;
        }

        var maximumDistance = query.Length >= LongTypoQueryCharacters ? 2 : 1;
        var bestDistance = maximumDistance + 1;
        var bestStart = -1;
        var bestLength = 0;
        var spanStart = -1;
        for ( var index = 0; index <= candidate.Length; index++ )
        {
            var atEnd = index == candidate.Length;
            var startsNewWord = !atEnd && IsWordStart( candidate, index ) && spanStart >= 0;
            var isWordCharacter = !atEnd && char.IsLetterOrDigit( candidate[index] );
            if ( spanStart < 0 && isWordCharacter )
            {
                spanStart = index;

                continue;
            }

            if ( spanStart < 0 || (isWordCharacter && !startsNewWord) )
            {
                continue;
            }

            var spanLength = index - spanStart;
            if ( Math.Abs( spanLength - query.Length ) <= maximumDistance )
            {
                var distance = BoundedDamerauLevenshtein( query, candidate.AsSpan( spanStart, spanLength ), maximumDistance );
                if ( distance < bestDistance )
                {
                    bestDistance = distance;
                    bestStart = spanStart;
                    bestLength = spanLength;
                }
            }

            spanStart = isWordCharacter ? index : -1;
        }

        if ( bestStart < 0 || bestDistance > maximumDistance )
        {
            return false;
        }

        var score = GetTypoScore( query, bestDistance, bestLength );

        match = new MatchEvaluation( Math.Clamp( score, MinimumTypoScore, MaximumTypoScore ),
            includeRanges ? [new MatchedRange( bestStart, bestLength )] : null );

        return true;
    }

    private static int BoundedDamerauLevenshtein( ReadOnlySpan<char> left, ReadOnlySpan<char> right, int maximumDistance )
    {
        if ( Math.Abs( left.Length - right.Length ) > maximumDistance )
        {
            return maximumDistance + 1;
        }

        Span<int> previousPrevious = stackalloc int[right.Length + 1];
        Span<int> previous = stackalloc int[right.Length + 1];
        Span<int> current = stackalloc int[right.Length + 1];

        for ( var rightIndex = 0; rightIndex <= right.Length; rightIndex++ )
        {
            previous[rightIndex] = rightIndex;
        }

        for ( var leftIndex = 1; leftIndex <= left.Length; leftIndex++ )
        {
            current[0] = leftIndex;

            for ( var rightIndex = 1; rightIndex <= right.Length; rightIndex++ )
            {
                var substitutionCost = GetSubstitutionCost( left, right, leftIndex, rightIndex );
                var distance = GetEditDistance( previous, current, rightIndex, substitutionCost );
                if ( IsTransposition( left, right, leftIndex, rightIndex ) )
                {
                    distance = Math.Min( distance, previousPrevious[rightIndex - 2] + 1 );
                }

                current[rightIndex] = distance;
            }

            var oldPreviousPrevious = previousPrevious;
            previousPrevious = previous;
            previous = current;
            current = oldPreviousPrevious;
        }

        var result = previous[right.Length];

        return result <= maximumDistance ? result : maximumDistance + 1;
    }

    private static bool CannotMatchTypo( string query )
    {
        return query.Length is < MinimumTypoQueryCharacters or > MaximumTypoTokenCharacters ||
               query.Any( static character => !char.IsLetterOrDigit( character ) );
    }

    private static double GetTypoScore( string query, int bestDistance, int bestLength )
    {
        return TypoBaseScore - (bestDistance * TypoDistancePenalty) - (Math.Abs( bestLength - query.Length ) * TypoLengthPenalty);
    }

    private static int GetSubstitutionCost( ReadOnlySpan<char> left, ReadOnlySpan<char> right, int leftIndex, int rightIndex )
    {
        if ( char.ToUpperInvariant( left[leftIndex - 1] ) == char.ToUpperInvariant( right[rightIndex - 1] ) )
        {
            return 0;
        }
        else
        {
            return 1;
        }
    }

    private static int GetEditDistance( ReadOnlySpan<int> previous, ReadOnlySpan<int> current, int rightIndex, int substitutionCost )
    {
        return Math.Min( previous[rightIndex] + 1, Math.Min( current[rightIndex - 1] + 1, previous[rightIndex - 1] + substitutionCost ) );
    }

    private static bool IsTransposition( ReadOnlySpan<char> left, ReadOnlySpan<char> right, int leftIndex, int rightIndex )
    {
        return leftIndex > 1 &&
               rightIndex > 1 &&
               char.ToUpperInvariant( left[leftIndex - 1] ) == char.ToUpperInvariant( right[rightIndex - 2] ) &&
               char.ToUpperInvariant( left[leftIndex - 2] ) == char.ToUpperInvariant( right[rightIndex - 1] );
    }
}
