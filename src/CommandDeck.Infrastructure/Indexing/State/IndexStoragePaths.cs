namespace CommandDeck;

// The index can live beneath a search root. Never index its SQLite files or
// journal their writes: the journal would otherwise continuously notify itself.
internal sealed class IndexStoragePaths
{
    private const string WriteAheadLogSuffix = "-wal";
    private const string SharedMemorySuffix = "-shm";
    private const string RollbackJournalSuffix = "-journal";
    private const string IndexSidecarPrefix = ".index-";

    private readonly string[] _exactPaths;
    private readonly string[] _prefixes;

    public IndexStoragePaths( string databasePath )
    {
        var database = PathExpansion.Expand( databasePath );
        var directory = Path.GetDirectoryName( database )!;
        var canonicalDatabase = ResolveCanonicalDatabase( database, directory );

        // Every traversed entry is checked, so the names are built once.
        string[] databases = [database, canonicalDatabase];

        _exactPaths =
        [
            .. databases.SelectMany( static value => new[] { value, value + WriteAheadLogSuffix, value + SharedMemorySuffix, value + RollbackJournalSuffix } )
        ];

        _prefixes = [.. databases.Select( static value => value + IndexSidecarPrefix )];
    }

    public bool Contains( string path )
    {
        foreach ( var exact in _exactPaths )
        {
            if ( string.Equals( path, exact, StringComparison.Ordinal ) )
            {
                return true;
            }
        }

        foreach ( var prefix in _prefixes )
        {
            if ( path.StartsWith( prefix, StringComparison.Ordinal ) )
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveCanonicalDatabase( string database, string directory )
    {
        if ( CanonicalPathBoundary.TryCreate( directory, out var boundary ) )
        {
            return Path.Combine( boundary!.CanonicalRoot, Path.GetFileName( database ) );
        }

        return database;
    }
}
