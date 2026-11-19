namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    private static DatabaseMigration ExcludedFoldersMigration => new( 24, "Excluded folders and built-in exclusion settings", """
        CREATE TABLE IF NOT EXISTS index_excluded_folders (
            id TEXT PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            created_unix_ms INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS index_builtin_exclusion_overrides (
            id TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL
        ) WITHOUT ROWID;
        """ );
}
