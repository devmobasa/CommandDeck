namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditQuotedPhraseAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-phrase-" );
        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root.FullName, "catalog.db" ) } );
            await catalog.InitializeAsync( default );
            await catalog.ReplaceAllAsync( AsAsync( [
                PhraseItem( "app:studio", "Visual Studio Code" ),
                PhraseItem( "app:phrase", "Visual Code" ),
                PhraseItem( "app:firefox", "Firefox" )
            ] ), null, default );
            var coordinator = CatalogCoordinator( catalog, catalog, TimeProvider.System );
            var quoted = await coordinator.SearchAsync( new SearchRequest { Query = "\"visual code\"" }, default );
            True( quoted.All( static result => result.Id != "app:studio" ) );
            True( quoted.Any( static result => result.Id == "app:phrase" ) );
            var words = await coordinator.SearchAsync( new SearchRequest { Query = "visual code" }, default );
            True( words.Any( static result => result.Id == "app:studio" ) );
            var typo = await coordinator.SearchAsync( new SearchRequest { Query = "friefox" }, default );
            True( typo.Any( static result => result.Id == "app:firefox" ) );
        }
        finally
        {
            root.Delete( recursive: true );
        }
    }

    private static IndexedItem PhraseItem( string id, string title ) => new( )
    {
        StableId = id,
        Kind = "application",
        Title = title,
        ProviderId = "catalog",
        SourceId = "applications"
    };

}
