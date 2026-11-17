namespace CommandDeck;

public sealed partial class FilePreviewService
{
    private const string ImageOnlyMessage = "Preview is only available for images.";

    /// <summary>
    /// Renders an image that the daemon chose itself, outside the indexed
    /// folders, such as an Omarchy theme's preview. The path comes from a result
    /// the daemon built, never from the launcher. The opened file must resolve
    /// inside <paramref name="root"/>, so a link in a downloaded theme cannot show
    /// a picture from elsewhere, and the never-index rules still apply. Only an
    /// image is rendered; any other file is refused and never shown as text.
    /// </summary>
    public async ValueTask<FilePreviewOutcome> ReadImageAsync( string path, string root, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );

        if ( !CanonicalPathBoundary.TryCreate( root, out var boundary ) || boundary is null || _privacy.IsDenied( path ) )
        {
            return FilePreviewOutcome.Failure( UnavailableMessage );
        }

        SafeRegularFile opened;

        try
        {
            // A theme folder may be a link to a checkout, so the leaf may be one too.
            opened = SafeRegularFile.Open( path, followLeafSymbolicLink: true );
        }
        catch ( Exception exception ) when ( exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException
                                                 or PlatformNotSupportedException )
        {
            return FilePreviewOutcome.Failure( UnavailableMessage );
        }

        using ( opened )
        {
            if ( !boundary.ContainsOpenedPath( opened.CanonicalPath, opened.Identity.MountId ) || _privacy.IsDenied( opened.CanonicalPath ) )
            {
                return FilePreviewOutcome.Failure( UnavailableMessage );
            }

            try
            {
                var header = new byte[HeaderBytes];
                int headerLength;

                await using ( var probe = File.OpenRead( opened.ProcessDescriptorPath ) )
                {
                    headerLength = await probe
                        .ReadAtLeastAsync( header, header.Length, throwOnEndOfStream: false, cancellationToken )
                        .ConfigureAwait( false );
                }

                var format = ImagePreviewRenderer.DetectFormat( header.AsSpan( 0, headerLength ) );
                if ( format is null )
                {
                    return FilePreviewOutcome.Failure( ImageOnlyMessage );
                }

                return await _images
                    .ReadAsync( opened, format, cancellationToken )
                    .ConfigureAwait( false );
            }
            catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
            {
                return FilePreviewOutcome.Failure( UnavailableMessage );
            }
        }
    }
}
