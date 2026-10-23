using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static CommandDeck.LinuxConstants;

namespace CommandDeck;

public sealed partial class SafeRegularFile : IDisposable
{
    private const int WriteBufferBytes = 4096;

    private const uint RenameExchange = 2;

    private SafeRegularFile( SafeFileHandle handle, string canonicalPath, LinuxFileIdentity identity )
    {
        Handle = handle;
        CanonicalPath = canonicalPath;
        Identity = identity;
    }

    public SafeFileHandle Handle { get; }
    public string CanonicalPath { get; }
    internal LinuxFileIdentity Identity { get; }
    public long Length => checked(( long ) Identity.Size);

    public string ProcessDescriptorPath =>
        $"/proc/{Environment.ProcessId}/fd/{checked(( int ) Handle.DangerousGetHandle( ))}";

    public string IdentityToken => string.Create( System.Globalization.CultureInfo.InvariantCulture,
        $"{Identity.MountId}:{Identity.DeviceMajor}:{Identity.DeviceMinor}:{Identity.Inode}:" +
        $"{Identity.Size}:{Identity.ModificationSeconds}:{Identity.ModificationNanoseconds}:" +
        $"{Identity.ChangeSeconds}:{Identity.ChangeNanoseconds}" );

    public string ReplacementIdentityToken => string.Create( System.Globalization.CultureInfo.InvariantCulture,
        $"{Identity.MountId}:{Identity.DeviceMajor}:{Identity.DeviceMinor}:{Identity.Inode}:" +
        $"{Identity.Size}:{Identity.ModificationSeconds}:{Identity.ModificationNanoseconds}" );

    public static SafeRegularFile Open( string path, bool followLeafSymbolicLink = false )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Safe file identity requires the Linux file-descriptor contract." );
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath( path );
        }
        catch ( Exception exception ) when ( exception is ArgumentException or NotSupportedException or PathTooLongException )
        {
            throw new InvalidOperationException( "The file path is invalid." );
        }

        var descriptor = OpenNative( fullPath, OReadOnly | ONonBlocking | OCloseOnExec | (followLeafSymbolicLink ? 0 : ONoFollow) );
        if ( descriptor < 0 )
        {
            throw new InvalidOperationException( "The file cannot be opened safely." );
        }

        return FromDescriptor( descriptor );
    }

    public static SafeRegularFile OpenFromDirectory( string directory, string fileName )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Safe file identity requires the Linux file-descriptor contract." );
        }

        if ( IsInvalidLeafName( fileName ) )
        {
            throw new InvalidOperationException( "The file name is invalid." );
        }

        var fullDirectory = Path.TrimEndingDirectorySeparator( Path.GetFullPath( directory ) );

        var directoryDescriptor = OpenNative( fullDirectory, OReadOnly | ONonBlocking | ODirectory | OCloseOnExec | ONoFollow );
        if ( directoryDescriptor < 0 )
        {
            throw new InvalidOperationException( "The file directory cannot be opened safely." );
        }

        using SafeFileHandle directoryHandle = new( ( IntPtr ) directoryDescriptor, ownsHandle: true );

        var descriptor = OpenAtNative( directoryDescriptor, fileName, OReadOnly | ONonBlocking | OCloseOnExec | ONoFollow );
        if ( descriptor < 0 )
        {
            throw new InvalidOperationException( "The file cannot be opened safely." );
        }

        return FromDescriptor( descriptor );
    }

    public static FileStream CreatePrivateWriteStream( string path )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Private file creation requires the Linux file-mode contract." );
        }

        return new FileStream( path, CreatePrivateWriteOptions( ) );
    }

    public SafeRegularFile Duplicate( )
    {
        ObjectDisposedException.ThrowIf( Handle.IsClosed, this );

        var descriptor = DupNative( checked(( int ) Handle.DangerousGetHandle( )) );
        if ( descriptor < 0 )
        {
            throw new InvalidOperationException( "The file identity cannot be retained safely." );
        }

        return FromDescriptor( descriptor );
    }

    public static void ExchangePaths( string firstPath, string secondPath )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Atomic file replacement requires the Linux rename contract." );
        }

        if ( RenameAt2Native( AtCurrentDirectory, Path.GetFullPath( firstPath ), AtCurrentDirectory, Path.GetFullPath( secondPath ), RenameExchange ) != 0 )
        {
            throw new InvalidOperationException( "The files could not be exchanged safely." );
        }
    }

    private static SafeRegularFile FromDescriptor( int descriptor )
    {
        SafeFileHandle handle = new( ( IntPtr ) descriptor, ownsHandle: true );

        try
        {
            if ( !LinuxMountIdentity.TryGet( handle, out var identity ) )
            {
                throw new InvalidOperationException( "The selected path is not a regular file." );
            }

            LinuxRegularFileIo.UsePositionalIo( handle );
            var target = File.ResolveLinkTarget( $"/proc/self/fd/{descriptor}", returnFinalTarget: true );
            if ( IsInvalidLinkTarget( target ) )
            {
                throw new InvalidOperationException( "The file identity cannot be resolved safely." );
            }

            return new SafeRegularFile( handle, Path.TrimEndingDirectorySeparator( Path.GetFullPath( target.FullName ) ), identity );
        }
        catch
        {
            handle.Dispose( );

            throw;
        }
    }

    public bool HasSameIdentity( SafeRegularFile other ) =>
        Identity == other.Identity && string.Equals( CanonicalPath, other.CanonicalPath, StringComparison.Ordinal );

    public async ValueTask<int> ReadPrefixAsync( Memory<byte> buffer, CancellationToken cancellationToken )
    {
        var total = 0;
        while ( total < buffer.Length )
        {
            var read = await RandomAccess
                .ReadAsync( Handle, buffer[total..], total, cancellationToken )
                .ConfigureAwait( false );

            if ( read == 0 )
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public void Dispose( ) => Handle.Dispose( );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int OpenNative( string path, int flags );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "dup", SetLastError = true )]
    private static partial int DupNative( int descriptor );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int OpenAtNative( int directoryDescriptor, string path, int flags );

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int RenameAt2Native( int oldDirectoryDescriptor, string oldPath, int newDirectoryDescriptor, string newPath, uint flags );

    private static bool IsInvalidLeafName( string fileName )
    {
        return string.IsNullOrEmpty( fileName ) ||
               fileName is "." or ".." ||
               fileName.Contains( Path.DirectorySeparatorChar ) ||
               fileName.Contains( Path.AltDirectorySeparatorChar );
    }

    private static bool IsInvalidLinkTarget( [NotNullWhen( false )] FileSystemInfo? target )
    {
        return target is null || !Path.IsPathFullyQualified( target.FullName );
    }

    [SupportedOSPlatform( "linux" )]
    private static FileStreamOptions CreatePrivateWriteOptions( )
    {
        return new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = WriteBufferBytes,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
    }
}
