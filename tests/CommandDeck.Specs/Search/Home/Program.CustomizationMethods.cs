namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task FileIndexSourcePreservesTraversalBehaviorAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-file-index-spec-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            await File.WriteAllTextAsync( Path.Combine( root, "notes.txt" ), "hello world" );
            await File.WriteAllTextAsync( Path.Combine( root, ".hidden.txt" ), "hidden" );

            await File.WriteAllTextAsync( Path.Combine( root, "settings.json" ),
                "{\"theme\":\"dark\",\"entries\":[{\"name\":\"outer\",\"entries\":[{\"name\":\"inner\",\"value\":\"nested\"}]}]}" );

            FileIndexSource source = new( CreateIndexingOptions( root ), new PrivacyOptions { NeverIndex = [] } );

            List<IndexedItem> items = [];
            await foreach ( var item in source.ScanAsync( progress: null, CancellationToken.None ) )
            {
                items.Add( item );
            }

            True( items.Any( item => item is { Kind: "file", Title: "notes.txt" } ) );
            True( items.Any( item => item is { Kind: "file", Title: "settings.json" } ) );
            True( items.Any( item => item is { Kind: "configuration", Title: "theme", Body: "dark" } ) );
            True( items.Any( item => item is { Kind: "configuration", Title: "entries[outer].entries[inner].value", Body: "nested" } ) );
            True( items.All( item => !item.Title.Contains( "entries[outer].entries[outer]", StringComparison.Ordinal ) ) );
            True( items.All( item => item.Title != ".hidden.txt" ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static IndexingOptions CreateIndexingOptions( string root )
        {
            return new IndexingOptions
            {
                Roots =
                [
                    new IndexRootOptions { Path = root, IncludeHidden = false, IncludeContentInSearch = true }
                ]
            };
        }
    }

    private static async Task CustomizationAndHomeSurviveRebuildAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-customization-spec-{Guid.NewGuid( ):N}.db" );

        try
        {
            SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( CancellationToken.None );

            await catalog.ReplaceAllAsync( CustomizationItems( includeFile: true ), null, CancellationToken.None );

            var application =
                (await catalog.SearchAsync( QueryParser.Parse( "Brave" ), 10, CancellationToken.None )).Single( value => value.Kind == "application" );

            var file = (await catalog.SearchAsync( QueryParser.Parse( "notes" ), 10, CancellationToken.None )).Single( value => value.Kind == "file" );

            True( application.CanCustomize && file.CanCustomize );
            var alias = await catalog.SetAliasAsync( application, "b", CancellationToken.None );
            True( alias.IsFavorite && alias.Aliases.SequenceEqual( ["b"] ) );
            await catalog.SetFavoriteAsync( file, true, CancellationToken.None );
            await catalog.ReorderFavoritesAsync( [file.Id, application.Id], CancellationToken.None );
            var exactAlias = await catalog.FindAliasAsync( "B", CancellationToken.None );
            Equal( application.Id, exactAlias?.Id );
            Equal( "B", exactAlias?.Metadata["exactAlias"] );
            var recasedAlias = await catalog.SetAliasAsync( application, "B", CancellationToken.None );
            Equal( "B", recasedAlias.Aliases[0] );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.SetAliasAsync( file, "B", CancellationToken.None ) );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.SetAliasAsync( application, "app", CancellationToken.None ) );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.SetAliasAsync( application, "home", CancellationToken.None ) );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.SetAliasAsync( application, "settings", CancellationToken.None ) );

            var defaults = LauncherPreferences.CreateDefault( );
            LauncherPreferences configured = CreateConfigured( defaults );
            var stored = await catalog.ReplacePreferencesAsync( configured, CancellationToken.None );
            True( stored.ReduceMotion );
            True( !stored.HideAfterAction );
            True( !(await catalog.GetPreferencesAsync( CancellationToken.None )).HideAfterAction );
            Equal( "arch-wiki", stored.WebFallbackQuicklinkId );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await catalog.ReplacePreferencesAsync( CreatePreferencesWithUnknownSource( defaults ), CancellationToken.None ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.ReplacePreferencesAsync( new LauncherPreferences
            {
                Sources = defaults
                    .Sources.Select( ( source, index ) => new SourcePreference
                    {
                        SourceId = index == 1 ? defaults.Sources[0].SourceId : source.SourceId,
                        EnabledInSearch = source.EnabledInSearch,
                        EnabledOnHome = source.EnabledOnHome,
                        Position = source.Position
                    } )
                    .ToList( )
            }, CancellationToken.None ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await catalog.ReplacePreferencesAsync( CreatePreferencesWithDuplicatePositions( defaults ), CancellationToken.None ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await catalog.ReplacePreferencesAsync( CreatePreferencesWithZeroPosition( defaults ), CancellationToken.None ) );

            SearchCoordinator coordinator = new( [new CatalogSearchProvider( catalog, catalog )], new SearchOptions( ), catalog, catalog );

            var disabled = await coordinator.SearchAsync( new SearchRequest { Query = "Brav", Limit = 20 }, CancellationToken.None );

            True( disabled.All( value => value.SourceId != "applications" ) );

            var explicitApplications = await coordinator.SearchAsync(
                new SearchRequest { Query = "Brav", Expression = new QueryExpression { IncludeFilterIds = [SearchFilterIds.Applications] }, Limit = 20 },
                CancellationToken.None );

            True( explicitApplications.All( value => value.Id != application.Id ) );

            var home = await coordinator.SearchAsync( new SearchRequest { Query = string.Empty, Limit = 20 }, CancellationToken.None );

            Equal( 1, home.Count );
            Equal( file.Id, home[0].Id );
            True( home.All( value => value.Section?.Id == "bookmarks" ) );
            var removedAlias = await catalog.SetAliasAsync( application, null, CancellationToken.None );
            True( removedAlias is { IsFavorite: true, Aliases.Count: 0 } );

            await catalog.ReplaceAllAsync( CustomizationItems( includeFile: false ), null, CancellationToken.None );

            var snapshot = await catalog.GetSnapshotAsync( includeShortcuts: false, CancellationToken.None );
            var orphan = snapshot.Customizations.Single( value => value.StableId == file.Id );
            True( !orphan.IsAvailable );
            await catalog.RemoveOrphanAsync( orphan.Id, CancellationToken.None );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.RemoveOrphanAsync( snapshot.Customizations
                .Single( value => value.StableId == application.Id )
                .Id, CancellationToken.None ) );

            var export = await catalog.ExportAsync( CancellationToken.None );
            Equal( 3, export.Version );
            Equal( 1, export.Customizations.Count );
            True( export.Customizations.All( value => value.Title != "Brav" ) );
            await catalog.ResetAsync( CancellationToken.None );
            var reset = await catalog.GetSnapshotAsync( false, CancellationToken.None );
            Equal( 0, reset.Customizations.Count );
            True( !reset.Preferences.ReduceMotion );
            True( reset.Preferences.HideAfterAction );

            var emptyHome = await coordinator.SearchAsync( new SearchRequest { Query = string.Empty, Limit = 20 }, CancellationToken.None );

            True( emptyHome.Count == 0 );
            await catalog.DisposeAsync( );
        }
        finally
        {
            DeleteDatabase( database );
        }

        static LauncherPreferences CreateConfigured( LauncherPreferences defaults )
        {
            return new LauncherPreferences( )
            {
                Sources = defaults
                    .Sources.Select( source => CreateSourcePreference( source ) )
                    .ToList( ),
                HideAfterAction = false,
                ReduceMotion = true,
                WebFallbackQuicklinkId = "arch-wiki"
            };
        }

        static LauncherPreferences CreatePreferencesWithUnknownSource( LauncherPreferences defaults )
        {
            return new LauncherPreferences
            {
                Sources = defaults
                    .Sources.Select( ( source, index ) => new SourcePreference
                    {
                        SourceId = index == 0 ? "unknown-source" : source.SourceId,
                        EnabledInSearch = source.EnabledInSearch,
                        EnabledOnHome = source.EnabledOnHome,
                        Position = source.Position
                    } )
                    .ToList( )
            };
        }

        static LauncherPreferences CreatePreferencesWithDuplicatePositions( LauncherPreferences defaults )
        {
            return new LauncherPreferences
            {
                Sources = defaults
                    .Sources.Select( source => new SourcePreference
                    {
                        SourceId = source.SourceId,
                        EnabledInSearch = source.EnabledInSearch,
                        EnabledOnHome = source.EnabledOnHome,
                        Position = 1
                    } )
                    .ToList( )
            };
        }

        static LauncherPreferences CreatePreferencesWithZeroPosition( LauncherPreferences defaults )
        {
            return new LauncherPreferences
            {
                Sources = defaults
                    .Sources.Select( ( source, index ) => new SourcePreference
                    {
                        SourceId = source.SourceId,
                        EnabledInSearch = source.EnabledInSearch,
                        EnabledOnHome = source.EnabledOnHome,
                        Position = index == 0 ? 0 : source.Position
                    } )
                    .ToList( )
            };
        }

        static SourcePreference CreateSourcePreference( SourcePreference source )
        {
            var disableOnHome = source.SourceId is SourceIds.WindowsWorkspaces or SourceIds.OmarchyControl or SourceIds.Applications;

            return new SourcePreference
            {
                SourceId = source.SourceId,
                Position = source.Position,
                EnabledInSearch = source.SourceId != SourceIds.Applications && source.EnabledInSearch,
                EnabledOnHome = !disableOnHome && source.EnabledOnHome
            };
        }
    }

    private static async Task DisabledLiveSourcesDoNotStartProviderAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-provider-preference-spec-{Guid.NewGuid( ):N}.db" );

        try
        {
            SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( CancellationToken.None );
            var defaults = LauncherPreferences.CreateDefault( );

            await catalog.ReplacePreferencesAsync( new LauncherPreferences
            {
                Sources = defaults
                    .Sources.Select( source => CreateSourcePreference( source ) )
                    .ToList( )
            }, CancellationToken.None );

            CountingSearchProvider hyprland = new( "hyprland" );
            SearchCoordinator coordinator = new( [hyprland], new SearchOptions( ), preferences: catalog );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "terminal", Limit = 20 }, CancellationToken.None );

            Equal( 0, hyprland.Calls );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "> terminal", Limit = 20 }, CancellationToken.None );

            Equal( 0, hyprland.Calls );

            _ = await coordinator.SearchAsync(
                new SearchRequest { Query = "terminal", Expression = new QueryExpression { IncludeFilterIds = [SearchFilterIds.Windows] }, Limit = 20 },
                CancellationToken.None );

            Equal( 1, hyprland.Calls );
            await catalog.DisposeAsync( );
        }
        finally
        {
            DeleteDatabase( database );
        }

        static SourcePreference CreateSourcePreference( SourcePreference source )
        {
            return new SourcePreference
            {
                SourceId = source.SourceId,
                EnabledInSearch = source.SourceId != SourceIds.WindowsWorkspaces && source.EnabledInSearch,
                EnabledOnHome = source.EnabledOnHome,
                Position = source.Position
            };
        }
    }
}
