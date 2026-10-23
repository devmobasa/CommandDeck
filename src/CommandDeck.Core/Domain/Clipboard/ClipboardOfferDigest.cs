using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

public sealed record ClipboardDigestOffer( string Mime, long Length, Stream Content );

public static class ClipboardOfferDigest
{
    private const int DigestBufferBytes = 60 * ByteUnits.BytesPerKibibyte;

    /// <summary>Hashes sorted length-delimited MIME names and exact retained bytes, using one bounded buffer.</summary>
    public static string Compute( IReadOnlyList<ClipboardDigestOffer> offers )
    {
        if ( HasInvalidOffers( offers ) )
        {
            throw new InvalidDataException( "Clipboard digest offers are outside retained-data bounds." );
        }

        using var hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
        var buffer = new byte[DigestBufferBytes];
        Span<byte> length = stackalloc byte[8];
        foreach ( var offer in offers.OrderBy( static offer => offer.Mime, StringComparer.Ordinal ) )
        {
            var name = Encoding.ASCII.GetBytes( offer.Mime );
            BinaryPrimitives.WriteUInt32LittleEndian( length, ( uint ) name.Length );
            hash.AppendData( length[..4] );
            hash.AppendData( name );
            BinaryPrimitives.WriteUInt64LittleEndian( length, ( ulong ) offer.Length );
            hash.AppendData( length );
            var remaining = offer.Length;
            while ( remaining > 0 )
            {
                var read = offer.Content.Read( buffer, 0, ( int ) Math.Min( remaining, buffer.Length ) );
                if ( read == 0 )
                {
                    throw new InvalidDataException( "Clipboard digest offer ended before its verified length." );
                }

                hash.AppendData( buffer.AsSpan( 0, read ) );
                remaining -= read;
            }

            if ( offer.Content.ReadByte( ) != -1 )
            {
                throw new InvalidDataException( "Clipboard digest offer exceeded its verified length." );
            }
        }

        return Convert.ToHexStringLower( hash.GetHashAndReset( ) );
    }

    private static bool HasInvalidOffers( IReadOnlyList<ClipboardDigestOffer> offers )
    {
        return offers.Count is 0 or > ClipboardOfferLimits.MaximumOffers ||
               offers.Any( static offer => offer.Length is < 0 or > ClipboardOfferPlan.MaximumSelectionBytes ||
                                           string.IsNullOrEmpty( offer.Mime ) ||
                                           offer.Mime.Length > ClipboardOfferLimits.MaximumMimeNameCharacters ||
                                           offer.Mime.Any( static character => character is < ' ' or > '~' ) ||
                                           offer
                                               .Mime.Split( ';', 2 )[0]
                                               .Trim( )
                                               .Equals( ClipboardOfferPlan.OwnerMime, StringComparison.OrdinalIgnoreCase ) ) ||
               offers.Sum( static offer => offer.Length ) > ClipboardOfferPlan.MaximumSelectionBytes ||
               offers
                   .Select( static offer => offer.Mime )
                   .Distinct( StringComparer.Ordinal )
                   .Count( ) !=
               offers.Count;
    }
}
