namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task HerdrCoordinatorGatesAndHomeAsync( )
    {
        var database = SpecTemporaryFiles.PathFor( $"commanddeck-herdr-{Guid.NewGuid( ):N}.db" );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = database } );
            await catalog.InitializeAsync( CancellationToken.None );
            HerdrFake fake = new( );
            HerdrProvider provider = new( new HerdrClient( fake.Runner, static ( ) => true ) );
            SearchCoordinator coordinator = new( [provider], new SearchOptions( ), catalog, catalog );

            // Enabled by default in typed search, off Home by default.
            var typed = await coordinator.SearchAsync( new SearchRequest { Query = "commanddeck", Limit = 20 }, CancellationToken.None );

            True( typed.Any( result => result.Id == "herdr:workspace:w1" ) );
            True( typed.Any( result => result.Id == "herdr:tab:w2:t2" ) );
            Equal( 2, fake.Calls.Count );
            var direct = typed.Single( result => result.Id == "herdr:tab:w2:t2" );
            var viaParent = typed.Single( result => result.Id == "herdr:tab:w1:t2" );
            True( direct.Score > viaParent.Score );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "", Limit = 20 }, CancellationToken.None );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "> commanddeck", Limit = 20 }, CancellationToken.None );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "-herdr: commanddeck", Limit = 20 }, CancellationToken.None );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "file: commanddeck", Limit = 20 }, CancellationToken.None );

            Equal( 2, fake.Calls.Count );

            // Disabling the source stops process creation even for herdr: queries.
            await SetHerdrPreferenceAsync( catalog, enabledInSearch: false, enabledOnHome: false );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "commanddeck", Limit = 20 }, CancellationToken.None );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "herdr: commanddeck", Limit = 20 }, CancellationToken.None );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "hattention:", HerdrWorkspaceId = "w1", Limit = 20 }, CancellationToken.None );

            _ = await coordinator.SearchAsync( new SearchRequest { HerdrWorkspaceId = "w1", Limit = 20 }, CancellationToken.None );

            Equal( 2, fake.Calls.Count );

            // Re-enabling with Home adds one bounded section without a restart.
            await SetHerdrPreferenceAsync( catalog, enabledInSearch: true, enabledOnHome: true );

            var home = await coordinator.SearchAsync( new SearchRequest { Query = "", Limit = 20 }, CancellationToken.None );

            var section = home
                .Where( result => result.Section?.Id == "herdr" )
                .ToArray( );

            Equal( 4, section.Length );
            Equal( "Herdr", section[0].Section!.Title );
            Equal( "herdr:workspace:w1", section[0].Id );
            Equal( 4, fake.Calls.Count );
            await SetHerdrPreferenceAsync( catalog, enabledInSearch: true, enabledOnHome: false );

            _ = await coordinator.SearchAsync( new SearchRequest { Query = "", Limit = 20 }, CancellationToken.None );

            Equal( 4, fake.Calls.Count );
        }
        finally
        {
            DeleteDatabase( database );
        }
    }

    private static async Task<HerdrStatus> StatusForAsync( string json )
    {
        HerdrFake fake = new( ) { Respond = _ => HerdrResult( json ) };

        return await new HerdrClient( fake.Runner, static ( ) => true ).GetStatusAsync( CancellationToken.None );
    }

    private static async Task<HerdrFocusOutcome> FocusForAsync( ProcessResult response, string targetKind = HerdrTargetKinds.Tab )
    {
        HerdrFake fake = new( ) { Respond = _ => response };

        return await new HerdrClient( fake.Runner, static ( ) => true ).FocusAsync( targetKind, "w1:t1", CancellationToken.None );
    }

    private static async Task<IReadOnlyList<SearchResult>> SearchHerdrAsync( string query, HerdrFake? fake = null, int limit = 50 )
    {
        HerdrProvider provider = new( new HerdrClient( (fake ?? new HerdrFake( )).Runner, static ( ) => true ) );

        return await provider.SearchAsync( QueryParser.Parse( query ),
            new SearchRequest { Query = query, Limit = limit, EnabledSearchSources = [SourceIds.Herdr] }, CancellationToken.None );
    }

    private static ProcessResult HerdrResult( string standardOutput, int exitCode = 0, string error = "" ) => new( )
    {
        ExitCode = exitCode,
        StandardOutput = standardOutput,
        StandardError = error
    };

    private sealed class HerdrFake
    {
        public List<(string File, IReadOnlyList<string> Args, TimeSpan Timeout, ProcessExecutionLimits Limits)> Calls { get; } = [];

        public Func<IReadOnlyList<string>, ProcessResult> Respond { get; init; } = static args => args[2] switch
        {
            "status" => HerdrResult( HerdrStatusRunningJson ),
            "workspace" => HerdrResult( HerdrWorkspaceListJson ),
            "tab" => HerdrResult( HerdrTabListJson ),
            _ => HerdrResult( "", 2, "usage" )
        };

        public BoundedProcessRunner Runner => ( file, args, timeout, limits, _ ) =>
        {
            lock ( Calls )
            {
                Calls.Add( (file, args, timeout, limits) );
            }

            return ValueTask.FromResult( Respond( args ) );
        };
    }

    // Keeps the environment assertion readable: fixed order, no set semantics.
    private sealed class EnvironmentKeyOrder : IComparer<string>
    {
        private static readonly string[] Order = ["PATH", "HOME", "XDG_CONFIG_HOME", "XDG_RUNTIME_DIR", "LANG", "LC_ALL"];

        public int Compare( string? x, string? y ) =>
            Array
                .IndexOf( Order, x )
                .CompareTo( Array.IndexOf( Order, y ) );
    }
}
