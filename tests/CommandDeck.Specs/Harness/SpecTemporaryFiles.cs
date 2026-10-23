using Microsoft.Data.Sqlite;

namespace CommandDeck.Specs;

public static partial class Program
{
    private sealed partial class SpecTemporaryFiles : IDisposable
    {
        private const int MaximumFileLabelCharacters = 8;
        private const int RandomSuffixCharacters = 12;
        private const int SocketDigestCharacters = 24;
        private const string SocketExtension = ".sock";
        private const string DefaultSocketName = "commanddeck.sock";
        private const string RunPrefix = "run-";
        private const string SocketDirectoryPrefix = "cds-";
        private const string LeaseFileName = "owner.lock";
        private const int StaleRunAgeDays = 1;

        private readonly string? _originalRoot = Environment.GetEnvironmentVariable( "COMMANDDECK_SPEC_TEMP_ROOT" );
        private readonly string? _originalTemp = Environment.GetEnvironmentVariable( "TMPDIR" );
        private readonly string? _originalSqliteTemp = Environment.GetEnvironmentVariable( "SQLITE_TMPDIR" );
        private readonly string? _originalState = Environment.GetEnvironmentVariable( "XDG_STATE_HOME" );
        private readonly string? _originalSockets = Environment.GetEnvironmentVariable( "COMMANDDECK_SPEC_SOCKET_ROOT" );
        private readonly string _directory;
        private readonly FileStream _lease;
        private int _disposed;

        private SpecTemporaryFiles( string directory, string sockets, FileStream lease )
        {
            _directory = directory;
            _lease = lease;
            Environment.SetEnvironmentVariable( "COMMANDDECK_SPEC_SOCKET_ROOT", sockets );
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Environment.SetEnvironmentVariable( "COMMANDDECK_SPEC_TEMP_ROOT", directory );
            Environment.SetEnvironmentVariable( "TMPDIR", directory );
            Environment.SetEnvironmentVariable( "SQLITE_TMPDIR", directory );
            Environment.SetEnvironmentVariable( "XDG_STATE_HOME", Path.Combine( directory, "state" ) );
            Directory.CreateDirectory( Path.Combine( directory, "state" ) );
        }

        internal static SpecTemporaryFiles Create( )
        {
            if ( !OperatingSystem.IsLinux( ) )
            {
                throw new PlatformNotSupportedException( "The spec harness requires Linux." );
            }

            // SQLite files stay on disk. Socket names use a separate short root.
            var parent = Environment.GetEnvironmentVariable( "COMMANDDECK_SPEC_TEMP_ROOT" ) ?? Path.Combine( FindRepositoryRoot( ), "tmp" );

            Directory.CreateDirectory( parent );
            RecoverStaleRuns( parent );

            var id = Guid
                .NewGuid( )
                .ToString( "N" );

            var directory = Path.Combine( parent, RunPrefix + id );
            var sockets = Path.Combine( "/tmp", SocketDirectoryPrefix + id );

            Directory.CreateDirectory( directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

            var lease = new FileStream( Path.Combine( directory, LeaseFileName ), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None );

            try
            {
                Directory.CreateDirectory( sockets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
                Directory.CreateDirectory( Path.Combine( directory, ".git" ) );

                return new SpecTemporaryFiles( directory, sockets, lease );
            }
            catch
            {
                lease.Dispose( );
                Directory.Delete( directory, recursive: true );

                throw;
            }
        }

        internal static string PathFor( string label )
        {
            var name = Path
                .GetFileNameWithoutExtension( label )
                .Replace( "commanddeck-", "", StringComparison.Ordinal );

            var extension = Path.GetExtension( label );

            var file = name[..Math.Min( name.Length, MaximumFileLabelCharacters )] +
                       "-" +
                       Guid
                           .NewGuid( )
                           .ToString( "N" )[..RandomSuffixCharacters] +
                       extension;

            return extension == SocketExtension ? SocketPath( Path.GetTempPath( ), file ) : Path.Combine( Path.GetTempPath( ), file );
        }

        internal static DirectoryInfo CreateDirectory( string prefix, bool preservePrefix = false )
        {
            if ( preservePrefix )
            {
                var path = Path.Combine( Path.GetTempPath( ), prefix +
                                                              Guid
                                                                  .NewGuid( )
                                                                  .ToString( "N" )[..RandomSuffixCharacters] );

                return Directory.CreateDirectory( path );
            }

            return Directory.CreateDirectory( PathFor( prefix ) );
        }

        internal static DirectoryInfo CreateHomeDirectory( string prefix )
        {
            var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
            if ( Path
                .GetTempPath( )
                .StartsWith( home.TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar, StringComparison.Ordinal ) )
            {
                return CreateDirectory( prefix );
            }
            else
            {
                return Directory.CreateDirectory( Path.Combine( home, prefix +
                                                                      Guid
                                                                          .NewGuid( )
                                                                          .ToString( "N" ) ) );
            }
        }

        internal static void ReportStorageFailure( Exception exception )
        {
            if ( !HasStorageFailure( exception ) )
            {
                return;
            }

            foreach ( var path in new[] { Path.GetTempPath( ), Environment.GetEnvironmentVariable( "XDG_STATE_HOME" ), "/tmp" } )
            {
                if ( path is null )
                {
                    continue;
                }

                try
                {
                    var resolved = new DirectoryInfo( path ).ResolveLinkTarget( returnFinalTarget: true )
                                       ?.FullName ??
                                   path;

                    var drive = GetDrive( resolved );

                    Console.Error.WriteLine( $"SPEC_STORAGE path={path} resolved={resolved} filesystem={drive.Name} " +
                                             $"freeBytes={drive.AvailableFreeSpace} totalBytes={drive.TotalSize}" );

                    static DriveInfo GetDrive( string resolved )
                    {
                        return DriveInfo
                            .GetDrives( )
                            .Where( item => resolved.StartsWith( item.Name, StringComparison.Ordinal ) )
                            .OrderByDescending( item => item.Name.Length )
                            .First( );
                    }
                }
                catch ( Exception diagnosticError ) when ( diagnosticError is IOException or UnauthorizedAccessException )
                {
                    Console.Error.WriteLine( $"SPEC_STORAGE path={path} free-space unavailable: {diagnosticError.Message}" );
                }
            }
        }

        private static bool HasStorageFailure( Exception exception ) => exception is SqliteException { SqliteErrorCode: 10 or 13 } ||
                                                                        (exception.InnerException is not null && HasStorageFailure( exception.InnerException ));

        internal static string SocketPath( string directory, string name = DefaultSocketName )
        {
            var root = Environment.GetEnvironmentVariable( "COMMANDDECK_SPEC_SOCKET_ROOT" );
            if ( root is null )
            {
                throw new InvalidOperationException( "The spec socket root has not been initialized." );
            }

            var identity = System.Text.Encoding.UTF8.GetBytes( Path.Combine( directory, name ) );

            return Path.Combine( root, Convert
                                           .ToHexString( System.Security.Cryptography.SHA256.HashData( identity ) )[..SocketDigestCharacters] +
                                       SocketExtension );
        }

        internal static DirectoryInfo CreateSocketDirectory( string identity )
        {
            var path = Path.ChangeExtension( SocketPath( identity, "runtime" ), null );

            if ( OperatingSystem.IsLinux( ) )
            {
                return Directory.CreateDirectory( path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
            }
            else
            {
                return Directory.CreateDirectory( path );
            }
        }

        private void OnProcessExit( object? sender, EventArgs args ) => Dispose( );

        public void Dispose( )
        {
            if ( Interlocked.Exchange( ref _disposed, 1 ) != 0 )
            {
                return;
            }

            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            Environment.SetEnvironmentVariable( "COMMANDDECK_SPEC_TEMP_ROOT", _originalRoot );
            Environment.SetEnvironmentVariable( "COMMANDDECK_SPEC_SOCKET_ROOT", _originalSockets );
            Environment.SetEnvironmentVariable( "TMPDIR", _originalTemp );
            Environment.SetEnvironmentVariable( "SQLITE_TMPDIR", _originalSqliteTemp );
            Environment.SetEnvironmentVariable( "XDG_STATE_HOME", _originalState );

            try
            {
                DeleteOwnedTree( _directory );
            }
            catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
            {
                Console.Error.WriteLine( $"SPEC_STORAGE cleanup deferred: {exception.Message}" );
            }
            finally
            {
                _lease.Dispose( );
            }
        }
    }
}
