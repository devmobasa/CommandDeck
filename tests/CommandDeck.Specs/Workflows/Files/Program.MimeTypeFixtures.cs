namespace CommandDeck.Specs;

public static partial class Program
{
    private sealed class FailingMimeTypeResolver : IMimeTypeResolver
    {
        public ValueTask<IReadOnlyDictionary<string, string?>> ResolveManyAsync( IReadOnlyList<string> paths, CancellationToken cancellationToken ) =>
            ValueTask.FromException<IReadOnlyDictionary<string, string?>>( new IOException( "fixture MIME failure" ) );
    }

    private sealed class RecordingMimeTypeResolver( string mimeType ) : IMimeTypeResolver
    {
        private readonly object _gate = new( );
        private readonly HashSet<string> _requested = new( StringComparer.Ordinal );

        public HashSet<string> RequestedPaths
        {
            get
            {
                lock ( _gate )
                {
                    return [.. _requested];
                }
            }
        }

        public ValueTask<IReadOnlyDictionary<string, string?>> ResolveManyAsync( IReadOnlyList<string> paths, CancellationToken cancellationToken )
        {
            lock ( _gate )
            {
                _requested.UnionWith( paths );
            }

            return ValueTask.FromResult<IReadOnlyDictionary<string, string?>>( paths
                .Distinct( StringComparer.Ordinal )
                .ToDictionary( static path => path, _ => ( string? ) mimeType, StringComparer.Ordinal ) );
        }
    }

    // Answers at once until stalled. A stalled call waits for cancellation, like a
    // `file` process that is killed, or, when it ignores cancellation, for Release,
    // like a stat on a mount that stopped answering.
    private sealed class SlowMimeTypeResolver( bool honoursCancellation ) : IMimeTypeResolver
    {
        private readonly TaskCompletionSource _started = new( TaskCreationOptions.RunContinuationsAsynchronously );
        private readonly TaskCompletionSource _cancelled = new( TaskCreationOptions.RunContinuationsAsynchronously );
        private readonly TaskCompletionSource _released = new( TaskCreationOptions.RunContinuationsAsynchronously );
        private volatile bool _stalled;
        private int _calls;
        private int _running;

        public Task Started => _started.Task;

        public Task Cancelled => _cancelled.Task;

        public int Calls => Volatile.Read( ref _calls );

        public int Running => Volatile.Read( ref _running );

        public void Stall( ) => _stalled = true;

        public void Release( ) => _released.TrySetResult( );

        public async ValueTask<IReadOnlyDictionary<string, string?>> ResolveManyAsync( IReadOnlyList<string> paths, CancellationToken cancellationToken )
        {
            Interlocked.Increment( ref _calls );
            Interlocked.Increment( ref _running );

            try
            {
                if ( _stalled )
                {
                    _started.TrySetResult( );
                }

                if ( _stalled && honoursCancellation )
                {
                    await Task
                        .Delay( Timeout.InfiniteTimeSpan, cancellationToken )
                        .ConfigureAwait( false );
                }
                else if ( _stalled )
                {
                    await _released.Task.ConfigureAwait( false );
                }
            }
            catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
            {
                _cancelled.TrySetResult( );

                throw;
            }
            finally
            {
                Interlocked.Decrement( ref _running );
            }

            return paths
                .Distinct( StringComparer.Ordinal )
                .ToDictionary( static path => path, static _ => ( string? ) "text/plain", StringComparer.Ordinal );
        }
    }

    private static CommandDeckOptions SlowMimeOptions( string root ) => new( )
    {
        Storage = new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) },
        Ai = new AiOptions { Enabled = false, Providers = [] }
    };

    private static SearchRequest ReportSearch( ) => new( ) { Query = "report", Limit = 10 };

    // Row ids and action ids in order. Open With actions are left out of the
    // complete rows so they compare with rows that never received them.
    private static string DescribeRows( IReadOnlyList<SearchResult> rows, bool withOpenWith ) => string.Join( " | ", rows.Select( row => $"{row.Id}: " +
        string.Join( ",", row
            .Actions.Select( static action => action.Id )
            .Where( id => withOpenWith || !id.StartsWith( "open-with:", StringComparison.Ordinal ) ) ) ) );
}
