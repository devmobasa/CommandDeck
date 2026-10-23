using System.Text.Json;

namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    private const string OpenWithActionIdPrefix = "open-with:";

    public async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<CommandAction>>> GetOpenWithActionsAsync( IReadOnlyList<string> paths,
        int maximumHandlersPerFile, CancellationToken cancellationToken )
    {
        if ( paths.Count == 0 )
        {
            return new Dictionary<string, IReadOnlyList<CommandAction>>( StringComparer.Ordinal );
        }

        var handlerLimit = Math.Clamp( maximumHandlersPerFile, 1, 12 );

        var handlers = await ReadFileHandlersAsync( cancellationToken )
            .ConfigureAwait( false );

        var mimeTypes = await _mimeTypeResolver
            .ResolveManyAsync( paths
                .Distinct( StringComparer.Ordinal )
                .ToArray( ), cancellationToken )
            .ConfigureAwait( false );

        Dictionary<string, IReadOnlyList<CommandAction>> result = new( StringComparer.Ordinal );

        foreach ( var path in paths.Distinct( StringComparer.Ordinal ) )
        {
            var mimeType = mimeTypes.GetValueOrDefault( path );
            if ( mimeType is null )
            {
                result[path] = [];

                continue;
            }

            result[path] = ActionsForMimeType( handlers, mimeType, path, handlerLimit );
        }

        return result;
    }

    private static CommandAction[] ActionsForMimeType( IReadOnlyList<HandlerRecord> handlers, string mimeType, string path, int handlerLimit ) => handlers
        .Where( value => value.MimeTypes.Contains( mimeType, StringComparer.OrdinalIgnoreCase ) )
        .OrderBy( static value => value.Title, StringComparer.OrdinalIgnoreCase )
        .Take( handlerLimit )
        .Select( value => BuildOpenWithAction( value, path ) )
        .ToArray( );

    // Called after a commit that changes applications. The reader holds this lock
    // while it reads and caches, so a list read before the commit cannot be
    // cached after the invalidation.
    private async ValueTask InvalidateFileHandlersAsync( )
    {
        await _fileHandlerCacheLock
            .WaitAsync( CancellationToken.None )
            .ConfigureAwait( false );

        _fileHandlerCache = null;
        _fileHandlerCacheLock.Release( );
    }

    private async ValueTask<IReadOnlyList<HandlerRecord>> ReadFileHandlersAsync( CancellationToken cancellationToken )
    {
        if ( _fileHandlerCache is not null )
        {
            return _fileHandlerCache;
        }

        await _fileHandlerCacheLock
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            if ( _fileHandlerCache is not null )
            {
                return _fileHandlerCache;
            }

            await using var connection = await OpenAsync( cancellationToken )
                .ConfigureAwait( false );

            await using var command = connection.CreateCommand( );

            command.CommandText = """
                                  SELECT title, payload_json
                                  FROM catalog_items
                                  WHERE kind = $kind AND payload_json IS NOT NULL
                                  ORDER BY title COLLATE NOCASE, stable_id;
                                  """;

            command.Parameters.AddWithValue( "$kind", ResultKinds.Application );

            await using var reader = await command
                .ExecuteReaderAsync( cancellationToken )
                .ConfigureAwait( false );

            List<HandlerRecord> handlers = [];

            while ( await reader
                       .ReadAsync( cancellationToken )
                       .ConfigureAwait( false ) )
            {
                var payload = ReadHandlerPayload( reader.GetString( 1 ) );
                var mimeTypes = ReadHandlerMimeTypes( payload );
                if ( HasUsableHandlerPayload( mimeTypes, payload ) )
                {
                    handlers.Add( new HandlerRecord( reader.GetString( 0 ), payload, mimeTypes ) );
                }
            }

            _fileHandlerCache = handlers;

            return handlers;
        }
        finally
        {
            _fileHandlerCacheLock.Release( );
        }
    }

    private static string[] ReadHandlerMimeTypes( Dictionary<string, string> payload )
    {
        return payload
            .GetValueOrDefault( ResultMetadataKeys.Application.MimeTypes, string.Empty )
            .Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
    }

    private static bool HasUsableHandlerPayload( string[] mimeTypes, Dictionary<string, string> payload )
    {
        return mimeTypes.Length > 0 &&
               payload.ContainsKey( ResultMetadataKeys.Application.Exec ) &&
               payload.ContainsKey( ResultMetadataKeys.Application.DesktopId );
    }

    private static Dictionary<string, string> ReadHandlerPayload( string json )
    {
        Dictionary<string, string> values = new( StringComparer.Ordinal );

        try
        {
            using var document = JsonDocument.Parse( json );
            foreach ( var property in document.RootElement.EnumerateObject( ) )
            {
                if ( property.Value.ValueKind == JsonValueKind.String )
                {
                    values[property.Name] = property.Value.GetString( ) ?? string.Empty;
                }
            }
        }
        catch ( JsonException )
        {
        }

        return values;
    }

    private static CommandAction BuildOpenWithAction( HandlerRecord handler, string path )
    {
        Dictionary<string, string> arguments = CreateOpenWithArguments( handler, path );

        return new CommandAction
        {
            Id = $"{OpenWithActionIdPrefix}{handler.Payload[ResultMetadataKeys.Application.DesktopId]}",
            Title = $"Open With {handler.Title}",
            Kind = ActionKinds.LaunchDesktop,
            Arguments = arguments
        };
    }

    private static Dictionary<string, string> CreateOpenWithArguments( HandlerRecord handler, string path )
    {
        return new Dictionary<string, string>( handler.Payload, StringComparer.Ordinal )
        {
            [ResultMetadataKeys.Application.Name] = handler.Title,
            [ResultMetadataKeys.Application.LaunchArguments] = LaunchArgumentCodec.Encode( [path] )
        };
    }

    private sealed record HandlerRecord( string Title, Dictionary<string, string> Payload, IReadOnlyList<string> MimeTypes );
}

public interface IMimeTypeResolver
{
    ValueTask<IReadOnlyDictionary<string, string?>> ResolveManyAsync( IReadOnlyList<string> paths, CancellationToken cancellationToken );
}

public sealed class SharedMimeTypeResolver : IMimeTypeResolver
{
    private const int MaximumMimeTypeCharacters = 255;

    private static readonly TimeSpan MimeProbeTimeout = TimeSpan.FromMilliseconds( 750 );
    private const int MaximumMimeOutputBytes = 20 * ByteUnits.BytesPerKibibyte;
    private const int MaximumMimeErrorBytes = 4 * ByteUnits.BytesPerKibibyte;
    private const long MaximumMimeProcessMemoryBytes = 64L * ByteUnits.BytesPerMebibyte;
    private const int MaximumBatchSize = 64;
    private const int MaximumCacheEntries = 4096;

    // Least recently used paths leave first, so the rows a user keeps seeing stay
    // cached when a burst of new paths passes through.
    private readonly object _cacheGate = new( );
    private readonly Dictionary<string, LinkedListNode<MimeCacheEntry>> _cache = new( StringComparer.Ordinal );
    private readonly LinkedList<MimeCacheEntry> _recency = new( );
    private readonly int _capacity;

    public SharedMimeTypeResolver( ) : this( MaximumCacheEntries )
    {
    }

    internal SharedMimeTypeResolver( int capacity )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero( capacity );

        _capacity = capacity;
    }

    internal int CachedPathCount
    {
        get
        {
            lock ( _cacheGate )
            {
                return _cache.Count;
            }
        }
    }

    public async ValueTask<IReadOnlyDictionary<string, string?>> ResolveManyAsync( IReadOnlyList<string> paths, CancellationToken cancellationToken )
    {
        Dictionary<string, string?> resolved = new( StringComparer.Ordinal );
        List<(string Path, FileStamp Stamp)> missing = [];

        foreach ( var path in paths.Distinct( StringComparer.Ordinal ) )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            var stamp = ReadStamp( path );
            if ( TryGetCached( path, stamp, out var cached ) )
            {
                resolved[path] = cached;
            }
            else
            {
                missing.Add( (path, stamp) );
            }
        }

        if ( CanProbeMissingMimeTypes( missing ) )
        {
            foreach ( var batch in missing.Chunk( MaximumBatchSize ) )
            {
                var result = await ProcessRunner
                    .RunBoundedAsync( "file", ["--brief", "--mime-type", "--no-pad", "--", .. batch.Select( static item => item.Path )], workingDirectory: null,
                        MimeProbeTimeout,
                        new ProcessExecutionLimits
                        {
                            MaximumStandardOutputBytes = MaximumMimeOutputBytes,
                            MaximumStandardErrorBytes = MaximumMimeErrorBytes,
                            MaximumMemoryBytes = MaximumMimeProcessMemoryBytes,
                            ClearEnvironment = true
                        }, cancellationToken )
                    .ConfigureAwait( false );

                var lines = SplitMimeTypeOutput( result );

                for ( var index = 0; index < batch.Length; index++ )
                {
                    var mimeType = ReadProbedMimeType( result, index, lines );
                    resolved[batch[index].Path] = mimeType;
                    Remember( batch[index].Path, batch[index].Stamp, mimeType );
                }
            }
        }

        foreach ( (var path, var stamp) in missing )
        {
            if ( !resolved.ContainsKey( path ) )
            {
                resolved[path] = null;
                Remember( path, stamp, null );
            }
        }

        return resolved;
    }

    private static bool CanProbeMissingMimeTypes( List<(string Path, FileStamp Stamp)> missing )
    {
        return missing.Count > 0 && ProcessRunner.ExistsOnPath( "file" );
    }

    private static string[] SplitMimeTypeOutput( ProcessResult result )
    {
        return result
            .StandardOutput.Replace( "\r\n", "\n", StringComparison.Ordinal )
            .Split( '\n', StringSplitOptions.RemoveEmptyEntries );
    }

    private static string? ReadProbedMimeType( ProcessResult result, int index, string[] lines )
    {
        if ( result is { ExitCode: 0, TimedOut: false, AnyOutputLimitExceeded: false } && index < lines.Length )
        {
            return Normalize( lines[index] );
        }

        return null;
    }

    private bool TryGetCached( string path, FileStamp stamp, out string? mimeType )
    {
        lock ( _cacheGate )
        {
            if ( _cache.TryGetValue( path, out var node ) && node.Value.Stamp == stamp )
            {
                _recency.Remove( node );
                _recency.AddLast( node );
                mimeType = node.Value.MimeType;

                return true;
            }
        }

        mimeType = null;

        return false;
    }

    private void Remember( string path, FileStamp stamp, string? mimeType )
    {
        lock ( _cacheGate )
        {
            if ( _cache.Remove( path, out var replaced ) )
            {
                _recency.Remove( replaced );
            }

            _cache[path] = _recency.AddLast( new MimeCacheEntry( path, stamp, mimeType ) );

            while ( _cache.Count > _capacity )
            {
                var oldest = _recency.First!;
                _recency.RemoveFirst( );
                _cache.Remove( oldest.Value.Path );
            }
        }
    }

    private static string? Normalize( string value )
    {
        var mimeType = value.Trim( );
        if ( mimeType.Length is > 0 and <= MaximumMimeTypeCharacters &&
             mimeType.Contains( '/', StringComparison.Ordinal ) &&
             mimeType.All( static character => !char.IsControl( character ) && !char.IsWhiteSpace( character ) ) )
        {
            return mimeType;
        }

        return null;
    }

    private static FileStamp ReadStamp( string path )
    {
        try
        {
            FileInfo info = new( path );

            return new FileStamp( info.Exists, info.Length, info.LastWriteTimeUtc.Ticks );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException )
        {
            return default;
        }
    }

    private readonly record struct FileStamp( bool Exists, long Length, long ModifiedTicks );

    private sealed record MimeCacheEntry( string Path, FileStamp Stamp, string? MimeType );
}
