namespace CommandDeck;

internal static class ExtensionInventoryProjector
{
    internal const int MaximumInventoryPageSize = 32;

    public static ExtensionInventory Project( ExtensionRegistryDocument document, string? error, ExtensionManagementOutcome outcome, string? query, int offset,
        int limit )
    {
        var packages = GetMatchingExtensionPackages( document, query );

        return new ExtensionInventory
        {
            Revision = document.Revision,
            Enabled = document.Enabled,
            Error = error,
            Total = packages.Length,
            Offset = offset,
            Manifest = outcome.Manifest,
            ArchiveDigest = outcome.ArchiveDigest,
            Settings = outcome.Settings,
            Diagnostics = outcome.Diagnostics,
            Debug = outcome.Debug,
            Connections = outcome.Connections,
            ConnectionMessage = outcome.ConnectionMessage,
            EffectAttempts = outcome.EffectAttempts,
            Packages = packages
                .Skip( offset )
                .Take( Math.Clamp( limit, 1, MaximumInventoryPageSize ) )
                .Select( package => new ExtensionPackageSummary
                {
                    Key = package.Key,
                    Id = package.Manifest.Id,
                    Name = package.Manifest.Name,
                    Version = package.Manifest.Version,
                    Origin = package.Origin,
                    Digest = package.Digest,
                    Enabled = package.Enabled,
                    Removed = package.Removed,
                    GlobalSearchEnabled = package.GlobalSearchEnabled,
                    Grants = [.. package.Grants],
                    RetainedDigests = package
                        .Versions.Select( version => version.Digest )
                        .ToArray( ),
                    RetainedVersions = package
                        .Versions.Select( version => new ExtensionRetainedVersion( version.Version, version.Digest ) )
                        .ToArray( )
                } )
                .ToList( )
        };
    }

    private static ExtensionInstallation[] GetMatchingExtensionPackages( ExtensionRegistryDocument document, string? query )
    {
        return document
            .Installations.Where( package =>
                string.IsNullOrEmpty( query ) ||
                package.Manifest.Name.Contains( query, StringComparison.OrdinalIgnoreCase ) ||
                package.Manifest.Id.Contains( query, StringComparison.OrdinalIgnoreCase ) )
            .ToArray( );
    }
}
