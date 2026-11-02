using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CommandDeck;

public static class UnixSocketPath
{
    // Only a refused connection to an owned socket proves that its entry is stale.
    public static void Prepare( string path )
    {
        Directory.CreateDirectory( Path.GetDirectoryName( path )! );
        var entry = LinuxMountIdentity.ReadSocketEntry( path );
        if ( entry is null )
        {
            return;
        }

        try
        {
            using Socket probe = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
            probe.Connect( new UnixDomainSocketEndPoint( path ) );

            throw new InvalidOperationException( $"Another CommandDeck daemon is already listening on {path}." );
        }
        catch ( SocketException exception ) when ( exception.SocketErrorCode == SocketError.ConnectionRefused )
        {
            if ( LinuxMountIdentity.ReadSocketEntry( path ) != entry )
            {
                throw new InvalidOperationException( $"The socket entry changed while checking {path}; leaving it in place." );
            }

            File.Delete( path );
        }
        catch ( SocketException exception )
        {
            throw new InvalidOperationException( $"Cannot prove that the socket at {path} is stale; leaving it in place.", exception );
        }
    }
}

internal static partial class LinuxMountIdentity
{
    private const uint StatxUserId = 0x00000008;
    private const ushort SocketType = 0xc000;
    private const int MissingEntryError = 2;

    internal sealed record SocketEntry( ulong Inode, uint DeviceMajor, uint DeviceMinor, uint UserId );

    internal static SocketEntry? ReadSocketEntry( string path )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Safe Unix socket cleanup requires Linux." );
        }

        const uint required = StatxType | StatxInode | StatxUserId;
        var result = Statx( LinuxConstants.AtCurrentDirectory, path, AtSymlinkNoFollow, required, out var buffer );
        if ( result != 0 )
        {
            var error = Marshal.GetLastPInvokeError( );
            if ( error == MissingEntryError )
            {
                return null;
            }

            throw new InvalidOperationException( $"Cannot inspect the socket entry at {path}.", new Win32Exception( error ) );
        }

        if ( (buffer.Mask & required) != required || (buffer.Mode & FileTypeMask) != SocketType || buffer.UserId != CurrentEffectiveUserId( ) )
        {
            throw new InvalidOperationException( $"Refusing to replace {path}: it is not an owned Unix socket." );
        }

        return new( buffer.Inode, buffer.DeviceMajor, buffer.DeviceMinor, buffer.UserId );
    }
}
