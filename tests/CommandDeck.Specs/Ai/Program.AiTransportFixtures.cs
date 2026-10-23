using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static HttpClient CreateClient( HttpMessageHandler handler, string baseUrl ) => new( handler )
    {
        BaseAddress = new Uri( baseUrl.TrimEnd( '/' ) + "/" )
    };

    private static async Task<string> ReadProviderTextAsync( IAiProvider provider, AiPrompt prompt )
    {
        StringBuilder output = new( );
        var request = provider.PrepareRequest( prompt );
        await foreach ( var value in provider.StreamAsync( request, default ) )
        {
            if ( value.Text is not null )
            {
                output.Append( value.Text );
            }
        }

        return output.ToString( );
    }

    private static HttpResponseMessage Json( HttpStatusCode status, string body ) => new( status )
    {
        Content = new StringContent( body, Encoding.UTF8, "application/json" )
    };

    private static HttpResponseMessage EventStream( string body ) => new( HttpStatusCode.OK )
    {
        Content = new StringContent( body, Encoding.UTF8, "text/event-stream" )
    };

    private sealed record RecordedAiRequest( string Authorization, string Body );

    private sealed class RecordingAiHandler( Func<HttpRequestMessage, HttpResponseMessage> responseFactory ) : HttpMessageHandler
    {
        public List<RecordedAiRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            var body = await ReadRequestBodyAsync( request, cancellationToken );

            Requests.Add( new RecordedAiRequest( request.Headers.Authorization?.ToString( ) ?? string.Empty, body ) );

            return responseFactory( request );
        }
    }

    private static async Task<string> ReadRequestBodyAsync( HttpRequestMessage request, CancellationToken cancellationToken )
    {
        if ( request.Content is null )
        {
            return string.Empty;
        }

        return await request.Content.ReadAsStringAsync( cancellationToken );
    }

    private sealed class FixedCredentialResolver( string value ) : IAiCredentialResolver
    {
        public ValueTask<AiCredentialLease?> ResolveAsync( AiCredentialOptions reference, CancellationToken cancellationToken ) =>
            ValueTask.FromResult<AiCredentialLease?>( new AiCredentialLease( Encoding.UTF8.GetBytes( value ) ) );
    }

    private sealed class MissingCredentialResolver : IAiCredentialResolver
    {
        public ValueTask<AiCredentialLease?> ResolveAsync( AiCredentialOptions reference, CancellationToken cancellationToken ) =>
            ValueTask.FromException<AiCredentialLease?>( new AiProviderException( AiErrorCategories.CredentialMissing,
                safeSummary: "The configured AI credential is unavailable." ) );
    }

    private sealed class CancellableAiStream( byte[] firstRead ) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException( );

        public override long Position
        {
            get => throw new NotSupportedException( );
            set => throw new NotSupportedException( );
        }

        public override ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default )
        {
            if ( !_sent )
            {
                _sent = true;

                firstRead
                    .AsSpan( )
                    .CopyTo( buffer.Span );

                return ValueTask.FromResult( firstRead.Length );
            }

            return WaitForCancellationAsync( cancellationToken );
        }

        private static async ValueTask<int> WaitForCancellationAsync( CancellationToken cancellationToken )
        {
            await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );

            return 0;
        }

        public override int Read( byte[] buffer, int offset, int count ) =>
            throw new NotSupportedException( );

        public override void Flush( ) => throw new NotSupportedException( );
        public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException( );
        public override void SetLength( long value ) => throw new NotSupportedException( );

        public override void Write( byte[] buffer, int offset, int count ) =>
            throw new NotSupportedException( );
    }

    private sealed class FixedTransportFactory( HttpMessageHandler handler ) : IAiHttpTransportFactory
    {
        public HttpClient Create( AiProviderOptions options ) => CreateClient( handler, options.BaseUrl );
    }

    private sealed class RecordingActionExecutor : IActionExecutor
    {
        public List<CommandAction> Actions { get; } = [];

        public ValueTask<ActionExecutionResult> ExecuteAsync( SearchResult result, CommandAction action, string query, CancellationToken cancellationToken )
        {
            Actions.Add( action );

            return ValueTask.FromResult( ActionExecutionResult.Success( ) );
        }
    }

    private sealed class NeverCompletingAiHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );

            throw new InvalidOperationException( "Unreachable after cancellation." );
        }
    }

    private sealed class CountingNeverCompletingAiHandler : HttpMessageHandler
    {
        private int _started;
        public int Started => Volatile.Read( ref _started );
        public TaskCompletionSource TwoRequestsStarted { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

        protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            if ( Interlocked.Increment( ref _started ) == 2 )
            {
                TwoRequestsStarted.TrySetResult( );
            }

            await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );

            throw new InvalidOperationException( "Unreachable after cancellation." );
        }
    }

    private sealed class TestAiTransportFactory : IAiHttpTransportFactory
    {
        public HttpClient Create( AiProviderOptions options ) => new( new TestAiHandler( ) ) { BaseAddress = new Uri( options.BaseUrl.TrimEnd( '/' ) + "/" ) };
    }

    private sealed class TestAiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken ) =>
            Task.FromResult( new HttpResponseMessage( System.Net.HttpStatusCode.NotFound ) );
    }

    private sealed class ChunkedReadStream( byte[] bytes, int chunkSize ) : MemoryStream( bytes )
    {
        public override ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default ) =>
            base.ReadAsync( buffer[..Math.Min( buffer.Length, chunkSize )], cancellationToken );
    }

    private sealed class NeverCompletingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException( );

        public override long Position
        {
            get => throw new NotSupportedException( );
            set => throw new NotSupportedException( );
        }

        public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default )
        {
            await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );

            return 0;
        }

        public override int Read( byte[] buffer, int offset, int count ) =>
            throw new NotSupportedException( );

        public override void Flush( ) => throw new NotSupportedException( );
        public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException( );
        public override void SetLength( long value ) => throw new NotSupportedException( );

        public override void Write( byte[] buffer, int offset, int count ) =>
            throw new NotSupportedException( );
    }

    private sealed class ScriptedOllamaServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cancellation = new( );
        private readonly Task _server;
        private readonly List<Task> _handlers = [];
        private readonly object _handlersLock = new( );
        private int _generationCount;

        private ScriptedOllamaServer( HttpListener listener, string baseUrl )
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _server = ServeAsync( );
        }

        public string BaseUrl { get; }
        public TaskCompletionSource GenerationStarted { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

        public static ValueTask<ScriptedOllamaServer> StartAsync( )
        {
            int port;

            using ( TcpListener reservation = new( IPAddress.Loopback, 0 ) )
            {
                reservation.Start( );
                port = (( IPEndPoint ) reservation.LocalEndpoint).Port;
            }

            var baseUrl = $"http://127.0.0.1:{port}/";
            HttpListener listener = new( );
            listener.Prefixes.Add( baseUrl );
            listener.Start( );

            return ValueTask.FromResult( new ScriptedOllamaServer( listener, baseUrl ) );
        }

        private async Task ServeAsync( )
        {
            try
            {
                while ( !_cancellation.IsCancellationRequested )
                {
                    var context = await _listener
                        .GetContextAsync( )
                        .WaitAsync( _cancellation.Token );

                    var handler = HandleAsync( context );

                    lock ( _handlersLock )
                    {
                        _handlers.Add( handler );
                    }
                }
            }
            catch ( Exception exception ) when ( exception is OperationCanceledException or HttpListenerException or IOException )
            {
            }
        }

        private async Task HandleAsync( HttpListenerContext context )
        {
            try
            {
                await context.Request.InputStream.CopyToAsync( Stream.Null, _cancellation.Token );
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/x-ndjson";
                context.Response.SendChunked = true;
                var generation = Interlocked.Increment( ref _generationCount );
                var payload = GetPayload( generation );
                var chunk = Encoding.UTF8.GetBytes( payload );
                await context.Response.OutputStream.WriteAsync( chunk, _cancellation.Token );
                await context.Response.OutputStream.FlushAsync( _cancellation.Token );

                if ( generation == 1 )
                {
                    GenerationStarted.TrySetResult( );
                    await Task.Delay( Timeout.InfiniteTimeSpan, _cancellation.Token );
                }
                else
                {
                    context.Response.Close( );
                }
            }
            catch ( Exception exception ) when ( exception is OperationCanceledException or HttpListenerException or IOException )
            {
            }

            static string GetPayload( int generation )
            {
                if ( generation == 1 )
                {
                    return "{\"model\":\"clear-model\",\"message\":{\"content\":\"partial\"}," + "\"done\":false}\n";
                }

                return "{\"model\":\"clear-model\",\"message\":{\"content\":\"complete\"}," +
                       "\"done\":false}\n" +
                       "{\"model\":\"clear-model\",\"message\":{\"content\":\"\"}," +
                       "\"done\":true,\"done_reason\":\"stop\"}\n";
            }
        }

        public async ValueTask DisposeAsync( )
        {
            _cancellation.Cancel( );
            _listener.Close( );
            await _server.ConfigureAwait( false );
            Task[] handlers;

            lock ( _handlersLock )
            {
                handlers = [.. _handlers];
            }

            await Task
                .WhenAll( handlers )
                .ConfigureAwait( false );

            _cancellation.Dispose( );
        }
    }
}
