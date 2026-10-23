using System.Buffers;
using System.Text;

namespace CommandDeck;

public sealed class BoundedLineReader
{
    private const int ReadBufferBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new( encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true );

    private readonly Stream _stream;
    private readonly int _maximumBytes;
    private readonly byte[] _buffer = new byte[ReadBufferBytes];
    private int _position;
    private int _length;
    private bool _endOfInput;

    public BoundedLineReader( Stream stream, int maximumBytes )
    {
        ArgumentNullException.ThrowIfNull( stream );
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero( maximumBytes );

        _stream = stream;
        _maximumBytes = maximumBytes;
    }

    public async ValueTask<string?> ReadLineAsync( CancellationToken cancellationToken )
    {
        ArrayBufferWriter<byte> line = new( Math.Min( _maximumBytes, ReadBufferBytes ) );
        var overflow = false;
        var sawInput = false;
        while ( true )
        {
            if ( _position >= _length )
            {
                if ( _endOfInput )
                {
                    return !sawInput ? null : CompleteLine( line, overflow );
                }

                _length = await _stream
                    .ReadAsync( _buffer, cancellationToken )
                    .ConfigureAwait( false );

                _position = 0;

                if ( _length == 0 )
                {
                    _endOfInput = true;

                    continue;
                }
            }

            ReadOnlySpan<byte> available = _buffer.AsSpan( _position, _length - _position );
            var newline = available.IndexOf( ( byte ) '\n' );
            var count = newline >= 0 ? newline : available.Length;
            sawInput = sawInput || count > 0 || newline >= 0;

            if ( CanAppendToLine( line, overflow, count ) )
            {
                available[..count]
                    .CopyTo( line.GetSpan( count ) );

                line.Advance( count );
            }
            else if ( count > 0 )
            {
                overflow = true;
            }

            _position += count;

            if ( newline < 0 )
            {
                continue;
            }

            _position++;

            return CompleteLine( line, overflow );
        }
    }

    private string CompleteLine( ArrayBufferWriter<byte> line, bool overflow )
    {
        if ( overflow )
        {
            throw new PayloadTooLargeException( $"JSONL frame exceeds the {_maximumBytes}-byte UTF-8 limit." );
        }

        var bytes = line.WrittenSpan;
        if ( HasTrailingCarriageReturn( bytes ) )
        {
            bytes = bytes[..^1];
        }

        try
        {
            return StrictUtf8.GetString( bytes );
        }
        catch ( DecoderFallbackException exception )
        {
            throw new InvalidDataException( "JSONL frame is not valid UTF-8.", exception );
        }
    }

    private bool CanAppendToLine( ArrayBufferWriter<byte> line, bool overflow, int count )
    {
        return !overflow && line.WrittenCount + count <= _maximumBytes;
    }

    private static bool HasTrailingCarriageReturn( ReadOnlySpan<byte> bytes )
    {
        return !bytes.IsEmpty && bytes[^1] == ( byte ) '\r';
    }
}

public sealed class PayloadTooLargeException( string message ) : Exception( message );
