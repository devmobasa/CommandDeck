using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ExtensionInventoryBoundsPreserveSourcesAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( "commanddeck-inventory-" +
                                               Guid
                                                   .NewGuid( )
                                                   .ToString( "N" ) );

        Directory.CreateDirectory( root );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            using var registry = await ExtensionPackageRegistry.OpenAsync( catalog, Path.Combine( root, "artifacts" ), CancellationToken.None );

            catalog.Contributions = registry;

            for ( var index = 0; index < ManifestValidation.MaximumPackages; index++ )
            {
                var manifest = PackageFixture( ) with { Id = "org.example.utility-" + index };

                await using var staged =
                    await ExtensionArchive.StageAsync( WritePackageFixture( root, manifest ), registry.ArtifactRoot, CancellationToken.None );

                await registry.InstallAsync( staged, ExtensionOrigins.Local, registry.Document.Revision, true, [], false,
                    CancellationToken.None );
            }

            Equal( 128, registry.Document.Installations.Length );
            var preferences = await catalog.GetPreferencesAsync( CancellationToken.None );
            Equal( 128 + LauncherSourceRegistry.Configurable.Count, preferences.Sources.Count );

            ProtocolRequestValidator.Validate( new ProtocolRequest
            {
                RequestId = 1,
                Operation = ProtocolOperations.PreferencesUpdate,
                Preferences = preferences
            } );

            var source = preferences.Sources[^1].SourceId;

            await catalog.PatchSourcePreferencesAsync(
                new( )
                {
                    RegistryRevision = preferences.RegistryRevision,
                    PreferenceRevision = preferences.PreferenceRevision,
                    Changes = [new( ) { SourceId = source, EnabledInSearch = false, Position = 1 }]
                }, CancellationToken.None );

            var patched = await catalog.GetPreferencesAsync( CancellationToken.None );
            Equal( source, patched.Sources[0].SourceId );
            True( !patched.Sources[0].EnabledInSearch );
            Equal( preferences.Sources.Count, patched.Sources.Count );

            await using var excess =
                await ExtensionArchive.StageAsync( WritePackageFixture( root, PackageFixture( ) ), registry.ArtifactRoot, CancellationToken.None );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await registry.InstallAsync( excess, ExtensionOrigins.Local, registry.Document.Revision, true, [], false,
                    CancellationToken.None ) );

            Equal( 128L, registry.Document.Revision );
            Equal( 128, registry.Document.Installations.Length );

            Throws<InvalidOperationException>( ( ) => QueryParser.Parse( "ext0000000000: UUID", registry: registry.Snapshot ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task ExtensionActivationFailurePreservesCurrentAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( "commanddeck-activation-" +
                                               Guid
                                                   .NewGuid( )
                                                   .ToString( "N" ) );

        Directory.CreateDirectory( root );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            using var registry = await ExtensionPackageRegistry.OpenAsync( catalog, Path.Combine( root, "artifacts" ), CancellationToken.None );

            catalog.Contributions = registry;

            await using var first =
                await ExtensionArchive.StageAsync( WritePackageFixture( root, PackageFixture( ) ), registry.ArtifactRoot, CancellationToken.None );

            var original = await registry.InstallAsync( first, ExtensionOrigins.Local, 0, true, [], false,
                CancellationToken.None );

            var updateArchive = WritePackageFixture( root, PackageFixture( "1.1.0" ) );
            List<string> revocationOrder = [];

            registry.RegisterRevocation( ExtensionRevocationStage.CommandSessions, ( _, _ ) =>
            {
                revocationOrder.Add( "commands" );

                throw new IOException( "Session shutdown failed after materialization." );
            } );

            registry.RegisterRevocation( ExtensionRevocationStage.Search, ( _, _ ) =>
            {
                revocationOrder.Add( "search" );

                return ValueTask.CompletedTask;
            } );

            await using ( var update = await ExtensionArchive.StageAsync( updateArchive, registry.ArtifactRoot, CancellationToken.None ) )
            {
                await ThrowsAsync<IOException>( async ( ) => await registry.InstallAsync( update, ExtensionOrigins.Local, 1, true, [], false,
                    CancellationToken.None ) );

                Equal( "search,commands", string.Join( ',', revocationOrder ) );

                True( !Directory.Exists( Path.Combine( registry.ArtifactRoot, original.Key, "versions", update.Digest ) ) );
            }

            using var reopened = await ExtensionPackageRegistry.OpenAsync( catalog, registry.ArtifactRoot, CancellationToken.None );

            catalog.Contributions = reopened;
            Equal( 1L, reopened.Document.Revision );
            Equal( original.Digest, reopened.Find( original.Key ).Digest );
            True( Directory.Exists( reopened.ArtifactDirectory( original ) ) );

            await using var retry = await ExtensionArchive.StageAsync( updateArchive, reopened.ArtifactRoot, CancellationToken.None );

            await reopened.InstallAsync( retry, ExtensionOrigins.Local, 1, true, [], false,
                CancellationToken.None );

            Equal( 2L, reopened.Document.Revision );
            Equal( "1.1.0", reopened.Find( original.Key ).Manifest.Version );

            await using var differentBytes = await ExtensionArchive.StageAsync( WritePackageFixture( root, PackageFixture( "1.1.0" ), "extra.txt" ),
                reopened.ArtifactRoot, CancellationToken.None );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await reopened.InstallAsync( differentBytes, ExtensionOrigins.Local, 2, true, [], false,
                CancellationToken.None ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await reopened.InstallAsync( differentBytes, "another-origin", 2, true, [], false,
                CancellationToken.None ) );

            Equal( 2L, reopened.Document.Revision );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task ExtensionArtifactCleanupPreservesRetainedVersionsAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( "commanddeck-artifact-cleanup-" +
                                               Guid
                                                   .NewGuid( )
                                                   .ToString( "N" ) );

        Directory.CreateDirectory( root );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );
            var artifacts = Path.Combine( root, "artifacts" );
            string? discardedDigest = null;
            string key;

            using ( var registry = await ExtensionPackageRegistry.OpenAsync( catalog, artifacts, CancellationToken.None ) )
            {
                catalog.Contributions = registry;

                for ( var index = 0; index < 9; index++ )
                {
                    await using var staged = await ExtensionArchive.StageAsync( WritePackageFixture( root, PackageFixture( $"1.{index}.0" ) ), artifacts,
                        CancellationToken.None );

                    if ( index == 0 )
                    {
                        discardedDigest = staged.Digest;
                    }

                    await registry.InstallAsync( staged, ExtensionOrigins.Local, registry.Document.Revision, true, [], false,
                        CancellationToken.None );
                }

                var installation = registry.Find( "org.example.utilities" );
                key = installation.Key;
                Equal( 8, installation.Versions.Length );
                Equal( 8, Directory.GetDirectories( Path.Combine( artifacts, key, "versions" ) ).Length );
                True( !Directory.Exists( Path.Combine( artifacts, key, "versions", discardedDigest! ) ) );
                True( installation.Versions.All( version => Directory.Exists( Path.Combine( artifacts, key, "versions", version.Digest ) ) ) );

                var rollback = installation.Versions[^1];

                await registry.RollbackAsync( key, rollback.Digest, registry.Document.Revision, CancellationToken.None );

                Equal( rollback.Digest, registry.Find( key ).Digest );
            }

            var orphanDigest = new string( 'a', 64 );
            var orphanVersion = ExtensionArtifactStore.VersionDirectoryFor( artifacts, key, orphanDigest );
            Directory.CreateDirectory( orphanVersion );
            var orphanPackage = Path.Combine( artifacts, new string( 'b', 64 ) );
            Directory.CreateDirectory( orphanPackage );
            var abandonedStaging = ExtensionArtifactStore.StagingDirectoryFor( artifacts, new string( 'c', 32 ) );
            Directory.CreateDirectory( abandonedStaging );

            using var reopened = await ExtensionPackageRegistry.OpenAsync( catalog, artifacts, CancellationToken.None );

            catalog.Contributions = reopened;
            True( !Directory.Exists( orphanVersion ) );
            True( !Directory.Exists( orphanPackage ) );
            True( !Directory.Exists( abandonedStaging ) );

            True( reopened
                .Find( key )
                .Versions.All( version => Directory.Exists( Path.Combine( artifacts, key, "versions", version.Digest ) ) ) );

            var versions = ExtensionArtifactStore.VersionsDirectoryFor( artifacts, key );
            var retainedVersions = Path.Combine( artifacts, key, "retained-versions" );
            var externalVersions = Path.Combine( root, "external-versions" );
            var externalDigest = Path.Combine( externalVersions, new string( 'd', 64 ) );
            var externalSentinel = Path.Combine( externalDigest, "sentinel" );
            Directory.CreateDirectory( externalDigest );
            await File.WriteAllTextAsync( externalSentinel, "outside the artifact root" );
            Directory.Move( versions, retainedVersions );
            Directory.CreateSymbolicLink( versions, externalVersions );

            try
            {
                using var linkSafe = await ExtensionPackageRegistry.OpenAsync( catalog, artifacts, CancellationToken.None );

                catalog.Contributions = linkSafe;
                True( File.Exists( externalSentinel ) );
            }
            finally
            {
                Directory.Delete( versions );
                Directory.Move( retainedVersions, versions );
            }

            var externalRoot = Path.Combine( root, "external-root" );
            var externalRootDigest = Path.Combine( externalRoot, new string( 'e', 64 ) );
            var externalRootSentinel = Path.Combine( externalRootDigest, "sentinel" );
            var linkedArtifactRoot = Path.Combine( root, "linked-artifacts" );
            Directory.CreateDirectory( externalRootDigest );
            await File.WriteAllTextAsync( externalRootSentinel, "outside the artifact root" );
            Directory.CreateSymbolicLink( linkedArtifactRoot, externalRoot );

            using var rootLinkSafe = await ExtensionPackageRegistry.OpenAsync( catalog, linkedArtifactRoot, CancellationToken.None );

            catalog.Contributions = rootLinkSafe;
            True( File.Exists( externalRootSentinel ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
