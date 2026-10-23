using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommandDeck;

internal sealed partial class IndexExclusionPolicy
{
    private readonly IndexingOptions _options;
    private readonly PathPolicy _privacy;
    private readonly IndexStoragePaths? _storage;
    private readonly IndexPolicyInputs _inputs = new( );
    private readonly GeneratedDirectoryPolicy _generated;
    private readonly RepositoryIgnorePolicy _repository;
    private readonly CanonicalPathBoundary?[]? _rootBoundaries;

    // Indexing caches each root's boundary and revalidates it on every use;
    // on-demand previews resolve roots afresh.
    public IndexExclusionPolicy( IndexingOptions options, PrivacyOptions privacy, string? databasePath = null, bool cacheRootBoundaries = false )
    {
        _options = options;
        _privacy = new PathPolicy( privacy );
        _storage = databasePath is null ? null : new IndexStoragePaths( databasePath );
        _generated = new GeneratedDirectoryPolicy( _inputs );
        _repository = new RepositoryIgnorePolicy( _inputs );
        _rootBoundaries = cacheRootBoundaries ? new CanonicalPathBoundary?[options.Roots.Count] : null;

        var configuration = "index-exclusions-v1\n" +

                            // Scheduler and watcher tuning do not change which paths are allowed.
                            JsonSerializer.Serialize( new IndexingOptions { Roots = options.Roots }, CommandDeckJsonContext.Default.IndexingOptions ) +
                            "\n" +
                            JsonSerializer.Serialize( privacy, CommandDeckJsonContext.Default.PrivacyOptions );

        ConfigurationFingerprint = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( configuration ) ) );
    }

    public string ConfigurationFingerprint { get; }
    public long InputRevision => _inputs.Revision;
    public PolicyInputStamp[] SnapshotInputs( out long revision ) => _inputs.Snapshot( out revision );

    public bool RestoreInputsIfCurrent( IReadOnlyList<PolicyInputStamp> inputs, CancellationToken cancellationToken = default ) =>
        _inputs.RestoreIfCurrent( inputs, cancellationToken );

    public IndexExclusion? Match( CanonicalPathBoundary boundary, IndexRootOptions options, string requestedPath, string canonicalPath, bool directory ) =>
        MatchOnePath( boundary, options, requestedPath, directory ) ?? (MatchCanonicalPath( boundary, options, requestedPath, canonicalPath, directory ));

    public (CanonicalPathBoundary Boundary, IndexRootOptions Options)? FindRoot( string path )
    {
        var options = _options.Roots;
        List<(CanonicalPathBoundary Boundary, IndexRootOptions Options)> candidates = [];

        for ( var index = 0; index < options.Count; index++ )
        {
            var boundary = GetRootBoundary( index, options[index] );
            if ( ContainsRequestedPath( path, boundary ) )
            {
                candidates.Add( (boundary, options[index]) );
            }
        }

        if ( candidates.Count == 0 )
        {
            return null;
        }

        var maximum = candidates.Max( static candidate => candidate.Boundary.CanonicalRoot.Length );
        var mostSpecific = FindMostSpecificRoots( candidates, maximum );

        return mostSpecific.Length == 1 ? mostSpecific[0] : null;
    }

    // Every configured root that currently exists, in configuration order.
    public IReadOnlyList<(CanonicalPathBoundary Boundary, IndexRootOptions Options)> ResolveRoots( )
    {
        var options = _options.Roots;
        List<(CanonicalPathBoundary Boundary, IndexRootOptions Options)> roots = new( options.Count );

        for ( var index = 0; index < options.Count; index++ )
        {
            var boundary = GetRootBoundary( index, options[index] );
            if ( boundary is not null )
            {
                roots.Add( (boundary, options[index]) );
            }
        }

        return roots;
    }

    private CanonicalPathBoundary? GetRootBoundary( int index, IndexRootOptions options )
    {
        if ( _rootBoundaries is not null )
        {
            var cached = Volatile.Read( ref _rootBoundaries[index] );
            if ( IsCurrentBoundary( cached ) )
            {
                return cached;
            }
        }

        if ( !CanonicalPathBoundary.TryCreate( options.Path, out var boundary ) )
        {
            boundary = null;
        }

        if ( _rootBoundaries is not null )
        {
            Volatile.Write( ref _rootBoundaries[index], boundary );
        }

        return boundary;
    }

    private IndexExclusion? MatchOnePath( CanonicalPathBoundary boundary, IndexRootOptions options, string path, bool directory )
    {
        if ( _storage?.Contains( path ) == true )
        {
            return new IndexExclusion( IndexExclusionReasons.IndexStorage, null );
        }

        foreach ( var ancestor in RepositoryIgnorePolicy.Ancestors( path, includeLeaf: true ) )
        {
            if ( _privacy.TryGetDenial( ancestor, out var privacyRule ) )
            {
                return new IndexExclusion( IndexExclusionReasons.PrivacyRule, privacyRule );
            }
        }

        var relative = boundary.GetRelativePath( path );
        if ( IsHiddenPathExcluded( options, relative ) )
        {
            return new IndexExclusion( IndexExclusionReasons.Hidden, "includeHidden=false" );
        }

        if ( TryMatchRootPatterns( boundary, path, options.ExcludeGlobs, out var explicitRule ) )
        {
            return new IndexExclusion( IndexExclusionReasons.RootGlob, explicitRule );
        }

        if ( IsIncludedByRootPatterns( boundary, options, path, directory ) )
        {
            return null;
        }

        if ( options.ExcludeGeneratedDirectories )
        {
            var generated = MatchGeneratedDirectory( boundary, path, directory );
            if ( generated is not null )
            {
                return generated;
            }
        }

        return options.RespectGitIgnore ? _repository.Match( path, directory ) : null;
    }

    private IndexExclusion? MatchGeneratedDirectory( CanonicalPathBoundary boundary, string path, bool directory )
    {
        foreach ( var ancestor in RepositoryIgnorePolicy.Ancestors( path, directory ) )
        {
            // An explicitly configured root is not suppressed merely for being
            // named bin/target/node_modules. Defaults apply to its descendants.
            if ( IsOutsideAncestorTraversal( boundary, ancestor ) )
            {
                continue;
            }

            var rule = _generated.Match( ancestor );
            if ( rule is not null )
            {
                return new IndexExclusion( IndexExclusionReasons.GeneratedDirectory, rule );
            }
        }

        return null;
    }

    private static bool TryMatchRootPatterns( CanonicalPathBoundary boundary, string path, IEnumerable<string> patterns, out string matched )
    {
        for ( string? current = path; current is not null && boundary.Contains( current ); current = Path.GetDirectoryName( current ) )
        {
            if ( PathPolicy.TryMatch( current, patterns, out matched ) )
            {
                return true;
            }

            if ( PathPolicy.TryMatch( boundary.GetRelativePath( current ), patterns, out matched ) )
            {
                return true;
            }
        }

        matched = string.Empty;

        return false;
    }

    private static bool CouldIncludeDescendant( CanonicalPathBoundary boundary, string directory, List<string> patterns )
    {
        foreach ( var raw in patterns )
        {
            var pattern = raw.Replace( '\\', '/' );
            if ( pattern.StartsWith( '~' ) || pattern.StartsWith( '$' ) )
            {
                pattern = PathExpansion.Expand( pattern );
            }

            if ( !pattern.Contains( '/' ) )
            {
                return true;
            }

            var wildcard = pattern.IndexOfAny( ['*', '?'] );
            var prefix = wildcard < 0 ? pattern : pattern[..wildcard];
            var candidate = Path.IsPathRooted( pattern ) ? directory : boundary.GetRelativePath( directory );
            candidate = candidate == "." ? "" : candidate.TrimEnd( '/' ) + "/";

            if ( CouldMatchDescendant( wildcard, prefix, candidate ) )
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsRequestedPath( string path, [NotNullWhen( true )] CanonicalPathBoundary? boundary )
    {
        return boundary is not null && boundary.Contains( path );
    }

    private static bool IsCurrentBoundary( [NotNullWhen( true )] CanonicalPathBoundary? cached )
    {
        return cached is not null && cached.IsCurrent( );
    }

    private static bool IsHiddenPathExcluded( IndexRootOptions options, string relative )
    {
        return !options.IncludeHidden &&
               relative
                   .Split( Path.DirectorySeparatorChar )
                   .Any( static segment => segment.Length > 1 && segment[0] == '.' && segment != ".." );
    }

    private static bool IsIncludedByRootPatterns( CanonicalPathBoundary boundary, IndexRootOptions options, string path, bool directory )
    {
        return TryMatchRootPatterns( boundary, path, options.IncludeGlobs, out _ ) ||
               directory && CouldIncludeDescendant( boundary, path, options.IncludeGlobs );
    }

    private static bool IsOutsideAncestorTraversal( CanonicalPathBoundary boundary, string ancestor )
    {
        return !boundary.Contains( ancestor ) || ancestor == boundary.LexicalRoot || ancestor == boundary.CanonicalRoot;
    }

    private static bool CouldMatchDescendant( int wildcard, string prefix, string candidate )
    {
        return prefix.StartsWith( candidate, StringComparison.Ordinal ) || wildcard >= 0 && candidate.StartsWith( prefix, StringComparison.Ordinal );
    }

    private static (CanonicalPathBoundary Boundary, IndexRootOptions Options)[] FindMostSpecificRoots(
        List<(CanonicalPathBoundary Boundary, IndexRootOptions Options)> candidates, int maximum )
    {
        return candidates
            .Where( candidate => candidate.Boundary.CanonicalRoot.Length == maximum )
            .ToArray( );
    }

    private IndexExclusion? MatchCanonicalPath( CanonicalPathBoundary boundary, IndexRootOptions options, string requestedPath, string canonicalPath,
        bool directory )
    {
        if ( string.Equals( requestedPath, canonicalPath, StringComparison.Ordinal ) )
        {
            return null;
        }

        return MatchOnePath( boundary, options, canonicalPath, directory );
    }
}
