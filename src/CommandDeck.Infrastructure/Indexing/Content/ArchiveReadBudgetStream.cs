namespace CommandDeck;

// TarReader must consume skipped data too. Disable seeking so no member can
// bypass the decompressed byte budget, including metadata and padding.
internal sealed class ArchiveReadBudgetStream( Stream input, long maximumBytes ) : Stream
{
    private long _readBytes;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            throw new NotSupportedException( );
        }
    }

    public override long Position
    {
        get
        {
            throw new NotSupportedException( );
        }
        set
        {
            throw new NotSupportedException( );
        }
    }

    public override int Read( byte[] buffer, int offset, int count ) => Read( buffer.AsSpan( offset, count ) );

    public override int Read( Span<byte> buffer )
    {
        var read = input.Read( buffer[..ReadSize( buffer.Length )] );
        CountRead( read );

        return read;
    }

    public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default )
    {
        var read = await input.ReadAsync( buffer[..ReadSize( buffer.Length )], cancellationToken )
            .ConfigureAwait( false );
        CountRead( read );

        return read;
    }

    public override Task<int> ReadAsync( byte[] buffer, int offset, int count, CancellationToken cancellationToken ) =>
        ReadAsync( buffer.AsMemory( offset, count ), cancellationToken ).AsTask( );

    private int ReadSize( int requested ) => ( int ) Math.Min( requested, maximumBytes - _readBytes + 1 );

    private void CountRead( int read )
    {
        _readBytes += read;
        if ( _readBytes > maximumBytes )
        {
            throw new InvalidDataException( "The decompressed archive exceeds its byte limit." );
        }
    }

    public override void Flush( )
    {
        throw new NotSupportedException( );
    }

    public override long Seek( long offset, SeekOrigin origin )
    {
        throw new NotSupportedException( );
    }

    public override void SetLength( long value )
    {
        throw new NotSupportedException( );
    }

    public override void Write( byte[] buffer, int offset, int count )
    {
        throw new NotSupportedException( );
    }
}
