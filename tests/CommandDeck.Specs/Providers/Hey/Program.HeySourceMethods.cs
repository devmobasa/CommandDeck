namespace CommandDeck.Specs;

public static partial class Program
{
    // HEY is a searchable source with no Home: Settings gets the flag from the
    // descriptor, and neither save path can switch it on.
    private static async Task HeySourceNeverAppearsOnHomeAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-hey-source-" ).FullName;

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( CancellationToken.None );
            var descriptor = catalog.ContributionSnapshot.Sources.Single( static source => source.Id == "hey" );
            Equal( "HEY", descriptor.Label );
            True( !descriptor.HomeAvailable );

            True( LauncherSourceRegistry
                .Configurable.Where( static source => source.Id != "hey" )
                .All( static source => source.HomeAvailable ) );

            var preferences = await catalog.GetPreferencesAsync( CancellationToken.None );
            var hey = preferences.Sources.Single( static source => source.SourceId == "hey" );
            True( hey is { EnabledInSearch: true, EnabledOnHome: false } );

            var patch = await CaptureInvalidOperationAsync( async ( ) =>
                await catalog.PatchSourcePreferencesAsync( CreateSourcePreferencePatch( preferences ), CancellationToken.None ) );

            Equal( "HEY never appears on Home.", patch?.Message );

            var replaced = await CaptureInvalidOperationAsync( async ( ) =>
                await catalog.ReplacePreferencesAsync( CreateLauncherPreferences( preferences ), CancellationToken.None ) );

            Equal( "HEY never appears on Home.", replaced?.Message );

            // Its Search switch still saves.
            var disabled = await catalog.PatchSourcePreferencesAsync(
                new SourcePreferencePatch
                {
                    RegistryRevision = preferences.RegistryRevision,
                    PreferenceRevision = preferences.PreferenceRevision,
                    Changes = [new SourcePreferenceChange { SourceId = SourceIds.Hey, EnabledInSearch = false }]
                }, CancellationToken.None );

            True( !disabled.Sources.Single( static source => source.SourceId == "hey" ).EnabledInSearch );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static SourcePreferencePatch CreateSourcePreferencePatch( LauncherPreferences preferences )
        {
            return new SourcePreferencePatch
            {
                RegistryRevision = preferences.RegistryRevision,
                PreferenceRevision = preferences.PreferenceRevision,
                Changes = [new SourcePreferenceChange { SourceId = SourceIds.Hey, EnabledOnHome = true }]
            };
        }

        static LauncherPreferences CreateLauncherPreferences( LauncherPreferences preferences )
        {
            return new LauncherPreferences
            {
                Sources =
                [
                    .. preferences.Sources.Select( static source => new SourcePreference
                    {
                        SourceId = source.SourceId,
                        EnabledInSearch = source.EnabledInSearch,
                        EnabledOnHome = source.SourceId == SourceIds.Hey || source.EnabledOnHome,
                        Position = source.Position
                    } )
                ]
            };
        }
    }
}
