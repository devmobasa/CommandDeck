namespace CommandDeck.Specs;

public static partial class Program
{
    // Startup restore reads every saved marker directory and rule file, and the
    // watch walk then asks for the same ones. The walk must still see the same
    // names and text, read again once either changes, and a failed restore must
    // observe nothing.
    private static async Task PolicyRestoreKeepsMarkersAndRulesCurrentAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-policy-restore-{Guid.NewGuid( ):N}" );
        var project = Path.Combine( root, "project" );
        var ignore = Path.Combine( project, RepositoryFileNames.GitIgnoreFileName );
        Directory.CreateDirectory( Path.Combine( project, IndexingConstants.GitDirectoryName ) );
        await File.WriteAllTextAsync( Path.Combine( project, ProjectMarkerNames.PackageJson ), "{}" );
        await File.WriteAllTextAsync( ignore, "build/\n" );

        try
        {
            IndexPolicyInputs first = new( );
            _ = first.Markers( project );
            _ = first.ReadText( ignore );
            var stamps = first.Snapshot( out _ );

            IndexPolicyInputs restored = new( );
            True( restored.RestoreIfCurrent( stamps ) );
            True( restored.Markers( project ).SetEquals(
                [IndexingConstants.GitDirectoryName, RepositoryFileNames.GitIgnoreFileName, ProjectMarkerNames.PackageJson] ) );
            Equal( "build/\n", restored.ReadText( ignore ).Text );

            await File.WriteAllTextAsync( Path.Combine( project, ProjectMarkerNames.CargoToml ), "" );
            await File.WriteAllTextAsync( ignore, "dist/\nbuild/\n" );
            True( restored.Markers( project ).Contains( ProjectMarkerNames.CargoToml ), "a marker added after restore is read" );
            Equal( "dist/\nbuild/\n", restored.ReadText( ignore ).Text );

            IndexPolicyInputs stale = new( );
            True( !stale.RestoreIfCurrent( stamps ), "inputs changed since the snapshot" );
            Equal( 0L, stale.Revision );
            True( stale.Markers( project ).Contains( ProjectMarkerNames.CargoToml ) );
            Equal( "dist/\nbuild/\n", stale.ReadText( ignore ).Text );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
