using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Xml;

namespace CommandDeck;

public sealed class CurrencyRateService : IAsyncDisposable
{
    private const int DefaultRequestTimeoutSeconds = 5;
    private const int ConnectTimeoutSeconds = 5;
    private const string RatesUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private const int ReadBufferBytes = 4096;
    private const string CacheDateFormat = "yyyy-MM-dd";
    private const string CacheTimestampFormat = "O";
    private const int CacheRateFieldCount = 2;
    private const int RefreshIntervalHours = 6;
    private const int RetryDelayMinutes = 30;
    private const int CurrencyCodeCharacters = 3;

    private const int MaximumBytes = 64 * ByteUnits.BytesPerKibibyte;
    private const int MaximumXmlCharacters = 64 * 1024;
    private readonly object _gate = new( );
    private readonly string _cachePath;
    private readonly bool _fetchEnabled;
    private readonly TimeProvider _time;
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _shutdown = new( );
    private CurrencyRateSnapshot? _snapshot;
    private DateTimeOffset? _fetched;
    private DateTimeOffset? _failed;
    private Task _fetch = Task.CompletedTask;
    private bool _loaded;
    private bool _disposed;
    private Task? _dispose;

    public CurrencyRateService( string cacheFilePath, bool fetchEnabled, TimeProvider? timeProvider = null, HttpMessageHandler? handler = null,
        TimeSpan? requestTimeout = null )
    {
        _cachePath = cacheFilePath;
        _fetchEnabled = fetchEnabled;
        _time = timeProvider ?? TimeProvider.System;
        _timeout = requestTimeout ?? TimeSpan.FromSeconds( DefaultRequestTimeoutSeconds );
        _client = new( handler ?? CreateHandler( ) ) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // No redirects, cookies or trace headers: the fetch sends only the documented request.
    internal static SocketsHttpHandler CreateHandler( ) => new( )
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ActivityHeadersPropagator = null,
        ConnectTimeout = TimeSpan.FromSeconds( ConnectTimeoutSeconds )
    };

    public CurrencyRateSnapshot? GetRates( )
    {
        lock ( _gate )
        {
            if ( !_loaded )
            {
                _loaded = true;
                LoadCache( );
            }

            var now = _time.GetUtcNow( );
            if ( ShouldFetchRates( now ) )
            {
                _fetch = Task.Run( FetchAsync );
            }

            return _snapshot;
        }
    }

    internal Task WhenIdleAsync( )
    {
        lock ( _gate )
        {
            return _fetch;
        }
    }

    private async Task FetchAsync( )
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource( _shutdown.Token );
            timeout.CancelAfter( _timeout );
            using HttpRequestMessage request = new( HttpMethod.Get, RatesUrl );
            request.Headers.UserAgent.ParseAdd( "CommandDeck" );

            using var response = await _client.SendAsync( request, HttpCompletionOption.ResponseHeadersRead, timeout.Token );

            response.EnsureSuccessStatusCode( );

            if ( response.Content.Headers.ContentLength > MaximumBytes )
            {
                throw new FormatException( );
            }

            await using var stream = await response.Content.ReadAsStreamAsync( timeout.Token );
            using MemoryStream body = new( );
            var buffer = new byte[ReadBufferBytes];

            while ( true )
            {
                var read = await stream.ReadAsync( buffer.AsMemory( 0, Math.Min( buffer.Length, MaximumBytes + 1 - ( int ) body.Length ) ), timeout.Token );
                if ( read == 0 )
                {
                    break;
                }

                body.Write( buffer, 0, read );

                if ( body.Length > MaximumBytes )
                {
                    throw new FormatException( );
                }
            }

            body.Position = 0;
            var snapshot = ParseXml( body );
            var fetched = _time.GetUtcNow( );

            lock ( _gate )
            {
                _snapshot = snapshot;
                _fetched = fetched;
                _failed = null;
            }

            WriteCache( snapshot, fetched );
        }
        catch ( Exception )
        {
            lock ( _gate )
            {
                _failed = _time.GetUtcNow( );
            }
        }
    }

    private static CurrencyRateSnapshot ParseXml( Stream stream )
    {
        using var reader = CreateRateReader( stream );
        DateOnly? date = null;
        Dictionary<string, decimal> rates = new( StringComparer.Ordinal );

        while ( reader.Read( ) )
        {
            if ( IsNotRateElement( reader ) )
            {
                continue;
            }

            var time = reader.GetAttribute( "time" );
            if ( time is not null )
            {
                if ( CannotReadRateDate( date, time, out var parsed ) )
                {
                    throw new FormatException( );
                }

                date = parsed;
            }

            var code = reader.GetAttribute( "currency" );
            var rate = reader.GetAttribute( "rate" );
            if ( code is not null || rate is not null )
            {
                AddRate( rates, code, rate );
            }
        }

        if ( date is null )
        {
            throw new FormatException( );
        }

        if ( rates.Count == 0 )
        {
            throw new FormatException( );
        }

        return new( date.Value, rates.ToFrozenDictionary( StringComparer.Ordinal ) );
    }

    private static void AddRate( Dictionary<string, decimal> rates, string? code, string? text )
    {
        if ( !TryAddRate( rates, code, text ) )
        {
            throw new FormatException( );
        }
    }

    private void LoadCache( )
    {
        try
        {
            using FileStream stream = new( _cachePath, FileMode.Open, FileAccess.Read, FileShare.Read );
            if ( stream.Length > MaximumBytes )
            {
                return;
            }

            using StreamReader reader = new( stream, Encoding.UTF8 );
            var dateText = reader.ReadLine( );
            var fetchedText = reader.ReadLine( );
            if ( CannotReadCacheHeader( dateText, fetchedText, out var date, out var fetched ) )
            {
                return;
            }

            Dictionary<string, decimal> rates = new( StringComparer.Ordinal );

            while ( reader.ReadLine( ) is { } line )
            {
                var fields = line.Split( ' ' );
                if ( fields.Length != CacheRateFieldCount )
                {
                    return;
                }

                AddRate( rates, fields[0], fields[1] );
            }

            if ( rates.Count > 0 )
            {
                _snapshot = new( date, rates.ToFrozenDictionary( StringComparer.Ordinal ) );
                _fetched = fetched;
            }
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException ) { }
    }

    private void WriteCache( CurrencyRateSnapshot snapshot, DateTimeOffset fetched )
    {
        var directory = Path.GetDirectoryName( Path.GetFullPath( _cachePath ) )!;
        if ( !Directory.Exists( directory ) )
        {
            if ( OperatingSystem.IsLinux( ) )
            {
                Directory.CreateDirectory( directory, FilePermissions.PrivateFile | UnixFileMode.UserExecute );
            }
            else
            {
                Directory.CreateDirectory( directory );
            }
        }

        var temporary = _cachePath +
                        "." +
                        Guid
                            .NewGuid( )
                            .ToString( "N" ) +
                        AtomicFileNames.TemporarySuffix;

        try
        {
            FileStreamOptions options = new( ) { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };

            if ( OperatingSystem.IsLinux( ) )
            {
                options.UnixCreateMode = FilePermissions.PrivateFile;
            }

            using ( FileStream stream = new( temporary, options ) )
            {
                using StreamWriter writer = new( stream, new UTF8Encoding( false ), leaveOpen: true );
                writer.WriteLine( snapshot.Date.ToString( CacheDateFormat, CultureInfo.InvariantCulture ) );

                writer.WriteLine( fetched
                    .ToUniversalTime( )
                    .ToString( CacheTimestampFormat, CultureInfo.InvariantCulture ) );

                foreach ( var (code, rate) in snapshot.PerEuro.OrderBy( pair => pair.Key, StringComparer.Ordinal ) )
                {
                    writer.WriteLine( code + " " + rate.ToString( CultureInfo.InvariantCulture ) );
                }

                writer.Flush( );
                stream.Flush( flushToDisk: true );
            }

            File.Move( temporary, _cachePath, overwrite: true );

            if ( OperatingSystem.IsLinux( ) )
            {
                File.SetUnixFileMode( _cachePath, FilePermissions.PrivateFile );
            }

            LinuxDirectoryDurability.FlushParent( _cachePath );
        }
        finally
        {
            File.Delete( temporary );
        }
    }

    public ValueTask DisposeAsync( )
    {
        lock ( _gate )
        {
            if ( _dispose is null )
            {
                _disposed = true;
                _dispose = DisposeCoreAsync( _fetch );
            }

            return new( _dispose );
        }
    }

    private async Task DisposeCoreAsync( Task fetch )
    {
        await _shutdown.CancelAsync( );
        await fetch;
        _client.Dispose( );
        _shutdown.Dispose( );
    }

    private bool ShouldFetchRates( DateTimeOffset now )
    {
        return !_disposed &&
               _fetchEnabled &&
               _fetch.IsCompleted &&
               (_snapshot is null || _fetched is null || now - _fetched >= TimeSpan.FromHours( RefreshIntervalHours )) &&
               (_failed is null || now - _failed >= TimeSpan.FromMinutes( RetryDelayMinutes ));
    }

    private static bool IsNotRateElement( XmlReader reader )
    {
        return reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cube";
    }

    private static bool TryAddRate( Dictionary<string, decimal> rates, string? code, string? text )
    {
        return code is not null &&
               code.Length == CurrencyCodeCharacters &&
               !code.Any( c => c is < 'A' or > 'Z' ) &&
               decimal.TryParse( text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate ) &&
               rate > 0 &&
               rates.TryAdd( code, rate );
    }

    private static XmlReader CreateRateReader( Stream stream )
    {
        return XmlReader.Create( stream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlCharacters,
                MaxCharactersFromEntities = 0
            } );
    }

    private static bool CannotReadRateDate( DateOnly? date, string time, out DateOnly parsed )
    {
        parsed = default;

        return date is not null || !DateOnly.TryParseExact( time, CacheDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed );
    }

    private static bool CannotReadCacheHeader( string? dateText, string? fetchedText, out DateOnly date, out DateTimeOffset fetched )
    {
        date = default;
        fetched = default;

        return !DateOnly.TryParseExact( dateText, CacheDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date ) ||
               !DateTimeOffset.TryParseExact( fetchedText, CacheTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out fetched ) ||
               fetched.Offset != TimeSpan.Zero;
    }
}
