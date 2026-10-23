using System.Diagnostics;
using System.Text;

namespace CommandDeck;

public sealed class ClipboardCapture( ClipboardHistoryStore store, ClipboardPayloadProjection? projections = null ) : IAsyncDisposable
{
    private const int MaximumBootIdBytes = 32 * ByteUnits.BytesPerKibibyte;
    private const string SensitiveSelection = "sensitive";
    private const string NilSelection = "nil";
    private const string ClearedSelection = "clear";
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds( 1 );
    private static readonly TimeSpan MaximumRetry = TimeSpan.FromSeconds( 30 );

    private readonly CancellationTokenSource _shutdown = new( );
    private Task? _task;
    private Task? _retention;
    private Func<ProcessStartInfo>? _nativeStart;
    private ClipboardPayloadProjection? _projections = projections;
    private ClipboardServing? _serving;
    private ulong _hostEpoch;
    public string Status { get; private set; } = "Capture is paused";

    internal ClipboardCapture( ClipboardHistoryStore store, Func<ProcessStartInfo> nativeStart, ClipboardPayloadProjection? projections = null ) : this( store,
        projections )
    {
        _nativeStart = nativeStart;
    }

    public void Start( string executable, IReadOnlyList<string> prefix )
    {
        if ( _task is not null )
        {
            return;
        }

        var arguments = prefix.ToArray( );
        _projections ??= new( new ClipboardHtmlProjectionService( executable ) );

        _nativeStart ??= ( ) =>
        {
            ProcessStartInfo start = new( executable );

            foreach ( var argument in arguments )
            {
                start.ArgumentList.Add( argument );
            }

            start.ArgumentList.Add( DaemonChildProcessContract.ClipboardHost );
            start.ArgumentList.Add( DaemonChildProcessContract.Capture );

            return start;
        };

        _serving = new( _nativeStart );
        _retention = RetainAsync( _shutdown.Token );
        _task = WatchAsync( _shutdown.Token );
    }

    public ValueTask<ActionExecutionResult> ServeAsync( IReadOnlyList<ClipboardServeOffer> offers, CancellationToken token )
    {
        var serving = _serving;

        return ServeSelectionAsync( offers, serving, token );
    }

    // Sleeps until the store signals a policy change; only a helper that stopped by itself is retried on a timer.
    private async Task WatchAsync( CancellationToken cancellationToken )
    {
        var retry = FirstRetry;

        try
        {
            while ( true )
            {
                // Take the signal before reading the policy so a change in between still wakes the watcher.
                var change = store.NextCaptureChange;
                var consent = PublishPolicyStatus( );
                if ( consent == ClipboardConsent.None )
                {
                    await change
                        .WaitAsync( cancellationToken )
                        .ConfigureAwait( false );

                    retry = FirstRetry;

                    continue;
                }

                if ( await CaptureNativeAsync( consent, cancellationToken )
                        .ConfigureAwait( false ) )
                {
                    retry = FirstRetry;
                }

                if ( change.IsCompleted )
                {
                    retry = FirstRetry;

                    continue;
                }

                // The helper stopped by itself: retry after a growing pause, or at once on a policy change.
                try
                {
                    await change
                        .WaitAsync( retry, cancellationToken )
                        .ConfigureAwait( false );

                    retry = FirstRetry;
                }
                catch ( TimeoutException )
                {
                    retry = TimeSpan.FromTicks( Math.Min( retry.Ticks * 2, MaximumRetry.Ticks ) );
                }
            }
        }
        catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested ) { }
    }

    // Returns the consent to capture with, or None while capture must not run.
    private ClipboardConsent PublishPolicyStatus( )
    {
        var policy = store.Policy;
        Status = DescribePolicyStatus( policy );

        return store.Enabled ? policy.Consent : ClipboardConsent.None;
    }

    private string DescribePolicyStatus( ClipboardHistoryPolicy policy )
    {
        if ( policy.Consent == ClipboardConsent.None )
        {
            return "Capture is disabled";
        }

        if ( policy.Paused )
        {
            return "Capture is paused";
        }

        if ( store.MigrationReview.Required )
        {
            return "Clipboard migration review is required";
        }

        return policy.Consent == ClipboardConsent.Rich ? "Starting native rich capture" : "Starting native text capture";
    }

    // Expired history is removed whether or not capture runs, when the next entry expires.
    private async Task RetainAsync( CancellationToken cancellationToken )
    {
        try
        {
            while ( true )
            {
                await store
                    .PruneThenWaitForExpiryAsync( cancellationToken )
                    .ConfigureAwait( false );
            }
        }
        catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested ) { }
    }

    // Only a session that stayed configured for thirty seconds resets a repeated failure backoff.
    private async Task<bool> CaptureNativeAsync( ClipboardConsent consent, CancellationToken token )
    {
        var captureEpoch = store.CaptureEpoch;
        var watching = consent == ClipboardConsent.Rich ? "Capturing text and rich content" : "Capturing text";
        var hostEpoch = checked(++_hostEpoch);
        long? configuredAt = null;

        try
        {
            await using var helper = new ClipboardHostProcess( _nativeStart!( ), hostEpoch );

            using ClipboardNativeCaptureSession session = new( store, helper, hostEpoch, captureEpoch, consent, _projections,
                reason => Status = watching + " · last selection skipped: " + reason );

            await session
                .RunAsync( ( ) =>
                {
                    configuredAt = Stopwatch.GetTimestamp( );
                    Status = watching;
                }, token )
                .ConfigureAwait( false );
        }
        catch ( OperationCanceledException ) when ( token.IsCancellationRequested ) { }
        catch ( Exception exception ) when ( exception is IOException
                                                or InvalidOperationException
                                                or OperationCanceledException
                                                or System.ComponentModel.Win32Exception
                                                or System.Threading.Channels.ChannelClosedException )
        {
            Status = "Capture unavailable: native helper stopped; no wl-paste fallback was started";
        }

        return configuredAt is { } started && Stopwatch.GetElapsedTime( started ) >= MaximumRetry;
    }

    public async ValueTask DisposeAsync( )
    {
        await _shutdown
            .CancelAsync( )
            .ConfigureAwait( false );

        if ( _task is not null )
        {
            await _task.ConfigureAwait( false );
        }

        if ( _retention is not null )
        {
            await _retention.ConfigureAwait( false );
        }

        if ( _serving is not null )
        {
            await _serving
                .DisposeAsync( )
                .ConfigureAwait( false );
        }

        _shutdown.Dispose( );
    }

    public static async Task<int> WriteFrameAsync( Stream input, TextWriter output, string? state, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( input );
        ArgumentNullException.ThrowIfNull( output );

        if ( state is SensitiveSelection or NilSelection or ClearedSelection )
        {
            return 0;
        }

        var bytes = new byte[MaximumBootIdBytes + 1];
        var count = 0;
        while ( count < bytes.Length )
        {
            var read = await input
                .ReadAsync( bytes.AsMemory( count ), cancellationToken )
                .ConfigureAwait( false );

            if ( read == 0 )
            {
                break;
            }

            count += read;
        }

        if ( IsBootIdInvalid( count ) )
        {
            return 0;
        }

        try
        {
            _ = new UTF8Encoding( false, true ).GetString( bytes, 0, count );
        }
        catch ( DecoderFallbackException )
        {
            return 0;
        }

        await output
            .WriteLineAsync( Convert
                .ToBase64String( bytes, 0, count )
                .AsMemory( ), cancellationToken )
            .ConfigureAwait( false );

        return 0;
    }

    private static ValueTask<ActionExecutionResult> ServeSelectionAsync( IReadOnlyList<ClipboardServeOffer> offers, ClipboardServing? serving,
        CancellationToken token )
    {
        if ( serving is null )
        {
            return ValueTask.FromResult( ActionExecutionResult.Failure( "Clipboard serving has not started." ) );
        }
        else
        {
            return serving.ServeAsync( offers, token );
        }
    }

    private static bool IsBootIdInvalid( int count )
    {
        return count is 0 or > MaximumBootIdBytes;
    }
}
