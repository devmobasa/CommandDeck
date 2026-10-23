using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CommandDeck;

public sealed record ClipboardPayloadFile( Guid Id, bool Staging, long Length );

/// <summary>Private payload operations remain relative to one retained directory handle.</summary>
public sealed partial class ClipboardPayloadDirectory : IDisposable
{
    internal const string StagingSuffix = ".stage";
    internal const string PayloadSuffix = ".blob";
    private const uint PrivateDirectoryMode = 0x1c0;
    private const uint PrivateFileMode = 0x180;
    private const int AlreadyExistsError = 17;
    private const int CreateFileFlag = 0x40;
    private const int ExclusiveFileFlag = 0x80;
    private const int GuidHexCharacters = 32;
    private const int MaximumPayloadFiles = 32768;
    private const int StreamBufferBytes = 4096;

    private readonly object _gate = new( );
    private readonly SafeFileHandle _directory;
    private readonly Dictionary<string, string> _identities = new( StringComparer.Ordinal );
    private readonly Dictionary<string, int> _leases = new( StringComparer.Ordinal );

    public ClipboardPayloadDirectory( string path )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Clipboard payload storage requires Linux directory handles." );
        }

        _directory = OpenDirectory( path );
    }

    internal long AvailableFreeSpace
    {
        get
        {
            lock ( _gate )
            {
                EnsureOpen( );

                return new DriveInfo( $"/proc/self/fd/{checked(( int ) _directory.DangerousGetHandle( ))}" ).AvailableFreeSpace;
            }
        }
    }

    public ClipboardPayloadLease CreateStaging( Guid id )
    {
        lock ( _gate )
        {
            EnsureOpen( );
            var name = Name( id, true );

            var descriptor = OpenAt( _directory, name, FileFlags | 1 | CreateFileFlag | ExclusiveFileFlag, PrivateFileMode );
            if ( descriptor < 0 )
            {
                throw new IOException( "Clipboard staging payload could not be created exclusively." );
            }

            SafeFileHandle handle = new( descriptor, true );

            try
            {
                ObserveIdentity( name, handle );
                FlushDirectory( );

                return Lease( name, handle, FileAccess.Write );
            }
            catch
            {
                handle.Dispose( );

                throw;
            }
        }
    }

    public ClipboardPayloadLease OpenRead( Guid id, bool staging = false )
    {
        lock ( _gate )
        {
            EnsureOpen( );
            var name = Name( id, staging );
            var handle = OpenExisting( name );
            if ( handle is null )
            {
                throw new IOException( "Clipboard payload is missing." );
            }

            return Lease( name, handle, FileAccess.Read );
        }
    }

    public void Publish( Guid id )
    {
        lock ( _gate )
        {
            EnsureOpen( );
            var staging = Name( id, true );
            var final = Name( id, false );
            RequireNoLeases( staging );
            RequireNoLeases( final );
            using var source = OpenExisting( staging );
            if ( source is null )
            {
                throw new IOException( "Clipboard staging payload is missing." );
            }

            if ( FlushNative( source ) != 0 )
            {
                throw new IOException( "Clipboard payload bytes could not be made durable." );
            }

            // RENAME_NOREPLACE: never overwrite a final payload, even after a crash.
            if ( RenameAt( _directory, staging, _directory, final, 1 ) != 0 )
            {
                throw new IOException( "Clipboard payload could not be published exclusively." );
            }

            _identities[final] = _identities[staging];
            _identities.Remove( staging );
            FlushDirectory( );
        }
    }

    public void Delete( Guid id, bool staging )
    {
        lock ( _gate )
        {
            EnsureOpen( );
            var name = Name( id, staging );
            RequireNoLeases( name );
            using var file = OpenExisting( name );
            if ( HasUnlinkFailed( file, name ) )
            {
                throw new IOException( "Clipboard payload cleanup could not unlink the owned file." );
            }

            // Absence also needs directory durability before the ledger releases its charge.
            FlushDirectory( );
            _identities.Remove( name );
        }
    }

    public IReadOnlyList<ClipboardPayloadFile> Inventory( )
    {
        lock ( _gate )
        {
            EnsureOpen( );
            var descriptorPath = $"/proc/self/fd/{checked(( int ) _directory.DangerousGetHandle( ))}";
            List<ClipboardPayloadFile> files = [];

            foreach ( var path in Directory.EnumerateFileSystemEntries( descriptorPath ) )
            {
                var name = Path.GetFileName( path );
                var staging = name.EndsWith( StagingSuffix, StringComparison.Ordinal );
                var suffix = staging ? StagingSuffix : PayloadSuffix;
                if ( !name.EndsWith( suffix, StringComparison.Ordinal ) ||
                     name.Length != GuidHexCharacters + suffix.Length ||
                     !Guid.TryParseExact( name.AsSpan( 0, GuidHexCharacters ), "N", out var id ) ||
                     id == Guid.Empty ||
                     name != Name( id, staging ) )
                {
                    throw new IOException( "Clipboard payload directory contains an unknown object; cleanup stopped." );
                }

                using var file = OpenExisting( name );
                if ( file is null )
                {
                    throw new IOException( "Clipboard payload inventory changed unexpectedly." );
                }

                files.Add( new( id, staging, RandomAccess.GetLength( file ) ) );

                if ( files.Count > MaximumPayloadFiles )
                {
                    throw new IOException( "Clipboard payload inventory exceeds its recovery bound." );
                }
            }

            return files.AsReadOnly( );
        }
    }

    private SafeFileHandle? OpenExisting( string name )
    {
        var descriptor = OpenAt( _directory, name, FileFlags, 0 );
        if ( descriptor < 0 )
        {
            if ( Marshal.GetLastPInvokeError( ) == 2 )
            {
                return null;
            }

            throw new IOException( "Clipboard payload could not be opened without following links." );
        }

        SafeFileHandle handle = new( descriptor, true );

        try
        {
            ObserveIdentity( name, handle );

            return handle;
        }
        catch
        {
            handle.Dispose( );

            throw;
        }
    }

    private void ObserveIdentity( string name, SafeFileHandle handle )
    {
        var identity = RequirePrivateNode( handle, false );
        if ( HasIdentityChanged( name, identity ) )
        {
            throw new IOException( "Clipboard payload identity changed unexpectedly." );
        }

        _identities[name] = identity;
    }

    private ClipboardPayloadLease Lease( string name, SafeFileHandle handle, FileAccess access )
    {
        try
        {
            LinuxRegularFileIo.UsePositionalIo( handle );
            var stream = new FileStream( handle, access, StreamBufferBytes, false );
            _leases[name] = _leases.GetValueOrDefault( name ) + 1;

            return new( stream, ( ) => ReleaseLease( name ) );
        }
        catch
        {
            handle.Dispose( );

            throw;
        }
    }

    private void ReleaseLease( string name )
    {
        lock ( _gate )
        {
            if ( --_leases[name] == 0 )
            {
                _leases.Remove( name );
            }
        }
    }

    private void RequireNoLeases( string name )
    {
        if ( _leases.ContainsKey( name ) )
        {
            throw new IOException( "Clipboard payload still has an active I/O lease." );
        }
    }

    private void FlushDirectory( )
    {
        if ( FlushNative( _directory ) != 0 )
        {
            throw new IOException( "Clipboard payload directory changes could not be made durable." );
        }
    }

    private void EnsureOpen( ) => ObjectDisposedException.ThrowIf( _directory.IsClosed, this );

    private static string Name( Guid id, bool staging )
    {
        if ( id == Guid.Empty )
        {
            throw new ArgumentException( "Clipboard payload identity must not be empty.", nameof( id ) );
        }

        return id.ToString( "N" ) + (staging ? StagingSuffix : PayloadSuffix);
    }

    public void Dispose( )
    {
        lock ( _gate )
        {
            _directory.Dispose( );
        }
    }

    private bool HasUnlinkFailed( SafeFileHandle? file, string name )
    {
        return file is not null && UnlinkAt( _directory, name, 0 ) != 0;
    }

    private bool HasIdentityChanged( string name, string identity )
    {
        return _identities.TryGetValue( name, out var known ) && known != identity;
    }
}

public sealed class ClipboardPayloadLease : IDisposable
{
    private Action? _release;
    private readonly SafeFileHandle _handle;
    public FileStream Stream { get; }

    internal ClipboardPayloadLease( FileStream stream, Action release )
    {
        Stream = stream;
        _handle = stream.SafeFileHandle;
        _release = release;
    }

    public void Dispose( )
    {
        var release = Interlocked.Exchange( ref _release, null );
        if ( release is null )
        {
            return;
        }

        try
        {
            Stream.Dispose( );
        }
        finally
        {
            _handle.Dispose( );
            release( );
        }
    }
}
