namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task FileSelectionAdmitsOnlyOwnedSnapshotsAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-selection-owner-" ).FullName;

        try
        {
            var path = Path.Combine( root, "notes.md" );
            await File.WriteAllTextAsync( path, "selected" );
            var options = InvocationFileOptions( root );
            SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( default );
            AcceptanceExecutor executor = new( );

            SearchCoordinator search = new( [new StaticSearchProvider( [InvocationFileRow( path )] )], options.Search );

            await using var runtime = InvocationFixture( options, catalog, catalog.UsageHistory, executor, search );

            var file = (await runtime.SearchAsync( new SearchRequest { Query = "notes", SearchOwner = "client-a" }, default )).Single( );

            Throws<InvalidOperationException>( ( ) => runtime.SelectFile( "client-b", file.Id, true ) );

            True( !(await runtime.ExecuteFileSelectionAsync( "client-b", "copy-paths", default )).Succeeded,
                "another client's snapshot must not enter this client's selection" );

            Equal( path, runtime
                .SelectFile( "client-a", file.Id, true )
                .Single( ).Path );

            await runtime.SearchAsync( new SearchRequest { Query = "notes", SearchOwner = "client-b" }, default );

            Equal( path, runtime
                .SelectFile( "client-b", file.Id, true )
                .Single( ).Path );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    // A row that offers "mark" with its hidden undo until it is marked, and
    // "unmark" with its own undo after; the refresh replaces the row.
    private sealed class UndoableRowProvider : ICommandProvider
    {
        public bool Marked
        {
            get;
            set;
        }

        public string Id => "static";

        public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            var change = Marked ? "unmark" : "mark";
            SearchResult row = CreateRow( change );

            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [row] );
        }

        private SearchResult CreateRow( string change )
        {
            return new( )
            {
                Id = "web-search:undoable",
                Kind = ResultKinds.WebSearch,
                Title = "Undoable fixture",
                SourceId = SourceIds.WebSearch,
                ProviderId = Id,
                Actions =
                [
                    new CommandAction { Id = change, Title = change, Kind = ActionKinds.OpenUrl },
                    new CommandAction
                    {
                        Id = UndoActions.For( change ),
                        Title = "Undo " + change,
                        Kind = ActionKinds.OpenUrl,
                        Hidden = true
                    }
                ]
            };
        }
    }

    private sealed class RecordingActionIdExecutor( UndoableRowProvider provider ) : IActionExecutor
    {
        public List<string> Actions { get; } = [];

        public ValueTask<ActionExecutionResult> ExecuteAsync( SearchResult result, CommandAction action, string? query, CancellationToken cancellationToken )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            Actions.Add( action.Id );
            provider.Marked = action.Id is "mark" or "undo:unmark";

            return ValueTask.FromResult( ActionExecutionResult.Success( action.Id ) );
        }
    }

    // A refresh replaces the changed row and retires its reference, and the new
    // row no longer offers the same undo. The undo reference the change
    // returned still reaches that one undo, for that client only.
    private static async Task InvocationUndoSurvivesTheRefreshAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-invocation-undo-" ).FullName;

        try
        {
            CommandDeckOptions options = CreateCatalogOptionsWithoutAi( root );
            SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( default );
            UndoableRowProvider provider = new( );
            RecordingActionIdExecutor executor = new( provider );
            SearchCoordinator search = new( [provider], options.Search );

            await using var runtime = InvocationFixture( options, catalog, catalog.UsageHistory, executor, search );

            async Task<SearchResult> SearchAsClientAsync( ) =>
                (await runtime.SearchAsync( new SearchRequest { Query = "undoable", SearchOwner = "client-a" }, default )).Single( );

            var row = await SearchAsClientAsync( );

            var changed = await runtime.InvokeReferenceAsync( row.Id, "mark", row.InvocationReference!, "client-a", false, default );

            True( changed.Succeeded );
            var undo = changed.UndoInvocationReference;

            True( !string.IsNullOrWhiteSpace( undo ) && undo != row.InvocationReference, "a change with a hidden undo returns its own undo reference" );

            var refreshed = await SearchAsClientAsync( );

            True( refreshed.Actions.All( static action => action.Id != "undo:mark" ), "the refreshed row offers other changes" );

            True( !(await runtime.InvokeReferenceAsync( row.Id, "undo:mark", row.InvocationReference!, "client-a", false, default )).Succeeded,
                "the refresh retired the changed row's own reference" );

            True( !(await runtime.InvokeReferenceAsync( row.Id, "mark", undo!, "client-a", false, default )).Succeeded,
                "the undo reference grants nothing but the undo" );

            True( !(await runtime.InvokeReferenceAsync( row.Id, "undo:mark", undo!, "client-b", false, default )).Succeeded,
                "another client cannot use the undo reference" );

            Equal( "mark", string.Join( ',', executor.Actions ) );

            var undone = await runtime.InvokeReferenceAsync( row.Id, "undo:mark", undo!, "client-a", false, default );

            True( undone.Succeeded );
            Equal( "mark,undo:mark", string.Join( ',', executor.Actions ) );
            Equal( ( string? ) null, undone.UndoInvocationReference );

            // Stable-ID lookups still see the live row, never the undo copy.
            True( !(await runtime.InvokeAsync( row.Id, "undo:mark", false, default )).Succeeded );
            True( (await runtime.InvokeAsync( row.Id, "unmark", false, default )).Succeeded );

            // Disconnect revokes the undo with the client's other actions.
            var again = await SearchAsClientAsync( );

            var second = await runtime.InvokeReferenceAsync( again.Id, again.Actions[0].Id, again.InvocationReference!, "client-a", false, default );

            runtime.RevokeInvocationClient( "client-a" );

            True(
                !(await runtime.InvokeReferenceAsync( again.Id, UndoActions.For( again.Actions[0].Id ), second.UndoInvocationReference!, "client-a", false,
                    default )).Succeeded, "disconnect revokes the undo" );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
