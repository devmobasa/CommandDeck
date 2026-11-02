using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CommandDeck;

public static class UnixPeerIdentity
{
    private const int CredentialBytes = 12;
    private const int UserIdOffset = 4;
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    public static uint CurrentUserId( ) => LinuxMountIdentity.CurrentEffectiveUserId( );

    public static void Verify( Socket socket, uint expectedUserId )
    {
        ArgumentNullException.ThrowIfNull( socket );

        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Unix peer authentication requires Linux." );
        }

        Span<byte> credentials = stackalloc byte[CredentialBytes];
        var length = socket.GetRawSocketOption( SolSocket, SoPeerCred, credentials );
        if ( length < CredentialBytes || MemoryMarshal.Read<uint>( credentials[UserIdOffset..] ) != expectedUserId )
        {
            throw new IOException( "The Unix socket peer does not belong to the current user." );
        }
    }
}
