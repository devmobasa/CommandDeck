namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task WebFallbackDoesNotConnectBeforeInvocationAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-fallback-network-spec-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );
        System.Net.Sockets.TcpListener listener = new( System.Net.IPAddress.Loopback, 0 );
        listener.Start( );

        try
        {
            var port = (( System.Net.IPEndPoint ) listener.LocalEndpoint).Port;
            var config = Path.Combine( root, "config.json" );
            var defaults = LauncherPreferences.CreateDefault( );
            CommandDeckOptions options = CreateOptions( root, port );

            await File.WriteAllTextAsync( config, System.Text.Json.JsonSerializer.Serialize( options, CommandDeckJsonContext.Default.CommandDeckOptions ) );

            await using var runtime = await CommandDeckRuntime.CreateAsync( config, CancellationToken.None );

            await runtime.Preferences.ReplacePreferencesAsync( CreateLauncherPreferences( defaults ), CancellationToken.None );

            var results = await runtime.SearchAsync( new SearchRequest { Query = "no local result", Limit = 10 }, default );

            Equal( "fallback", results.Single( ).ProviderId );
            True( !listener.Pending( ) );
        }
        finally
        {
            listener.Stop( );
            Directory.Delete( root, recursive: true );
        }

        static CommandDeckOptions CreateOptions( string root, int port )
        {
            return new( )
            {
                Storage = new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) },
                Indexing = new IndexingOptions { Roots = [], IndexDesktopApplications = false, IndexOmarchyCommands = false },
                Quicklinks =
                [
                    new QuicklinkOptions { Id = "local-web", Title = "Local web", UrlTemplate = $"http://127.0.0.1:{port}/?q={UrlTemplateTokens.Query}" }
                ],
                Ai = new AiOptions { Enabled = false, Providers = [] }
            };
        }

        static LauncherPreferences CreateLauncherPreferences( LauncherPreferences defaults )
        {
            return new LauncherPreferences
            {
                Sources = defaults
                    .Sources.Select( source => new SourcePreference
                    {
                        SourceId = source.SourceId,
                        EnabledInSearch = source.SourceId != SourceIds.WindowsWorkspaces && source.EnabledInSearch,
                        EnabledOnHome = source.EnabledOnHome,
                        Position = source.Position
                    } )
                    .ToList( ),
                WebFallbackQuicklinkId = "local-web"
            };
        }
    }

    private static void FilterRegistryDrivesQueries( )
    {
        var raw = QueryParser.Parse( "text: notes" );
        Equal( "files", raw.IncludedFilters.Single( ).Id );
        Equal( "files", raw.IncludedFilters.Single( ).SourceId );
        Equal( "file", raw.IncludedFilters.Single( ).Kind );

        var explicitFilter = QueryParser.Parse( "notes", new QueryExpression { IncludeFilterIds = [SearchFilterIds.Files] } );

        Equal( "catalog", explicitFilter.ProviderFilter );

        Throws<InvalidOperationException>( ( ) =>
            QueryParser.Parse( "app: brave", new QueryExpression { ExcludeFilterIds = [SearchFilterIds.Applications] } ) );

        Throws<InvalidOperationException>( ( ) => AliasValidator.NormalizeAndValidate( "app" ) );
    }

    private static void FilterTokensAndAliasesAllResolve( )
    {
        // The ergonomic plurals the README has always advertised.
        Equal( "workspaces", QueryParser
            .Parse( "ws: 3" )
            .IncludedFilters.Single( ).Id );

        Equal( "workspaces", QueryParser
            .Parse( "workspaces: 3" )
            .IncludedFilters.Single( ).Id );

        Equal( "files", QueryParser
            .Parse( "files: notes" )
            .IncludedFilters.Single( ).Id );

        // Every token the registry publishes must parse back to its own filter,
        // so adding an alias without wiring it up fails here rather than in the
        // launcher.
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );
        foreach ( var descriptor in SearchFilterRegistry.All )
        {
            foreach ( var token in descriptor.Aliases.Prepend( descriptor.Token ) )
            {
                True( token.EndsWith( ':' ) );
                True( seen.Add( token ) );

                Equal( descriptor.Id, QueryParser
                    .Parse( $"{token} value" )
                    .IncludedFilters.Single( ).Id );

                Equal( descriptor.Id, SearchFilterRegistry.FindByToken( token )
                    ?.Id );

                True( SearchFilterRegistry.IsReserved( token.TrimEnd( ':' ) ) );
            }
        }
    }

    private static void OmarchyCurrentMenuRoutesPreserveIds( )
    {
        const string defaults = """
                                {
                                  // current dotted schema
                                  "apps": {"label":"Apps","provider":"apps"},
                                  "learn": {"label":"Learn"},
                                  "learn.docs": {"label":"Docs","action":"open-docs"},
                                  "learn.hidden": {"label":"Hidden default","action":"must-not-survive"},
                                  "system": {"label":"System"},
                                  "system.reboot": {"label":"Reboot","action":"reboot"},
                                  "system.shutdown": {"label":"Shutdown","action":"shutdown"},
                                }
                                """;

        const string extension = """
                                 {
                                   "learn.docs": {"label":"Custom Docs","action":"open-custom-docs"},
                                   "learn.hidden": {"label":"Disabled by replacement"},
                                   "trigger.custom": {"label":"Custom","action":"custom-action"}
                                 }
                                 """;

        var items = OmarchyMenuScanner.ParseDocuments( [
            new OmarchyMenuDocument( "default.jsonc", defaults ),
            new OmarchyMenuDocument( "extension.jsonc", extension )
        ] );

        True( items.Any( item =>
            item is { StableId: "omarchy:learn.docs", Title: "Custom Docs" } &&
            item.PayloadJson?.Contains( "open-custom-docs", StringComparison.Ordinal ) == true ) );

        True( items.All( item => item.StableId != "omarchy:learn.hidden" ) );
        True( items.Any( item => item.StableId == "omarchy:trigger.custom" ) );
        True( items.Any( item => item is { StableId: "omarchy-route:root", SourceId: "omarchy-control" } ) );
        True( items.Any( item => item is { StableId: "omarchy-route:system", SourceId: "omarchy-system" } ) );

        True( items.All( item => !item.StableId.Contains( "system.reboot", StringComparison.Ordinal ) &&
                                 !item.StableId.Contains( "system.shutdown", StringComparison.Ordinal ) ) );

        True( items.All( item => item.StableId != "omarchy:apps" ) );
    }
}
