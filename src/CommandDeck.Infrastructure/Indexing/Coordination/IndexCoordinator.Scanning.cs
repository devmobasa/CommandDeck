using System.Runtime.CompilerServices;

namespace CommandDeck;

public sealed partial class IndexCoordinator
{
    private const string ScanBeginStage = "begin";

    // Internal counters must be updated before the scan commits. Progress<T>
    // posts callbacks asynchronously and can leave root status incomplete.
    private sealed class ScanProgress( Action<IndexProgress> report ) : IProgress<IndexProgress>
    {
        public void Report( IndexProgress value ) => report( value );
    }

    private async ValueTask<ReindexReport> ReindexCoreAsync( IProgress<IndexProgress>? progress, CancellationToken cancellationToken, bool background = false )
    {
        if ( !background )
        {
            CancelFullScan( );
        }

        using var scan = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken, _lifetime.Token );

        if ( !background )
        {
            _fullScanCancellation = scan;
        }

        var token = scan.Token;
        var locked = false;
        var scanning = false;
        _indexLock.Release( );

        try
        {
            await _fullScanLock
                .WaitAsync( token )
                .ConfigureAwait( false );

            scanning = true;

            await _indexLock
                .WaitAsync( token )
                .ConfigureAwait( false );

            locked = true;
            token.ThrowIfCancellationRequested( );

            if ( IsUnneededBackgroundScan( background ) )
            {
                return new ReindexReport( );
            }

            _fullScanCancellation = scan;

            using var files = _files.CreateScanSource( _options.Indexing with { Roots = [.. _options.Indexing.Roots] }, _options.Privacy,
                _options.Storage.DatabasePath );

            _indexLock.Release( );
            locked = false;

            // Discovery and extraction run outside the catalog gate. Enumeration
            // reacquires it before the staged generation can publish any row.
            var report = await BuildGenerationAsync( files, async ( ) =>
                {
                    await _indexLock
                        .WaitAsync( token )
                        .ConfigureAwait( false );

                    locked = true;
                    token.ThrowIfCancellationRequested( );
                }, progress, token )
                .ConfigureAwait( false );

            _reindexPending = false;
            _reindexError = null;

            return report;
        }
        finally
        {
            if ( !locked )
            {
                // Callers entered with this gate held and release it themselves.
                await _indexLock
                    .WaitAsync( CancellationToken.None )
                    .ConfigureAwait( false );
            }

            if ( ReferenceEquals( _fullScanCancellation, scan ) )
            {
                _fullScanCancellation = null;
            }

            if ( scanning )
            {
                _fullScanLock.Release( );
            }
        }
    }

    private async ValueTask<ReindexReport> BuildGenerationAsync( FileIndexSource files, Func<Task> acquirePublication, IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken )
    {
        var started = DateTimeOffset.UtcNow;
        _incremental.RestoreWatchers( null, cancellationToken: cancellationToken );
        var checkpoint = _incremental.CaptureCheckpoint( );
        Counter counter = new( );
        Dictionary<string, long> rootCounts = new( StringComparer.Ordinal );

        async IAsyncEnumerable<IndexedItem> Enumerate( [EnumeratorCancellation] CancellationToken token )
        {
            if ( _options.Indexing.IndexDesktopApplications )
            {
                await foreach ( var item in DesktopEntryScanner
                                   .ScanAsync( token )
                                   .ConfigureAwait( false ) )
                {
                    counter.Discovered++;
                    counter.Indexed++;

                    yield return item;
                }
            }

            if ( _options.Indexing.IndexOmarchyCommands )
            {
                await foreach ( var item in OmarchyMenuScanner
                                   .ScanAsync( token )
                                   .ConfigureAwait( false ) )
                {
                    counter.Discovered++;
                    counter.Indexed++;

                    yield return item;
                }
            }

            await foreach ( var item in _configured
                               .ScanAsync( token )
                               .ConfigureAwait( false ) )
            {
                counter.Discovered++;
                counter.Indexed++;

                yield return item;
            }

            ScanProgress fileProgress = CreateFileProgress( progress, counter, rootCounts );
            await foreach ( var item in files
                               .ScanAsync( fileProgress, token )
                               .ConfigureAwait( false ) )
            {
                counter.Indexed++;

                yield return item;
            }

            await acquirePublication( )
                .ConfigureAwait( false );
        }

        progress?.Report( new IndexProgress { Stage = ScanBeginStage } );

        await _catalog
            .ReplaceGenerationAsync( Enumerate( cancellationToken ), files.SnapshotDiagnostics, files.SnapshotWorkItems, progress, cancellationToken )
            .ConfigureAwait( false );

        // Incremental publication must start from the committed scan's diagnostics.
        _files.ReplaceDiagnostics( files.SnapshotDiagnostics( ) );
        _incremental.CompleteFullScan( checkpoint );

        foreach ( (var root, var count) in rootCounts )
        {
            _incremental.RecordRootScan( root, count );
        }

        var completed = DateTimeOffset.UtcNow;
        progress?.Report( CreateCompletionProgress( counter ) );

        return CreateGenerationReport( started, counter, completed );
    }

    private async ValueTask<bool> ApplyPathsAsync( IReadOnlyList<IncrementalPathRequest> paths, CancellationToken cancellationToken )
    {
        await _indexLock
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            if ( CannotQueueReconciliation( ) )
            {
                return false;
            }

            List<(IncrementalPathRequest Request, string ChangePath, bool IsDirectory)> batch = new( paths.Count );

            foreach ( var request in paths )
            {
                if ( _files.TryResolveChangePath( request.Path, out var changePath ) )
                {
                    batch.Add( (request, changePath, Directory.Exists( request.Path )) );
                }
            }

            await ApplyPathBatchAsync( batch, cancellationToken )
                .ConfigureAwait( false );

            await _catalog
                .ReplaceExclusionDiagnosticsAsync( _files.SnapshotDiagnostics( ), cancellationToken )
                .ConfigureAwait( false );

            _incremental.SavePolicyState( );

            return true;
        }
        finally
        {
            StartBackgroundReindex( );
            _indexLock.Release( );
        }
    }

    private async ValueTask<bool> ReconcileAsync( IReadOnlyList<string> affectedRoots, CancellationToken cancellationToken )
    {
        await _indexLock
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            if ( CannotRunReconciliation( ) )
            {
                return false;
            }

            if ( affectedRoots.Count == 0 )
            {
                var currentStatus = _incremental.GetStatus( );
                if ( string.Equals( currentStatus.ReconciliationReason, IndexingConstants.ExtractorChangedReason, StringComparison.Ordinal ) )
                {
                    await ReindexStaleExtractorPathsAsync( cancellationToken )
                        .ConfigureAwait( false );
                }
                else
                {
                    await ReindexCoreAsync( progress: null, cancellationToken )
                        .ConfigureAwait( false );
                }
            }
            else
            {
                var status = _incremental.GetStatus( );

                foreach ( var rootPath in affectedRoots )
                {
                    var root = status.Roots.SingleOrDefault( value => string.Equals( value.Path, rootPath, StringComparison.Ordinal ) );
                    if ( root is null )
                    {
                        continue;
                    }

                    await ReindexScopedCoreAsync( new IndexReindexScope { Kind = IndexReindexScopeKinds.Root, RootId = root.Id }, progress: null,
                            cancellationToken )
                        .ConfigureAwait( false );
                }

                _incremental.SavePolicyState( );
            }
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            // A newer full scan owns reconciliation. Keep the journal worker
            // alive and leave this snapshot unacknowledged until it completes.
            return false;
        }
        finally
        {
            StartBackgroundReindex( );
            _indexLock.Release( );
        }

        foreach ( var root in _options.Indexing.Roots )
        {
            _incremental.RetryUnwatchedDirectories( IndexRootPaths.Normalize( root.Path ), cancellationToken );
        }

        return true;
    }

    private async ValueTask ReindexStaleExtractorPathsAsync( CancellationToken cancellationToken )
    {
        const int stalePathBatchSize = 256;

        while ( true )
        {
            var paths = await _catalog
                .GetPathsRequiringExtractorVersionAsync( ContentExtractor.ExtractorVersion, stalePathBatchSize, cancellationToken )
                .ConfigureAwait( false );

            if ( paths.Count == 0 )
            {
                return;
            }

            List<IndexedPathChange> changes = new( paths.Count );

            foreach ( var path in paths )
            {
                var items = await _files
                    .IndexPathAsync( path, cancellationToken )
                    .ConfigureAwait( false );

                changes.Add( new IndexedPathChange { Path = path, Items = items } );
            }

            await _catalog
                .ApplyPathChangesAsync( changes, cancellationToken )
                .ConfigureAwait( false );
        }
    }

    private async ValueTask<T> RunExclusiveAsync<T>( Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken )
    {
        ObjectDisposedException.ThrowIf( _disposed, this );

        await _indexLock
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            return await operation( cancellationToken )
                .ConfigureAwait( false );
        }
        finally
        {
            _indexLock.Release( );
        }
    }

    private async ValueTask<T> RunMutationExclusiveAsync<T>( Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken )
    {
        ObjectDisposedException.ThrowIf( _disposed, this );

        await StartCoreAsync( reconcilePolicy: false, cancellationToken )
            .ConfigureAwait( false );

        EnsureCatalogMutationOwnership( );

        return await RunExclusiveAsync( async token =>
            {
                CancelFullScan( );

                return await operation( token )
                    .ConfigureAwait( false );
            }, cancellationToken )
            .ConfigureAwait( false );
    }

    private void EnsureCatalogMutationOwnership( )
    {
        if ( !_incremental.CanMutateCatalog )
        {
            throw new InvalidOperationException( "Catalog mutation is owned by another CommandDeck runtime. " +
                                                 "Use the running daemon or stop it before running this command." );
        }
    }

    private sealed class Counter
    {
        public long Discovered;
        public long Indexed;
        public long Skipped;
        public long Failed;
    }

    private bool IsUnneededBackgroundScan( bool background )
    {
        return background && !_reindexPending;
    }

    private bool CannotQueueReconciliation( )
    {
        return _incremental.IsPausedUntilFullScan || _fullScanCancellation is not null;
    }

    private bool CannotRunReconciliation( )
    {
        return _incremental.IsPausedUntilFullScan ||
               _fullScanCancellation is not null ||
               !_incremental.GetStatus( ).ReconciliationRequired;
    }

    private static ScanProgress CreateFileProgress( IProgress<IndexProgress>? progress, Counter counter, Dictionary<string, long> rootCounts )
    {
        return new( value =>
        {
            if ( value is { Stage: IndexingConstants.RootCompleteStage, RootPath: not null } )
            {
                rootCounts[value.RootPath] = value.Indexed;
            }

            counter.Discovered = Math.Max( counter.Discovered, value.Discovered );
            counter.Indexed = Math.Max( counter.Indexed, value.Indexed );
            counter.Skipped = Math.Max( counter.Skipped, value.Skipped );
            counter.Failed = Math.Max( counter.Failed, value.Failed );
            progress?.Report( value );
        } );
    }

    private static IndexProgress CreateCompletionProgress( Counter counter )
    {
        return new IndexProgress
        {
            Stage = IndexProgressStages.Complete,
            Discovered = counter.Discovered,
            Indexed = counter.Indexed,
            Skipped = counter.Skipped,
            Failed = counter.Failed
        };
    }

    private static ReindexReport CreateGenerationReport( DateTimeOffset started, Counter counter, DateTimeOffset completed )
    {
        return new ReindexReport
        {
            StartedAt = started,
            CompletedAt = completed,
            Discovered = counter.Discovered,
            Indexed = counter.Indexed,
            Skipped = counter.Skipped,
            Failed = counter.Failed,
            Scope = IndexReindexScopeKinds.Full
        };
    }
}
