namespace CommandDeck;

internal sealed partial class IndexExclusionPolicy
{
    // A folder may be shown in the file manager when it is an existing folder
    // inside an enabled root and neither of its forms is storage or private.
    public bool TryAuthorizeReveal( string folder, out string canonicalFolder )
    {
        canonicalFolder = string.Empty;

        var root = FindRoot( folder );
        if ( root is null || !Directory.Exists( folder ) )
        {
            return false;
        }

        if ( !root.Value.Boundary.TryMapPath( folder, root.Value.Options.FollowSymbolicLinks, out var canonical ) )
        {
            return false;
        }

        if ( MatchStorageOrPrivacy( folder ) is not null || MatchStorageOrPrivacy( canonical ) is not null )
        {
            return false;
        }

        canonicalFolder = canonical;

        return true;
    }
}
