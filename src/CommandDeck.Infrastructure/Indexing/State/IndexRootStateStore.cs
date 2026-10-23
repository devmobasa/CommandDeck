using System.Globalization;
using System.Text;

namespace CommandDeck;

internal sealed class IndexRootStateStore
{
    private const int StateFieldCount = 7;

    private const string PausedStateValue = "1";
    private const string RunningStateValue = "0";

    private readonly string _path;
    private readonly object _gate = new( );

    public IndexRootStateStore( string databasePath ) =>
        _path = PathExpansion.Expand( databasePath ) + ".index-roots";

    public IReadOnlyDictionary<string, PersistedIndexRootState> Read( )
    {
        lock ( _gate )
        {
            if ( !File.Exists( _path ) )
            {
                return new Dictionary<string, PersistedIndexRootState>( StringComparer.Ordinal );
            }

            try
            {
                return File
                    .ReadLines( _path, Encoding.UTF8 )
                    .Select( Deserialize )
                    .Where( static value => value is not null )
                    .Cast<PersistedIndexRootState>( )
                    .GroupBy( static value => value.Id, StringComparer.Ordinal )
                    .ToDictionary( static group => group.Key, static group => group.Last( ), StringComparer.Ordinal );
            }
            catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or FormatException )
            {
                return new Dictionary<string, PersistedIndexRootState>( StringComparer.Ordinal );
            }
        }
    }

    public void Write( IEnumerable<PersistedIndexRootState> states )
    {
        lock ( _gate )
        {
            var directory = Path.GetDirectoryName( _path )!;
            Directory.CreateDirectory( directory );
            var temporary = _path + AtomicFileNames.TemporarySuffix;

            FileStreamOptions options = new( )
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan
            };

            if ( OperatingSystem.IsLinux( ) )
            {
                options.UnixCreateMode = FilePermissions.PrivateFile;
            }

            using ( FileStream stream = new( temporary, options ) )
            {
                using ( StreamWriter writer = new( stream, Encoding.UTF8, leaveOpen: true ) )
                {
                    foreach ( var state in states.OrderBy( static value => value.Id, StringComparer.Ordinal ) )
                    {
                        writer.WriteLine( Serialize( state ) );
                    }

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

    private static string Serialize( PersistedIndexRootState state ) =>
        string.Join( '|', IndexStateEncoding.Encode( state.Id ), IndexStateEncoding.Encode( state.Path ), state.Paused ? PausedStateValue : RunningStateValue,
            state.PendingEventCount.ToString( CultureInfo.InvariantCulture ), state.IndexedItemCount.ToString( CultureInfo.InvariantCulture ),
            state.LastSuccessUnixMilliseconds?.ToString( CultureInfo.InvariantCulture ) ?? string.Empty, IndexStateEncoding.Encode( state.LastError ) );

    private static PersistedIndexRootState? Deserialize( string line )
    {
        var fields = line.Split( '|' );
        if ( CannotReadStateFields( fields, out var pending, out var indexed ) )
        {
            return null;
        }

        return new PersistedIndexRootState( IndexStateEncoding.Decode( fields[0] ) ?? string.Empty, IndexStateEncoding.Decode( fields[1] ) ?? string.Empty,
            fields[2] == PausedStateValue, pending, indexed, ParseLastSuccessTime( fields ), IndexStateEncoding.Decode( fields[6] ) );
    }

    private static bool CannotReadStateFields( string[] fields, out long pending, out long indexed )
    {
        pending = default;
        indexed = default;

        return fields.Length != StateFieldCount ||
               !long.TryParse( fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out pending ) ||
               !long.TryParse( fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out indexed ) ||
               fields[5].Length > 0 && !long.TryParse( fields[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _ );
    }

    private static long? ParseLastSuccessTime( string[] fields )
    {
        if ( fields[5].Length == 0 )
        {
            return null;
        }

        return long.Parse( fields[5], CultureInfo.InvariantCulture );
    }
}

internal sealed record PersistedIndexRootState(
    string Id,
    string Path,
    bool Paused,
    long PendingEventCount,
    long IndexedItemCount,
    long? LastSuccessUnixMilliseconds,
    string? LastError );
