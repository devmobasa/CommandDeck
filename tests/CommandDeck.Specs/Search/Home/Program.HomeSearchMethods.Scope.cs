namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task HomeSearchScopePersistsAndFiltersCandidatesAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-home-search-{Guid.NewGuid( ):N}.db" );
        var home = PathExpansion.Expand( "~" );
        True( CanonicalPathBoundary.TryCreate( home, out var boundary ) );
        home = boundary!.CanonicalRoot;

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.ReplaceAllAsync( HomeSearchItems( home ), null, CancellationToken.None );
            var defaults = await catalog.GetPreferencesAsync( CancellationToken.None );
            True( !defaults.SearchHomeOnly );

            var outside = await catalog.SearchAsync( QueryParser.Parse( "file: title:needle-a" ), 20, CancellationToken.None );

            Equal( 20, outside.Count );

            foreach ( var result in outside )
            {
                await catalog.SetFavoriteAsync( result, true, CancellationToken.None );
            }

            await catalog.SetAliasAsync( outside[0], "outside-note", CancellationToken.None );

            var inside = (await catalog.SearchAsync( QueryParser.Parse( "file: title:needle-z" ), 1, CancellationToken.None )).Single( );

            await catalog.SetFavoriteAsync( inside, true, CancellationToken.None );

            var enabled = await catalog.ReplacePreferencesAsync(
                new LauncherPreferences { Sources = defaults.Sources, SearchHomeOnly = true, ReduceMotion = true }, CancellationToken.None );

            True( enabled is { SearchHomeOnly: true, ReduceMotion: true } );
            await using SqliteCatalog reopened = new( new StorageOptions { DatabasePath = database } );
            True( (await reopened.GetPreferencesAsync( CancellationToken.None )).SearchHomeOnly );

            // Outside rows would fill both the ordinary LIMIT and the 512-row
            // fuzzy candidate budget if filtering happened after retrieval.
            foreach ( var query in new[] { "file: needle", "file: nedle", "file:" } )
            {
                var results = await reopened.SearchAsync( QueryParser.Parse( query ), 1, CancellationToken.None );
                Equal( inside.Id, results.Single( ).Id );
            }

            Equal( 0, (await reopened.SearchAsync( QueryParser.Parse( "file: sibling" ), 10, CancellationToken.None )).Count );
            Equal( "home-config", (await reopened.SearchAsync( QueryParser.Parse( "cfg: setting" ), 10, CancellationToken.None )).Single( ).Id );
            Equal( "system-app", (await reopened.SearchAsync( QueryParser.Parse( "app: scopeapp" ), 10, CancellationToken.None )).Single( ).Id );

            True( await reopened.FindAliasAsync( "outside-note", CancellationToken.None ) is null );

            Equal( inside.Id, (await reopened.GetHomeResultsAsync( enabled, 1, CancellationToken.None )).Single( ).Id );

            await reopened.ReplacePreferencesAsync( defaults, CancellationToken.None );
            True( await reopened.FindAliasAsync( "outside-note", CancellationToken.None ) is not null );

            Equal( 20, (await reopened.SearchAsync( QueryParser.Parse( "file: needle" ), 20, CancellationToken.None )).Count );

            True( !(await reopened.GetPreferencesAsync( CancellationToken.None )).SearchHomeOnly );
            await reopened.ReplacePreferencesAsync( enabled, CancellationToken.None );
            await reopened.SetFavoriteAsync( inside, false, CancellationToken.None );

            await reopened.UsageHistory.RecordInvocationAsync( inside.Id, "open", null, CancellationToken.None );

            foreach ( var result in outside )
            {
                await reopened.SetFavoriteAsync( result, false, CancellationToken.None );

                await reopened.UsageHistory.RecordInvocationAsync( result.Id, "open", null, CancellationToken.None );
            }

            Equal( inside.Id, (await reopened.GetHomeResultsAsync( enabled, 1, CancellationToken.None )).Single( ).Id );

            await reopened.ResetAsync( CancellationToken.None );
            True( !(await reopened.GetPreferencesAsync( CancellationToken.None )).SearchHomeOnly );
        }
        finally
        {
            DeleteDatabase( database );
        }
    }

    private static async IAsyncEnumerable<IndexedItem> HomeSearchItems( string home )
    {
        await Task.Yield( );

        for ( var index = 0; index < 520; index++ )
        {
            yield return CreateIndexedItem( index );
        }

        yield return CreateNeedleZTxtItem( home );

        yield return CreateSiblingTxtItem( home );

        yield return CreateHomeConfigurationItem( home );

        yield return CreateSystemConfigurationItem( );

        yield return CreateScopeappItem( );

        static IndexedItem CreateIndexedItem( int index )
        {
            return new IndexedItem
            {
                StableId = $"outside-{index}",
                Kind = ResultKinds.File,
                Title = $"needle-a{index:D3}.txt",
                Path = $"/outside/needle-a{index:D3}.txt",
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Files
            };
        }

        static IndexedItem CreateNeedleZTxtItem( string home )
        {
            return new IndexedItem
            {
                StableId = "inside",
                Kind = ResultKinds.File,
                Title = "needle-z.txt",
                Path = Path.Combine( home, "Documents", "nested", "needle-z.txt" ),
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Files
            };
        }

        static IndexedItem CreateSiblingTxtItem( string home )
        {
            return new IndexedItem
            {
                StableId = "sibling",
                Kind = ResultKinds.File,
                Title = "sibling.txt",
                Path = home + "-backup/sibling.txt",
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Files
            };
        }

        static IndexedItem CreateHomeConfigurationItem( string home )
        {
            return new IndexedItem
            {
                StableId = "home-config",
                Kind = ResultKinds.Configuration,
                Title = "setting",
                Path = Path.Combine( home, ".config", "scope.conf" ),
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Configuration
            };
        }

        static IndexedItem CreateSystemConfigurationItem( )
        {
            return new IndexedItem
            {
                StableId = "system-config",
                Kind = ResultKinds.Configuration,
                Title = "setting",
                Path = "/etc/scope.conf",
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Configuration
            };
        }

        static IndexedItem CreateScopeappItem( )
        {
            return new IndexedItem
            {
                StableId = "system-app",
                Kind = ResultKinds.Application,
                Title = "scopeapp",
                Path = "/usr/share/applications/scopeapp.desktop",
                ProviderId = ProviderIds.Applications,
                SourceId = SourceIds.Applications
            };
        }
    }
}
