namespace CommandDeck;

// Folders a tool tagged as a cache with CACHEDIR.TAG are skipped below an
// indexed folder, never the indexed folder itself. The tag is read through the
// policy inputs, so a tag that appears or goes away rescans its folder.
internal sealed partial class IndexExclusionPolicy
{
    // Traversal: a folder reached through folders already checked.
    private IndexExclusion? MatchCacheDirectory( string path, bool directory )
    {
        return directory && _folders.CacheDirectoryTagsId is not null && IsTaggedCache( path ) ? CacheDirectoryExclusion( ) : null;
    }

    // Any path inside a tagged folder that lies below the indexed folder.
    private IndexExclusion? MatchInCacheDirectory( CanonicalPathBoundary boundary, string path, bool directory )
    {
        if ( _folders.CacheDirectoryTagsId is null )
        {
            return null;
        }

        foreach ( var ancestor in RepositoryIgnorePolicy.Ancestors( path, directory ) )
        {
            if ( !IsOutsideAncestorTraversal( boundary, ancestor ) && IsTaggedCache( ancestor ) )
            {
                return CacheDirectoryExclusion( );
            }
        }

        return null;
    }

    private bool IsTaggedCache( string directory ) =>
        _inputs.Markers( directory ).Contains( IndexingConstants.CacheDirectoryTag )
        && _inputs
            .ReadText( Path.Join( directory, IndexingConstants.CacheDirectoryTag ) )
            .Text.StartsWith( IndexingConstants.CacheDirectoryTagSignature, StringComparison.Ordinal );

    private static IndexExclusion CacheDirectoryExclusion( ) =>
        new( IndexExclusionReasons.ExcludedFolder, IndexingConstants.CacheDirectoryTag );
}
