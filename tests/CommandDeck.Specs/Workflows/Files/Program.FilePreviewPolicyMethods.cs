namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task FilePreviewEnforcesBoundsAndPathPolicyAsync( )
    {
        var sandbox = SpecTemporaryFiles.PathFor( $"commanddeck-preview-spec-{Guid.NewGuid( ):N}" );
        var root = Path.Combine( sandbox, "root" );
        var outside = Path.Combine( sandbox, "outside" );
        Directory.CreateDirectory( root );
        Directory.CreateDirectory( outside );

        try
        {
            var linesPath = Path.Combine( root, "lines.md" );

            await File.WriteAllTextAsync( linesPath, string.Join( '\n', Enumerable
                .Range( 1, 250 )
                .Select( number => $"line {number}" ) ) );

            var service = PreviewService( root, followSymbolicLinks: false, neverIndex: [Path.Combine( root, "denied.txt" )] );

            var lines = await service.ReadAsync( linesPath, CancellationToken.None );
            True( lines.Succeeded );
            True( lines.Preview?.Truncated == true );
            Equal( "markdown", lines.Preview?.Language );
            Equal( 200, lines.Preview?.Text.Split( '\n' ).Length );
            var bytesPath = Path.Combine( root, "long.txt" );

            await File.WriteAllTextAsync( bytesPath, new string( 'x', ProtocolLimits.MaximumPreviewBytes + 100 ) );

            var bytes = await service.ReadAsync( bytesPath, CancellationToken.None );
            True( bytes.Preview?.Truncated == true );
            Equal( 8192, bytes.Preview?.Text.Length );
            var deniedPath = Path.Combine( root, "denied.txt" );
            await File.WriteAllTextAsync( deniedPath, "must not be returned" );
            True( !(await service.ReadAsync( deniedPath, CancellationToken.None )).Succeeded );
            var binaryPath = Path.Combine( root, "binary.dat" );
            await File.WriteAllBytesAsync( binaryPath, [1, 0, 2] );
            var binary = await service.ReadAsync( binaryPath, CancellationToken.None );
            True( !binary.Succeeded );
            True( !binary.Error?.Contains( "must not", StringComparison.Ordinal ) == true );
            var outsidePath = Path.Combine( outside, "secret.txt" );
            await File.WriteAllTextAsync( outsidePath, "outside secret" );
            var linkPath = Path.Combine( root, "escape.txt" );
            File.CreateSymbolicLink( linkPath, outsidePath );
            var following = PreviewService( root, followSymbolicLinks: true, neverIndex: [] );
            var escaped = await following.ReadAsync( linkPath, CancellationToken.None );
            True( !escaped.Succeeded );
            True( !escaped.Error?.Contains( "outside secret", StringComparison.Ordinal ) == true );
            using CancellationTokenSource canceled = new( );
            canceled.Cancel( );

            await ThrowsAsync<OperationCanceledException>( async ( ) => await service.ReadAsync( linesPath, canceled.Token ) );
        }
        finally
        {
            Directory.Delete( sandbox, recursive: true );
        }
    }

    private static FilePreviewService PreviewService( string root, bool followSymbolicLinks, List<string> neverIndex, ImagePreviewRenderer? images = null ) =>
        new( new IndexingOptions
        {
            Roots =
            [
                new IndexRootOptions
                {
                    Path = root,
                    IncludeHidden = false,
                    IncludeContentInSearch = true,
                    FollowSymbolicLinks = followSymbolicLinks
                }
            ]
        }, new PrivacyOptions { NeverIndex = neverIndex }, images );
}
