namespace CommandDeck.Specs;

public static partial class Program
{
    private static void AuditCliPositionals( )
    {
        Equal( "calculator", new CliArguments( ["search", "--json", "calculator"] ).JoinPositionalsAfterCommand( ) );
        Equal( "calculator", new CliArguments( ["search", "calculator", "--json"] ).JoinPositionalsAfterCommand( ) );
        Equal( "calculator", new CliArguments( ["search", "--limit", "5", "calculator"] ).JoinPositionalsAfterCommand( ) );
        Equal( "calculator", new CliArguments( ["search", "--limit=5", "calculator"] ).JoinPositionalsAfterCommand( ) );
        True( !new CliArguments( ["search", "--", "--json"] ).HasFlag( "--json" ) );
        Equal<string?>( null, new CliArguments( ["search", "--", "--query", "literal"] ).GetOption( "--query" ) );
        Equal( "calculator", new CliArguments( ["search", "--timeout", "5", "calculator"] ).JoinPositionalsAfterCommand( ) );
        Equal( "--json -5 --limit 2", new CliArguments( ["search", "--", "--json", "-5", "--limit", "2"] ).JoinPositionalsAfterCommand( ) );

        // Values read through helpers, and options with no entry in the flag list, keep their values.
        Equal( "set", new CliArguments( ["agent", "set", "--enabled", "true", "--hidden-models", "false"] ).JoinPositionalsAfterCommand( ) );
        Equal( "calculator", new CliArguments( ["search", "--some-later-option", "x", "calculator"] ).JoinPositionalsAfterCommand( ) );
        Equal( "calculator", new CliArguments( ["search", "--quiet", "--verbose", "calculator"] ).JoinPositionalsAfterCommand( ) );
    }
}
