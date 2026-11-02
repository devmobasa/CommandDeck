using System.Runtime.InteropServices;

namespace CommandDeck;

internal static partial class PrivateRuntimeDirectory
{
    private const int AtCurrentDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint RequiredMetadata = 0x0b; // STATX_TYPE | STATX_MODE | STATX_UID
    private const ushort FileTypeMask = 0xf000;
    private const ushort DirectoryType = 0x4000;
    private const ushort PermissionMask = 0xfff;
    private const ushort PrivatePermissions = 0x1c0; // 0700
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void Ensure( string path )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Private fallback socket directories require Linux." );
        }

        Directory.CreateDirectory( path, PrivateMode );
        var result = Statx( AtCurrentDirectory, path, AtSymlinkNoFollow, RequiredMetadata, out var entry );
        if ( result != 0 || !IsOwnedPrivateDirectory( entry ) )
        {
            throw new IOException( "The fallback socket directory must be an owned directory with mode 0700, without a symbolic link." );
        }
    }

    private static bool IsOwnedPrivateDirectory( Entry entry ) =>
        (entry.Mask & RequiredMetadata) == RequiredMetadata && (entry.Mode & FileTypeMask) == DirectoryType &&
        (entry.Mode & PermissionMask) == PrivatePermissions && entry.UserId == GetEffectiveUserId( );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "geteuid" )]
    private static partial uint GetEffectiveUserId( );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8 )]
    private static partial int Statx( int directory, string path, int flags, uint mask, out Entry entry );

    // Linux statx has a fixed, architecture-independent 256-byte layout.
    [StructLayout( LayoutKind.Explicit, Size = 256 )]
    private struct Entry
    {
        [FieldOffset( 0 )] public uint Mask;
        [FieldOffset( 20 )] public uint UserId;
        [FieldOffset( 28 )] public ushort Mode;
    }
}
