using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

internal sealed partial class IncrementalIndexEngine
{
    private const int MinimumBatchLimit = 32;
    private const int MaximumBatchLimit = 16_384;

    private const string JournalWriteFailedReason = "event-journal-write-failed";

    private const string EventStormReason = "event-storm";
    private const string WatcherOverflowReason = "watcher-overflow";
    private const int MinimumDebounceMilliseconds = 20;
    private const int MaximumDebounceMilliseconds = 500;
    private const int MaximumEventLatencyMilliseconds = 950;

    private void OnUpsert( object sender, FileSystemEventArgs args ) =>
        Record( IncrementalEventKind.Upsert, args.FullPath, oldPath: null );

    private void OnDelete( object sender, FileSystemEventArgs args ) =>
        Record( IncrementalEventKind.Delete, args.FullPath, oldPath: null );

    private void OnRename( object sender, RenamedEventArgs args ) =>
        Record( IncrementalEventKind.Rename, args.FullPath, args.OldFullPath );

    private void RecordOverflow( string root ) =>
        Record( IncrementalEventKind.Overflow, root, oldPath: null );

    private void Record( IncrementalEventKind kind, string path, string? oldPath )
    {
        if ( kind != IncrementalEventKind.Overflow )
        {
            var scopes = GetRuleChangeScopes( path, oldPath );
            if ( scopes.Length > 0 )
            {
                foreach ( var scope in scopes )
                {
                    RecordAccepted( IncrementalEventKind.PolicyChanged, scope, null );
                    RefreshWatchedScope( scope );
                }

                // Refreshing the old rule scope does not index a rename's new
                // destination. Preserve both sides of the normal rename below.
                if ( kind != IncrementalEventKind.Rename )
                {
                    return;
                }
            }

            var accepted = _exclusions.ShouldObserve( path );
            var oldAccepted = oldPath is not null && _exclusions.ShouldObserve( oldPath );
            if ( IsOutsideAcceptedPaths( accepted, oldAccepted ) )
            {
                Interlocked.Increment( ref _ignoredEventCount );

                return;
            }

            if ( !accepted )
            {
                kind = IncrementalEventKind.Delete;
                path = oldPath!;
                oldPath = null;
            }
            else if ( HasRejectedOldPath( oldPath, oldAccepted ) )
            {
                kind = IncrementalEventKind.Upsert;
                oldPath = null;
            }
        }

        RecordAccepted( kind, path, oldPath );
    }

    // The journaled events not yet applied, oldest first.
    internal IReadOnlyList<IncrementalJournalEvent> SnapshotPendingEvents( ) => _journal.ReadPending( );

    private void RecordAccepted( IncrementalEventKind kind, string path, string? oldPath )
    {
        lock ( _statusLock )
        {
            var root = FindRoot( path );
            if ( root?.Paused == true )
            {
                root.PendingEventCount++;
                _lastEventAt = DateTimeOffset.UtcNow;
                PersistRootStatesUnderLock( );

                return;
            }
        }

        try
        {
            _journal.Append( kind, path, oldPath );

            lock ( _statusLock )
            {
                _lastEventAt = DateTimeOffset.UtcNow;
            }
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            RequireReconciliation( JournalWriteFailedReason );
            SetError( exception.Message );
        }

        Interlocked.Increment( ref _eventVersion );
        Signal( );
    }

    private async Task RunAsync( CancellationToken cancellationToken )
    {
        while ( !cancellationToken.IsCancellationRequested )
        {
            await _signal
                .WaitAsync( cancellationToken )
                .ConfigureAwait( false );

            await DebounceAsync( cancellationToken )
                .ConfigureAwait( false );

            CommitJournalWindow( );
            var batch = CapturePendingBatch( );
            if ( batch is not null )
            {
                await ProcessPendingAsync( batch, cancellationToken )
                    .ConfigureAwait( false );
            }
        }
    }

    // One disk flush covers every event recorded during the debounce window.
    private void CommitJournalWindow( )
    {
        try
        {
            _journal.Commit( );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            RequireReconciliation( JournalWriteFailedReason );
            SetError( exception.Message );
        }
    }

    private async ValueTask DebounceAsync( CancellationToken cancellationToken )
    {
        var debounceMilliseconds = GetDebounceMilliseconds( );
        var maximumLatencyMilliseconds = GetMaximumLatencyMilliseconds( debounceMilliseconds );
        var started = Environment.TickCount64;
        var observed = Volatile.Read( ref _eventVersion );

        while ( Environment.TickCount64 - started < maximumLatencyMilliseconds )
        {
            await Task
                .Delay( debounceMilliseconds, cancellationToken )
                .ConfigureAwait( false );

            var current = Volatile.Read( ref _eventVersion );
            if ( current == observed )
            {
                return;
            }

            observed = current;
        }
    }

    internal PendingIndexBatch? CapturePendingBatch( )
    {
        var batchLimit = Math.Clamp( _options.IncrementalEventBatchLimit, MinimumBatchLimit, MaximumBatchLimit );
        var pending = _journal.ReadPending( batchLimit + 1 );
        _maximumObservedBatchCount = Math.Max( _maximumObservedBatchCount, pending.Count );
        bool mustReconcile;

        lock ( _statusLock )
        {
            if ( _pausedUntilFullScan )
            {
                return null;
            }

            mustReconcile = _reconciliationRequired;
        }

        if ( NeedsBatchReconciliation( batchLimit, pending ) )
        {
            if ( !mustReconcile )
            {
                RequireReconciliation( GetBatchReconciliationReason( batchLimit ) );
            }

            mustReconcile = true;
        }

        return new PendingIndexBatch( pending, mustReconcile );
    }

    internal async ValueTask ProcessPendingAsync( PendingIndexBatch batch, CancellationToken cancellationToken )
    {
        var pending = batch.Events;

        if ( batch.MustReconcile )
        {
            await ReconcilePendingAsync( cancellationToken )
                .ConfigureAwait( false );

            return;
        }

        if ( pending.Count == 0 )
        {
            return;
        }

        Dictionary<string, bool> paths = new( StringComparer.Ordinal );

        foreach ( var change in pending )
        {
            switch ( change.Kind )
            {
                case IncrementalEventKind.Upsert:
                    paths[change.Path] = false;

                    break;
                case IncrementalEventKind.Delete:
                case IncrementalEventKind.PolicyChanged:
                    paths[change.Path] = true;

                    break;
                case IncrementalEventKind.Rename:
                    if ( change.OldPath is not null )
                    {
                        paths[change.OldPath] = true;
                    }

                    paths[change.Path] = false;

                    break;
            }
        }

        try
        {
            var applied = await _applyPaths( paths
                    .Select( static value => new IncrementalPathRequest( value.Key, value.Value ) )
                    .ToArray( ), cancellationToken )
                .ConfigureAwait( false );

            if ( !applied )
            {
                ScheduleRetry( cancellationToken );

                return;
            }

            SavePolicyState( );
            _journal.AdvanceCheckpoint( pending[^1].Sequence, ContentExtractor.ExtractorVersion );

            lock ( _statusLock )
            {
                _lastSuccessAt = DateTimeOffset.UtcNow;
                _lastError = null;
            }
        }
        catch ( Exception exception ) when ( exception is not OperationCanceledException )
        {
            SetError( exception.Message );
            ScheduleRetry( cancellationToken );
        }

        if ( _journal.PendingCount > 0 )
        {
            Signal( );
        }
    }

    internal sealed record PendingIndexBatch( IReadOnlyList<IncrementalJournalEvent> Events, bool MustReconcile );

    private static bool IsOutsideAcceptedPaths( bool accepted, bool oldAccepted )
    {
        return !accepted && !oldAccepted;
    }

    private static bool HasRejectedOldPath( [NotNullWhen( true )] string? oldPath, bool oldAccepted )
    {
        return oldPath is not null && !oldAccepted;
    }

    private bool NeedsBatchReconciliation( int batchLimit, IReadOnlyList<IncrementalJournalEvent> pending )
    {
        return _journal.PendingCount > batchLimit || pending.Any( static value => value.Kind == IncrementalEventKind.Overflow );
    }

    private string[] GetRuleChangeScopes( string path, string? oldPath )
    {
        return _exclusions
            .RuleChangeScopes( path )
            .Concat( oldPath is null ? [] : _exclusions.RuleChangeScopes( oldPath ) )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );
    }

    private int GetDebounceMilliseconds( )
    {
        return Math.Clamp( _options.IncrementalDebounceMilliseconds, MinimumDebounceMilliseconds, MaximumDebounceMilliseconds );
    }

    private int GetMaximumLatencyMilliseconds( int debounceMilliseconds )
    {
        return Math.Clamp( _options.IncrementalMaximumLatencyMilliseconds, debounceMilliseconds, MaximumEventLatencyMilliseconds );
    }

    private string GetBatchReconciliationReason( int batchLimit )
    {
        if ( _journal.PendingCount > batchLimit )
        {
            return EventStormReason;
        }

        return WatcherOverflowReason;
    }
}
