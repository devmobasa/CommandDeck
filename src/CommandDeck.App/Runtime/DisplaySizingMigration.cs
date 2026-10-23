using System.Text;

namespace CommandDeck;

// Run before catalog initialization: after it, a new installation and an old
// default user with no display-size file are indistinguishable to the UI.
internal static class DisplaySizingMigration
{
    private const int MigrationBufferBytes = 4096;

    internal static async Task InitializeAsync( string? configPath, CancellationToken cancellationToken )
    {
        var stateDirectory = PathExpansion.Expand( "$XDG_STATE_HOME/commanddeck" );
        if ( File.Exists( Path.Combine( stateDirectory, DisplaySizingPersistence.ProfileFileName ) ) )
        {
            return;
        }

        var options = await ConfigurationLoader
            .LoadAsync( configPath, cancellationToken )
            .ConfigureAwait( false );

        await EnsureProfileAsync( PathExpansion.Expand( options.Storage.DatabasePath ), stateDirectory, cancellationToken )
            .ConfigureAwait( false );
    }

    internal static async Task EnsureProfileAsync( string databasePath, string stateDirectory, CancellationToken cancellationToken )
    {
        var path = Path.Combine( stateDirectory, DisplaySizingPersistence.ProfileFileName );

        try
        {
            if ( File.Exists( path ) )
            {
                return;
            }

            var existing = ReadExistingState( databasePath, stateDirectory );
            Directory.CreateDirectory( stateDirectory );

            // CreateNew preserves an explicit choice or a concurrent initializer.
            await using var stream = new FileStream( path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: MigrationBufferBytes,
                useAsync: true );

            var profile = existing ? DisplaySizingPersistence.LegacyProfile : DisplaySizingPersistence.LogicalProfile;

            await stream
                .WriteAsync( Encoding.UTF8.GetBytes( profile + "\n" ), cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
        {
            if ( !File.Exists( path ) )
            {
                AppLog.Warning( "Display sizing migration could not be saved; keeping previous sizing: " + error.Message );
            }
        }
    }

    private static bool ReadExistingState( string databasePath, string stateDirectory )
    {
        return File.Exists( databasePath ) ||
               (Directory.Exists( stateDirectory ) &&
                Directory
                    .EnumerateFileSystemEntries( stateDirectory )
                    .Any( ));
    }
}
