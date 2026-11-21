namespace CommandDeck.Specs;

public static partial class Program
{
    // A search term matches what a row says, never the source it came from.
    // The source_id column is indexed for source filters only; matching terms
    // against it made "f" match every file and "a" every application, which
    // ranked each of them and flattened the term's weight.
    private static async Task SearchTermsNeverMatchTheSourceIdAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-fts-columns-{Guid.NewGuid( ):N}.db" );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( default );

            IndexedItem[] items =
            [
                new( )
                {
                    StableId = "file:notes", Kind = ResultKinds.File, Title = "notes.txt", Path = "/data/notes.txt", Body = "budget",
                    ProviderId = ProviderIds.Files, SourceId = SourceIds.Files
                },
                new( )
                {
                    StableId = "file:fonts", Kind = ResultKinds.File, Title = "fonts.conf", Path = "/data/fonts.conf", Body = "budget files",
                    ProviderId = ProviderIds.Files, SourceId = SourceIds.Files
                },
                new( )
                {
                    StableId = "app:zed.desktop", Kind = ResultKinds.Application, Title = "Zed",
                    ProviderId = ProviderIds.Applications, SourceId = SourceIds.Applications
                },
                new( )
                {
                    StableId = "app:alacritty.desktop", Kind = ResultKinds.Application, Title = "Alacritty",
                    ProviderId = ProviderIds.Applications, SourceId = SourceIds.Applications
                }
            ];

            await catalog.ReplaceAllAsync( AsAsync( items ), null, default );

            async Task<string> IdsAsync( string query )
            {
                var results = await catalog.SearchAsync( QueryParser.Parse( query ), 50, default );

                return string.Join( ", ", results.Select( static result => result.Id ).Order( StringComparer.Ordinal ) );
            }

            Equal( "file:fonts", await IdsAsync( "f" ) );
            Equal( "file:fonts", await IdsAsync( "files" ) );
            Equal( "app:alacritty.desktop", await IdsAsync( "a" ) );
            Equal( "", await IdsAsync( "applications" ) );

            // Excluding a term excludes rows that say it, not rows from a source
            // named like it. Neither title starts with "b", so the fuzzy title
            // fallback cannot bring back a row the full-text match dropped.
            Equal( "file:notes", await IdsAsync( "budget -files" ) );
        }
        finally
        {
            DeleteDatabase( database );
        }
    }
}
