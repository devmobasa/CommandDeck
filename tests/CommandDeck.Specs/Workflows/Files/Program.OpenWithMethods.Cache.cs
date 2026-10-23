namespace CommandDeck.Specs;

public static partial class Program
{
    // The MIME cache used to drop every entry once it overflowed, so the rows a
    // user keeps seeing paid for `file` again. It now evicts the least recent path.
    private static async Task MimeTypeCacheEvictsLeastRecentlyUsedPathAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return;
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-mime-cache-" ).FullName;
        var originalPath = Environment.GetEnvironmentVariable( "PATH" );

        try
        {
            var log = Path.Combine( root, "file-arguments.txt" );
            var mimeExecutable = Path.Combine( root, "file" );

            await File.WriteAllTextAsync( mimeExecutable, $"""
                                                           #!/bin/sh
                                                           while [ "$#" -gt 0 ]; do
                                                             [ "$1" = "--" ] && shift && break
                                                             shift
                                                           done
                                                           for path in "$@"; do
                                                             printf '%s\n' "$path" >> '{log}'
                                                             printf '%s\n' 'text/plain'
                                                           done
                                                           """.ReplaceLineEndings( "\n" ) );

            File.SetUnixFileMode( mimeExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

            Environment.SetEnvironmentVariable( "PATH", root + Path.PathSeparator + originalPath );
            var first = Path.Combine( root, "first.txt" );
            var second = Path.Combine( root, "second.txt" );
            var third = Path.Combine( root, "third.txt" );
            foreach ( var path in new[] { first, second, third } )
            {
                await File.WriteAllTextAsync( path, "text" );
            }

            SharedMimeTypeResolver resolver = new( capacity: 2 );
            foreach ( var path in new[] { first, second, first, third, first, second } )
            {
                var resolved = await resolver.ResolveManyAsync( [path], default );
                Equal<string?>( "text/plain", resolved[path] );
            }

            // The repeated first path stays cached; the second is the least recent
            // entry when the third arrives, so only it is detected again.
            string[] detected = await File.ReadAllLinesAsync( log );
            string[] expected = [first, second, third, second];

            True( expected.SequenceEqual( detected ), $"file ran for: {string.Join( ", ", detected.Select( Path.GetFileName ) )}" );

            Equal( 2, resolver.CachedPathCount );
        }
        finally
        {
            Environment.SetEnvironmentVariable( "PATH", originalPath );
            Directory.Delete( root, recursive: true );
        }
    }

    private static async IAsyncEnumerable<IndexedItem> OpenWithReportItems( string root, int count )
    {
        yield return CreateTextViewerApplicationItem( );

        for ( var index = 0; index < count; index++ )
        {
            yield return CreateReportFileItem( root, index );
        }

        await Task.CompletedTask;

        static IndexedItem CreateReportFileItem( string root, int index )
        {
            return new IndexedItem
            {
                StableId = $"file:report-{index}",
                Kind = ResultKinds.File,
                Title = $"report {index}.txt",
                Path = Path.Combine( root, "reports", $"report {index}.txt" ),
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Files
            };
        }
    }
}
