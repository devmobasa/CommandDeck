using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CommandDeck;

internal static partial class LinuxMountIdentity
{
    private const int MillisecondsPerSecond = 1000;
    private const int NanosecondsPerMillisecond = 1_000_000;

    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x00000001;
    private const uint StatxMode = 0x00000002;
    private const uint StatxModificationTime = 0x00000040;
    private const uint StatxChangeTime = 0x00000080;
    private const uint StatxInode = 0x00000100;
    private const uint StatxSize = 0x00000200;
    private const uint StatxBasicStats = 0x000007ff;
    private const uint StatxMountId = 0x00001000;
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryType = 0x4000;
    private const ushort SymbolicLinkType = 0xa000;

    internal static bool TryGetNode( SafeFileHandle handle, out string identity, out bool directory, out uint links )
    {
        var result = Statx( checked(( int ) handle.DangerousGetHandle( )), string.Empty, AtEmptyPath, StatxBasicStats | StatxMountId, out var buffer );

        directory = (buffer.Mode & FileTypeMask) == DirectoryType;
        links = buffer.LinkCount;
        identity = $"{buffer.MountId}:{buffer.DeviceMajor}:{buffer.DeviceMinor}:{buffer.Inode}";

        return result == 0 && (buffer.Mask & StatxMountId) != 0 && (directory || (buffer.Mode & FileTypeMask) == RegularFileType);
    }

    public static bool TryGet( string path, out ulong mountId )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            mountId = 0;

            return true;
        }

        var result = Statx( LinuxConstants.AtCurrentDirectory, path, flags: 0, StatxMountId, out var buffer );

        mountId = buffer.MountId;

        return result == 0 && (buffer.Mask & StatxMountId) != 0;
    }

    // One lstat-equivalent call: the entry itself, never a symbolic link's target.
    public static bool TryGetEntry( string path, out LinuxEntryStatus status )
    {
        status = default;

        if ( !OperatingSystem.IsLinux( ) )
        {
            return TryGetPortableEntry( path, out status );
        }

        var result = Statx( LinuxConstants.AtCurrentDirectory, path, AtSymlinkNoFollow, StatxType | StatxSize | StatxModificationTime | StatxMountId,
            out var buffer );

        const uint required = StatxType | StatxMountId;
        if ( IsMissingFileMetadata( result, buffer, required ) )
        {
            return false;
        }

        var type = buffer.Mode & FileTypeMask;

        try
        {
            // Native timestamps need not fit DateTime's calendar range. The
            // catalog stores Unix milliseconds, including for such entries.
            var modified = GetModificationMilliseconds( buffer );

            status = new LinuxEntryStatus( type == DirectoryType, type == SymbolicLinkType, checked(( long ) buffer.Size), modified, buffer.MountId );
        }
        catch ( OverflowException )
        {
            return false;
        }

        return true;
    }

    // Identifies the directory a path resolves to, following symbolic links.
    public static bool TryGetDirectory( string path, bool followSymbolicLinks, out LinuxDirectoryIdentity identity )
    {
        identity = default;

        if ( !OperatingSystem.IsLinux( ) )
        {
            return Directory.Exists( path );
        }

        var result = Statx( LinuxConstants.AtCurrentDirectory, path, followSymbolicLinks ? 0 : AtSymlinkNoFollow, StatxType | StatxInode | StatxMountId,
            out var buffer );

        const uint required = StatxType | StatxInode | StatxMountId;
        if ( IsMissingDirectoryMetadata( result, buffer, required ) )
        {
            return false;
        }

        identity = new LinuxDirectoryIdentity( buffer.MountId, buffer.DeviceMajor, buffer.DeviceMinor, buffer.Inode );

        return true;
    }

    // A cache key for policy inputs: any change to the entry's type, identity,
    // size, content time, or status time yields a different version.
    public static LinuxPathVersion GetVersion( string path )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return GetPortableVersion( path );
        }

        var result = Statx( LinuxConstants.AtCurrentDirectory, path, AtSymlinkNoFollow,
            StatxType | StatxMode | StatxInode | StatxSize | StatxModificationTime | StatxChangeTime, out var buffer );

        if ( result != 0 )
        {
            return LinuxPathVersion.Missing;
        }

        return new LinuxPathVersion( true, buffer.Mode, buffer.Inode, buffer.Size, buffer.ModificationTime.Seconds, buffer.ModificationTime.Nanoseconds,
            buffer.ChangeTime.Seconds, buffer.ChangeTime.Nanoseconds );
    }

    private static LinuxPathVersion GetPortableVersion( string path )
    {
        try
        {
            var attributes = File.GetAttributes( path );
            FileSystemInfo info = GetPortablePathInfo( path, attributes );
            var length = info is FileInfo file ? file.Length : 0;

            return new LinuxPathVersion( true, ( ushort ) attributes, 0, ( ulong ) length, info.LastWriteTimeUtc.Ticks, 0,
                info.CreationTimeUtc.Ticks, 0 );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            return LinuxPathVersion.Missing;
        }
    }

    private static bool TryGetPortableEntry( string path, out LinuxEntryStatus status )
    {
        status = default;

        try
        {
            var attributes = File.GetAttributes( path );
            var symbolicLink = (attributes & FileAttributes.ReparsePoint) != 0;
            var directory = !symbolicLink && (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo info = directory ? new DirectoryInfo( path ) : new FileInfo( path );

            status = new LinuxEntryStatus( directory, symbolicLink, info is FileInfo file && !symbolicLink ? file.Length : 0,
                new DateTimeOffset( info.LastWriteTimeUtc ).ToUnixTimeMilliseconds( ), 0 );

            return true;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            return false;
        }
    }

    public static bool TryGet( SafeFileHandle handle, out LinuxFileIdentity identity )
    {
        ArgumentNullException.ThrowIfNull( handle );

        identity = default;

        if ( IsUnavailableHandle( handle ) )
        {
            return false;
        }

        var descriptor = checked(( int ) handle.DangerousGetHandle( ));

        var result = Statx( descriptor, string.Empty, AtEmptyPath, StatxBasicStats | StatxMountId, out var buffer );
        if ( IsMissingRegularFileMetadata( result, buffer ) )
        {
            return false;
        }

        identity = new LinuxFileIdentity( buffer.MountId, buffer.DeviceMajor, buffer.DeviceMinor, buffer.Inode, buffer.Size, buffer.ModificationTime.Seconds,
            buffer.ModificationTime.Nanoseconds, buffer.ChangeTime.Seconds, buffer.ChangeTime.Nanoseconds );

        return true;
    }

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int Statx( int directoryFileDescriptor, string path, int flags, uint mask, out StatxBuffer buffer );

    [StructLayout( LayoutKind.Sequential )]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout( LayoutKind.Sequential )]
    private struct StatxBuffer
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint DeviceTypeMajor;
        public uint DeviceTypeMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
        public ulong Spare01;
        public ulong Spare02;
        public ulong Spare03;
        public ulong Spare04;
        public ulong Spare05;
        public ulong Spare06;
        public ulong Spare07;
        public ulong Spare08;
        public ulong Spare09;
        public ulong Spare10;
        public ulong Spare11;
        public ulong Spare12;
    }

    private static bool IsMissingFileMetadata( int result, StatxBuffer buffer, uint required )
    {
        return result != 0 || (buffer.Mask & required) != required;
    }

    private static bool IsMissingDirectoryMetadata( int result, StatxBuffer buffer, uint required )
    {
        return result != 0 || (buffer.Mask & required) != required || (buffer.Mode & FileTypeMask) != DirectoryType;
    }

    private static bool IsUnavailableHandle( SafeFileHandle handle )
    {
        return !OperatingSystem.IsLinux( ) || handle.IsInvalid || handle.IsClosed;
    }

    private static bool IsMissingRegularFileMetadata( int result, StatxBuffer buffer )
    {
        return result != 0 || (buffer.Mask & (StatxType | StatxMountId)) != (StatxType | StatxMountId) || (buffer.Mode & FileTypeMask) != RegularFileType;
    }

    private static long GetModificationMilliseconds( StatxBuffer buffer )
    {
        return checked(buffer.ModificationTime.Seconds * MillisecondsPerSecond + buffer.ModificationTime.Nanoseconds / NanosecondsPerMillisecond);
    }

    internal static FileSystemInfo GetPortablePathInfo( string path, FileAttributes attributes )
    {
        if ( (attributes & FileAttributes.Directory) != 0 )
        {
            return new DirectoryInfo( path );
        }

        return new FileInfo( path );
    }
}

internal readonly record struct LinuxFileIdentity(
    ulong MountId,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong Inode,
    ulong Size,
    long ModificationSeconds,
    uint ModificationNanoseconds,
    long ChangeSeconds,
    uint ChangeNanoseconds );

internal readonly record struct LinuxEntryStatus( bool Directory, bool SymbolicLink, long Size, long ModifiedUnixMilliseconds, ulong MountId );

internal readonly record struct LinuxDirectoryIdentity( ulong MountId, uint DeviceMajor, uint DeviceMinor, ulong Inode );

internal readonly record struct LinuxPathVersion(
    bool Exists,
    ushort Mode,
    ulong Inode,
    ulong Size,
    long ModificationSeconds,
    uint ModificationNanoseconds,
    long ChangeSeconds,
    uint ChangeNanoseconds )
{
    public static LinuxPathVersion Missing => default;
}
