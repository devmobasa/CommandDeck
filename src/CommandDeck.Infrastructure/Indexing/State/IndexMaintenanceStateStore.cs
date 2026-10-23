using System.Globalization;
using System.Text;

namespace CommandDeck;

internal sealed class IndexMaintenanceStateStore
{
    private const int StateFieldCount = 6;

    private readonly string _path;
    private readonly object _gate = new( );

    public IndexMaintenanceStateStore( string databasePath ) =>
        _path = PathExpansion.Expand( databasePath ) + ".index-maintenance";

    public PersistedIndexMaintenanceState Read( )
    {
        lock ( _gate )
        {
            if ( !File.Exists( _path ) )
            {
                return new PersistedIndexMaintenanceState( );
            }

            try
            {
                var fields = ReadStateFields( );
                if ( fields.Length != StateFieldCount )
                {
                    return new PersistedIndexMaintenanceState( );
                }

                return new PersistedIndexMaintenanceState
                {
                    LastStartedUnixMilliseconds = ParseNullable( fields[0] ),
                    LastCompletedUnixMilliseconds = ParseNullable( fields[1] ),
                    NextRunUnixMilliseconds = ParseNullable( fields[2] ),
                    LastError = IndexStateEncoding.Decode( fields[3] ),
                    RemovedStalePathCount = long.Parse( fields[4], NumberStyles.None, CultureInfo.InvariantCulture ),
                    LastRunSucceeded = fields[5] == "1"
                };
            }
            catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or FormatException )
            {
                return new PersistedIndexMaintenanceState( );
            }
        }
    }

    public void Write( PersistedIndexMaintenanceState state )
    {
        lock ( _gate )
        {
            var directory = Path.GetDirectoryName( _path )!;
            Directory.CreateDirectory( directory );
            var temporary = _path + AtomicFileNames.TemporarySuffix;
            FileStreamOptions options = new( ) { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };

            if ( OperatingSystem.IsLinux( ) )
            {
                options.UnixCreateMode = FilePermissions.PrivateFile;
            }

            using ( FileStream stream = new( temporary, options ) )
            {
                using ( StreamWriter writer = new( stream, Encoding.UTF8, leaveOpen: true ) )
                {
                    writer.Write( string.Join( '|', Format( state.LastStartedUnixMilliseconds ), Format( state.LastCompletedUnixMilliseconds ),
                        Format( state.NextRunUnixMilliseconds ), IndexStateEncoding.Encode( state.LastError ),
                        state.RemovedStalePathCount.ToString( CultureInfo.InvariantCulture ), state.LastRunSucceeded ? "1" : "0" ) );

                    writer.Flush( );
                    stream.Flush( flushToDisk: true );
                }
            }

            File.Move( temporary, _path, overwrite: true );

            if ( OperatingSystem.IsLinux( ) )
            {
                File.SetUnixFileMode( _path, FilePermissions.PrivateFile );
            }

            LinuxDirectoryDurability.FlushParent( _path );
        }
    }

    private static long? ParseNullable( string value )
    {
        if ( value.Length == 0 )
        {
            return null;
        }

        return long.Parse( value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture );
    }

    private static string Format( long? value ) =>
        value?.ToString( CultureInfo.InvariantCulture ) ?? string.Empty;

    private string[] ReadStateFields( )
    {
        return File
            .ReadAllText( _path, Encoding.UTF8 )
            .Trim( )
            .Split( '|' );
    }
}

internal sealed class PersistedIndexMaintenanceState
{
    public long? LastStartedUnixMilliseconds { get; init; }
    public long? LastCompletedUnixMilliseconds { get; init; }
    public long? NextRunUnixMilliseconds { get; init; }
    public string? LastError { get; init; }
    public long RemovedStalePathCount { get; init; }
    public bool LastRunSucceeded { get; init; }
}
