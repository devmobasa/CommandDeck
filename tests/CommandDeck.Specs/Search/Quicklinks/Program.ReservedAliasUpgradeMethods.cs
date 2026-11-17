using Microsoft.Data.Sqlite;

namespace CommandDeck.Specs;

public static partial class Program
{
    // A later release reserves the names of the filters it adds: "agents" in
    // preview.2, "theme" and "themes" after it. Aliases saved before then keep
    // working; a new or changed alias may not use any reserved name.
    private static void SavedAliasesKeepNamesReservedAfterThem( )
    {
        Equal( "theme", AliasValidator.NormalizeAndValidate( "theme", saved: true ) );
        Equal( "agents", AliasValidator.NormalizeAndValidate( "agents", saved: true ) );
        Equal( "themes", FilterAliasValidator.NormalizeAndValidate( "themes:", saved: true ) );
        Throws<InvalidOperationException>( ( ) => AliasValidator.NormalizeAndValidate( "theme" ) );
        Throws<InvalidOperationException>( ( ) => FilterAliasValidator.NormalizeAndValidate( "themes" ) );

        // A name reserved since the first release was never a valid alias, so it stays an error.
        Throws<InvalidOperationException>( ( ) => AliasValidator.NormalizeAndValidate( "app", saved: true ) );
        Throws<InvalidOperationException>( ( ) => FilterAliasValidator.NormalizeAndValidate( "home", saved: true ) );

        // A configuration file written before "theme" was reserved still loads.
        ConfigurationValidator.Validate( new CommandDeckOptions
        {
            Quicklinks = [TestQuicklink( "themes-site", alias: "theme" )],
            Search = new SearchOptions { WebEngines = [SpecWebEngine( "themes" )] }
        } );
        Throws<InvalidOperationException>( ( ) => ConfigurationValidator.Validate( new CommandDeckOptions
        {
            Search = new SearchOptions { WebEngines = [SpecWebEngine( "app" )] }
        } ) );
    }

    private static async Task QuicklinksAndFilterAliasesSavedBeforeANameWasReservedKeepWorkingAsync( )
    {
        var root = CreateCliSpecRoot( );
        var token = CancellationToken.None;
        var database = Path.Combine( root, "links.db" );

        try
        {
            await using SqliteCatalog store = new( new StorageOptions { DatabasePath = database } );
            await store.ConfigureQuicklinksAsync( [], token );

            // Rows an earlier release saved, before "theme" and "themes" were reserved.
            await using ( SqliteConnection connection = new( $"Data Source={database};Pooling=False" ) )
            {
                await connection.OpenAsync( token );
                await using var command = connection.CreateCommand( );
                command.CommandText = """
                                      INSERT INTO user_quicklinks(id, title, url_template, alias, icon, created_unix_ms)
                                      VALUES('themes-site', 'Themes', 'https://example.test/?q={query}', 'theme', NULL, 1);
                                      INSERT INTO filter_aliases(alias, filter_id, created_unix_ms, updated_unix_ms)
                                      VALUES('themes', $filter, 1, 1);
                                      """;
                command.Parameters.AddWithValue( "$filter", SearchFilterIds.Applications );
                await command.ExecuteNonQueryAsync( token );
            }

            // The saved quicklink still loads and can be edited with its alias.
            Equal( "theme", (await store.GetQuicklinksAsync( token )).Quicklinks.Single( entry => entry.Quicklink.Id == "themes-site" ).Quicklink.Alias );
            await store.ChangeQuicklinkAsync( "save", TestQuicklink( "themes-site", "Theme gallery", alias: "theme" ), null, token );
            // Other quicklinks still save while it exists.
            await store.ChangeQuicklinkAsync( "save", TestQuicklink( ), null, token );
            Equal( 2, (await store.GetQuicklinksAsync( token )).Quicklinks.Count );

            // A new or changed alias may not take a reserved name.
            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await store.ChangeQuicklinkAsync( "save", TestQuicklink( alias: "themes" ), null, token ) );
            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await store.ChangeQuicklinkAsync( "save", TestQuicklink( "other", alias: "agents" ), null, token ) );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await store.SetFilterAliasAsync( "theme", SearchFilterIds.Applications, token ) );

            // The built-in filter wins over the saved filter alias, which can still be removed.
            Equal( SearchFilterIds.Themes, QueryParser
                .Parse( "themes: tokyo", null, new Dictionary<string, string> { ["themes"] = SearchFilterIds.Applications } )
                .IncludedFilters.Single( )
                .Id );

            await store.SetFilterAliasAsync( "themes", null, token );
            True( (await store.GetFilterAliasesAsync( token )).All( static alias => alias.Alias != "themes" ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static WebEngineOptions SpecWebEngine( string alias ) => new( )
    {
        Id = "engine-" + alias,
        Name = "Engine",
        UrlTemplate = "https://example.test/?q=" + UrlTemplateTokens.Query,
        Alias = alias
    };
}
