using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditSkippedTarAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-skipped-tar-" );
        try
        {
            IndexRootOptions indexRoot = new( ) { Path = root.FullName, IncludeContentInSearch = true };
            using ContentExtractor extractor = new( new IndexingOptions { MaximumExtractedBytes = 64 * 1024 } );
            foreach ( var skippedBytes in new[] { 1024, 2 * 1024 * 1024 } )
            {
                var path = Path.Combine( root.FullName, "skipped.tgz" );
                using ( var file = File.Create( path ) )
                using ( GZipStream gzip = new( file, CompressionLevel.SmallestSize ) )
                using ( TarWriter writer = new( gzip ) )
                {
                    WriteEntry( "before.txt", Encoding.UTF8.GetBytes( "before marker" ) );
                    WriteEntry( "zeros.bin", new byte[skippedBytes] );
                    WriteEntry( "after.txt", Encoding.UTF8.GetBytes( "after marker" ) );

                    void WriteEntry( string name, byte[] bytes )
                    {
                        using MemoryStream data = new( bytes );
                        writer.WriteEntry( new UstarTarEntry( TarEntryType.RegularFile, name ) { DataStream = data } );
                    }
                }

                var policy = FileIndexPolicyResolver.Resolve( indexRoot, path );
                var result = await extractor.ExtractAsync( path, new FileInfo( path ).Length, policy, default );
                if ( skippedBytes == 1024 )
                {
                    True( result.Text?.Contains( "after marker", StringComparison.Ordinal ) == true );
                }
                else
                {
                    Equal( "archive-invalid-or-limit", result.FailureReason );
                    Equal<string?>( null, result.Text );
                }
            }
        }
        finally
        {
            root.Delete( recursive: true );
        }
    }
}
