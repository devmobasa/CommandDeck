using System.Buffers;
using System.Text.Json;

namespace CommandDeck.ExtensionProtocol;

/// <summary>Strict, bounded JSONL framing. Overflow fails immediately without draining hostile input.</summary>
public sealed class ExtensionFraming( Stream input )
{
    private const int ReadBufferBytes = 4096;
    private const string ProtocolVersionProperty = "jsonrpc";
    private const int MaximumErrorCodeCharacters = 48;
    private const string IdProperty = "id";
    private const string MethodProperty = "method";
    private const string ResultProperty = "result";
    private const string ErrorProperty = "error";
    private const string ParamsProperty = "params";
    private const string DataProperty = "data";
    private const int MaximumRequestIdCharacters = 64;
    private const int MaximumMethodCharacters = 64;
    private const int MaximumErrorMessageCharacters = 512;

    // JSONL is carried only over the private RPC pipe, never embedded in HTML.
    // Keep base64 '+' characters literal so 128 KiB responses fit the frame budget.
    private static readonly ExtensionJsonContext WireJson = CreateWireJsonContext( );
    private readonly byte[] _buffer = new byte[ReadBufferBytes];
    private int _position;
    private int _length;
    public int LastFrameBytes { get; private set; }

    public async ValueTask<ExtensionFrame?> ReadAsync( CancellationToken cancellationToken )
    {
        ArrayBufferWriter<byte> line = new( ReadBufferBytes );

        while ( true )
        {
            if ( _position == _length )
            {
                _length = await input
                    .ReadAsync( _buffer, cancellationToken )
                    .ConfigureAwait( false );

                _position = 0;

                if ( _length == 0 )
                {
                    if ( line.WrittenCount != 0 )
                    {
                        throw new InvalidDataException( "An extension frame ended without a newline." );
                    }

                    return null;
                }
            }

            var newline = Array.IndexOf( _buffer, ( byte ) '\n', _position, _length - _position );
            var count = (newline < 0 ? _length : newline) - _position;
            if ( line.WrittenCount + count > ExtensionLimits.MaximumFrameBytes )
            {
                throw new InvalidDataException( "The extension frame exceeds its byte limit." );
            }

            line.Write( _buffer.AsSpan( _position, count ) );
            _position += count;

            if ( newline >= 0 )
            {
                _position++;
                LastFrameBytes = line.WrittenCount + 1;

                return Parse( line.WrittenMemory );
            }
        }
    }

    public static ExtensionFrame Parse( ReadOnlyMemory<byte> bytes )
    {
        if ( bytes.Length > ExtensionLimits.MaximumFrameBytes )
        {
            throw new InvalidDataException( "The extension frame exceeds its byte limit." );
        }

        // JsonDocument validates token syntax; strict decoding also rejects invalid UTF-8 inside strings.
        using var document = StrictJson.Parse( bytes, ExtensionLimits.MaximumDepth );
        var root = document.RootElement;
        if ( root.ValueKind != JsonValueKind.Object )
        {
            throw new InvalidDataException( "An extension frame must be an object." );
        }

        var frame = root.Deserialize( ExtensionJsonContext.Default.ExtensionFrame );
        if ( frame is null )
        {
            throw new InvalidDataException( "An extension frame is missing." );
        }

        if ( HasInvalidProtocolVersion( root, frame ) )
        {
            throw new InvalidDataException( "The extension JSON-RPC version is unsupported." );
        }

        ValidateEnvelope( root, frame );

        if ( HasInvalidErrorData( root ) )
        {
            throw new InvalidDataException( "Extension error data must be an object." );
        }

        if ( HasInvalidErrorCode( frame ) )
        {
            throw new InvalidDataException( "Invalid extension error data." );
        }

        return frame;
    }

    private static void ValidateEnvelope( JsonElement root, ExtensionFrame frame )
    {
        var hasId = root.TryGetProperty( IdProperty, out _ );
        if ( HasInvalidRequestId( frame, hasId ) )
        {
            throw new InvalidDataException( "An extension request ID is invalid." );
        }

        var hasMethod = root.TryGetProperty( MethodProperty, out _ );
        var hasResult = root.TryGetProperty( ResultProperty, out _ );
        var hasError = root.TryGetProperty( ErrorProperty, out _ );
        var hasParams = root.TryGetProperty( ParamsProperty, out _ );

        if ( hasMethod )
        {
            if ( HasInvalidMethodFrame( frame, hasResult, hasError, hasParams ) )
            {
                throw new InvalidDataException( "An extension request envelope is invalid." );
            }
        }
        else if ( HasInvalidResponseFrame( frame, hasId, hasResult, hasError, hasParams ) )
        {
            throw new InvalidDataException( "An extension response envelope is invalid." );
        }
    }

    public static byte[] Serialize( ExtensionFrame frame )
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes( frame, WireJson.ExtensionFrame );
        _ = Parse( bytes );
        var line = new byte[bytes.Length + 1];
        bytes.CopyTo( line, 0 );
        line[^1] = ( byte ) '\n';

        return line;
    }

    private static bool HasInvalidErrorData( JsonElement root )
    {
        return root.TryGetProperty( ErrorProperty, out var error ) &&
               error.TryGetProperty( DataProperty, out var errorData ) &&
               errorData.ValueKind != JsonValueKind.Object;
    }

    private static bool HasInvalidRequestId( ExtensionFrame frame, bool hasId )
    {
        return hasId &&
               (frame.Id is not { Length: > 2 and <= MaximumRequestIdCharacters } ||
                !(frame.Id.StartsWith( ExtensionLimits.HostIdPrefix, StringComparison.Ordinal ) ||
                  frame.Id.StartsWith( ExtensionLimits.ExtensionIdPrefix, StringComparison.Ordinal )) ||
                frame.Id.Any( c => !char.IsAsciiLetterOrDigit( c ) && c is not ':' and not '-' and not '_' ));
    }

    private static bool HasInvalidMethodFrame( ExtensionFrame frame, bool hasResult, bool hasError, bool hasParams )
    {
        return frame.Method is not { Length: > 0 and <= MaximumMethodCharacters } ||
               hasResult ||
               hasError ||
               hasParams && frame.Params?.ValueKind != JsonValueKind.Object;
    }

    private static bool HasInvalidResponseFrame( ExtensionFrame frame, bool hasId, bool hasResult, bool hasError, bool hasParams )
    {
        return !hasId || hasParams || hasResult == hasError || hasError && frame.Error is not { Message.Length: <= MaximumErrorMessageCharacters };
    }

    private static bool HasInvalidErrorCode( ExtensionFrame frame )
    {
        return frame.Error?.Data is { } data &&
               (data.Code is not { Length: > 0 and <= MaximumErrorCodeCharacters } ||
                data.Code[0] is < 'a' or > 'z' ||
                data.Code.Any( character => character is not (>= 'a' and <= 'z') and not '_' ));
    }

    private static bool HasInvalidProtocolVersion( JsonElement root, ExtensionFrame frame )
    {
        return !root.TryGetProperty( ProtocolVersionProperty, out _ ) || frame.Jsonrpc != ExtensionFrame.JsonRpcVersion;
    }

    private static ExtensionJsonContext CreateWireJsonContext( )
    {
        return new( new JsonSerializerOptions( ExtensionJsonContext.Default.Options )
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        } );
    }
}
