using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public sealed partial class FileIndexSource
{
    // Yields the file entries a scoped scan of one root's directory visits,
    // pruning directories exactly as that scan does, without reading content.
    internal IEnumerable<string> EnumerateDirectoryFiles( string path, CancellationToken cancellationToken )
    {
        string requestedPath;

        try
        {
            requestedPath = Path.GetFullPath( path );
            ClearScopeDiagnostics( requestedPath );
        }
        catch ( Exception exception ) when ( exception is ArgumentException or NotSupportedException )
        {
            yield break;
        }

        var owningRoot = Exclusions.FindRoot( requestedPath );
        if ( owningRoot is null )
        {
            yield break;
        }

        if ( !TryResolveAuthorizedDirectory( owningRoot.Value, requestedPath, out var fullPath ) )
        {
            yield break;
        }

        try
        {
            if ( IsDisallowedDirectoryLink( owningRoot.Value, fullPath ) )
            {
                yield break;
            }
        }
        catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
        {
            yield break;
        }

        Stack<string> directories = new( );
        HashSet<string> visitedDirectories = new( StringComparer.Ordinal );
        directories.Push( fullPath );

        while ( directories.Count > 0 )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            var directory = directories.Pop( );
            if ( !visitedDirectories.Add( directory ) )
            {
                continue;
            }

            IEnumerable<string> entries;

            try
            {
                entries = Directory.EnumerateFileSystemEntries( directory );
            }
            catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
            {
                continue;
            }

            foreach ( var entry in entries )
            {
                cancellationToken.ThrowIfCancellationRequested( );
                FileAttributes attributes;

                try
                {
                    attributes = File.GetAttributes( entry );
                }
                catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
                {
                    continue;
                }

                if ( (attributes & FileAttributes.Directory) != 0 )
                {
                    var entryRoot = Exclusions.FindRoot( entry );
                    if ( IsOutsideOwningRoot( owningRoot.Value, entryRoot ) )
                    {
                        continue;
                    }

                    if ( !TryAuthorizeOfflineDirectory( owningRoot.Value, entry, out var canonicalDirectory, out var reason, out var rule ) )
                    {
                        RecordDiagnostic( owningRoot.Value.Boundary, entry, reason, rule );

                        continue;
                    }

                    directories.Push( canonicalDirectory );

                    continue;
                }

                yield return entry;
            }
        }
    }

    private static bool ContainsHiddenSegment( string relativePath ) =>
        relativePath
            .Split( [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries )
            .Any( static segment => segment is not ("." or "..") && segment.StartsWith( '.', StringComparison.Ordinal ) );

    private static bool IsDisallowedDirectoryLink( (CanonicalPathBoundary Boundary, IndexRootOptions Options) owningRoot, string fullPath )
    {
        return (File.GetAttributes( fullPath ) & FileAttributes.ReparsePoint) != 0 && !owningRoot.Options.FollowSymbolicLinks;
    }

    private static bool IsOutsideOwningRoot( (CanonicalPathBoundary Boundary, IndexRootOptions Options) owningRoot,
        (CanonicalPathBoundary Boundary, IndexRootOptions Options)? entryRoot )
    {
        return entryRoot is null || entryRoot.Value.Boundary.CanonicalRoot != owningRoot.Boundary.CanonicalRoot;
    }

    private bool TryAuthorizeOfflineDirectory( (CanonicalPathBoundary Boundary, IndexRootOptions Options) owningRoot, string entry,
        [NotNullWhen( true )] out string? canonicalDirectory, out string reason, out string? rule )
    {
        canonicalDirectory = default;
        reason = string.Empty;
        rule = default;

        return TryAuthorizeEntry( owningRoot.Boundary, owningRoot.Options, entry, out canonicalDirectory, out reason, out rule ) &&
               (owningRoot.Options.IncludeHidden || !ContainsHiddenSegment( owningRoot.Boundary.GetRelativePath( entry ) ));
    }

    private bool TryResolveAuthorizedDirectory( (CanonicalPathBoundary Boundary, IndexRootOptions Options) root, string requestedPath, out string fullPath )
    {
        return root.Boundary.TryResolve( requestedPath, root.Options.FollowSymbolicLinks, out fullPath ) &&
               Directory.Exists( fullPath ) &&
               TryAuthorizePath( root.Boundary, root.Options, requestedPath, fullPath );
    }
}
