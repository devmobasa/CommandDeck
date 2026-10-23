using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ExtensionDuplexNestedRequestAsync( )
    {
        await using var pipes = await ExtensionTestPipes.CreateAsync( );
        using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 5 ) );

        await using ExtensionPeer host = new( pipes.Host, pipes.Host, true, ["storage.get"],
            ( _, _ ) => ValueTask.FromResult( JsonElement.Parse( "{\"value\":\"nested\"}" ) ) );

        host.Start( );

        TaskCompletionSource<ExtensionPeer> ready = new( TaskCreationOptions.RunContinuationsAsynchronously );

        await using ExtensionPeer child = new( pipes.Child, pipes.Child, false, [ExtensionMethods.CommandOpen],
            async ( _, ct ) => await (await ready.Task).CallAsync( "storage.get", JsonElement.Parse( "{}" ), ct ) );

        child.Start( );
        ready.SetResult( child );

        var result = await host.CallAsync( ExtensionMethods.CommandOpen, JsonElement.Parse( "{}" ), deadline.Token );

        Equal( "nested", result
            .GetProperty( "value" )
            .GetString( ) );
    }

    private static async Task ExtensionCancellationCrossingAsync( )
    {
        await using var pipes = await ExtensionTestPipes.CreateAsync( );
        using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 5 ) );
        await using ExtensionPeer host = NoRequestsPeer( pipes.Host );
        using CancellationTokenSource cancel = new( );

        var call = host
            .CallAsync( ExtensionMethods.CommandOpen, null, cancel.Token )
            .AsTask( );

        ExtensionFraming reader = new( pipes.Child );
        var request = (await reader.ReadAsync( deadline.Token ))!;
        await cancel.CancelAsync( );
        await ThrowsAsync<OperationCanceledException>( async ( ) => await call );
        var cancellation = (await reader.ReadAsync( deadline.Token ))!;
        Equal( "$/cancelRequest", cancellation.Method );
        var reply = new ExtensionFrame { Id = request.Id, Result = JsonElement.Parse( "{\"ok\":true}" ) };
        await pipes.Child.WriteAsync( ExtensionFraming.Serialize( reply ), deadline.Token );

        var next = host
            .CallAsync( ExtensionMethods.CommandOpen, null, deadline.Token )
            .AsTask( );

        var nextRequest = (await reader.ReadAsync( deadline.Token ))!;

        await pipes.Child.WriteAsync( ExtensionFraming.Serialize( reply with { Id = nextRequest.Id } ), deadline.Token );

        True( (await next)
            .GetProperty( "ok" )
            .GetBoolean( ) );

        True( host.Fault is null );
        await pipes.Child.WriteAsync( ExtensionFraming.Serialize( reply ), deadline.Token );
        await host.Completion.WaitAsync( deadline.Token );
        True( host.Fault is InvalidDataException );
    }

    private static async Task ExtensionCompletionBeforeCancellationAsync( )
    {
        await using var pipes = await ExtensionTestPipes.CreateAsync( );
        using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 5 ) );
        await using ExtensionPeer host = NoRequestsPeer( pipes.Host );
        using CancellationTokenSource cancel = new( );

        var call = host
            .CallAsync( ExtensionMethods.CommandOpen, null, cancel.Token )
            .AsTask( );

        var request = (await new ExtensionFraming( pipes.Child ).ReadAsync( deadline.Token ))!;

        await pipes.Child.WriteAsync( ExtensionFraming.Serialize( new ExtensionFrame { Id = request.Id, Result = JsonElement.Parse( "null" ) } ),
            deadline.Token );

        Equal( JsonValueKind.Null, (await call.WaitAsync( deadline.Token )).ValueKind );
        await cancel.CancelAsync( );
        True( host.Fault is null );
    }

    private static async Task ExtensionCancellationReachesHandlerAsync( )
    {
        await using var pipes = await ExtensionTestPipes.CreateAsync( );
        using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 5 ) );
        TaskCompletionSource entered = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource cancelled = new( TaskCreationOptions.RunContinuationsAsynchronously );
        await using ExtensionPeer host = NoRequestsPeer( pipes.Host );

        await using ExtensionPeer child = new( pipes.Child, pipes.Child, false, [ExtensionMethods.CommandOpen], async ( _, ct ) =>
        {
            entered.SetResult( );

            try
            {
                await Task.Delay( Timeout.Infinite, ct );
            }
            catch ( OperationCanceledException )
            {
                cancelled.SetResult( );

                throw;
            }

            return JsonElement.Parse( "{}" );
        } );

        child.Start( );
        using CancellationTokenSource cancel = new( );

        var call = host
            .CallAsync( ExtensionMethods.CommandOpen, null, cancel.Token )
            .AsTask( );

        await entered.Task.WaitAsync( deadline.Token );
        await cancel.CancelAsync( );
        await ThrowsAsync<OperationCanceledException>( async ( ) => await call );
        await cancelled.Task.WaitAsync( deadline.Token );
    }

    private static async Task ExtensionForgedFramesFaultAsync( )
    {
        string[] frames =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":\"h:77\",\"result\":{}}\n",
            "{\"jsonrpc\":\"2.0\",\"id\":\"e:1\",\"method\":\"runShell\"}\n",
            "{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":\"h:1\"}}\n"
        ];

        foreach ( var frame in frames )
        {
            await using var pipes = await ExtensionTestPipes.CreateAsync( );
            using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 5 ) );
            await using ExtensionPeer host = NoRequestsPeer( pipes.Host );
            await pipes.Child.WriteAsync( Encoding.UTF8.GetBytes( frame ), deadline.Token );
            await host.Completion.WaitAsync( deadline.Token );
            True( host.Fault is InvalidDataException );
        }
    }

    private static async Task ExtensionPendingCallsAreBoundedAsync( )
    {
        await using var pipes = await ExtensionTestPipes.CreateAsync( );
        using CancellationTokenSource cancel = new( );
        await using ExtensionPeer host = NoRequestsPeer( pipes.Host );

        var calls = Enumerable
            .Range( 0, ExtensionLimits.MaximumPendingCalls )
            .Select( _ => host
                .CallAsync( ExtensionMethods.CommandOpen, null, cancel.Token )
                .AsTask( ) )
            .ToArray( );

        await ThrowsAsync<ExtensionRpcException>( async ( ) => await host.CallAsync( ExtensionMethods.CommandOpen, null, cancel.Token ) );

        await cancel.CancelAsync( );

        foreach ( var call in calls )
        {
            await ThrowsAsync<OperationCanceledException>( async ( ) => await call );
        }
    }

    private static async Task ExtensionBacklogAllowsRepliesAsync( )
    {
        await using var pipes = await ExtensionTestPipes.CreateAsync( );
        using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 5 ) );
        TaskCompletionSource<ExtensionPeer> ready = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource completed = new( TaskCreationOptions.RunContinuationsAsynchronously );

        await using ExtensionPeer host = new( pipes.Host, pipes.Host, true, [ExtensionMethods.ViewsReplace], async ( frame, ct ) =>
        {
            if ( frame.Id == "e:1" )
            {
                await (await ready.Task).CallAsync( ExtensionMethods.CommandEvent, null, ct );
                completed.SetResult( );
            }

            return JsonElement.Parse( "{}" );
        } );

        host.Start( );
        ready.SetResult( host );

        for ( var i = 1; i <= 32; i++ )
        {
            await pipes.Child.WriteAsync( ExtensionFraming.Serialize( new ExtensionFrame { Id = $"e:{i}", Method = ExtensionMethods.ViewsReplace } ),
                deadline.Token );
        }

        ExtensionFraming reader = new( pipes.Child );
        ExtensionFrame? nested;

        do
        {
            nested = await reader.ReadAsync( deadline.Token );
        } while ( nested?.Method != ExtensionMethods.CommandEvent );

        await pipes.Child.WriteAsync( ExtensionFraming.Serialize( new ExtensionFrame { Id = nested.Id, Result = JsonElement.Parse( "{}" ) } ), deadline.Token );

        await completed.Task.WaitAsync( deadline.Token );
        True( host.Fault is null );
    }

    private static ExtensionPeer NoRequestsPeer( Stream stream )
    {
        ExtensionPeer peer = new( stream, stream, true, [], ( _, _ ) => throw new InvalidOperationException( "No request is expected." ) );

        peer.Start( );

        return peer;
    }

    private sealed class ExtensionTestPipes( TcpClient host, TcpClient child ) : IAsyncDisposable
    {
        public NetworkStream Host { get; } = host.GetStream( );
        public NetworkStream Child { get; } = child.GetStream( );

        public static async Task<ExtensionTestPipes> CreateAsync( )
        {
            using TcpListener listener = new( IPAddress.Loopback, 0 );
            listener.Start( );
            TcpClient host = new( );
            var accept = listener.AcceptTcpClientAsync( );
            await host.ConnectAsync( ( IPEndPoint ) listener.LocalEndpoint );

            return new ExtensionTestPipes( host, await accept );
        }

        public ValueTask DisposeAsync( )
        {
            host.Dispose( );
            child.Dispose( );

            return ValueTask.CompletedTask;
        }
    }
}
