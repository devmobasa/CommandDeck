using System.Buffers.Binary;
using System.Globalization;

namespace CommandDeck;

/// <summary>Converts authorized raster snapshots to bounded PNG thumbnails.</summary>
public sealed class ImagePreviewRenderer( BoundedProcessRunner? runner = null, Func<bool>? available = null )
{
    internal const int MaximumImageDimensionPixels = 32768;
    private const int MinimumPngBytes = 33;

    public const int MaximumInputBytes = 16 * ByteUnits.BytesPerMebibyte;
    public const int MaximumOutputBytes = 1536 * ByteUnits.BytesPerKibibyte;
    public const int ThumbnailDimensionPixels = 512;
    private const string PngFormatName = "PNG";
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> Gif87Signature => "GIF87a"u8;
    private static ReadOnlySpan<byte> Gif89Signature => "GIF89a"u8;
    private static ReadOnlySpan<byte> RiffSignature => "RIFF"u8;
    private static ReadOnlySpan<byte> WebpSignature => "WEBP"u8;
    private static ReadOnlySpan<byte> BmpSignature => "BM"u8;

    private readonly BoundedProcessRunner _run = runner ?? RunAsync;
    private readonly Func<bool> _available = available ?? ImagePreviewSandbox.IsAvailable;
    internal bool Available => _available( );

    internal static string? DetectFormat( ReadOnlySpan<byte> bytes )
    {
        if ( bytes.StartsWith( PngFormat.Signature ) )
        {
            return PngFormatName;
        }

        if ( bytes.StartsWith( JpegSignature ) )
        {
            return "JPEG";
        }

        if ( IsGif( bytes ) )
        {
            return "GIF";
        }

        if ( IsWebp( bytes ) )
        {
            return "WEBP";
        }

        if ( IsBmp( bytes ) )
        {
            return "BMP";
        }

        return null;
    }

    internal async ValueTask<FilePreviewOutcome> ReadAsync( SafeRegularFile file, string format, CancellationToken cancellationToken )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return FilePreviewOutcome.Failure( "Image previews require Linux." );
        }

        if ( file.Length > MaximumInputBytes )
        {
            return FilePreviewOutcome.Failure( "Image preview is limited to files up to 16 MiB." );
        }

        if ( !_available( ) )
        {
            return FilePreviewOutcome.Failure( "Image previews require ImageMagick and Bubblewrap in /usr/bin." );
        }

        var bytes = new byte[( int ) file.Length + 1];

        var count = await file
            .ReadPrefixAsync( bytes, cancellationToken )
            .ConfigureAwait( false );

        if ( HasImageChanged( file, format, bytes, count ) )
        {
            return FilePreviewOutcome.Failure( "The image changed while it was being read. Select it again." );
        }

        return await ReadSnapshotAsync( bytes.AsMemory( 0, count ), format, cancellationToken )
            .ConfigureAwait( false );
    }

    internal ValueTask<FilePreviewOutcome> ReadClipboardPngAsync( ReadOnlyMemory<byte> bytes, CancellationToken token )
    {
        token.ThrowIfCancellationRequested( );

        if ( CannotRenderPng( bytes ) )
        {
            return ValueTask.FromResult( FilePreviewOutcome.Failure( "Clipboard PNG preview is unavailable." ) );
        }

        return ReadSnapshotAsync( bytes, PngFormatName, token );
    }

    private async ValueTask<FilePreviewOutcome> ReadSnapshotAsync( ReadOnlyMemory<byte> bytes, string format, CancellationToken cancellationToken )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return FilePreviewOutcome.Failure( "Image previews require Linux." );
        }

        // Copy only the already-authorized bytes. The decoder never reopens a
        // user path or sees an extension-selected decoder, URL, or SVG document.
        var directory = Path.Combine( Path.GetTempPath( ), "commanddeck-image-" +
                                                           Guid
                                                               .NewGuid( )
                                                               .ToString( "N" ) );

        Directory.CreateDirectory( directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

        try
        {
            var snapshot = Path.Combine( directory, "source" );
            using ( var output = SafeRegularFile.CreatePrivateWriteStream( snapshot ) )
            {
                await output
                    .WriteAsync( bytes, cancellationToken )
                    .ConfigureAwait( false );
            }

            string[] arguments =
            [
                "-limit", "thread", "1", "-limit", "memory", "128MiB", "-limit", "map", "0",
                "-limit", "disk", "0", "-limit", "width", "32768", "-limit", "height", "32768",
                format + ":" + ImagePreviewSandbox.Input + "[0]", "-auto-orient",
                "-format", "%w %h\n", "-write", "info:-",
                "-thumbnail", $"{ThumbnailDimensionPixels}x{ThumbnailDimensionPixels}>", "-strip", "-depth", "8", "-interlace", "none",
                "-define", PngFormatName.ToLowerInvariant( ) + ":exclude-chunks=all", "inline:" + PngFormatName.ToLowerInvariant( ) + ":-"
            ];

            var result = await _run( ImagePreviewSandbox.Executable, ImagePreviewSandbox.BuildArguments( snapshot, arguments ), TimeSpan.FromSeconds( 3 ),
                    CreateImageProcessLimits( ), cancellationToken )
                .ConfigureAwait( false );

            cancellationToken.ThrowIfCancellationRequested( );

            if ( RenderingFailed( result ) )
            {
                return FilePreviewOutcome.Failure(
                    "The image could not be previewed. It may be damaged, exceed preview limits, or the Bubblewrap sandbox could not start." );
            }

            return ParseThumbnail( result.StandardOutput, format );
        }
        finally
        {
            Directory.Delete( directory, recursive: true );
        }
    }

    private static FilePreviewOutcome ParseThumbnail( string output, string format )
    {
        var newline = output.IndexOf( '\n' );
        string[] dimensions = [];
        var source = "";
        if ( newline >= 0 )
        {
            dimensions = output[..newline]
                .Split( ' ' );

            source = output[(newline + 1)..]
                .Trim( );
        }

        if ( IsInvalidImageDescription( dimensions, source, out var width, out var height ) )
        {
            return FilePreviewOutcome.Failure( "The image decoder returned an invalid preview." );
        }

        var png = new byte[source.Length];
        if ( IsInvalidPng( source, png ) )
        {
            return FilePreviewOutcome.Failure( "The image decoder returned an invalid preview." );
        }

        return FilePreviewOutcome.Success( CreateImagePreview( format, source, width, height ) );
    }

    private static ValueTask<ProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, TimeSpan timeout, ProcessExecutionLimits limits,
        CancellationToken cancellationToken ) =>
        ProcessRunner.RunBoundedAsync( file, arguments, null, timeout, limits, cancellationToken );

    private static bool IsGif( ReadOnlySpan<byte> bytes )
    {
        return bytes.StartsWith( Gif87Signature ) || bytes.StartsWith( Gif89Signature );
    }

    private static bool IsWebp( ReadOnlySpan<byte> bytes )
    {
        return bytes.Length >= 12 &&
               bytes.StartsWith( RiffSignature ) &&
               bytes[8..]
                   .StartsWith( WebpSignature );
    }

    private static bool IsBmp( ReadOnlySpan<byte> bytes )
    {
        return bytes.Length >= 14 && bytes.StartsWith( BmpSignature ) && BinaryPrimitives.ReadUInt32LittleEndian( bytes[6..] ) == 0;
    }

    private static bool HasImageChanged( SafeRegularFile file, string format, byte[] bytes, int count )
    {
        return count != file.Length ||
               DetectFormat( bytes.AsSpan( 0, count ) ) != format ||
               !LinuxMountIdentity.TryGet( file.Handle, out var after ) ||
               after != file.Identity;
    }

    private bool CannotRenderPng( ReadOnlyMemory<byte> bytes )
    {
        return !OperatingSystem.IsLinux( ) || bytes.Length is 0 or > MaximumInputBytes || DetectFormat( bytes.Span ) != PngFormatName || !_available( );
    }

    private static bool RenderingFailed( ProcessResult result )
    {
        return result is not { ExitCode: 0, TimedOut: false, MemoryLimitExceeded: false, AnyOutputLimitExceeded: false };
    }

    private static bool IsInvalidPng( string source, byte[] png )
    {
        return !Convert.TryFromBase64String( source[PngFormat.PngDataUrlPrefix.Length..], png, out var length ) ||
               length < MinimumPngBytes ||
               DetectFormat( png.AsSpan( 0, length ) ) != PngFormatName ||
               !png
                   .AsSpan( 12, 4 )
                   .SequenceEqual( PngFormat.HeaderChunkType ) ||
               BinaryPrimitives.ReadUInt32BigEndian( png.AsSpan( 16 ) ) is < 1 or > ThumbnailDimensionPixels ||
               BinaryPrimitives.ReadUInt32BigEndian( png.AsSpan( 20 ) ) is < 1 or > ThumbnailDimensionPixels;
    }

    private static bool IsInvalidImageDescription( string[] dimensions, string source, out int width, out int height )
    {
        width = default;
        height = default;

        return dimensions.Length != 2 ||
               !int.TryParse( dimensions[0], CultureInfo.InvariantCulture, out width ) ||
               !int.TryParse( dimensions[1], CultureInfo.InvariantCulture, out height ) ||
               width is < 1 or > MaximumImageDimensionPixels ||
               height is < 1 or > MaximumImageDimensionPixels ||
               source.Length > MaximumOutputBytes ||
               !source.StartsWith( PngFormat.PngDataUrlPrefix, StringComparison.Ordinal );
    }

    private static ProcessExecutionLimits CreateImageProcessLimits( )
    {
        return new ProcessExecutionLimits
        {
            MaximumStandardOutputBytes = MaximumOutputBytes,
            MaximumStandardErrorBytes = 4096,
            MaximumMemoryBytes = 384L * ByteUnits.BytesPerMebibyte,
            ClearEnvironment = true
        };
    }

    private static FilePreview CreateImagePreview( string format, string source, int width, int height )
    {
        return new FilePreview
        {
            Text = "",
            Language = format.ToLowerInvariant( ),
            ImageSource = source,
            ImageWidth = width,
            ImageHeight = height
        };
    }
}
