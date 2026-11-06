using System.Net.Sockets;
using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task HyprlandSocketAnswersLikeHyprctlAsync( )
    {
        Equal( "/run/user/1/hypr/sig/.socket.sock", HyprlandSocket.RequestPath( static name => name switch
        {
            "HYPRLAND_INSTANCE_SIGNATURE" => "sig",
            "XDG_RUNTIME_DIR" => "/run/user/1",
            _ => null
        } ) );
        True( HyprlandSocket.RequestPath( static name => name == "XDG_RUNTIME_DIR" ? "/run/user/1" : null ) is null,
            "outside a Hyprland session there is no socket" );
        foreach ( var (instance, runtime) in new[] { ("../other", "/run/user/1"), ("a/b", "/run/user/1"), ("..", "/run/user/1"), ("sig", "run/user/1") } )
        {
            True( HyprlandSocket.RequestPath( name => name == "HYPRLAND_INSTANCE_SIGNATURE" ? instance : name == "XDG_RUNTIME_DIR" ? runtime : null ) is null,
                "the signature names one directory under an absolute runtime directory: " + instance + " " + runtime );
        }

        var directory = SpecTemporaryFiles.PathFor( $"commanddeck-hyprland-socket-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( directory );
        var path = SpecTemporaryFiles.SocketPath( directory );

        try
        {
            var timeout = TimeSpan.FromSeconds( 5 );
            True( await HyprlandSocket.RequestAsync( path, "j/clients", timeout, default ) is null, "a missing socket answers nothing" );

            using Socket listener = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
            listener.Bind( new UnixDomainSocketEndPoint( path ) );
            listener.Listen( );

            var answered = AnswerOnceAsync( listener, "[{\"address\":\"0x1\"}]" );
            Equal( "[{\"address\":\"0x1\"}]", await HyprlandSocket.RequestAsync( path, "j/clients", timeout, default ) );
            Equal( "j/clients", await answered );

            var silent = listener.AcceptAsync( );
            True( await HyprlandSocket.RequestAsync( path, "j/workspaces", TimeSpan.FromMilliseconds( 100 ), default ) is null,
                "a socket that does not answer in time answers nothing" );
            (await silent).Dispose( );

            var oversized = AnswerOnceAsync( listener, new string( 'x', 16 * 1024 * 1024 + 1 ) );
            True( await HyprlandSocket.RequestAsync( path, "j/clients", timeout, default ) is null, "an oversized reply is refused" );
            await oversized;

            using CancellationTokenSource cancelled = new( );
            await cancelled.CancelAsync( );
            await ThrowsAsync<OperationCanceledException>( async ( ) =>
                await HyprlandSocket.RequestAsync( path, "j/clients", timeout, cancelled.Token ) );
        }
        finally
        {
            File.Delete( path );
            Directory.Delete( directory, recursive: true );
        }
    }

    private static async Task HyprlandQueriesShareOneBudgetAsync( )
    {
        var runtime = Path.Combine( Path.GetDirectoryName( SpecTemporaryFiles.SocketPath( "hyprland-query" ) )!, "hq" + Guid.NewGuid( ).ToString( "N" )[..6] );
        var directory = Path.Combine( runtime, "hypr", "sig" );
        Directory.CreateDirectory( directory );
        var path = Path.Combine( directory, ".socket.sock" );
        var budget = TimeSpan.FromMilliseconds( 300 );
        TimeSpan? given = null;
        Task<string> Hyprctl( string request, TimeSpan remaining, CancellationToken cancellationToken )
        {
            given = remaining;

            return Task.FromResult( "hyprctl " + request );
        }

        Func<string, string?> Session( string runtimeDirectory ) => name => name switch
        {
            "HYPRLAND_INSTANCE_SIGNATURE" => "sig",
            "XDG_RUNTIME_DIR" => runtimeDirectory,
            _ => null
        };

        try
        {
            // Without a socket, hyprctl gets nearly the whole budget; a long one
            // keeps a scheduling pause from deciding the check.
            var whole = TimeSpan.FromSeconds( 10 );
            Equal( "hyprctl clients", await HyprlandSocket.QueryAsync( "clients", Session( runtime ), Hyprctl, whole, default ) );
            True( given > whole / 2, "hyprctl keeps the budget the socket did not use" );

            using Socket listener = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
            listener.Bind( new UnixDomainSocketEndPoint( path ) );
            listener.Listen( );
            given = null;
            var answered = AnswerOnceAsync( listener, "[]" );
            Equal( "[]", await HyprlandSocket.QueryAsync( "clients", Session( runtime ), Hyprctl, budget, default ) );
            Equal( "j/clients", await answered );
            True( given is null, "an answering socket needs no process" );

            // hyprctl asks the same socket, so one that used the budget leaves nothing to fall back to.
            var silent = listener.AcceptAsync( );
            Equal( "", await HyprlandSocket.QueryAsync( "workspaces", Session( runtime ), Hyprctl, budget, default ) );
            True( given is null, "a socket that did not answer in time leaves no time for hyprctl" );
            (await silent).Dispose( );

            // A path too long for a Unix socket falls back at once.
            Equal( "hyprctl clients", await HyprlandSocket.QueryAsync( "clients", Session( "/" + new string( 'r', 120 ) ), Hyprctl, budget, default ) );
        }
        finally
        {
            Directory.Delete( runtime, recursive: true );
        }
    }

    private static async Task<string> AnswerOnceAsync( Socket listener, string reply )
    {
        using var client = await listener.AcceptAsync( );
        var buffer = new byte[4096];
        var length = await client.ReceiveAsync( buffer, SocketFlags.None );

        try
        {
            await client.SendAsync( Encoding.UTF8.GetBytes( reply ), SocketFlags.None );
            client.Shutdown( SocketShutdown.Send );
        }
        catch ( SocketException )
        {
        }

        return Encoding.UTF8.GetString( buffer, 0, length );
    }
}
