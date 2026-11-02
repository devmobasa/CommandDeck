namespace CommandDeck.Specs;

public static partial class Program
{
    private static void AuditHighlightGraphemes( )
    {
        foreach ( var (query, text, length) in new[] { ("👍", "👍🏽", 4), ("👩", "👩‍💻", 5), ("e", "e\u0301", 2) } )
        {
            Equal( new MatchedRange( 0, length ), FuzzyMatcher.FindRanges( query, text ).Single( ) );
            Equal( new MatchedRange( 2, length ), FuzzyMatcher.FindRanges( query, "x " + text + " suffix" ).Single( ) );
        }
    }
}
