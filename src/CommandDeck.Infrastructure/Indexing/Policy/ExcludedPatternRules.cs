namespace CommandDeck;

// A built-in folder name matched at any depth, such as .claude/worktrees. The
// names match whole path components, the last one a folder, and the matched
// folder must lie below the indexed folder, so an indexed folder is never
// hidden by its own name.
internal sealed record ExcludedNameRule( string Id, string Display, IReadOnlyList<string> Segments )
{
    // Any match contains the names behind a separator, such as /.claude/worktrees.
    private readonly string _needle = "/" + string.Join( '/', Segments );

    public bool Matches( string path, string lexicalRoot, string canonicalRoot, bool directory )
    {
        for ( var start = path.IndexOf( _needle, StringComparison.Ordinal ); start >= 0; start = path.IndexOf( _needle, start + 1, StringComparison.Ordinal ) )
        {
            var end = start + _needle.Length;
            if ( !EndsFolder( path, end, directory ) )
            {
                continue;
            }

            var folder = path[..end];
            if ( IsStrictlyBelow( lexicalRoot, folder ) || IsStrictlyBelow( canonicalRoot, folder ) )
            {
                return true;
            }
        }

        return false;
    }

    // The match ends at a separator, or ends the path of a folder.
    private static bool EndsFolder( string path, int end, bool directory )
    {
        if ( end == path.Length )
        {
            return directory;
        }

        return path[end] == '/';
    }

    private static bool IsStrictlyBelow( string root, string folder ) =>
        folder.Length > root.Length && ExcludedFolderPaths.IsSameOrDescendant( root, folder );
}

// Folders with one of the names inside any app folder of a scope, such as
// ~/.config/<app>/…/Cache, or only directly inside it when TopLevel is set.
// The matched folder must lie below the indexed folder.
internal sealed record ExcludedScopeRule( string Id, string Display, string LexicalScope, string? CanonicalScope, IReadOnlySet<string> Names )
{
    public bool TopLevel { get; init; }

    public bool Matches( string path, string lexicalRoot, string canonicalRoot, bool directory ) =>
        MatchesIn( LexicalScope, path, lexicalRoot, canonicalRoot, directory )
        || (CanonicalScope is not null && MatchesIn( CanonicalScope, path, lexicalRoot, canonicalRoot, directory ));

    private bool MatchesIn( string scope, string path, string lexicalRoot, string canonicalRoot, bool directory )
    {
        if ( path.Length <= scope.Length + 1 || !ExcludedFolderPaths.IsSameOrDescendant( scope, path ) )
        {
            return false;
        }

        // The first component is the app folder; a match is a folder below it.
        var start = path.IndexOf( '/', scope.Length + 1 );

        while ( start >= 0 )
        {
            var next = path.IndexOf( '/', start + 1 );
            var end = next < 0 ? path.Length : next;

            if ( (next >= 0 || directory) && Names.Contains( path[(start + 1)..end] ) && IsBelowRoot( path[..end], lexicalRoot, canonicalRoot ) )
            {
                return true;
            }

            start = TopLevel ? -1 : next;
        }

        return false;
    }

    private static bool IsBelowRoot( string folder, string lexicalRoot, string canonicalRoot ) =>
        IsStrictlyBelow( lexicalRoot, folder ) || IsStrictlyBelow( canonicalRoot, folder );

    private static bool IsStrictlyBelow( string root, string folder ) =>
        folder.Length > root.Length && ExcludedFolderPaths.IsSameOrDescendant( root, folder );
}

// Files that only ever exist beside the data they belong to: SQLite journals
// next to their database, LevelDB files in a LevelDB folder, pid files and Vim
// swap files. Nobody searches for them, and they change on every write.
internal static class TransientFiles
{
    private const string PidSuffix = ".pid";
    private const string LevelDbManifestPrefix = "MANIFEST-";
    private const string LevelDbCurrentFile = "CURRENT";
    private const int LevelDbCurrentBytes = 64;
    private static readonly string[] JournalSuffixes = ["-wal", "-shm", "-journal"];
    private static readonly string[] LevelDbNames = ["LOCK", "LOG", "LOG.old", LevelDbCurrentFile];
    private static readonly string[] LevelDbNumberedSuffixes = [".ldb", ".log", ".sst"];
    private static readonly string[] SwapSuffixes = [".swp", ".swo"];

    // Every SQLite database file starts with these 16 bytes.
    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;

    public static bool IsTransient( string path )
    {
        var name = Path.GetFileName( path );

        try
        {
            if ( name.EndsWith( PidSuffix, StringComparison.Ordinal ) || IsSwapFile( name ) )
            {
                return true;
            }

            var journal = JournalSuffixes.FirstOrDefault( suffix => name.Length > suffix.Length && name.EndsWith( suffix, StringComparison.Ordinal ) );
            if ( journal is not null )
            {
                return IsSqliteDatabase( path[..^journal.Length] );
            }

            return IsLevelDbName( name ) && IsLevelDbFolder( Path.GetDirectoryName( path ) );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException )
        {
            return false;
        }
    }

    private static bool IsSwapFile( string name ) =>
        name.StartsWith( '.' ) && SwapSuffixes.Any( suffix => name.EndsWith( suffix, StringComparison.Ordinal ) );

    // A journal belongs to a regular file beside it that is a SQLite database.
    private static bool IsSqliteDatabase( string path )
    {
        if ( !File.Exists( path ) )
        {
            return false;
        }

        Span<byte> header = stackalloc byte[SqliteHeader.Length];

        return ReadStart( path, header ) == header.Length && header.SequenceEqual( SqliteHeader );
    }

    private static bool IsLevelDbName( string name ) =>
        LevelDbNames.Contains( name, StringComparer.Ordinal ) || IsNumberedTable( name ) || IsManifest( name );

    // Numbered tables and logs, such as 000123.ldb and 000124.log.
    private static bool IsNumberedTable( string name )
    {
        var dot = name.LastIndexOf( '.' );

        return dot > 0 && LevelDbNumberedSuffixes.Contains( name[dot..], StringComparer.Ordinal ) && name[..dot].All( char.IsAsciiDigit );
    }

    private static bool IsManifest( string name ) =>
        name.Length > LevelDbManifestPrefix.Length && name.StartsWith( LevelDbManifestPrefix, StringComparison.Ordinal )
        && name[LevelDbManifestPrefix.Length..].All( char.IsAsciiDigit );

    // A LevelDB folder's CURRENT file names its manifest, which exists.
    private static bool IsLevelDbFolder( string? folder )
    {
        if ( folder is null )
        {
            return false;
        }

        var current = Path.Join( folder, LevelDbCurrentFile );
        if ( !File.Exists( current ) )
        {
            return false;
        }

        Span<byte> text = stackalloc byte[LevelDbCurrentBytes];
        var manifest = System.Text.Encoding.ASCII.GetString( text[..ReadStart( current, text )] ).TrimEnd( '\n' );

        return IsManifest( manifest ) && File.Exists( Path.Join( folder, manifest ) );
    }

    // Reads the start of a regular file without following a link.
    private static int ReadStart( string path, Span<byte> buffer )
    {
        using var file = SafeRegularFile.Open( path );

        return RandomAccess.Read( file.Handle, buffer, 0 );
    }
}
