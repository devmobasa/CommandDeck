using System.Runtime.InteropServices;

namespace CommandDeck;

internal static unsafe partial class InotifyNative
{
    internal const short PollIn = 0x1;

    internal const int NoSpaceError = 28;

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "inotify_init1", SetLastError = true )]
    internal static partial int Initialize( int flags );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "inotify_add_watch", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    internal static partial int AddWatch( int descriptor, string path, uint mask );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "inotify_rm_watch", SetLastError = true )]
    internal static partial int RemoveWatch( int descriptor, int watch );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "eventfd", SetLastError = true )]
    internal static partial int CreateEvent( uint initialValue, int flags );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "poll", SetLastError = true )]
    internal static partial int Poll( PollEntry* entries, nuint count, int timeoutMilliseconds );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "read", SetLastError = true )]
    internal static partial nint Read( int descriptor, byte* buffer, nuint count );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "write", SetLastError = true )]
    internal static partial nint Write( int descriptor, byte* buffer, nuint count );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "close" )]
    internal static partial int Close( int descriptor );

    [StructLayout( LayoutKind.Sequential )]
    internal struct PollEntry
    {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }
}
