using CommandDeck.ExtensionProtocol;

namespace CommandDeck;

public sealed partial class ExtensionPackageRegistry
{
    private static bool IsOriginInvalid( string origin )
    {
        return origin is not { Length: > 0 and <= MaximumOriginCharacters } || origin.Any( char.IsControl );
    }

    private static bool RequiresPermissionReview( bool reviewedPermissionChanges, ExtensionInstallation? previous, IEnumerable<string> requests )
    {
        return !reviewedPermissionChanges &&
               requests
                   .Except( previous?.Manifest.Capabilities.Keys.AsEnumerable( ) ?? [], StringComparer.Ordinal )
                   .Any( );
    }

    private static bool IsPackageIdentityChanged( ExtensionInstallation? previous, string key )
    {
        return previous is not null && previous.Key != key;
    }

    private bool ExceedsPackageLimit( ExtensionInstallation? previous )
    {
        return previous is null && Document.Installations.Length >= ManifestValidation.MaximumPackages;
    }

    private static bool IsRollbackUnavailable( string digest, ExtensionInstallation installation )
    {
        return installation.Removed || !installation.Versions.Any( version => version.Digest == digest );
    }

    private static bool IsInstallationInvalid( ExtensionInstallation installation )
    {
        return installation.Key != InstallationKey( installation.Origin, installation.Manifest.Id ) ||
               installation.Digest.Length != ExtensionArtifactStore.DigestHexCharacters ||
               installation.Digest.Any( c => !char.IsAsciiHexDigit( c ) ) ||
               installation.Versions is null ||
               installation.Versions.Length > MaximumRetainedVersions ||
               installation.Settings is null ||
               installation.Settings.Count > 16 ||
               installation.GrantRevision < 1;
    }

    private static bool IsVersionInvalid( ExtensionVersion? version )
    {
        return version is not { Digest.Length: ExtensionArtifactStore.DigestHexCharacters } || version.Digest.Any( c => !char.IsAsciiHexDigit( c ) );
    }

    private static bool AreGrantsInvalid( ExtensionManifest manifest, string[]? grants )
    {
        return grants is null ||
               grants.Length > 16 ||
               grants
                   .Distinct( StringComparer.Ordinal )
                   .Count( ) !=
               grants.Length ||
               grants.Any( grant => grant is null || !manifest.Capabilities.ContainsKey( grant ) );
    }

    private bool IsExecutionUnavailable( ExtensionInstallation installation )
    {
        return Error is not null || !Document.Enabled || !installation.Enabled || installation.Removed;
    }
}
