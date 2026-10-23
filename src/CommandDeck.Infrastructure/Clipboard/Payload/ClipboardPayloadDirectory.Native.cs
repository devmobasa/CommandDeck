using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CommandDeck;

public sealed partial class ClipboardPayloadDirectory
{
    // The directory and no-follow flags differ between x64 and arm64, so these are read at run time.
    private static readonly int DirectoryFlags = LinuxConstants.ODirectory | LinuxConstants.ONoFollow | LinuxConstants.OCloseOnExec;
    private static readonly int FileFlags = LinuxConstants.ONonBlocking | LinuxConstants.ONoFollow | LinuxConstants.OCloseOnExec;

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int OpenNative( string path, int flags );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int OpenAt( SafeFileHandle directory, string name, int flags, uint mode );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int MakeDirectoryAt( SafeFileHandle directory, string name, uint mode );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int RenameAt( SafeFileHandle source, string sourceName, SafeFileHandle destination, string destinationName, uint flags );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int UnlinkAt( SafeFileHandle directory, string name, int flags );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "fsync", SetLastError = true )]
    private static partial int FlushNative( SafeFileHandle descriptor );

    private static SafeFileHandle OpenDirectory( string path )
    {
        var fullPath = System.IO.Path.GetFullPath( path );
        var components = fullPath.Split( '/', StringSplitOptions.RemoveEmptyEntries );
        if ( components.Length == 0 )
        {
            throw new IOException( "Clipboard payload directory cannot be the filesystem root." );
        }

        SafeFileHandle current = new( OpenNative( "/", DirectoryFlags ), true );

        try
        {
            for ( var index = 0; index < components.Length; index++ )
            {
                var final = index == components.Length - 1;
                var created = false;

                if ( final )
                {
                    created = MakeDirectoryAt( current, components[index], PrivateDirectoryMode ) == 0;

                    if ( HasDirectoryCreationFailed( created ) )
                    {
                        throw new IOException( "Clipboard payload directory could not be created." );
                    }
                }

                var descriptor = OpenAt( current, components[index], DirectoryFlags, 0 );
                if ( descriptor < 0 )
                {
                    throw new IOException( "Clipboard payload directory cannot contain symbolic links or missing parents." );
                }

                SafeFileHandle next = new( descriptor, true );

                if ( HasDirectoryFlushFailed( created, current ) )
                {
                    next.Dispose( );

                    throw new IOException( "Clipboard payload directory creation could not be made durable." );
                }

                current.Dispose( );
                current = next;
            }

            RequirePrivateNode( current, true );

            return current;
        }
        catch
        {
            current.Dispose( );

            throw;
        }
    }

    private static string RequirePrivateNode( SafeFileHandle handle, bool expectedDirectory )
    {
        if ( !LinuxMountIdentity.TryGetNode( handle, out var identity, out var directory, out var links ) ||
             directory != expectedDirectory ||
             !directory && links != 1 ||
             !LinuxMountIdentity.TryGetOwnership( handle, out var user, out var mode ) ||
             user != LinuxMountIdentity.CurrentEffectiveUserId( ) ||
             mode != (expectedDirectory ? PrivateDirectoryMode : PrivateFileMode) )
        {
            throw new IOException( "Clipboard payload node is not an owned private directory or single-link regular file." );
        }

        return identity;
    }

    private static bool HasDirectoryCreationFailed( bool created )
    {
        return !created && Marshal.GetLastPInvokeError( ) != AlreadyExistsError;
    }

    private static bool HasDirectoryFlushFailed( bool created, SafeFileHandle current )
    {
        return created && FlushNative( current ) != 0;
    }
}
