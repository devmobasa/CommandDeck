namespace CommandDeck;

public static class IndexRootPaths
{
    public static string Normalize( string path ) =>
        Path.TrimEndingDirectorySeparator( PathExpansion.Expand( path ) );
}

public sealed class AddedIndexRoot
{
    public required IndexRootOptions Options { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class IndexRootPreferences
{
    private const int MaximumDisabledRoots = 512;

    public const int MaximumAddedRoots = 128;
    public List<AddedIndexRoot> Added { get; init; } = [];
    public List<string> DisabledConfiguredPaths { get; init; } = [];

    // Null in an import means "keep the current list"; an empty list clears it.
    // Stored preferences always carry both lists.
    public List<ExcludedFolder>? ExcludedFolders { get; set; }
    public List<BuiltInExclusionOverride>? BuiltInExclusionOverrides { get; set; }

    public void Validate( )
    {
        if ( HasInvalidRootCollections( ) )
        {
            throw new InvalidOperationException( "Indexed folders accept at most 128 added and 512 disabled roots." );
        }

        foreach ( var root in Added )
        {
            ConfigurationValidator.ValidateIndexRoot( root?.Options );
        }

        foreach ( var path in DisabledConfiguredPaths )
        {
            ConfigurationValidator.ValidateIndexRoot( new IndexRootOptions { Path = path } );
        }

        ValidateExclusions( );
    }

    private bool HasInvalidRootCollections( )
    {
        return Added is null || DisabledConfiguredPaths is null || Added.Count > MaximumAddedRoots || DisabledConfiguredPaths.Count > MaximumDisabledRoots;
    }

    private void ValidateExclusions( )
    {
        if ( HasTooManyExclusions( ) )
        {
            throw new InvalidOperationException( $"Excluded folders accept at most {ExcludedFolderLimits.MaximumUserFolders} folders and "
                + $"{ExcludedFolderLimits.MaximumOverrides} built-in settings." );
        }

        foreach ( var folder in ExcludedFolders ?? [] )
        {
            if ( IsInvalidExcludedFolder( folder ) )
            {
                throw new InvalidOperationException( "Excluded folders need an id and an absolute path." );
            }
        }

        foreach ( var setting in BuiltInExclusionOverrides ?? [] )
        {
            if ( IsInvalidIdentifier( setting?.Id ) )
            {
                throw new InvalidOperationException( "Built-in exclusion settings need an id." );
            }
        }
    }

    private bool HasTooManyExclusions( )
    {
        return ExcludedFolders?.Count > ExcludedFolderLimits.MaximumUserFolders
            || BuiltInExclusionOverrides?.Count > ExcludedFolderLimits.MaximumOverrides;
    }

    private static bool IsInvalidExcludedFolder( ExcludedFolder? folder )
    {
        return folder is null || IsInvalidIdentifier( folder.Id ) || string.IsNullOrWhiteSpace( folder.Path )
            || folder.Path.Length > ExcludedFolderLimits.MaximumPathCharacters || !Path.IsPathFullyQualified( folder.Path );
    }

    private static bool IsInvalidIdentifier( string? id )
    {
        return string.IsNullOrWhiteSpace( id ) || id.Length > ExcludedFolderLimits.MaximumIdCharacters;
    }
}

public sealed class ManagedIndexRoot
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string Source { get; init; }
    public bool Enabled { get; init; }
    public required string Status { get; init; }
    public string? Issue { get; init; }
    public IndexRootStatus? Runtime { get; init; }
}

public sealed class IndexRootsResult
{
    public bool ReindexPending { get; init; }
    public IndexProgress? Progress { get; init; }
    public string? ReindexError { get; init; }
    public List<ManagedIndexRoot> Roots { get; init; } = [];
    public List<ManagedIndexExclusion> Exclusions { get; init; } = [];
}
