using System.Net.Sockets;
using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void AuditFallbackSocketMode( ) => AuditFallbackDirectory( false );

    private static void AuditFallbackSocketLink( ) => AuditFallbackDirectory( true );

    private static void AuditFallbackDirectory( bool link )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "Socket security requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-fallback-" );
        var previousRuntime = Environment.GetEnvironmentVariable( "XDG_RUNTIME_DIR" );
        var previousTemp = Environment.GetEnvironmentVariable( "TMPDIR" );
        try
        {
            Environment.SetEnvironmentVariable( "XDG_RUNTIME_DIR", null );
            Environment.SetEnvironmentVariable( "TMPDIR", root.FullName );
            var directory = Path.Combine( root.FullName, "commanddeck-" + Environment.UserName );
            if ( link )
            {
                var target = Directory.CreateDirectory( Path.Combine( root.FullName, "target" ) );
                Directory.CreateSymbolicLink( directory, target.FullName );
                Throws<IOException>( ( ) => PathExpansion.GetDefaultSocketPath( ) );
                True( Directory.Exists( target.FullName ) );
            }
            else
            {
                var socket = PathExpansion.GetDefaultSocketPath( );
                Equal( Path.Combine( directory, "commanddeck.sock" ), socket );
                Equal( UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode( directory ) );
                Equal( socket, PathExpansion.GetDefaultSocketPath( ) );
                File.SetUnixFileMode( directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite );
                Throws<IOException>( ( ) => PathExpansion.GetDefaultSocketPath( ) );
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable( "XDG_RUNTIME_DIR", previousRuntime );
            Environment.SetEnvironmentVariable( "TMPDIR", previousTemp );
            root.Delete( recursive: true );
        }
    }

    private static async Task AuditSocketPeerAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-peer-" );
        var path = SpecTemporaryFiles.SocketPath( root.FullName, "peer.sock" );
        try
        {
            using Socket listener = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
            listener.Bind( new UnixDomainSocketEndPoint( path ) );
            listener.Listen( 1 );
            using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 5 ) );
            await using var client = await UnixProtocolClient.ConnectAsync( path, deadline.Token );
            using var accepted = await listener.AcceptAsync( deadline.Token );
            var userId = UnixPeerIdentity.CurrentUserId( );
            UnixPeerIdentity.Verify( accepted, userId );
            Throws<IOException>( ( ) => UnixPeerIdentity.Verify( accepted, userId + 1 ) );

            await client.SendRawAsync( "peer fixture", deadline.Token );
            using NetworkStream input = new( accepted, ownsSocket: false );
            using StreamReader reader = new( input, Encoding.UTF8 );
            Equal( "peer fixture", await reader.ReadLineAsync( deadline.Token ) );
        }
        finally
        {
            File.Delete( path );
            root.Delete( recursive: true );
        }
    }
}
