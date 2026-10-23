using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace CommandDeck.ExtensionProtocol;

/// <summary>A single-session duplex peer. Receiving and control traffic never await application handlers.</summary>
public sealed partial class ExtensionPeer : IAsyncDisposable
{
    private const int RateWindowMilliseconds = 1000;
    private const int ExtensionApplicationErrorCode = -32001;
    private const int CancelledErrorCode = -32800;
    private const int SessionErrorCode = -32000;
    private const int MaximumFramesPerWindow = 256;
    private const int MaximumBytesPerWindow = ByteUnits.BytesPerMebibyte;
    private const int InternalErrorCode = -32603;

    private static readonly JsonElement NullResult = JsonElement.Parse( "null" );
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly string _prefix;
    private readonly string _remotePrefix;
    private readonly HashSet<string> _allowedMethods;
    private readonly Func<ExtensionFrame, CancellationToken, ValueTask<JsonElement>> _handler;
    private readonly object _gate = new( );
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new( StringComparer.Ordinal );
    private readonly HashSet<string> _retired = new( StringComparer.Ordinal );
    private readonly HashSet<string> _sent = new( StringComparer.Ordinal );
    private readonly Dictionary<string, CancellationTokenSource> _incoming = new( StringComparer.Ordinal );
    private readonly Channel<ExtensionFrame> _requests = BoundedChannel( ExtensionLimits.MaximumQueuedRequests );
    private readonly Channel<ExtensionFrame> _control = BoundedChannel( 128 );
    private readonly Channel<ExtensionFrame> _outgoing = BoundedChannel( ExtensionLimits.MaximumPendingCalls );
    private readonly SemaphoreSlim _writeSignal = new( 0 );
    private readonly CancellationTokenSource _lifetime = new( );
    private Task _readTask = Task.CompletedTask;
    private Task _writeTask = Task.CompletedTask;
    private Task _dispatchTask = Task.CompletedTask;
    private int _started;
    private long _nextId;
    private long _lastRemoteId;
    private int _disposed;
    private int _errorData;

    public void EnableErrorData( ) => Interlocked.Exchange( ref _errorData, 1 );

    public ExtensionPeer( Stream input, Stream output, bool isHost, IEnumerable<string> allowedMethods,
        Func<ExtensionFrame, CancellationToken, ValueTask<JsonElement>> handler )
    {
        _input = input;
        _output = output;
        _prefix = isHost ? ExtensionLimits.HostIdPrefix : ExtensionLimits.ExtensionIdPrefix;
        _remotePrefix = isHost ? ExtensionLimits.ExtensionIdPrefix : ExtensionLimits.HostIdPrefix;
        _allowedMethods = new HashSet<string>( allowedMethods, StringComparer.Ordinal );
        _handler = handler;
    }

    public void Start( )
    {
        lock ( _gate )
        {
            ObjectDisposedException.ThrowIf( Volatile.Read( ref _disposed ) != 0, this );

            if ( _started != 0 )
            {
                return;
            }

            Volatile.Write( ref _started, 1 );
            _readTask = Task.Run( ReadLoopAsync );
            _writeTask = Task.Run( WriteLoopAsync );
            _dispatchTask = Task.Run( DispatchLoopAsync );
        }
    }

    public Exception? Fault { get; private set; }
    public Task Completion => _readTask;

    private static Channel<ExtensionFrame> BoundedChannel( int capacity ) =>
        Channel.CreateBounded<ExtensionFrame>( new BoundedChannelOptions( capacity )
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        } );

    public async ValueTask<JsonElement> CallAsync( string method, JsonElement? parameters, CancellationToken cancellationToken )
    {
        EnsureStarted( );
        cancellationToken.ThrowIfCancellationRequested( );
        string id;
        TaskCompletionSource<JsonElement> completion = new( TaskCreationOptions.RunContinuationsAsynchronously );

        lock ( _gate )
        {
            // Disposal stops the peer before releasing it, and callers' linked tokens observe that asynchronously.
            if ( _lifetime.IsCancellationRequested )
            {
                throw new OperationCanceledException( "The extension connection was closed." );
            }

            if ( _pending.Count == ExtensionLimits.MaximumPendingCalls )
            {
                throw new ExtensionRpcException( SessionErrorCode, "The pending request limit was reached." );
            }

            id = _prefix + (++_nextId).ToString( CultureInfo.InvariantCulture );
            _pending.Add( id, completion );

            if ( !_outgoing.Writer.TryWrite( new ExtensionFrame { Id = id, Method = method, Params = parameters } ) )
            {
                _pending.Remove( id );

                throw new ExtensionRpcException( SessionErrorCode, "The outgoing request queue is full." );
            }

            _writeSignal.Release( );
        }

        using var registration = cancellationToken.Register( ( ) => CancelOutgoing( id, cancellationToken ) );

        return await completion.Task.ConfigureAwait( false );
    }

    public void Notify( string method, JsonElement? parameters = null )
    {
        EnsureStarted( );
        QueueControl( new ExtensionFrame { Method = method, Params = parameters } );
    }

    private void EnsureStarted( )
    {
        if ( Volatile.Read( ref _started ) == 0 )
        {
            throw new InvalidOperationException( "Start the peer first." );
        }
    }

    private void CancelOutgoing( string id, CancellationToken token )
    {
        lock ( _gate )
        {
            if ( !_pending.Remove( id, out var completion ) )
            {
                return;
            }

            _retired.Add( id );
            completion.TrySetCanceled( token );

            if ( _retired.Count > ExtensionLimits.MaximumRetiredCalls )
            {
                Stop( new InvalidDataException( "The retired request limit was reached." ) );

                return;
            }

            if ( !_sent.Contains( id ) )
            {
                return;
            }

            QueueControl( CreateCancellationFrame( id ) );
        }
    }

    private void QueueControl( ExtensionFrame frame )
    {
        if ( _lifetime.IsCancellationRequested )
        {
            return;
        }

        if ( !_control.Writer.TryWrite( frame ) )
        {
            Stop( new InvalidDataException( "The extension control queue is full." ) );

            return;
        }

        _writeSignal.Release( );
    }

    private void CompleteOutgoing( ExtensionFrame frame )
    {
        lock ( _gate )
        {
            var id = frame.Id!;
            if ( !_sent.Remove( id ) )
            {
                throw new InvalidDataException( "The extension reply has no sent request." );
            }

            if ( _pending.Remove( id, out var completion ) )
            {
                if ( frame.Error is { } error )
                {
                    completion.TrySetException( new ExtensionRpcException( error.Code, error.Message, error.Data?.Code ) );
                }
                else
                {
                    completion.TrySetResult( frame.Result ?? NullResult );
                }
            }
            else if ( !_retired.Remove( id ) )
            {
                throw new InvalidDataException( "The extension reply is unsolicited or repeated." );
            }
        }
    }

    private async Task DispatchLoopAsync( )
    {
        try
        {
            await foreach ( var frame in _requests
                               .Reader.ReadAllAsync( _lifetime.Token )
                               .ConfigureAwait( false ) )
            {
                await DispatchAsync( frame )
                    .ConfigureAwait( false );
            }
        }
        catch ( OperationCanceledException ) when ( _lifetime.IsCancellationRequested ) { }
        catch ( Exception exception )
        {
            Stop( exception );
        }
    }

    private async ValueTask DispatchAsync( ExtensionFrame frame )
    {
        CancellationTokenSource? source = null;

        if ( frame.Id is { } requestId )
        {
            lock ( _gate )
            {
                source = _incoming[requestId];
            }
        }

        ExtensionFrame reply;

        try
        {
            var token = source?.Token ?? _lifetime.Token;
            token.ThrowIfCancellationRequested( );

            var result = await _handler( frame, token )
                .ConfigureAwait( false );

            token.ThrowIfCancellationRequested( );
            reply = new ExtensionFrame { Id = frame.Id, Result = result };
        }
        catch ( ExtensionApplicationException exception )
        {
            reply = CreateApplicationErrorFrame( frame, exception );
        }
        catch ( OperationCanceledException )
        {
            reply = CreateCancellationErrorFrame( frame );
        }
        catch ( Exception )
        {
            reply = new ExtensionFrame { Id = frame.Id, Error = new ExtensionError( InternalErrorCode, "The extension request failed." ) };
        }
        finally
        {
            if ( frame.Id is { } id )
            {
                lock ( _gate )
                {
                    _incoming.Remove( id );
                }

                source?.Dispose( );
            }
        }

        if ( frame.Id is not null )
        {
            QueueControl( reply );
        }
    }

    private async Task WriteLoopAsync( )
    {
        try
        {
            long windowStart = Environment.TickCount64;
            var bytesInWindow = 0;

            while ( !_lifetime.IsCancellationRequested )
            {
                await _writeSignal
                    .WaitAsync( _lifetime.Token )
                    .ConfigureAwait( false );

                if ( !TryReadOutgoingFrame( out var frame ) )
                {
                    continue;
                }

                if ( frame is { Method: not null, Id: { } requestId } )
                {
                    lock ( _gate )
                    {
                        if ( !_pending.ContainsKey( requestId ) )
                        {
                            _retired.Remove( requestId );

                            continue;
                        }

                        _sent.Add( requestId );
                    }
                }

                var bytes = ExtensionFraming.Serialize( frame );

                if ( Environment.TickCount64 - windowStart >= RateWindowMilliseconds )
                {
                    windowStart = Environment.TickCount64;
                    bytesInWindow = 0;
                }

                bytesInWindow += bytes.Length;

                if ( bytesInWindow > MaximumBytesPerWindow )
                {
                    throw new InvalidDataException( "The extension output rate limit was reached." );
                }

                await _output
                    .WriteAsync( bytes, _lifetime.Token )
                    .ConfigureAwait( false );

                await _output
                    .FlushAsync( _lifetime.Token )
                    .ConfigureAwait( false );
            }
        }
        catch ( Exception exception ) when ( exception is IOException
                                                or InvalidDataException
                                                or JsonException
                                                or ArgumentException
                                                or OperationCanceledException
                                                or ObjectDisposedException )
        {
            Stop( exception );
        }
    }

    private void Stop( Exception exception )
    {
        lock ( _gate )
        {
            if ( _lifetime.IsCancellationRequested )
            {
                return;
            }

            Fault = exception;
            _ = _lifetime.CancelAsync( );

            foreach ( var completion in _pending.Values )
            {
                completion.TrySetException( new IOException( "The extension connection closed.", exception ) );
            }

            _pending.Clear( );
            _requests.Writer.TryComplete( );
        }
    }

    public async ValueTask DisposeAsync( )
    {
        if ( Interlocked.Exchange( ref _disposed, 1 ) != 0 )
        {
            return;
        }

        Stop( new OperationCanceledException( "The extension connection was closed." ) );

        try
        {
            await _input
                .DisposeAsync( )
                .ConfigureAwait( false );

            if ( !ReferenceEquals( _input, _output ) )
            {
                await _output
                    .DisposeAsync( )
                    .ConfigureAwait( false );
            }

            try
            {
                await Task
                    .WhenAll( _readTask, _writeTask, _dispatchTask )
                    .WaitAsync( TimeSpan.FromSeconds( 2 ) )
                    .ConfigureAwait( false );
            }
            catch ( TimeoutException ) { }
        }
        finally
        {
            lock ( _gate )
            {
                foreach ( var source in _incoming.Values )
                {
                    source.Dispose( );
                }

                _incoming.Clear( );
            }

            _writeSignal.Dispose( );
            _lifetime.Dispose( );
        }
    }

    private bool TryReadOutgoingFrame( [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out ExtensionFrame? frame )
    {
        return _control.Reader.TryRead( out frame ) || _outgoing.Reader.TryRead( out frame );
    }

    private static ExtensionFrame CreateCancellationFrame( string id )
    {
        return new ExtensionFrame
        {
            Method = ExtensionMethods.CancelRequest,
            Params = JsonSerializer.SerializeToElement( new CancelRequest( id ), ExtensionJsonContext.Default.CancelRequest )
        };
    }

    private ExtensionFrame CreateApplicationErrorFrame( ExtensionFrame frame, ExtensionApplicationException exception )
    {
        return new ExtensionFrame
        {
            Id = frame.Id,
            Error = new( ExtensionApplicationErrorCode, exception.Message, Volatile.Read( ref _errorData ) != 0 ? new( exception.ErrorCode ) : null )
        };
    }

    private ExtensionFrame CreateCancellationErrorFrame( ExtensionFrame frame )
    {
        return new ExtensionFrame
        {
            Id = frame.Id,
            Error = new ExtensionError( CancelledErrorCode, "The request was cancelled.",
                Volatile.Read( ref _errorData ) != 0 ? new( ExtensionErrorCodes.Cancelled ) : null )
        };
    }
}
