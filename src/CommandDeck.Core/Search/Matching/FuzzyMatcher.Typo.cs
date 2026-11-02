using System.Numerics;

namespace CommandDeck;

public static partial class FuzzyMatcher
{
    private const int LongTypoQueryCharacters = 8;
    private const double MinimumTypoScore = 0.53;
    private const double MaximumTypoScore = 0.59;
    private const int MinimumTypoQueryCharacters = 4;
    private const int MaximumTypoQueryCharacters = 48;
    private const double TypoDistancePenalty = 0.04;
    private const double TypoBaseScore = 0.63;
    private const double TypoLengthPenalty = 0.01;
    private const double TypoPartialWordPenalty = 0.015;
    private const int LetterMaskBits = 64;

    // A typo match starts at a word start and covers the whole query, but it
    // may stop inside a word, so a typo is found while the word is still being
    // typed, and it may run across words. Only letters are edited: digits,
    // spaces and punctuation must match as typed, so "python3" never matches
    // "python2".
    private static bool TryMatchTypo( string query, string candidate, bool includeRanges, out MatchEvaluation match )
    {
        match = default;

        if ( CannotMatchTypo( query ) )
        {
            return false;
        }

        var budget = CountLettersOrDigits( query ) >= LongTypoQueryCharacters ? 2 : 1;
        var queryLetters = GetLetterMask( query );
        if ( CannotCoverLetters( queryLetters, candidate, budget ) )
        {
            return false;
        }

        Span<int> rows = stackalloc int[TypoLayer.RowCount * 2 * (query.Length + budget + 1)];
        var best = new TypoAlignment( 0, -1, 0 );
        for ( var start = 0; start < candidate.Length; start++ )
        {
            var width = Math.Min( candidate.Length - start, query.Length + budget );
            if ( IsWordStart( candidate, start ) && width + budget >= query.Length &&
                 !CannotCoverLetters( queryLetters, candidate.AsSpan( start, width ), budget ) )
            {
                best = AlignTypo( query, candidate, start, width, budget, rows, best );
            }
        }

        if ( best.Start < 0 )
        {
            return false;
        }

        match = new MatchEvaluation( Math.Clamp( best.Score, MinimumTypoScore, MaximumTypoScore ),
            includeRanges ? [new MatchedRange( best.Start, best.Length )] : null );

        return true;
    }

    // Optimal string alignment from one word start. The dropped layer holds
    // alignments that skipped a query letter. Only a word end accepts them;
    // anywhere else every query would match the prefixes one letter shorter.
    private static TypoAlignment AlignTypo( string query, string candidate, int start, int width, int budget, Span<int> rows, TypoAlignment best )
    {
        var unreachable = budget + 1;
        var kept = new TypoLayer( rows[..(rows.Length / 2)], width + 1, unreachable );
        var dropped = new TypoLayer( rows[(rows.Length / 2)..], width + 1, unreachable );

        // Candidate letters before the first aligned one are skipped edits.
        kept.Previous[0] = 0;
        for ( var column = 1; column <= Math.Min( width, budget ) && char.IsLetter( candidate[start + column - 1] ); column++ )
        {
            kept.Previous[column] = column;
        }

        for ( var row = 1; row <= query.Length; row++ )
        {
            if ( AlignTypoRow( query, candidate, start, row, budget, ref kept, ref dropped ) > budget )
            {
                return best;
            }
        }

        for ( var column = Math.Max( 0, query.Length - budget ); column <= width; column++ )
        {
            var wordEnd = IsWordEnd( candidate, start + column );
            var distance = wordEnd ? Math.Min( kept.Previous[column], dropped.Previous[column] ) : kept.Previous[column];
            var score = GetTypoScore( query.Length, distance, column, wordEnd );
            if ( distance <= budget && score > best.Score )
            {
                best = new TypoAlignment( score, start, column );
            }
        }

        return best;
    }

    // Fills one query row of both layers and returns its smallest distance.
    private static int AlignTypoRow( string query, string candidate, int start, int row, int budget, ref TypoLayer kept, ref TypoLayer dropped )
    {
        var unreachable = budget + 1;
        var width = kept.Current.Length - 1;
        var queryCharacter = query[row - 1];
        var queryLetter = char.IsLetter( queryCharacter );
        kept.Current.Fill( unreachable );
        dropped.Current.Fill( unreachable );
        if ( queryLetter )
        {
            dropped.Current[0] = Math.Min( unreachable, Math.Min( kept.Previous[0], dropped.Previous[0] ) + 1 );
        }

        var smallest = dropped.Current[0];
        for ( var column = Math.Max( 1, row - budget ); column <= Math.Min( width, row + budget ); column++ )
        {
            var candidateCharacter = candidate[start + column - 1];
            var candidateLetter = char.IsLetter( candidateCharacter );
            var replace = ReplacementDistance( queryCharacter, candidateCharacter, queryLetter && candidateLetter, unreachable );
            var transposed = row > 1 && column > 1 && queryLetter && candidateLetter &&
                             SameCharacter( queryCharacter, candidate[start + column - 2] ) &&
                             SameCharacter( query[row - 2], candidateCharacter );
            var skipped = queryLetter ? Math.Min( kept.Previous[column], dropped.Previous[column] ) + 1 : unreachable;

            kept.Current[column] = kept.Step( column, replace, candidateLetter, transposed, unreachable );
            dropped.Current[column] = Math.Min( dropped.Step( column, replace, candidateLetter, transposed, unreachable ), skipped );
            smallest = Math.Min( smallest, Math.Min( kept.Current[column], dropped.Current[column] ) );
        }

        kept.Rotate( );
        dropped.Rotate( );

        return smallest;
    }

    private static int ReplacementDistance( char query, char candidate, bool bothLetters, int unreachable )
    {
        if ( SameCharacter( query, candidate ) )
        {
            return 0;
        }

        return bothLetters ? 1 : unreachable;
    }

    private static bool CannotMatchTypo( string query )
    {
        return query.Length > MaximumTypoQueryCharacters || !char.IsLetterOrDigit( query[0] ) ||
               CountLettersOrDigits( query ) < MinimumTypoQueryCharacters;
    }

    private static int CountLettersOrDigits( string value )
    {
        return value.Count( static character => char.IsLetterOrDigit( character ) );
    }

    // Each query letter the candidate lacks costs at least one edit.
    private static bool CannotCoverLetters( ulong queryLetters, ReadOnlySpan<char> candidate, int budget )
    {
        return BitOperations.PopCount( queryLetters & ~GetLetterMask( candidate ) ) > budget;
    }

    private static ulong GetLetterMask( ReadOnlySpan<char> value )
    {
        ulong mask = 0;
        foreach ( var character in value )
        {
            if ( char.IsLetter( character ) )
            {
                mask |= 1UL << (char.ToUpperInvariant( character ) % LetterMaskBits);
            }
        }

        return mask;
    }

    private static bool IsWordEnd( string value, int index )
    {
        return index >= value.Length || !char.IsLetter( value[index] ) || IsWordStart( value, index );
    }

    private static bool SameCharacter( char left, char right )
    {
        return char.ToUpperInvariant( left ) == char.ToUpperInvariant( right );
    }

    // A match that stops inside a word scores just below one that ends at a word end.
    private static double GetTypoScore( int queryLength, int distance, int length, bool wordEnd )
    {
        var score = TypoBaseScore - (distance * TypoDistancePenalty) - (Math.Abs( length - queryLength ) * TypoLengthPenalty);

        return wordEnd ? score : score - TypoPartialWordPenalty;
    }

    private readonly record struct TypoAlignment( double Score, int Start, int Length );

    // One layer's distance rows: the current row, the previous one, and the
    // row before it, which transpositions read.
    private ref struct TypoLayer
    {
        public const int RowCount = 3;

        public Span<int> Older;
        public Span<int> Previous;
        public Span<int> Current;

        public TypoLayer( Span<int> rows, int columns, int unreachable )
        {
            var stride = rows.Length / RowCount;
            Older = rows.Slice( 0, columns );
            Previous = rows.Slice( stride, columns );
            Current = rows.Slice( stride * 2, columns );
            Older.Fill( unreachable );
            Previous.Fill( unreachable );
        }

        public readonly int Step( int column, int replace, bool candidateLetter, bool transposed, int unreachable )
        {
            var distance = Previous[column - 1] + replace;
            if ( candidateLetter )
            {
                distance = Math.Min( distance, Current[column - 1] + 1 );
            }

            if ( transposed )
            {
                distance = Math.Min( distance, Older[column - 2] + 1 );
            }

            return Math.Min( distance, unreachable );
        }

        public void Rotate( )
        {
            var older = Older;
            Older = Previous;
            Previous = Current;
            Current = older;
        }
    }
}
