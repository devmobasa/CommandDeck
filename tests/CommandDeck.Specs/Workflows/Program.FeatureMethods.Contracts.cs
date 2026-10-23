namespace CommandDeck.Specs;

public static partial class Program
{
    private static void ShortcutInspectionReportsEffectiveChords( )
    {
        const string binds = """
                             [
                               {"modmask":64,"key":"Q","dispatcher":"exec","arg":"omarchy-shell toggle local.commanddeck"},
                               {"modmask":72,"key":"space","dispatcher":"exec","arg":"omarchy-menu toggle apps"},
                               {"modmask":64,"key":"escape","dispatcher":"exec","arg":"omarchy-menu toggle system"},
                               {"modmask":4,"key":"space","dispatcher":"__lua","arg":"39","description":"CommandDeck"},
                               {"modmask":64,"key":"Q","dispatcher":"__lua","arg":"251","description":"Omarchy menu"}
                             ]
                             """;

        var shortcuts = HyprlandShortcutInspector.Parse( binds );

        True( shortcuts
            .Single( value => value.SurfaceId == "commanddeck" )
            .Chords.SequenceEqual( ["Ctrl+Space", "Super+Q"] ) );

        Equal( "Super+Alt+Space", shortcuts
            .Single( value => value.SurfaceId == "applications" )
            .Chords[0] );

        Equal( "Super+Escape", shortcuts
            .Single( value => value.SurfaceId == "system-menu" )
            .Chords[0] );

        Equal( "Super+Q", shortcuts
            .Single( value => value.SurfaceId == "omarchy-control" )
            .Chords[0] );
    }

    private static async Task ShortcutInspectionDistinguishesFailuresAsync( )
    {
        var unconfigured = HyprlandShortcutInspector.Parse( "[]" );
        True( unconfigured.All( value => value.Status == "not-configured" ) );
        var malformed = HyprlandShortcutInspector.Parse( "not-json" );
        True( malformed.All( value => value.Status == "not-configured" ) );
        True( malformed.All( value => value.Detail?.Contains( "malformed", StringComparison.Ordinal ) == true ) );

        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var directory = SpecTemporaryFiles.PathFor( $"commanddeck-shortcut-spec-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( directory );
        var executable = Path.Combine( directory, "hyprctl" );
        var previousPath = Environment.GetEnvironmentVariable( "PATH" );

        try
        {
            Environment.SetEnvironmentVariable( "PATH", directory );
            var missing = await HyprlandShortcutInspector.InspectAsync( CancellationToken.None );
            True( missing.All( value => value.Status == "not-configured" ) );
            True( missing.All( value => value.Detail?.Contains( "PATH", StringComparison.Ordinal ) == true ) );
            await File.WriteAllTextAsync( executable, "#!/bin/sh\nexit 7\n" );

            File.SetUnixFileMode( executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

            var failed = await HyprlandShortcutInspector.InspectAsync( CancellationToken.None );
            True( failed.All( value => value.Status == "not-configured" ) );
            await File.WriteAllTextAsync( executable, "#!/bin/sh\n/usr/bin/sleep 2\nprintf '[]'\n" );

            File.SetUnixFileMode( executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

            var timedOut = await HyprlandShortcutInspector.InspectAsync( CancellationToken.None );
            True( timedOut.All( value => value.Status == "not-configured" ) );
        }
        finally
        {
            Environment.SetEnvironmentVariable( "PATH", previousPath );
            Directory.Delete( directory, recursive: true );
        }
    }

    private static async IAsyncEnumerable<IndexedItem> CustomizationItems( bool includeFile )
    {
        yield return CreateIndexedItem( );

        if ( includeFile )
        {
            yield return CreateNoteItem( );
        }

        await Task.CompletedTask;

        static IndexedItem CreateIndexedItem( )
        {
            return new IndexedItem
            {
                StableId = "app:brave.desktop",
                Kind = ResultKinds.Application,
                Title = "Brave",
                PayloadJson = PayloadWriter.Create( (ResultMetadataKeys.Application.DesktopFile, "/usr/share/applications/brave.desktop"),
                    (ResultMetadataKeys.Application.DesktopId, "brave.desktop"), (ResultMetadataKeys.Application.Exec, "brave %U") ),
                ProviderId = ProviderIds.Applications,
                SourceId = SourceIds.Applications
            };
        }

        static IndexedItem CreateNoteItem( )
        {
            return new IndexedItem
            {
                StableId = "file:notes",
                Kind = ResultKinds.File,
                Title = "notes.txt",
                Path = "/tmp/notes.txt",
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Files
            };
        }
    }

    private static void StructuredConfigArraysUseSemanticElementNames( )
    {
        const string json = """
                            {
                              "entries": [
                                {
                                  "name": "outer",
                                  "entries": [
                                    { "name": "inner", "value": "named" },
                                    { "name": "inner", "value": "duplicate" },
                                    { "id": "by-id", "value": "identified" },
                                    { "title": "By title", "value": "titled" },
                                    { "value": "indexed" }
                                  ]
                                }
                              ]
                            }
                            """;

        var entries = StructuredConfigExtractor.Extract( "/tmp/config.json", json );
        True( entries.Any( entry => entry.Key == "entries[outer].entries[inner].name" ) );
        True( entries.Any( entry => entry.Key == "entries[outer].entries[1].value" ) );
        True( entries.Any( entry => entry.Key == "entries[outer].entries[by-id].value" ) );
        True( entries.Any( entry => entry.Key == "entries[outer].entries[By title].value" ) );
        True( entries.Any( entry => entry.Key == "entries[outer].entries[4].value" ) );
        True( entries.All( entry => !entry.Key.Contains( "entries[0].entries", StringComparison.Ordinal ) ) );
    }

    private static async Task DetailFieldsRemainOrderedAndSerializeAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-detail-spec-{Guid.NewGuid( ):N}.db" );

        try
        {
            SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( CancellationToken.None );
            await catalog.ReplaceAllAsync( DetailFieldItems( ), null, CancellationToken.None );

            var results = await catalog.SearchAsync( QueryParser.Parse( string.Empty ), 20, CancellationToken.None );

            AssertDetailLabels( results, "application", "Desktop file", "Desktop ID", "Command", "Terminal" );

            AssertDetailLabels( results, "configuration", "File", "Key location", "Value location", "Key",
                "Section", "Value" );

            AssertDetailLabels( results, "file", "Path" );
            AssertDetailLabels( results, "omarchy", "Command", "Source" );
            AssertDetailLabels( results, "quicklink", "URL", "Alias" );
            AssertDetailLabels( results, "custom-command", "Command", "Working directory", "Confirmation" );
            SearchCoordinator coordinator = new( [new CalculatorProvider( )], new SearchOptions( ) );

            var calculation = (await coordinator.SearchAsync( new SearchRequest { Query = "= 2 + 2", Limit = 5 }, default ))[0];

            Equal( "Expression", calculation.Details[0].Label );
            True( calculation.Details[0].Monospace );
            True( !calculation.Metadata.ContainsKey( "queryText" ) );
            ProtocolMessage message = new( ) { RequestId = 1, Type = "result", Result = calculation };
            var json = System.Text.Json.JsonSerializer.Serialize( message, CommandDeckJsonContext.Default.ProtocolMessage );
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize( json, CommandDeckJsonContext.Default.ProtocolMessage );

            Equal( "Expression", roundTrip?.Result?.Details[0].Label );
            Equal( "2 + 2", roundTrip?.Result?.Details[0].Value );
            True( roundTrip?.Result?.Details[0].Monospace == true );
            await catalog.DisposeAsync( );
        }
        finally
        {
            foreach ( var suffix in new[] { string.Empty, "-shm", "-wal" } )
            {
                File.Delete( database + suffix );
            }
        }
    }

    private static void ConfigurableSourcesShipLabelAndIcon( )
    {
        // The settings panel renders one row per configurable source. A source
        // without a label or icon is indistinguishable from every other row.
        foreach ( var descriptor in LauncherSourceRegistry.Configurable )
        {
            True( !string.IsNullOrWhiteSpace( descriptor.Label ) );
            True( !string.IsNullOrWhiteSpace( descriptor.Icon ) );
        }

        Equal( LauncherSourceRegistry.Configurable.Count, LauncherSourceRegistry
            .Configurable.Select( static descriptor => descriptor.Label )
            .Distinct( StringComparer.Ordinal )
            .Count( ) );
    }
}
