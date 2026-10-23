using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

internal sealed partial class IndexExclusionPolicy
{
    private const string RepositoryExcludeSuffix = "/.git/info/exclude";

    public bool ShouldObserve( string path )
    {
        var root = FindRoot( path );
        if ( root is null )
        {
            return false;
        }

        if ( !root.Value.Boundary.TryMapPath( path, root.Value.Options.FollowSymbolicLinks, out var canonicalPath ) )
        {
            return false;
        }

        // A deleted directory has no attributes. Its surviving ancestors still
        // enforce subtree exclusions; retaining the leaf event removes stale rows.
        return Match( root.Value.Boundary, root.Value.Options, path, canonicalPath, Directory.Exists( canonicalPath ) ) is null;
    }

    public string[] RuleChangeScopes( string path )
    {
        var registered = _inputs
            .ControlScopes( path )
            .ToArray( );

        var name = Path.GetFileName( path );
        if ( IsUnrelatedControlFile( path, registered, name ) )
        {
            return [];
        }

        _inputs.Invalidate( path );
        List<string> scopes = [.. registered];
        var root = FindRoot( path );
        var parent = Path.GetDirectoryName( path );

        if ( root is not null )
        {
            if ( parent is not null )
            {
                var options = root.Value.Options;
                string? scope = null;

                if ( IsRepositoryRuleChange( name, options ) )
                {
                    scope = parent;
                }
                else if ( IsGeneratedMarkerChange( name, options ) )
                {
                    scope = name == IndexingConstants.PythonEnvironmentMarker ? Path.GetDirectoryName( parent ) : parent;
                }
                else if ( IsRepositoryExcludeChange( path, options ) )
                {
                    scope = Path.GetDirectoryName( Path.GetDirectoryName( parent ) );
                }

                if ( IsObservableScope( scope ) )
                {
                    scopes.Add( scope );
                }
            }
        }

        return scopes
            .SelectMany( AffectedScopes )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );
    }

    public string[] ExternalControlDirectories( )
    {
        var roots = GetCanonicalRoots( );
        _inputs.PruneControlScopes( roots );

        return _inputs
            .ControlFiles( )
            .Where( path => !roots.Any( root => IsWithin( path, root ) ) )
            .Select( static path => ExistingParent( path ) )
            .Where( static path => path is not null )
            .Cast<string>( )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );
    }

    private IEnumerable<string> AffectedScopes( string scope )
    {
        foreach ( (var boundary, _) in ResolveRoots( ) )
        {
            if ( ContainsRoot( scope, boundary ) )
            {
                yield return boundary.LexicalRoot;
            }
            else if ( boundary.Contains( scope ) )
            {
                yield return scope;
            }
        }
    }

    private static string? ExistingParent( string path )
    {
        var parent = Path.GetDirectoryName( path );
        while ( parent is not null && !Directory.Exists( parent ) )
        {
            parent = Path.GetDirectoryName( parent );
        }

        return parent;
    }

    internal static bool IsWithin( string path, string root ) => path == root ||
                                                                 path.StartsWith(
                                                                     Path.EndsInDirectorySeparator( root ) ? root : root + Path.DirectorySeparatorChar,
                                                                     StringComparison.Ordinal );

    private static bool IsUnrelatedControlFile( string path, string[] registered, string name )
    {
        return registered.Length == 0 && !IndexPolicyInputs.IsMarkerName( name ) && !path.EndsWith( RepositoryExcludeSuffix, StringComparison.Ordinal );
    }

    private static bool IsRepositoryRuleChange( string name, IndexRootOptions options )
    {
        return options.RespectGitIgnore && name is RepositoryFileNames.GitIgnoreFileName or IndexingConstants.GitDirectoryName;
    }

    private static bool IsGeneratedMarkerChange( string name, IndexRootOptions options )
    {
        return options.ExcludeGeneratedDirectories && IndexPolicyInputs.IsMarkerName( name );
    }

    private static bool IsRepositoryExcludeChange( string path, IndexRootOptions options )
    {
        return options.RespectGitIgnore && path.EndsWith( RepositoryExcludeSuffix, StringComparison.Ordinal );
    }

    private bool IsObservableScope( [NotNullWhen( true )] string? scope )
    {
        return scope is not null && ShouldObserve( scope );
    }

    private static bool ContainsRoot( string scope, CanonicalPathBoundary boundary )
    {
        return IsWithin( boundary.LexicalRoot, scope ) || IsWithin( boundary.CanonicalRoot, scope );
    }

    private string[] GetCanonicalRoots( )
    {
        return ResolveRoots( )
            .Select( static root => root.Boundary.CanonicalRoot )
            .ToArray( );
    }
}
