using System.Text;

namespace CommandDeck;

// Atomic per-file replacements plus rollback across the JSON/module pair. The
// reload is part of the transaction, and rollback restores directory entries too.
public static class ShortcutFileTransaction
{
    private sealed record Snapshot( string? LinkTarget, string ContentPath, byte[]? Content, UnixFileMode? Mode );

    public static async ValueTask ApplyAsync( IReadOnlyDictionary<string, string?> updates, Func<CancellationToken, ValueTask> reload,
        CancellationToken cancellationToken )
    {
        var previous = updates.Keys.ToDictionary( static path => path, Capture, StringComparer.Ordinal );
        List<string> applied = [];
        try
        {
            foreach ( var (path, content) in updates )
            {
                var snapshot = previous[path];
                if ( snapshot.Content is not null )
                {
                    var backup = Capture( path + ".bak" );
                    WriteBytes( backup.ContentPath, snapshot.Content, backup.Mode );
                }

                Write( path, content );
                applied.Add( path );
            }

            await reload( cancellationToken ).ConfigureAwait( false );
        }
        catch
        {
            foreach ( var path in applied.AsEnumerable( ).Reverse( ) )
            {
                Restore( path, previous[path] );
            }

            try
            {
                await reload( CancellationToken.None ).ConfigureAwait( false );
            }
            catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
            {
                // Originals remain on disk even when the old configuration cannot reload.
            }

            throw;
        }
    }

    private static Snapshot Capture( string path )
    {
        var entry = new FileInfo( path );
        var link = entry.LinkTarget;
        var contentPath = path;
        if ( link is not null )
        {
            var target = entry.ResolveLinkTarget( returnFinalTarget: true );
            if ( target is null )
            {
                throw new IOException( "The shortcut link changed while taking its rollback snapshot." );
            }

            contentPath = target.FullName;
        }

        var content = File.Exists( contentPath ) ? File.ReadAllBytes( contentPath ) : null;
        UnixFileMode? mode = null;
        if ( content is not null && !OperatingSystem.IsWindows( ) )
        {
            mode = File.GetUnixFileMode( contentPath );
        }

        return new( link, contentPath, content, mode );
    }

    private static void Restore( string path, Snapshot snapshot )
    {
        if ( snapshot.LinkTarget is null && new FileInfo( path ).LinkTarget is not null )
        {
            File.Delete( path );
        }

        if ( snapshot.Content is null )
        {
            File.Delete( snapshot.ContentPath );
        }
        else
        {
            WriteBytes( snapshot.ContentPath, snapshot.Content, snapshot.Mode );
        }

        if ( snapshot.LinkTarget is not null )
        {
            File.Delete( path );
            File.CreateSymbolicLink( path, snapshot.LinkTarget );
        }
    }

    private static void Write( string path, string? content )
    {
        if ( content is null )
        {
            File.Delete( path );

            return;
        }

        var snapshot = Capture( path );
        WriteBytes( snapshot.ContentPath, Encoding.UTF8.GetBytes( content ), snapshot.Mode );
    }

    private static void WriteBytes( string path, byte[] content, UnixFileMode? mode )
    {
        Directory.CreateDirectory( Path.GetDirectoryName( path )! );
        var temporary = path + "." + Guid.NewGuid( ).ToString( "N" ) + ".tmp";
        try
        {
            File.WriteAllBytes( temporary, content );
            if ( mode is not null && !OperatingSystem.IsWindows( ) )
            {
                File.SetUnixFileMode( temporary, mode.Value );
            }

            File.Move( temporary, path, overwrite: true );
        }
        finally
        {
            File.Delete( temporary );
        }
    }
}
