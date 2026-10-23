using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CommandDeck;

internal static partial class LinuxRegularFileIo
{
    internal static void UsePositionalIo( SafeFileHandle handle )
    {
        if ( !LinuxMountIdentity.TryGet( handle, out _ ) )
        {
            throw new IOException( "Positional I/O requires a verified regular file." );
        }

        // Keep O_NONBLOCK during open/type admission so a substituted FIFO cannot
        // block. Clear it before managed I/O caches IsAsync: .NET 11 interprets
        // that flag as nonblocking stream I/O and ignores positional offsets.
        var flags = FileControl( handle, 3, 0 );
        if ( !TryClearNonBlockingFlag( handle, flags ) )
        {
            throw new IOException( "Regular-file positional I/O could not be configured." );
        }
    }

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "fcntl", SetLastError = true )]
    private static partial int FileControl( SafeFileHandle descriptor, int command, int argument );

    private static bool TryClearNonBlockingFlag( SafeFileHandle handle, int flags )
    {
        return flags >= 0 &&
               ((flags & LinuxConstants.ONonBlocking) == 0 || FileControl( handle, LinuxConstants.SetStatusFlags, flags & ~LinuxConstants.ONonBlocking ) == 0);
    }
}
