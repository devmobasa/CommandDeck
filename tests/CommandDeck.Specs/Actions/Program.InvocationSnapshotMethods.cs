namespace CommandDeck.Specs;

public static partial class Program
{
    private sealed class QueryActionProvider : ICommandProvider
    {
        public string Id => "static";

        public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            SearchResult row = CreateRow( request );

            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [row] );
        }

        private SearchResult CreateRow( SearchRequest request )
        {
            return new( )
            {
                Id = "web-search:fixture",
                Kind = ResultKinds.WebSearch,
                Title = "Search fixture",
                SourceId = SourceIds.WebSearch,
                ProviderId = Id,
                Actions =
                [
                    new CommandAction
                    {
                        Id = "open",
                        Title = "Open",
                        Kind = ActionKinds.OpenUrl,
                        Hidden = true,
                        Inputs = [new ActionInput { Name = "note", DefaultValue = "literal" }],
                        Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.UrlTemplate] = "https://example.invalid/?q=" + request.Query }
                    }
                ]
            };
        }
    }

    private sealed class RecordingInvocationExecutor : IActionExecutor
    {
        public List<string> Targets { get; } = [];
        public List<CommandAction> Actions { get; } = [];

        public ValueTask<ActionExecutionResult> ExecuteAsync( SearchResult result, CommandAction action, string? query, CancellationToken cancellationToken )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            Targets.Add( action.Arguments[ActionArgumentNames.UrlTemplate] );
            Actions.Add( action );

            return ValueTask.FromResult( ActionExecutionResult.Success( ) );
        }
    }

    private static async Task InvocationSnapshotRejectsReplacedActionAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-invocation-snapshot-" ).FullName;

        try
        {
            CommandDeckOptions options = CreateCatalogOptionsWithoutAi( root );
            SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( default );
            RecordingInvocationExecutor executor = new( );
            SearchCoordinator search = new( [new QueryActionProvider( )], options.Search );

            await using var runtime = InvocationFixture( options, catalog, catalog.UsageHistory, executor, search );

            var alpha = (await runtime.SearchAsync( new SearchRequest { Query = "alpha", SearchOwner = "client-a" }, default )).Single( );

            var beta = (await runtime.SearchAsync( new SearchRequest { Query = "beta", SearchOwner = "client-b" }, default )).Single( );

            Equal( alpha.Id, beta.Id );

            True( alpha
                      .Actions[0]
                      .Arguments["urlTemplate"] !=
                  beta
                      .Actions[0]
                      .Arguments["urlTemplate"] );

            True( !string.IsNullOrWhiteSpace( alpha.InvocationReference ) );
            True( !string.IsNullOrWhiteSpace( beta.InvocationReference ) );
            True( alpha.InvocationReference != beta.InvocationReference );

            var outcome = await runtime.InvokeReferenceAsync( alpha.Id, "open", alpha.InvocationReference!, "client-a", false, default );

            True( outcome.Succeeded, "another client cannot replace this client's action" );
            Equal( "https://example.invalid/?q=alpha", executor.Targets.Single( ) );

            True( executor.Actions.Single( )
                      .Hidden ==
                  true, "Invocation snapshots and bound actions preserve internal action visibility." );

            Equal( "note", executor
                .Actions.Single( )
                .Inputs.Single( ).Name );

            Equal( "literal", executor
                .Actions.Single( )
                .Arguments["input:note"] );

            outcome = await runtime.InvokeReferenceAsync( beta.Id, "open", beta.InvocationReference!, "client-a", false, default );

            True( !outcome.Succeeded, "another client cannot reuse the reference" );
            Equal( 1, executor.Targets.Count );

            beta
                .Actions[0]
                .Arguments[ActionArgumentNames.UrlTemplate] = "https://example.invalid/changed";

            outcome = await runtime.InvokeReferenceAsync( beta.Id, "open", beta.InvocationReference!, "client-b", false, default );

            True( outcome.Succeeded );
            Equal( "https://example.invalid/?q=beta", executor.Targets.Last( ) );

            var replacement = (await runtime.SearchAsync( new SearchRequest { Query = "gamma", SearchOwner = "client-a" }, default )).Single( );

            Equal( alpha.Id, replacement.Id );

            outcome = await runtime.InvokeReferenceAsync( alpha.Id, "open", alpha.InvocationReference!, "client-a", false, default );

            True( !outcome.Succeeded, "a same-client replacement revokes its earlier action" );
            Equal( 2, executor.Targets.Count );
            runtime.RevokeInvocationClient( "client-b" );

            outcome = await runtime.InvokeReferenceAsync( beta.Id, "open", beta.InvocationReference!, "client-b", false, default );

            True( !outcome.Succeeded, "disconnect revokes the client's action" );
            Equal( 2, executor.Targets.Count );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static SearchResult InvocationFileRow( string path ) => new( )
    {
        Id = "file:invocation-fixture",
        Kind = ResultKinds.File,
        Title = Path.GetFileName( path ),
        SourceId = SourceIds.Files,
        ProviderId = ProviderIds.Files,
        Metadata = new( StringComparer.Ordinal ) { ["path"] = path },
        Actions =
        [
            new CommandAction
            {
                Id = "collect",
                Title = "Toggle file selection",
                Kind = ActionKinds.CollectFile,
                Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Path] = path }
            }
        ]
    };

    private static CommandDeckOptions InvocationFileOptions( string root ) => new( )
    {
        Storage = new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) },
        Indexing = new IndexingOptions
        {
            Roots = [new IndexRootOptions { Path = root, IncludeContentInSearch = true }],
            IndexDesktopApplications = false,
            IndexOmarchyCommands = false
        },
        Privacy = new PrivacyOptions { StoreQueryHistory = true, NeverIndex = [] },
        Ai = new AiOptions { Enabled = false, Providers = [] }
    };

    private static async Task PreviewMatchingLineInheritsQueryPrivacyAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-preview-privacy-" ).FullName;

        try
        {
            var path = Path.Combine( root, "notes.md" );
            await File.WriteAllTextAsync( path, "first line\nsecond needle line\n" );
            var options = InvocationFileOptions( root );
            SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( default );
            AcceptedUsageHistory history = new( catalog.UsageHistory );
            AcceptanceExecutor executor = new( );

            SearchCoordinator search = new( [new StaticSearchProvider( [InvocationFileRow( path )] )], options.Search );

            await using var runtime = InvocationFixture( options, catalog, history, executor, search );

            async Task<ActionExecutionResult> OpenMatchingLineAsync( string query )
            {
                var file = (await runtime.SearchAsync( new SearchRequest { Query = query, SearchOwner = "client-a" }, default )).Single( );

                var preview = await runtime.PreviewAsync( file.Id, default, "content:needle", null, 0, "client-a",
                    file.InvocationReference );

                Equal( 2, preview.Preview?.MatchLine );

                return await runtime.InvokeReferenceAsync( preview.Preview!.OpenResultId!, "edit", preview.Preview.OpenInvocationReference!, "client-a", false,
                    default );
            }

            True( (await OpenMatchingLineAsync( "needle -hey:" )).Succeeded );
            Equal( 1, executor.Calls );
            Equal( 0, history.Writes );
            True( (await OpenMatchingLineAsync( "needle" )).Succeeded );
            Equal( 2, executor.Calls );
            Equal( 1, history.Writes );
            Equal( "needle", history.LastQuery );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
