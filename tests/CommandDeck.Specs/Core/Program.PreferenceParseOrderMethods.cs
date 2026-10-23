using Microsoft.Data.Sqlite;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task PreferenceRowsParseInReadOrderAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-preference-order-" ).FullName;

        try
        {
            var path = Path.Combine( root, "catalog.db" );
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = path } );
            await catalog.InitializeAsync( default );
            using SqliteConnection connection = new( $"Data Source={path};Pooling=False" );
            await connection.OpenAsync( );
            using var command = connection.CreateCommand( );

            // The WITHOUT ROWID table reads these keys in key order. Both rows
            // are corrupt: the shortcut must fail before the later JSON row.
            command.CommandText = """
                                  INSERT INTO launcher_preferences(key, value, updated_unix_ms)
                                  VALUES('clipboard_history_shortcut', '+', 0), ('graphical_editor', '{', 0)
                                  ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                                  """;

            await command.ExecuteNonQueryAsync( );

            var error = await CaptureInvalidOperationAsync( async ( ) => await catalog.GetPreferencesAsync( default ) );

            Equal( "Clipboard shortcut must be Ctrl+Shift+letter or Ctrl+Alt+letter.", error?.Message );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
