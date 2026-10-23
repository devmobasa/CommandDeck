using System.Buffers.Binary;

namespace CommandDeck;

/// <summary>Checks a daemon-admitted sequence without retaining payload buffers.</summary>
public sealed class ClipboardHelperReceiveState
{
    private readonly ulong _epoch;
    private readonly Guid _selection;
    private readonly KeyValuePair<int, long>[] _offers;
    private uint _sequence;
    private int _offer;
    private long _offerBytes;
    private long _selectionBytes;
    public bool Complete { get; private set; }
    public long ReceivedBytes => _selectionBytes;

    public ClipboardHelperReceiveState( ulong epoch, Guid selection, IReadOnlyList<KeyValuePair<int, long>> admittedOffers, uint firstSequence )
    {
        if ( HasInvalidAdmission( epoch, selection, admittedOffers ) )
        {
            throw new InvalidDataException( "Invalid clipboard receive admission." );
        }

        _epoch = epoch;
        _selection = selection;
        _offers = [.. admittedOffers];
        _sequence = firstSequence;
    }

    public void ValidateHeader( ClipboardHelperHeader header )
    {
        if ( HasInvalidFrameIdentity( header ) )
        {
            throw new InvalidDataException( "Clipboard payload identity or sequence changed." );
        }

        if ( header.Type == ClipboardHelperFrameType.SelectionEnd )
        {
            if ( HasInvalidSelectionEnd( header ) )
            {
                throw new InvalidDataException( "Clipboard selection ended before all admitted offers." );
            }

            return;
        }

        if ( HasInvalidOfferIndex( header ) )
        {
            throw new InvalidDataException( "Clipboard payload refers to an unrequested offer." );
        }

        if ( IsOfferEnd( header ) )
        {
            return;
        }

        if ( HasInvalidOfferChunk( header ) )
        {
            throw new InvalidDataException( "Clipboard payload exceeds its admitted offer or selection budget." );
        }
    }

    public void Accept( ClipboardHelperFrame frame )
    {
        ValidateHeader( frame.Header );

        if ( frame.Payload.Length != frame.Header.PayloadLength )
        {
            throw new InvalidDataException( "Clipboard payload length changed." );
        }

        switch ( frame.Header.Type )
        {
            case ClipboardHelperFrameType.OfferChunk:
                _offerBytes += frame.Payload.Length;
                _selectionBytes += frame.Payload.Length;

                break;
            case ClipboardHelperFrameType.OfferEnd:
                if ( BinaryPrimitives.ReadUInt64LittleEndian( frame.Payload.Span ) != ( ulong ) _offerBytes )
                {
                    throw new InvalidDataException( "Clipboard offer byte count does not match received chunks." );
                }

                _offer++;
                _offerBytes = 0;

                break;
            case ClipboardHelperFrameType.SelectionEnd:
                if ( BinaryPrimitives.ReadUInt32LittleEndian( frame.Payload.Span ) != _offers.Length )
                {
                    throw new InvalidDataException( "Clipboard selection offer count does not match admission." );
                }

                Complete = true;

                break;
        }

        _sequence = checked(_sequence + 1);
    }

    private static bool HasInvalidAdmission( ulong epoch, Guid selection, IReadOnlyList<KeyValuePair<int, long>> admittedOffers )
    {
        return epoch == 0 ||
               selection == Guid.Empty ||
               admittedOffers.Count is < 1 or > ClipboardOfferLimits.MaximumOffers ||
               admittedOffers.Any( static offer =>
                   offer.Key is < 0 or >= ClipboardOfferLimits.MaximumInventoryNames || offer.Value is < 1 or > ClipboardOfferPlan.MaximumSelectionBytes ) ||
               admittedOffers
                   .Select( static offer => offer.Key )
                   .Distinct( )
                   .Count( ) !=
               admittedOffers.Count ||
               admittedOffers.Sum( static offer => offer.Value ) > ClipboardOfferPlan.MaximumSelectionBytes;
    }

    private bool HasInvalidFrameIdentity( ClipboardHelperHeader header )
    {
        return Complete || header.HostEpoch != _epoch || header.SelectionId != _selection || header.Sequence != _sequence;
    }

    private bool HasInvalidSelectionEnd( ClipboardHelperHeader header )
    {
        return _offer != _offers.Length || header.OfferIndex != -1 || header.PayloadLength != 4;
    }

    private bool HasInvalidOfferIndex( ClipboardHelperHeader header )
    {
        return _offer >= _offers.Length || header.OfferIndex != _offers[_offer].Key;
    }

    private static bool IsOfferEnd( ClipboardHelperHeader header )
    {
        return header is { Type: ClipboardHelperFrameType.OfferEnd, PayloadLength: 8 };
    }

    private bool HasInvalidOfferChunk( ClipboardHelperHeader header )
    {
        return header.Type != ClipboardHelperFrameType.OfferChunk ||
               header.PayloadLength is < 1 or > ClipboardHelperHeader.MaximumPayload ||
               header.PayloadLength > _offers[_offer].Value - _offerBytes ||
               header.PayloadLength > ClipboardOfferPlan.MaximumSelectionBytes - _selectionBytes;
    }
}
