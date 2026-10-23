using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const int DefaultSuggestionResults = 8;

    private static async Task<int> RunSuggestAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var query = arguments.GetOption( CliOptionNames.Query ) ?? arguments.JoinPositionalsAfterCommand( );

        var limit = ParseInteger( arguments.GetOption( CliOptionNames.Limit ), DefaultSuggestionResults, 1, AppSearchLimits.MaximumSuggestions );

        var snapshot = await runtime
            .SuggestAsync( new SearchRequest { Query = query, Limit = limit }, cancellationToken )
            .ConfigureAwait( false );

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( snapshot, CommandDeckJsonContext.Default.SearchSuggestionSnapshot ) );

            return 0;
        }

        foreach ( var suggestion in snapshot.Suggestions )
        {
            Console.WriteLine( $"[{suggestion.Source}] " + PreferencesTextFormatter.EscapeTerminalText( suggestion.Text ) );
        }

        return 0;
    }
}
