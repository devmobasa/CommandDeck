using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CommandDeck;

internal static partial class LinuxMountIdentity
{
    private const ushort PermissionMask = 0x1FF;

    public static bool TryGetOwnership( SafeFileHandle handle, out uint userId, out ushort permissions )
    {
        ArgumentNullException.ThrowIfNull( handle );

        userId = 0;
        permissions = 0;

        if ( IsUnavailableHandle( handle ) )
        {
            return false;
        }

        var descriptor = checked(( int ) handle.DangerousGetHandle( ));
        var result = Statx( descriptor, string.Empty, AtEmptyPath, StatxBasicStats, out var buffer );
        if ( result != 0 )
        {
            return false;
        }

        userId = buffer.UserId;
        permissions = ( ushort ) (buffer.Mode & PermissionMask);

        return true;
    }

    public static uint CurrentEffectiveUserId( ) => GetEffectiveUserIdNative( );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "geteuid" )]
    private static partial uint GetEffectiveUserIdNative( );
}

internal static partial class LinuxMountIdentity
{
    public static bool TryGetPathIdentity( string path, out string identityToken )
    {
        identityToken = string.Empty;

        if ( !OperatingSystem.IsLinux( ) )
        {
            return false;
        }

        var result = Statx( LinuxConstants.AtCurrentDirectory, path, 0, StatxBasicStats | StatxMountId, out var buffer );
        if ( result != 0 )
        {
            return false;
        }

        identityToken = string.Create( System.Globalization.CultureInfo.InvariantCulture,
            $"{buffer.MountId}:{buffer.DeviceMajor}:{buffer.DeviceMinor}:{buffer.Inode}" );

        return true;
    }
}

public static class LinuxPathIdentity
{
    public static bool TryGet( string path, out string identityToken ) =>
        LinuxMountIdentity.TryGetPathIdentity( path, out identityToken );
}
