using System.Buffers.Binary;
using System.Text;

namespace CommandDeck;

public sealed class ClipboardHelperInventory( ulong hostEpoch, Guid selectionId, int advertisedCount )
{
    private const int FirstPrintableAsciiByte = 0x20;
    private const int LastPrintableAsciiByte = 0x7e;

    private readonly List<string> _names = [];
    private uint _sequence = 1;
    public bool Complete { get; private set; }
    public IReadOnlyList<string> Names => _names.AsReadOnly( );

    public void Accept( ClipboardHelperFrame frame )
    {
        if ( HasInvalidInventoryFrame( frame ) )
        {
            throw new InvalidDataException( "Clipboard inventory identity, sequence or bounds are invalid." );
        }

        if ( frame.Header.Type == ClipboardHelperFrameType.InventoryEnd )
        {
            if ( HasIncompleteInventory( frame ) )
            {
                throw new InvalidDataException( "Clipboard inventory ended with an incorrect MIME count." );
            }

            Complete = true;

            return;
        }

        if ( HasInvalidInventoryChunk( frame ) )
        {
            throw new InvalidDataException( "Expected a bounded clipboard inventory chunk." );
        }

        ReadChunk( frame.Payload.Span );
        _sequence++;
    }

    private void ReadChunk( ReadOnlySpan<byte> payload )
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian( payload );
        if ( HasInvalidChunkCount( count ) )
        {
            throw new InvalidDataException( "Clipboard inventory chunk exceeds its MIME count." );
        }

        var offset = 2;
        List<string> pending = [];
        for ( var index = 0; index < count; index++ )
        {
            if ( payload.Length - offset < 4 )
            {
                throw new InvalidDataException( "Truncated clipboard MIME declaration." );
            }

            var offerIndex = BinaryPrimitives.ReadUInt16LittleEndian( payload[offset..] );
            var length = BinaryPrimitives.ReadUInt16LittleEndian( payload[(offset + 2)..] );
            offset += 4;

            if ( HasInvalidInventoryEntry( payload, offset, index, offerIndex, length ) )
            {
                throw new InvalidDataException( "Clipboard MIME index or length is invalid." );
            }

            var name = payload.Slice( offset, length );
            if ( name.ContainsAnyExceptInRange( ( byte ) FirstPrintableAsciiByte, ( byte ) LastPrintableAsciiByte ) )
            {
                throw new InvalidDataException( "Clipboard MIME names must contain printable ASCII." );
            }

            pending.Add( Encoding.ASCII.GetString( name ) );
            offset += length;
        }

        if ( offset != payload.Length )
        {
            throw new InvalidDataException( "Clipboard inventory chunk contains trailing bytes." );
        }

        _names.AddRange( pending );
    }

    private bool HasInvalidInventoryFrame( ClipboardHelperFrame frame )
    {
        return advertisedCount is < 0 or > ClipboardOfferLimits.MaximumInventoryNames ||
               Complete ||
               frame.Header.HostEpoch != hostEpoch ||
               frame.Header.SelectionId != selectionId ||
               frame.Header.OfferIndex != -1 ||
               frame.Header.Sequence != _sequence ||
               frame.Payload.Length != frame.Header.PayloadLength;
    }

    private bool HasIncompleteInventory( ClipboardHelperFrame frame )
    {
        return frame.Payload.Length != 0 || _names.Count != advertisedCount;
    }

    private static bool HasInvalidInventoryChunk( ClipboardHelperFrame frame )
    {
        return frame.Header.Type != ClipboardHelperFrameType.InventoryChunk || frame.Payload.Length is < 2 or > ClipboardOfferLimits.MaximumInventoryChunkBytes;
    }

    private bool HasInvalidChunkCount( ushort count )
    {
        return count is < 1 or > ClipboardOfferLimits.MaximumChunkNames || _names.Count + count > advertisedCount;
    }

    private bool HasInvalidInventoryEntry( ReadOnlySpan<byte> payload, int offset, int index, ushort offerIndex, ushort length )
    {
        return offerIndex != _names.Count + index || length is < 1 or > ClipboardOfferLimits.MaximumMimeNameCharacters || payload.Length - offset < length;
    }
}
