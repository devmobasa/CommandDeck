using System.Text;

namespace CommandDeck;

public sealed partial class IndexCoordinator
{
    private const string ExclusionIdKind = "index-exclusion";

    private readonly ExcludedFolderRulesSource _folderRules = new( );

    // The excluded folders applied now. Previews and attachments read the same snapshot.
    public ExcludedFolderRulesSource FolderRules => _folderRules;

    public async ValueTask<IndexRootsResult> ManageExclusionsAsync( string operation, string? path, string? exclusionId, CancellationToken cancellationToken )
    {
        await StartAsync( cancellationToken )
            .ConfigureAwait( false );

        EnsureCatalogMutationOwnership( );

        await _indexLock
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        try
        {
            if ( _rootPreferences is null )
            {
                throw new InvalidOperationException( "Excluded folder preferences are unavailable." );
            }

            var preferences = await LoadRootPreferencesAsync( cancellationToken )
                .ConfigureAwait( false );

            // The rules applied now, not a fresh resolution that a moved link could change.
            var before = _folderRules.Current;
            var after = ChangeExclusions( preferences, operation, path, exclusionId );

            await CommitExclusionChangeAsync( after, before, cancellationToken )
                .ConfigureAwait( false );

            return CreateRootPreferencesResult( after, _incremental.GetStatus( ) );
        }
        finally
        {
            StartBackgroundReindex( );
            _indexLock.Release( );
        }
    }

    // A change that leaves the effective folders as they were, such as turning
    // on a built-in whose folder is also excluded by the user, is only stored.
    private async ValueTask CommitExclusionChangeAsync( IndexRootPreferences after, ExcludedFolderRules before, CancellationToken cancellationToken )
    {
        var next = CreateFolderRules( after );
        if ( string.Equals( next.Fingerprint, before.Fingerprint, StringComparison.Ordinal ) )
        {
            await _rootPreferences!
                .ReplaceIndexRootsAsync( after, cancellationToken )
                .ConfigureAwait( false );

            return;
        }

        var changed = ExcludedFolderRules.ChangedFolders( before, next );

        var scoped = CanScopePurge( changed ) && !ExcludedFolderRules.PatternsDiffer( before, next );

        await CommitRootChangeAsync( after, reset: false, cancellationToken, scoped ? changed : null, next )
            .ConfigureAwait( false );
    }

    // Catalog rows use canonical paths. When a changed folder touches a root
    // that resolves elsewhere, its rows may lie outside the changed ranges.
    private bool CanScopePurge( IReadOnlyList<string> changed )
    {
        return _files
            .Exclusions.ResolveRoots( )
            .Where( static root => !string.Equals( root.Boundary.LexicalRoot, root.Boundary.CanonicalRoot, StringComparison.Ordinal ) )
            .All( root => !changed.Any( folder => IsRelatedFolder( folder, root.Boundary.LexicalRoot )
                || IsRelatedFolder( folder, root.Boundary.CanonicalRoot ) ) );
    }

    private static bool IsRelatedFolder( string first, string second )
    {
        return ExcludedFolderPaths.IsSameOrDescendant( first, second ) || ExcludedFolderPaths.IsSameOrDescendant( second, first );
    }

    private IndexRootPreferences ChangeExclusions( IndexRootPreferences preferences, string operation, string? path, string? exclusionId )
    {
        List<ExcludedFolder> folders = [.. preferences.ExcludedFolders ?? []];
        List<BuiltInExclusionOverride> overrides = [.. preferences.BuiltInExclusionOverrides ?? []];

        switch ( operation )
        {
            case ProtocolOperations.IndexExclusionAdd:
                AddExcludedFolder( preferences, folders, path );

                break;
            case ProtocolOperations.IndexExclusionUpdate:
                UpdateExcludedFolder( preferences, folders, exclusionId, path );

                break;
            case ProtocolOperations.IndexExclusionRemove:
                folders.RemoveAt( FindExcludedFolder( folders, exclusionId ) );

                break;
            case ProtocolOperations.IndexExclusionEnable or ProtocolOperations.IndexExclusionDisable:
                SetBuiltInExclusion( overrides, exclusionId, operation == ProtocolOperations.IndexExclusionEnable );

                break;
            default:
                throw new InvalidOperationException( $"Unknown excluded folder operation '{operation}'." );
        }

        preferences.ExcludedFolders = folders;
        preferences.BuiltInExclusionOverrides = overrides;
        preferences.Validate( );

        return preferences;
    }

    private void AddExcludedFolder( IndexRootPreferences preferences, List<ExcludedFolder> folders, string? path )
    {
        if ( folders.Count >= ExcludedFolderLimits.MaximumUserFolders )
        {
            throw new InvalidOperationException( $"You can exclude up to {ExcludedFolderLimits.MaximumUserFolders} folders." );
        }

        var normalized = NormalizeExclusionPath( preferences, folders, path, null );
        var created = _timeProvider.GetUtcNow( );
        var id = StableId.Create( ExclusionIdKind, $"{normalized}\n{created.ToUnixTimeMilliseconds( )}" );

        folders.Add( new ExcludedFolder { Id = id, Path = normalized, CreatedAt = created } );
    }

    // An edit keeps the id and creation time, so the row keeps its place.
    private void UpdateExcludedFolder( IndexRootPreferences preferences, List<ExcludedFolder> folders, string? exclusionId, string? path )
    {
        var index = FindExcludedFolder( folders, exclusionId );
        var existing = folders[index];
        var normalized = NormalizeExclusionPath( preferences, folders, path, existing.Id );

        folders[index] = new ExcludedFolder { Id = existing.Id, Path = normalized, CreatedAt = existing.CreatedAt };
    }

    private static int FindExcludedFolder( List<ExcludedFolder> folders, string? exclusionId )
    {
        var index = folders.FindIndex( folder => string.Equals( folder.Id, exclusionId, StringComparison.Ordinal ) );
        if ( index < 0 )
        {
            throw new InvalidOperationException( "That excluded folder no longer exists." );
        }

        return index;
    }

    // Only a setting that differs from the default is stored.
    private static void SetBuiltInExclusion( List<BuiltInExclusionOverride> overrides, string? exclusionId, bool enabled )
    {
        var exclusion = BuiltInExclusions.Find( exclusionId ?? "" );
        if ( exclusion is null )
        {
            throw new InvalidOperationException( $"Unknown built-in exclusion '{exclusionId}'." );
        }

        overrides.RemoveAll( setting => string.Equals( setting.Id, exclusion.Id, StringComparison.Ordinal ) );

        if ( enabled != exclusion.EnabledByDefault )
        {
            overrides.Add( new BuiltInExclusionOverride { Id = exclusion.Id, Enabled = enabled } );
        }
    }

    private string NormalizeExclusionPath( IndexRootPreferences preferences, List<ExcludedFolder> folders, string? path, string? editedId )
    {
        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        var normalized = NormalizeLiteralFolder( path, home );

        if ( IsEnabledRoot( preferences, normalized ) )
        {
            throw new InvalidOperationException( "This folder is indexed. Disable or remove it in Indexed folders instead." );
        }

        if ( folders.Any( folder => folder.Path == normalized && folder.Id != editedId ) )
        {
            throw new InvalidOperationException( "That folder is already excluded." );
        }

        var builtIn = FindBuiltInFor( normalized, home );
        if ( builtIn is not null && BuiltInExclusions.IsEnabled( builtIn, preferences.BuiltInExclusionOverrides ) )
        {
            throw new InvalidOperationException( "A built-in exclusion already skips this folder." );
        }

        if ( builtIn is not null )
        {
            throw new InvalidOperationException( "Turn on the built-in exclusion for this folder instead." );
        }

        return normalized;
    }

    private bool IsEnabledRoot( IndexRootPreferences preferences, string normalized )
    {
        return DescribeRoots( preferences )
            .Any( root => root.Enabled && IndexRootPaths.Normalize( root.Options.Path ) == normalized );
    }

    private static BuiltInExclusion? FindBuiltInFor( string normalized, string home )
    {
        // A built-in that keeps your files does not stop you excluding the whole folder.
        return BuiltInExclusions.All.FirstOrDefault( exclusion => exclusion is { Kind: BuiltInExclusionKind.Folder, Keep.Count: 0 }
            && BuiltInExclusions.ResolveAll( exclusion, home ).Contains( normalized, StringComparer.Ordinal ) );
    }

    // Imported folders pass the same literal path rules as typed ones: no
    // environment expansion, no home or filesystem root, and no files.
    private static List<ExcludedFolder> NormalizeImportedFolders( IReadOnlyList<ExcludedFolder>? folders )
    {
        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        List<ExcludedFolder> normalized = [];

        foreach ( var folder in folders ?? [] )
        {
            var path = NormalizeLiteralFolder( folder.Path, home );
            if ( normalized.All( value => value.Path != path ) )
            {
                normalized.Add( new ExcludedFolder { Id = folder.Id, Path = path, CreatedAt = folder.CreatedAt } );
            }
        }

        return normalized;
    }

    private static string NormalizeLiteralFolder( string? path, string home )
    {
        var normalized = ExcludedFolderPaths.Normalize( path, home );
        if ( File.Exists( normalized ) )
        {
            throw new InvalidOperationException( "That is a file. Exclude the folder that contains it." );
        }

        // The home folder can be reached through a link, such as /home on another disk.
        if ( CanonicalPathBoundary.TryCreate( home, out var boundary ) && boundary is not null
             && ExcludedFolderPaths.IsSameOrDescendant( normalized, boundary.CanonicalRoot ) )
        {
            throw new InvalidOperationException( "Excluding the home folder or a folder above it would hide everything." );
        }

        return normalized;
    }

    private List<ManagedIndexExclusion> BuildManagedExclusions( IndexRootPreferences preferences )
    {
        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        List<ManagedIndexExclusion> exclusions = [];

        foreach ( var folder in preferences.ExcludedFolders ?? [] )
        {
            exclusions.Add( new ManagedIndexExclusion
            {
                Id = folder.Id,
                Path = folder.Path,
                Source = IndexExclusionSources.User,
                Enabled = true,
                Exists = Directory.Exists( folder.Path ),
                Issue = DescribeHiddenRoots( preferences, folder.Path )
            } );
        }

        foreach ( var exclusion in BuiltInExclusions.All )
        {
            var location = string.IsNullOrEmpty( home ) ? null : BuiltInExclusions.DescribeLocation( exclusion, home );
            if ( location is not null )
            {
                exclusions.Add( CreateManagedBuiltIn( exclusion, location, home, preferences ) );
            }
        }

        return exclusions;
    }

    // Settings shows where each built-in applies: the first of its folders on
    // this computer, the others it also covers, what it keeps, and the folder
    // names an app cache rule skips.
    private static ManagedIndexExclusion CreateManagedBuiltIn( BuiltInExclusion exclusion, string location, string home, IndexRootPreferences preferences )
    {
        var folders = BuiltInExclusions.ResolveAll( exclusion, home );
        var present = folders.Where( Directory.Exists ).ToArray( );
        var isFolder = exclusion.Kind == BuiltInExclusionKind.Folder;
        var shown = isFolder && present.Length > 0 ? present[0] : location;

        // An app cache row shows its first scope as a pattern; other scopes are listed.
        var shownFolder = isFolder ? shown : BuiltInExclusions.Resolve( exclusion, home );
        var also = present.Where( folder => folder != shownFolder );

        return new ManagedIndexExclusion
        {
            Id = exclusion.Id,
            Path = shown,
            Source = IndexExclusionSources.BuiltIn,
            Category = exclusion.Category,
            Description = exclusion.Description + DescribeDetails( exclusion, also.Select( folder => HomeRelative( folder, home ) ) ),
            Enabled = BuiltInExclusions.IsEnabled( exclusion, preferences.BuiltInExclusionOverrides ),
            EnabledByDefault = exclusion.EnabledByDefault,
            Exists = folders.Count == 0 || present.Length > 0
        };
    }

    private static string DescribeDetails( BuiltInExclusion exclusion, IEnumerable<string> also )
    {
        StringBuilder details = new( );

        if ( exclusion.Kind == BuiltInExclusionKind.AppCaches && exclusion.Names.Count > 1 )
        {
            details.Append( " · folders " ).AppendJoin( ", ", exclusion.Names );
        }

        if ( exclusion.Keep.Count > 0 )
        {
            details.Append( " · keeps " ).AppendJoin( ", ", exclusion.Keep );
        }

        var others = also.ToArray( );
        if ( others.Length > 0 )
        {
            details.Append( " · also " ).AppendJoin( ", ", others );
        }

        return details.ToString( );
    }

    private static string HomeRelative( string folder, string home ) =>
        ExcludedFolderPaths.IsSameOrDescendant( home, folder ) ? "~" + folder[home.TrimEnd( '/' ).Length..] : folder;

    // A user exclusion also hides an indexed folder that lies inside it.
    private string? DescribeHiddenRoots( IndexRootPreferences preferences, string folder )
    {
        var hidden = DescribeRoots( preferences )
            .Where( root => root.Enabled )
            .Select( static root => IndexRootPaths.Normalize( root.Options.Path ) )
            .Where( root => root != folder && ExcludedFolderPaths.IsSameOrDescendant( folder, root ) )
            .ToArray( );

        return hidden.Length == 0 ? null : $"Also hides indexed folder {string.Join( ", ", hidden )}.";
    }
}
