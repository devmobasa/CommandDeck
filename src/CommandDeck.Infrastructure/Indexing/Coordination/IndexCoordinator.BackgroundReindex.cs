namespace CommandDeck;

public sealed partial class IndexCoordinator
{
    private const string ReindexRequiredReason = "reindex-required";

    private volatile bool _reindexPending;
    private Task? _backgroundReindex;
    private CancellationTokenSource? _fullScanCancellation;
    private readonly SemaphoreSlim _fullScanLock = new( 1, 1 );
    private volatile IndexProgress? _reindexProgress;
    private volatile string? _reindexError;

    internal Task BackgroundReindex
    {
        get
        {
            lock ( _startLock )
            {
                return _backgroundReindex ?? Task.CompletedTask;
            }
        }
    }

    public Task WaitForBackgroundReindexAsync( CancellationToken cancellationToken ) => BackgroundReindex.WaitAsync( cancellationToken );

    // Shutdown cancels work owned by the daemon. Client disconnection does not
    // cancel a choice that its preference transaction already committed.
    public void CancelBackgroundWork( )
    {
        if ( !_disposed )
        {
            _lifetime.Cancel( );
        }
    }

    private async ValueTask CommitRootChangeAsync( IndexRootPreferences after, bool reset, CancellationToken cancellationToken )
    {
        if ( _rootPreferences is not IIndexRootChangeStore store )
        {
            throw new InvalidOperationException( "Atomic indexed folder preferences are unavailable." );
        }

        var next = GetEnabledRootOptions( after );

        using FileIndexSource replacement = new( _options.Indexing with { Roots = next }, _options.Privacy, _options.Storage.DatabasePath );

        await store
            .CommitRootsAsync( after, _files.Exclusions, replacement.Exclusions, reset, cancellationToken )
            .ConfigureAwait( false );

        CancelFullScan( );
        _reindexPending = true;
        _reindexError = null;
        _reindexProgress = null;
        _incremental.PauseUntilFullScan( ReindexRequiredReason );
        ApplyRootPreferences( after );
    }

    // Called under the catalog gate. A cancelled generation must reacquire that
    // gate and check its token before publication, so it cannot undo a purge.
    private void CancelFullScan( ) => _fullScanCancellation?.Cancel( );

    private void StartBackgroundReindex( )
    {
        lock ( _startLock )
        {
            if ( CannotStartBackgroundReindex( ) )
            {
                return;
            }

            _backgroundReindex = Task.Run( ( ) => RunBackgroundReindexAsync( _lifetime.Token ), CancellationToken.None );
        }
    }

    private async Task RunBackgroundReindexAsync( CancellationToken cancellationToken )
    {
        try
        {
            await ReindexPendingAsync( cancellationToken )
                .ConfigureAwait( false );
        }
        finally
        {
            lock ( _startLock )
            {
                _backgroundReindex = null;
                StartBackgroundReindex( );
            }
        }
    }

    private async Task ReindexPendingAsync( CancellationToken cancellationToken )
    {
        while ( !cancellationToken.IsCancellationRequested )
        {
            try
            {
                await _indexLock
                    .WaitAsync( cancellationToken )
                    .ConfigureAwait( false );

                try
                {
                    if ( !_reindexPending )
                    {
                        return;
                    }

                    EnsureCatalogMutationOwnership( );

                    await ReindexCoreAsync( new ScanProgress( value => _reindexProgress = value ), cancellationToken, background: true )
                        .ConfigureAwait( false );
                }
                finally
                {
                    _indexLock.Release( );
                }
            }
            catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
            {
                // A later edit replaces the entire scan; one durable flag
                // represents every queued edit, including across a restart.
                continue;
            }
            catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
            {
                return;
            }
            catch ( Exception exception )
            {
                _reindexError = exception.Message;

                ReportDiagnostic( $"event=index.reindex.failed error_type={exception.GetType( ).Name}", IndexDiagnosticSeverity.Warning );

                try
                {
                    await Task
                        .Delay( TimeSpan.FromSeconds( 1 ), cancellationToken )
                        .ConfigureAwait( false );
                }
                catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
                {
                    return;
                }
            }
        }
    }

    private string? BackgroundReindexStatus( )
    {
        if ( _reindexPending )
        {
            if ( _reindexError is not null )
            {
                return "Retrying indexing";
            }

            return $"Indexing · {_reindexProgress?.Indexed ?? 0} items";
        }

        return null;
    }

    private bool CannotStartBackgroundReindex( )
    {
        return !_reindexPending || _lifetime.IsCancellationRequested || !_incremental.CanMutateCatalog || _backgroundReindex is { IsCompleted: false };
    }

    private List<IndexRootOptions> GetEnabledRootOptions( IndexRootPreferences after )
    {
        return DescribeRoots( after )
            .Where( static root => root.Enabled )
            .Select( static root => root.Options )
            .ToList( );
    }
}
