namespace CommandDeck.Specs;

public static partial class Program
{
    // Every window search resolves icons, and every search with file rows
    // resolves Open With handlers. Both read only the applications through the
    // partial applications index, so neither grows with the file rows a large
    // index holds, and both still see an application under any source, such as
    // one that migration 4 moved to the status source before its rebuild.
    private static async Task ApplicationLookupsUseTheApplicationsIndexAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-application-lookups-{Guid.NewGuid( ):N}.db" );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database },
                mimeTypeResolver: new RecordingMimeTypeResolver( "text/plain" ) );

            await catalog.InitializeAsync( CancellationToken.None );
            await using var connection = await catalog.OpenAsync( CancellationToken.None );

            await using ( var seed = connection.CreateCommand( ) )
            {
                seed.CommandText = """
                                   WITH RECURSIVE numbers(n) AS (
                                       SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 1999
                                   )
                                   INSERT INTO catalog_items(stable_id, kind, title, provider_id, source_id)
                                   SELECT 'file:' || n, 'file', 'Notes ' || n, 'catalog', 'files' FROM numbers;

                                   INSERT INTO catalog_items(stable_id, kind, title, icon, startup_wm_class, payload_json, provider_id, source_id)
                                   VALUES ('app:zen.desktop', 'application', 'Zen', 'zen-browser', 'zen-alpha', $zen, 'applications', 'applications'),
                                          ('app:legacy.desktop', 'application', 'Legacy', 'legacy-icon', 'legacy-class', $legacy, 'applications', 'status');
                                   """;

                seed.Parameters.AddWithValue( "$zen", Handler( "zen.desktop" ) );
                seed.Parameters.AddWithValue( "$legacy", Handler( "legacy.desktop" ) );
                await seed.ExecuteNonQueryAsync( );
            }

            await using ( var icons = SqliteCatalog.CreateApplicationIconsCommand( connection, ["zen-alpha", "Unknown"] ) )
            {
                ReadsOnlyApplications( await ExplainQueryPlanAsync( icons ) );
            }

            await using ( var handlers = SqliteCatalog.CreateFileHandlersCommand( connection ) )
            {
                ReadsOnlyApplications( await ExplainQueryPlanAsync( handlers ) );
            }

            var resolved = await catalog.ResolveApplicationIconsAsync( ["zen-alpha", "legacy-class", "Unknown"], CancellationToken.None );
            Equal( "zen-browser", resolved["zen-alpha"] );
            Equal( "legacy-icon", resolved["legacy-class"] );
            Equal( "unknown", resolved["Unknown"] );

            var openWith = await catalog.GetOpenWithActionsAsync( ["/notes/plan.txt"], 5, CancellationToken.None );
            Equal( "open-with:legacy.desktop, open-with:zen.desktop",
                string.Join( ", ", openWith["/notes/plan.txt"].Select( static action => action.Id ) ) );
        }
        finally
        {
            DeleteDatabase( database );
        }

        static string Handler( string desktopId ) =>
            PayloadWriter.Create( (ResultMetadataKeys.Application.DesktopId, desktopId), (ResultMetadataKeys.Application.Exec, "handler %f"),
                (ResultMetadataKeys.Application.MimeTypes, "text/plain") );

        static void ReadsOnlyApplications( string[] plan )
        {
            var detail = string.Join( " | ", plan );

            True( plan.Any( static step => step.Contains( "USING INDEX ix_catalog_items_applications", StringComparison.Ordinal ) ),
                $"the lookup reads the applications index: {detail}" );

            True( !plan.Any( static step => step is "SCAN i" or "SCAN catalog_items" ||
                                            step.StartsWith( "SCAN i ", StringComparison.Ordinal ) ||
                                            step.StartsWith( "SCAN catalog_items ", StringComparison.Ordinal ) ),
                $"the lookup never scans the catalog: {detail}" );
        }
    }
}
