using System.Net.Sockets;
using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditSocketPathsAsync( )
    {
        using CancellationTokenSource stop = new( );
        var (runtime, server, root, _) = await StartAgentRuntimeAsync( new ScriptedAgentBackend( ), stop.Token );
        var path = SpecTemporaryFiles.SocketPath( root, "protected.sock" );
        try
        {
            await using ( await ConnectWithRetryAsync( SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" ) ) )
            {
            }

            await File.WriteAllTextAsync( path, "sentinel" );
            await RejectAsync( path );
            Equal( "sentinel", await File.ReadAllTextAsync( path ) );
            File.Delete( path );
            Directory.CreateDirectory( path );
            await RejectAsync( path );
            True( Directory.Exists( path ) );
            Directory.Delete( path );

            var target = path + ".target";
            await File.WriteAllTextAsync( target, "link target" );
            File.CreateSymbolicLink( path, target );
            await RejectAsync( path );
            Equal( target, new FileInfo( path ).LinkTarget );
            Equal( "link target", await File.ReadAllTextAsync( target ) );
            File.Delete( target );
            await RejectAsync( path );
            Equal( target, new FileInfo( path ).LinkTarget );
            File.Delete( path );

            await RejectAsync( SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" ) );
            using ( Socket stale = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified ) )
            {
                stale.Bind( new UnixDomainSocketEndPoint( path ) );
            }

            using CancellationTokenSource staleStop = new( TimeSpan.FromSeconds( 5 ) );
            UnixProtocolServer replacement = new( runtime, path );
            var serving = replacement.RunAsync( staleStop.Token );
            try
            {
                await using var client = await ConnectWithRetryAsync( path );
            }
            finally
            {
                staleStop.Cancel( );
                await serving;
            }
        }
        finally
        {
            stop.Cancel( );
            await server.Completion;
            await runtime.DisposeAsync( );
            File.Delete( path );
            Directory.Delete( root, recursive: true );
        }

        async Task RejectAsync( string socketPath )
        {
            using CancellationTokenSource timeout = new( TimeSpan.FromMilliseconds( 250 ) );
            UnixProtocolServer rejected = new( runtime, socketPath );
            await ThrowsAsync<InvalidOperationException>( ( ) => rejected.RunAsync( timeout.Token ) );
        }
    }

    private static async Task AuditNullShortcutFieldsAsync( )
    {
        using CancellationTokenSource stop = new( );
        var (runtime, server, root, _) = await StartAgentRuntimeAsync( new ScriptedAgentBackend( ), stop.Token );
        try
        {
            await using var client = await ConnectWithRetryAsync( SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" ) );
            long requestId = 0;
            foreach ( var field in new[] { "id", "chord", "description", "command" } )
            {
                var shortcut = new Dictionary<string, string?> { ["id"] = "fixture", [field] = null };
                var payload = JsonSerializer.Serialize( new { version = 3, requestId = ++requestId, operation = "shortcut.set", shortcut } );
                await client.SendRawAsync( payload, default );
                var rejected = await CollectUntilDoneAsync( client, requestId );
                True( rejected.Any( static message => message.Type == "error" ) );
                Equal( "done", rejected[^1].Type );
                await client.SendRawAsync( $$"""{"version":3,"requestId":{{++requestId}},"operation":"ping"}""", default );
                var ping = await CollectUntilDoneAsync( client, requestId );
                True( ping.All( static message => message.Type != "error" ) );
                Equal( "done", ping[^1].Type );
            }
        }
        finally
        {
            stop.Cancel( );
            await server.Completion;
            await runtime.DisposeAsync( );
            Directory.Delete( root, recursive: true );
        }
    }
}
