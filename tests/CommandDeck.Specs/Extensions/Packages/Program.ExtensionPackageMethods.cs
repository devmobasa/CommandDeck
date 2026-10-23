using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static ExtensionManifest PackageFixture( string version = "1.0.0" ) => new( )
    {
        ManifestVersion = 1,
        Id = "org.example.utilities",
        Version = version,
        Name = "Utilities",
        Description = "Local utilities",
        License = "MIT",
        Protocol = new( 1, 0 ),
        Execution = new( "restricted" ),
        Targets = new( ) { [ExtensionTargets.LinuxX64] = new( "bin/linux-x64/utility", ExtensionTargets.NativeAot, ManifestValidation.RuntimeProfile ) },
        Commands = [new( ) { Id = "uuid", Title = "Create UUID", Keywords = ["identifier"] }]
    };

    private static void HandwrittenExtensionDefaultsSurviveDeserialization( )
    {
        var bytes = File.ReadAllBytes( Path.Combine( FindRepositoryRoot( ), "samples/extensions/UuidTools/commanddeck.extension.json" ) );

        var manifest = ManifestValidation.Parse( bytes );
        Equal( 0, manifest.Preferences.Length );
        Equal( "view", manifest.Commands[0].Mode );
        Equal( 0, manifest.Commands[0].Keywords.Length );
        Equal( 0, manifest.Commands[0].RequiredFeatures.Length );

        var missingMinor = Encoding
            .UTF8.GetString( bytes )
            .Replace( ", \"minimumMinor\": 0", "", StringComparison.Ordinal );

        True( missingMinor != Encoding.UTF8.GetString( bytes ) );
        Throws<JsonException>( ( ) => ManifestValidation.Parse( Encoding.UTF8.GetBytes( missingMinor ) ) );

        var explicitNull = Encoding
            .UTF8.GetString( bytes )
            .Insert( 1, "\"preferences\":null," );

        Throws<InvalidDataException>( ( ) => ManifestValidation.Parse( Encoding.UTF8.GetBytes( explicitNull ) ) );

        var list = JsonSerializer.Deserialize( "{\"kind\":\"list\",\"id\":\"list\",\"title\":\"Results\"}", ExtensionViewJsonContext.Default.ExtensionView )!;

        CommandDeck.Extensions.Views.ViewValidation.Validate( list );
        Equal( 0, (( CommandDeck.Extensions.Views.ListView ) list).Items.Length );

        var withItem = JsonSerializer.Deserialize( """{"kind":"list","id":"list","title":"Results","items":[{"id":"a","title":"A"}]}""",
            ExtensionViewJsonContext.Default.ExtensionView )!;

        CommandDeck.Extensions.Views.ViewValidation.Validate( withItem );
        Equal( 0, (( CommandDeck.Extensions.Views.ListView ) withItem).Items[0].Actions.Length );

        var nullActions = JsonSerializer.Deserialize( """{"kind":"list","id":"list","title":"Results","items":[{"id":"a","title":"A","actions":null}]}""",
            ExtensionViewJsonContext.Default.ExtensionView )!;

        Throws<InvalidDataException>( ( ) => CommandDeck.Extensions.Views.ViewValidation.Validate( nullActions ) );

        Throws<JsonException>( ( ) =>
            JsonSerializer.Deserialize( """{"kind":"list","id":"list","title":"Results","items":[{"id":"a","title":"A","unexpected":true}]}""",
                ExtensionViewJsonContext.Default.ExtensionView ) );

        var form = JsonSerializer.Deserialize(
            "{\"kind\":\"form\",\"id\":\"form\",\"title\":\"Form\",\"fields\":[{\"id\":\"name\",\"label\":\"Name\",\"kind\":\"text\"}],\"submit\":{\"id\":\"submit\",\"kind\":\"callback\",\"title\":\"Save\"}}",
            ExtensionViewJsonContext.Default.ExtensionView )!;

        CommandDeck.Extensions.Views.ViewValidation.Validate( form );
        Equal( 8192, (( CommandDeck.Extensions.Views.FormView ) form).Fields[0].MaxLength );
    }

    private static string WritePackageFixture( string root, ExtensionManifest manifest, string? extraPath = null, int attributes = 0 )
    {
        var path = Path.Combine( root, Guid
                                           .NewGuid( )
                                           .ToString( "N" ) +
                                       ".cdext" );

        using var archive = ZipFile.Open( path, ZipArchiveMode.Create );

        using ( var output = archive
                   .CreateEntry( CommandDeck.ExtensionProtocol.ExtensionPackageFiles.Manifest )
                   .Open( ) )
        {
            output.Write( JsonSerializer.SerializeToUtf8Bytes( manifest, ExtensionManifestJsonContext.Default.ExtensionManifest ) );
        }

        foreach ( var (rid, target) in manifest.Targets )
        {
            using var output = archive
                .CreateEntry( target.Entrypoint )
                .Open( );

            byte[] header = new byte[64];
            "\u007fELF"u8.CopyTo( header );
            header[4] = 2;
            header[5] = 1;
            header[6] = 1;
            header[16] = 3;
            header[18] = rid == ExtensionTargets.LinuxX64 ? ( byte ) 62 : ( byte ) 183;
            output.Write( header );
        }

        if ( extraPath is not null )
        {
            var entry = archive.CreateEntry( extraPath );
            entry.ExternalAttributes = attributes;
            using var output = entry.Open( );
            output.Write( "unexpected"u8 );
        }

        return path;
    }

    private static async Task ExtensionPackagesRejectUnsafeArchivesAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( "commanddeck-package-" +
                                               Guid
                                                   .NewGuid( )
                                                   .ToString( "N" ) );

        Directory.CreateDirectory( root );

        try
        {
            var artifacts = Path.Combine( root, "artifacts" );

            foreach ( var (path, mode) in new[]
                     {
                         ("../escape", 0), ("/absolute", 0), ("bin/linux-x64/link", 0xA000 << 16), ("COMMANDDECK.EXTENSION.JSON", 0), ("bin", 0)
                     } )
            {
                var archive = WritePackageFixture( root, PackageFixture( ), path, mode );

                await ThrowsAsync<InvalidDataException>( async ( ) => await ExtensionArchive.StageAsync( archive, artifacts, CancellationToken.None ) );

                Equal( 0, Directory.GetDirectories( artifacts ).Length );
            }

            var valid = WritePackageFixture( root, PackageFixture( ) );

            await ThrowsAsync<InvalidDataException>( async ( ) =>
                await ExtensionArchive.StageAsync( valid, artifacts, CancellationToken.None, new string( '0', 64 ) ) );

            await using var staged = await ExtensionArchive.StageAsync( valid, artifacts, CancellationToken.None );

            Equal( "org.example.utilities", staged.Manifest.Id );
            True( File.Exists( Path.Combine( staged.StagingDirectory, "content/bin/linux-x64/utility" ) ) );

            var json = JsonSerializer.Serialize( PackageFixture( ), ExtensionManifestJsonContext.Default.ExtensionManifest );

            Throws<InvalidDataException>( ( ) => ManifestValidation.Parse( Encoding.UTF8.GetBytes( json.Insert( 1, "\"manifestVersion\":1," ) ) ) );
            Throws<InvalidDataException>( ( ) => ManifestValidation.Validate( PackageFixture( "1.0.0-01" ) ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task ExtensionPackageLifecyclePreservesIdentityAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( "commanddeck-registry-" +
                                               Guid
                                                   .NewGuid( )
                                                   .ToString( "N" ) );

        Directory.CreateDirectory( root );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            using var registry = await ExtensionPackageRegistry.OpenAsync( catalog, Path.Combine( root, "artifacts" ), CancellationToken.None );

            catalog.Contributions = registry;
            var stale = await catalog.GetPreferencesAsync( CancellationToken.None );

            await using var staged =
                await ExtensionArchive.StageAsync( WritePackageFixture( root, PackageFixture( ) ), registry.ArtifactRoot, CancellationToken.None );

            var installed = await registry.InstallAsync( staged, ExtensionOrigins.Local, 0, true, [], false,
                CancellationToken.None );

            Equal( 0, registry.Snapshot.Commands.Count( command => command.Available ) );
            await registry.SetEnabledAsync( null, true, registry.Document.Revision, CancellationToken.None );

            SearchCoordinator search = new( [new ExtensionCommandProvider( registry, catalog )], new SearchOptions( ), catalog, catalog,
                contributions: registry );

            var result = (await search.SearchAsync( new SearchRequest { Query = "UUID", Limit = 10 }, CancellationToken.None )).Single( );

            Equal( $"extension:{installed.Key}:uuid", result.Id );
            Equal( 1, result.Actions.Count );
            Equal( "extension-open", result.Actions[0].Kind );
            Equal( 0, result.Actions[0].Arguments.Count ); // Discovery carries no executable or effect payload.
            await catalog.SetAliasAsync( result, "uid", CancellationToken.None );
            Equal( result.Id, (await catalog.FindAliasAsync( "uid", CancellationToken.None ))!.Id );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.ReplacePreferencesAsync( stale, CancellationToken.None ) );

            var preferences = await catalog.GetPreferencesAsync( CancellationToken.None );
            SourcePreferencePatch patch = CreatePatch( result, preferences );
            await catalog.PatchSourcePreferencesAsync( patch, CancellationToken.None );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.ReplacePreferencesAsync( preferences, CancellationToken.None ) );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await catalog.PatchSourcePreferencesAsync( patch, CancellationToken.None ) );

            Equal( 0, (await search.SearchAsync( new SearchRequest { Query = "UUID" }, CancellationToken.None )).Count );

            await using var updated = await ExtensionArchive.StageAsync( WritePackageFixture( root, PackageFixture( "1.1.0" ) ), registry.ArtifactRoot,
                CancellationToken.None );

            await registry.InstallAsync( updated, ExtensionOrigins.Local, registry.Document.Revision, true, null, false,
                CancellationToken.None );

            await registry.RollbackAsync( installed.Key, installed.Digest, registry.Document.Revision, CancellationToken.None );

            Equal( "1.0.0", registry.Find( installed.Key ).Manifest.Version );

            await catalog.ReplaceAllAsync( AsyncEnumerable.Empty<IndexedItem>( ), null, CancellationToken.None );

            Equal( result.Id, (await catalog.FindAliasAsync( "uid", CancellationToken.None ))!.Id );
            var filter = registry.Snapshot.Filters.Single( filter => filter.SourceId == result.SourceId );

            await registry.SetEnabledAsync( installed.Key, false, registry.Document.Revision, CancellationToken.None );

            Throws<InvalidOperationException>( ( ) => QueryParser.Parse( filter.Token, registry: registry.Snapshot ) );

            True( await catalog.FindAliasAsync( "uid", CancellationToken.None ) is null );

            await registry.RemoveAsync( installed.Key, false, registry.Document.Revision, CancellationToken.None );

            await using var reinstalled = await ExtensionArchive.StageAsync( WritePackageFixture( root, PackageFixture( "1.2.0" ) ), registry.ArtifactRoot,
                CancellationToken.None );

            await registry.InstallAsync( reinstalled, ExtensionOrigins.Local, registry.Document.Revision, true, null, false,
                CancellationToken.None );

            using var reopened = await ExtensionPackageRegistry.OpenAsync( catalog, registry.ArtifactRoot, CancellationToken.None );

            catalog.Contributions = reopened;
            Equal( result.Id, reopened.Snapshot.Commands.Single( ).StableId );
            Equal( result.Id, (await catalog.FindAliasAsync( "uid", CancellationToken.None ))!.Id );

            True( !(await catalog.GetPreferencesAsync( CancellationToken.None )).Sources.Single( source => source.SourceId == result.SourceId )
                .EnabledInSearch );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static SourcePreferencePatch CreatePatch( SearchResult result, LauncherPreferences preferences )
        {
            return new( )
            {
                RegistryRevision = preferences.RegistryRevision,
                PreferenceRevision = preferences.PreferenceRevision,
                Changes = [new( ) { SourceId = result.SourceId, EnabledInSearch = false }]
            };
        }
    }
}
