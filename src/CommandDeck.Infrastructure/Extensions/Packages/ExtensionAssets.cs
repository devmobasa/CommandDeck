using System.Buffers.Binary;
using System.IO.Compression;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions.Views;

namespace CommandDeck;

/// <summary>Copies bounded inert package images into renderer data URLs. No package paths cross this boundary.</summary>
public sealed class ExtensionAssets( string artifactDirectory )
{
    private const int MinimumPngBytes = 45;
    private const int ChunkOverheadBytes = 12;
    private const int ImageHeaderBytes = 13;
    private const int InterlaceMethodOffset = 12;
    private const int MaximumImageDimensionPixels = 512;

    public string Resolve( string asset )
    {
        ViewValidation.ValidateAsset( asset );

        using var file = SafeRegularFile.OpenFromDirectory( Path.Combine( artifactDirectory, ExtensionPackageFiles.AssetsDirectory ),
            asset[ExtensionPackageFiles.AssetPrefix.Length..] );

        if ( file.Length is < MinimumPngBytes or > ExtensionViewLimits.MaximumPngAssetBytes )
        {
            throw new InvalidDataException( "An image must fit in 32 KiB." );
        }

        var bytes = new byte[( int ) file.Length];
        var offset = 0;
        while ( offset < bytes.Length )
        {
            var read = RandomAccess.Read( file.Handle, bytes.AsSpan( offset ), offset );
            if ( read == 0 )
            {
                throw new InvalidDataException( "The image changed while reading." );
            }

            offset += read;
        }

        ValidatePng( bytes );

        return CommandDeck.PngFormat.PngDataUrlPrefix + Convert.ToBase64String( bytes );
    }

    public static void ValidatePng( ReadOnlySpan<byte> bytes )
    {
        if ( IsPngHeaderInvalid( bytes ) )
        {
            Invalid( );
        }

        using var compressed = new MemoryStream( );
        var width = 0;
        var height = 0;
        var channels = 0;
        var ended = false;

        for ( var offset = 8; offset < bytes.Length; )
        {
            if ( bytes.Length - offset < ChunkOverheadBytes )
            {
                Invalid( );
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian( bytes[offset..] );
            if ( length > bytes.Length - offset - ChunkOverheadBytes )
            {
                Invalid( );
            }

            var kind = bytes.Slice( offset + 4, 4 );
            var data = bytes.Slice( offset + 8, ( int ) length );

            if ( Crc( bytes.Slice( offset + 4, ( int ) length + 4 ) ) != BinaryPrimitives.ReadUInt32BigEndian( bytes[(offset + 8 + ( int ) length)..] ) )
            {
                Invalid( );
            }

            if ( offset == 8 )
            {
                if ( IsImageHeaderInvalid( length, kind ) )
                {
                    Invalid( );
                }

                (width, height, channels) = Header( data );
            }
            else if ( kind.SequenceEqual( "IDAT"u8 ) )
            {
                compressed.Write( data );
            }
            else if ( IsImageEnd( bytes, offset, length, kind ) )
            {
                ended = true;
            }
            else
            {
                Invalid( ); // No animations, embedded profiles, text, or external resources.
            }

            offset += ( int ) length + ChunkOverheadBytes;
        }

        if ( !ended )
        {
            Invalid( );
        }

        compressed.Position = 0;
        using var decoded = new ZLibStream( compressed, CompressionMode.Decompress );
        var row = new byte[width * channels + 1];

        for ( var y = 0; y < height; y++ )
        {
            decoded.ReadExactly( row );

            if ( row[0] > 4 )
            {
                Invalid( );
            }
        }

        if ( decoded.ReadByte( ) != -1 )
        {
            Invalid( );
        }
    }

    private static (int Width, int Height, int Channels) Header( ReadOnlySpan<byte> data )
    {
        var w = BinaryPrimitives.ReadUInt32BigEndian( data );
        var h = BinaryPrimitives.ReadUInt32BigEndian( data[4..] );
        if ( IsImageFormatInvalid( data, w, h ) )
        {
            Invalid( );
        }

        var channels = data[9] switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => 0
        };

        if ( channels == 0 )
        {
            Invalid( );
        }

        return (( int ) w, ( int ) h, channels);
    }

    private static uint Crc( ReadOnlySpan<byte> bytes )
    {
        var crc = uint.MaxValue;

        foreach ( var value in bytes )
        {
            crc ^= value;

            for ( var bit = 0; bit < 8; bit++ )
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
        }

        return ~crc;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid( ) => throw new InvalidDataException( "Only bounded, non-interlaced 8-bit PNG images without ancillary chunks are supported." );

    private static bool IsPngHeaderInvalid( ReadOnlySpan<byte> bytes )
    {
        return bytes.Length is < MinimumPngBytes or > ExtensionViewLimits.MaximumPngAssetBytes ||
               !bytes[..8]
                   .SequenceEqual( PngFormat.Signature );
    }

    private static bool IsImageHeaderInvalid( uint length, ReadOnlySpan<byte> kind )
    {
        return !kind.SequenceEqual( PngFormat.HeaderChunkType ) || length != ImageHeaderBytes;
    }

    private static bool IsImageEnd( ReadOnlySpan<byte> bytes, int offset, uint length, ReadOnlySpan<byte> kind )
    {
        return kind.SequenceEqual( "IEND"u8 ) && length == 0 && offset + ChunkOverheadBytes == bytes.Length;
    }

    private static bool IsImageFormatInvalid( ReadOnlySpan<byte> data, uint w, uint h )
    {
        return w is < 1 or > MaximumImageDimensionPixels ||
               h is < 1 or > MaximumImageDimensionPixels ||
               data[8] != 8 ||
               data[10] != 0 ||
               data[11] != 0 ||
               data[InterlaceMethodOffset] != 0;
    }
}
