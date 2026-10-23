using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const string SaveCommand = "save";
    private const string RenameCommand = "rename";
    private const string RunCommand = "run";
    private const string DeleteCommand = "delete";

    private static async Task<int> RunSavedSearchAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var values = arguments.PositionalsAfterCommand( );
        var action = ReadCliAction( values, 0, ListCommand );

        var searches = await runtime
            .Preferences.GetSavedSearchesAsync( cancellationToken )
            .ConfigureAwait( false );

        if ( action == ListCommand )
        {
            WriteSavedSearches( searches, arguments.HasFlag( CliOptionNames.Json ) );

            return 0;
        }

        if ( action == SaveCommand && values.Count >= 2 )
        {
            return await SaveSavedSearchFromCliAsync( runtime, arguments, values[1], cancellationToken )
                .ConfigureAwait( false );
        }

        if ( action == RenameCommand && values.Count == 3 )
        {
            return await RenameSavedSearchFromCliAsync( runtime, arguments, FindSavedSearch( searches, values[1] ), values[2], cancellationToken )
                .ConfigureAwait( false );
        }

        if ( action == RunCommand && values.Count == 2 )
        {
            return await RunSavedSearchQueryAsync( runtime, arguments, FindSavedSearch( searches, values[1] ), cancellationToken )
                .ConfigureAwait( false );
        }

        if ( action == DeleteCommand && values.Count == 2 )
        {
            return await DeleteSavedSearchFromCliAsync( runtime, arguments, values[1], cancellationToken )
                .ConfigureAwait( false );
        }

        Console.Error.WriteLine( "Use saved-search list, save NAME --query QUERY, rename ID NAME, run ID, or delete ID --yes." );

        return 2;
    }

    private static async Task<int> SaveSavedSearchFromCliAsync( CommandDeckRuntime runtime, CliArguments arguments, string name,
        CancellationToken cancellationToken )
    {
        var query = arguments.GetOption( CliOptionNames.Query );
        if ( query is null )
        {
            throw new InvalidOperationException( "saved-search save requires --query." );
        }

        var aliases = await runtime
            .Preferences.GetFilterAliasesAsync( cancellationToken )
            .ConfigureAwait( false );

        var aliasMap = aliases.ToDictionary( static value => value.Alias, static value => value.FilterId, StringComparer.OrdinalIgnoreCase );
        var parsed = QueryParser.Parse( query, aliases: aliasMap );

        var result = await runtime
            .SaveSavedSearchAsync( new SavedSearch
            {
                Id = arguments.GetOption( CliOptionNames.Id ) ?? string.Empty,
                Name = name,
                Expression = parsed.Expression,
                Shortcut = arguments.GetOption( CliOptionNames.Shortcut ),
                ShowOnHome = arguments.HasFlag( CliOptionNames.Home )
            }, cancellationToken )
            .ConfigureAwait( false );

        WriteSavedSearches( result.Searches, arguments.HasFlag( CliOptionNames.Json ) );

        return 0;
    }

    private static async Task<int> RenameSavedSearchFromCliAsync( CommandDeckRuntime runtime, CliArguments arguments, SavedSearch current, string name,
        CancellationToken cancellationToken )
    {
        var result = await runtime
            .SaveSavedSearchAsync( new SavedSearch
            {
                Id = current.Id,
                Name = name,
                Expression = current.Expression,
                Shortcut = current.Shortcut,
                ShowOnHome = current.ShowOnHome,
                Position = current.Position
            }, cancellationToken )
            .ConfigureAwait( false );

        WriteSavedSearches( result.Searches, arguments.HasFlag( CliOptionNames.Json ) );

        return 0;
    }

    private static async Task<int> RunSavedSearchQueryAsync( CommandDeckRuntime runtime, CliArguments arguments, SavedSearch current,
        CancellationToken cancellationToken )
    {
        var results = await runtime
            .SearchAsync(
                new SearchRequest
                {
                    Expression = current.Expression,
                    Limit = ParseInteger( arguments.GetOption( CliOptionNames.Limit ), DefaultSearchResults, 1, MaximumSearchResults )
                }, cancellationToken )
            .ConfigureAwait( false );

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( results.ToList( ), CommandDeckJsonContext.Default.ListSearchResult ) );
        }
        else
        {
            if ( results.Count == 0 )
            {
                Console.WriteLine( "No results." );
            }

            for ( var index = 0; index < results.Count; index++ )
            {
                var result = results[index];
                Console.WriteLine( $"{index + 1,2}. [{result.Kind}] {result.Title}" );
            }
        }

        return 0;
    }

    private static async Task<int> DeleteSavedSearchFromCliAsync( CommandDeckRuntime runtime, CliArguments arguments, string id,
        CancellationToken cancellationToken )
    {
        if ( !arguments.HasFlag( CliOptionNames.Yes ) )
        {
            throw new InvalidOperationException( "saved-search delete requires --yes." );
        }

        var result = await runtime
            .DeleteSavedSearchAsync( id, cancellationToken )
            .ConfigureAwait( false );

        WriteSavedSearches( result.Searches, arguments.HasFlag( CliOptionNames.Json ) );

        return 0;
    }

    private static SavedSearch FindSavedSearch( IReadOnlyList<SavedSearch> searches, string id )
    {
        var result = searches.FirstOrDefault( value => string.Equals( value.Id, id, StringComparison.Ordinal ) );
        if ( result is null )
        {
            throw new InvalidOperationException( $"Saved search '{id}' was not found." );
        }

        return result;
    }

    private static void WriteSavedSearches( IReadOnlyList<SavedSearch> searches, bool json )
    {
        if ( json )
        {
            Console.WriteLine( JsonSerializer.Serialize( searches.ToList( ), CommandDeckJsonContext.Default.ListSavedSearch ) );

            return;
        }

        if ( searches.Count == 0 )
        {
            Console.WriteLine( "No saved searches." );

            return;
        }

        foreach ( var search in searches )
        {
            var shortcut = search.Shortcut is null ? string.Empty : $" [{search.Shortcut}]";

            Console.WriteLine( $"{search.Id}  {PreferencesTextFormatter.EscapeTerminalText( search.Name )}{shortcut}" );
        }
    }
}
