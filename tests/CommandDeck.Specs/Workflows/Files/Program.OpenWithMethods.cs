using Microsoft.Data.Sqlite;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task RuntimeSearchSurvivesMimeDetectionFailureAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-failed-mime-" ).FullName;

        try
        {
            await using var runtime = await SlowMimeRuntimeAsync( SlowMimeOptions( root ), new FailingMimeTypeResolver( ) );

            var outcome = await runtime.SearchWithOutcomeAsync( ReportSearch( ), default );
            True( outcome.Results.Any( static row => row.Kind == "application" ) );
            var file = outcome.Results.Single( static row => row.Id == "file:report" );
            True( file.Actions.Any( static action => action.Id == "open" ) );
            True( !file.Actions.Any( static action => action.Id.StartsWith( "open-with:", StringComparison.Ordinal ) ) );
        }
        finally
        {
            SqliteConnection.ClearAllPools( );
            Directory.Delete( root, recursive: true );
        }
    }

    // "Open With" needs a MIME type per file: a stat, plus a `file` process for
    // each cache miss. Candidates that ranking discards must not pay for it.
    private static async Task RuntimeResolvesMimeTypesOnlyForShownFilesAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-open-with-" ).FullName;

        try
        {
            CommandDeckOptions options = CreateCatalogOptionsWithoutAi( root );
            RecordingMimeTypeResolver mime = new( "text/plain" );
            SqliteCatalog catalog = new( options.Storage, mimeTypeResolver: mime );
            await catalog.InitializeAsync( default );
            await catalog.ReplaceAllAsync( OpenWithReportItems( root, 120 ), null, default );

            SearchCoordinator search = new( [new ApplicationSearchProvider( catalog ), new CatalogSearchProvider( catalog, catalog )], options.Search, catalog,
                catalog, catalog.UsageHistory );

            await using var runtime = InvocationFixture( options, catalog, catalog.UsageHistory, new AcceptanceExecutor( ), search );

            var outcome = await runtime.SearchWithOutcomeAsync( new SearchRequest { Query = "report", Limit = 10 }, default );

            var shown = outcome
                .Results.Where( static result => result.Kind == "file" )
                .ToArray( );

            True( shown.Length is > 0 and <= 10, $"expected shown file rows, got {shown.Length}" );

            var shownPaths = shown
                .Select( static result => result.Metadata["path"] )
                .ToHashSet( StringComparer.Ordinal );

            True( mime.RequestedPaths.SetEquals( shownPaths ),
                $"MIME detection ran for {mime.RequestedPaths.Count} paths; the launcher shows {shownPaths.Count} files" );

            foreach ( var file in shown )
            {
                var openWith = file.Actions.Single( static action => action.Id == "open-with:viewer.desktop" );
                Equal( "launch-desktop", openWith.Kind );

                Equal( file.Metadata["path"], LaunchArgumentCodec
                    .Decode( openWith.Arguments["launchArguments"] )
                    .Single( ) );

                Equal( "open", file.Actions[0].Id );
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools( );
            Directory.Delete( root, recursive: true );
        }
    }

    // Open With is optional. The runtime used to finish MIME detection before any
    // row reached the caller, so a stalled `file` held up even an application
    // match. A search now waits a bounded time, then cancels detection and returns
    // the rows with their own actions.
    private static async Task RuntimeSearchDoesNotWaitForSlowMimeDetectionAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-slow-mime-" ).FullName;

        try
        {
            var options = SlowMimeOptions( root );
            SlowMimeTypeResolver mime = new( honoursCancellation: true );
            await using var runtime = await SlowMimeRuntimeAsync( options, mime );

            // A prompt answer gives the complete rows and reads the handler list once.
            var answered = await runtime.SearchWithOutcomeAsync( ReportSearch( ), default );

            // The declared bound: the providers' deadline, then the Open With wait.
            mime.Stall( );

            var bound = TimeSpan.FromMilliseconds( options.Search.ProviderTimeoutMilliseconds ) + OpenWithDetection.DefaultWait;
            using CancellationTokenSource abandon = new( );

            var searching = runtime
                .SearchWithOutcomeAsync( ReportSearch( ), abandon.Token )
                .AsTask( );

            await (( Task ) searching)
                .WaitAsync( bound )
                .ConfigureAwait( ConfigureAwaitOptions.SuppressThrowing );

            var returned = searching.IsCompleted;
            if ( !returned )
            {
                abandon.Cancel( );
                await (( Task ) searching).ConfigureAwait( ConfigureAwaitOptions.SuppressThrowing );
            }

            True( returned, $"no row reached the caller within {bound.TotalMilliseconds:F0} ms while MIME detection stalled" );

            var stalled = await searching;
            True( stalled.IsComplete );

            Equal( DescribeRows( answered.Results, withOpenWith: false ), DescribeRows( stalled.Results, withOpenWith: true ) );

            True( answered
                .Results.Single( static row => row.Id == "file:report" )
                .Actions.Any( static action => action.Id == "open-with:viewer.desktop" ) );

            await mime
                .Cancelled.WaitAsync( TimeSpan.FromSeconds( 5 ) )
                .ConfigureAwait( ConfigureAwaitOptions.SuppressThrowing );

            True( mime.Cancelled.IsCompleted, "MIME detection kept running after the search returned" );
        }
        finally
        {
            SqliteConnection.ClearAllPools( );
            Directory.Delete( root, recursive: true );
        }
    }

    // Cancelling a search while it waits for Open With stops detection as well. A
    // long wait leaves the request's cancellation as the only way out.
    private static async Task RuntimeSearchCancellationEndsMimeDetectionAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-cancelled-mime-" ).FullName;

        try
        {
            SlowMimeTypeResolver mime = new( honoursCancellation: true );
            mime.Stall( );

            await using var runtime = await SlowMimeRuntimeAsync( SlowMimeOptions( root ), mime,
                new RuntimeConstructionOptions { OpenWithWait = TimeSpan.FromMinutes( 5 ) } );

            using CancellationTokenSource request = new( );
            var searching = GetSearching( runtime, request );
            await mime.Started.WaitAsync( TimeSpan.FromSeconds( 5 ), CancellationToken.None );
            request.Cancel( );

            await ThrowsAsync<OperationCanceledException>( ( ) => searching.WaitAsync( TimeSpan.FromSeconds( 5 ) ) );

            await mime
                .Cancelled.WaitAsync( TimeSpan.FromSeconds( 5 ) )
                .ConfigureAwait( ConfigureAwaitOptions.SuppressThrowing );

            True( mime.Cancelled.IsCompleted, "MIME detection kept running after its search was cancelled" );
        }
        finally
        {
            SqliteConnection.ClearAllPools( );
            Directory.Delete( root, recursive: true );
        }

        static Task<SearchOutcome> GetSearching( CommandDeckRuntime runtime, CancellationTokenSource request )
        {
            return runtime
                .SearchWithOutcomeAsync( ReportSearch( ), request.Token )
                .AsTask( );
        }
    }

    // A stat on a mount that stopped answering ignores cancellation. Detection
    // that outlives its search keeps one of two slots, a later search skips Open
    // With instead of starting more, and shutdown waits for that work to end.
    private static async Task RuntimeBoundsAndJoinsAbandonedMimeDetectionAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-abandoned-mime-" ).FullName;
        SlowMimeTypeResolver mime = new( honoursCancellation: false );
        CommandDeckRuntime? runtime = null;

        try
        {
            mime.Stall( );
            runtime = await SlowMimeRuntimeAsync( SlowMimeOptions( root ), mime );

            for ( var search = 0; search < 3; search++ )
            {
                var outcome = await runtime
                    .SearchWithOutcomeAsync( ReportSearch( ), default )
                    .AsTask( )
                    .WaitAsync( TimeSpan.FromSeconds( 5 ), CancellationToken.None );

                Equal( "app:report-viewer: launch | file:report: open,collect,browse-parent,reveal,copy-path,copy-file-url",
                    DescribeRows( outcome.Results, withOpenWith: true ) );
            }

            Equal( 2, mime.Calls );
            Equal( 2, mime.Running );

            var disposing = runtime
                .DisposeAsync( )
                .AsTask( );

            True( !disposing.IsCompleted, "runtime shutdown did not wait for MIME detection that was still running" );

            mime.Release( );
            await disposing.WaitAsync( TimeSpan.FromSeconds( 5 ), CancellationToken.None );
            Equal( 0, mime.Running );
        }
        finally
        {
            mime.Release( );

            if ( runtime is not null )
            {
                await runtime.DisposeAsync( );
            }

            SqliteConnection.ClearAllPools( );
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task<CommandDeckRuntime> SlowMimeRuntimeAsync( CommandDeckOptions options, IMimeTypeResolver mime,
        RuntimeConstructionOptions? constructionOptions = null )
    {
        SqliteCatalog catalog = new( options.Storage, mimeTypeResolver: mime );
        await catalog.InitializeAsync( default );
        await catalog.ReplaceAllAsync( ReportViewerItems( options.Storage.DatabasePath ), null, default );
        SearchCoordinator search = CreateSearch( options, catalog );

        return InvocationFixture( options, catalog, catalog.UsageHistory, new AcceptanceExecutor( ), search, constructionOptions );

        static SearchCoordinator CreateSearch( CommandDeckOptions options, SqliteCatalog catalog )
        {
            return new( [new ApplicationSearchProvider( catalog ), new CatalogSearchProvider( catalog, catalog )], options.Search, catalog,
                catalog.LauncherPreferences, catalog.UsageHistory );
        }
    }

    private static async IAsyncEnumerable<IndexedItem> ReportViewerItems( string databasePath )
    {
        yield return CreateIndexedItem( );

        yield return CreateDatabaseFileItem( databasePath );

        await Task.CompletedTask;

        static IndexedItem CreateIndexedItem( )
        {
            return new IndexedItem
            {
                StableId = "app:report-viewer",
                Kind = ResultKinds.Application,
                Title = "Report Viewer",
                PayloadJson = PayloadWriter.Create( (ResultMetadataKeys.Application.DesktopFile, "/tmp/viewer.desktop"),
                    (ResultMetadataKeys.Application.DesktopId, "viewer.desktop"), (ResultMetadataKeys.Application.Exec, "viewer %U"),
                    (ResultMetadataKeys.Application.MimeTypes, "text/plain"), (ResultMetadataKeys.Application.Terminal, "false") ),
                ProviderId = ProviderIds.Applications,
                SourceId = SourceIds.Applications
            };
        }

        static IndexedItem CreateDatabaseFileItem( string databasePath )
        {
            return new IndexedItem
            {
                StableId = "file:report",
                Kind = ResultKinds.File,
                Title = "report.txt",
                Path = Path.Combine( Path.GetDirectoryName( databasePath )!, "report.txt" ),
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Files
            };
        }
    }
}
