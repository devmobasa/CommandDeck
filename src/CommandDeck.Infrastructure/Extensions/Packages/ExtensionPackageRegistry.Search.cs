namespace CommandDeck;

public sealed partial class ExtensionPackageRegistry
{
    public ValueTask SetGlobalSearchAsync( string key, bool enabled, long expectedRevision, CancellationToken cancellationToken ) =>
        MutateAsync( key, expectedRevision, installation =>
        {
            if ( CannotEnableSearch( enabled, installation ) )
            {
                throw new InvalidOperationException( "Global search requires an installed release search provider." );
            }

            return installation with { GlobalSearchEnabled = enabled };
        }, cancellationToken );

    private static bool CannotEnableSearch( bool enabled, ExtensionInstallation installation )
    {
        return installation.Removed || installation.Manifest.SearchProviders.Length == 0 || enabled && installation.Origin == ExtensionOrigins.Development;
    }
}
