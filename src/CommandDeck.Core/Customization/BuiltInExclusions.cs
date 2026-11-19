namespace CommandDeck;

public static class IndexExclusionCategories
{
    public const string Caches = "caches";
    public const string Packages = "packages";
    public const string Agents = "agents";
    public const string Containers = "containers";
    public const string Applications = "applications";
}

public static class IndexExclusionSources
{
    public const string BuiltIn = "builtin";
    public const string User = "user";
}

// The base a built-in folder lives under. XDG bases and the agents' homes
// count only when their variable is set to an absolute path.
public enum ExclusionBase
{
    Home,
    CacheHome,
    DataHome,
    ConfigHome,
    CodexHome,
    ClaudeHome
}

public enum BuiltInExclusionKind
{
    // One folder and everything below it, except its Keep entries.
    Folder,

    // A folder whose path ends with the Names segments, at any depth.
    NamedFolder,

    // Folders with one of the Names, in any app folder below the base folder.
    AppCaches,

    // Database journals, LevelDB files, pid files and editor swap files.
    TransientFiles,

    // Folders marked as caches by a CACHEDIR.TAG file with its signature.
    CacheDirectoryTags
}

// Another place an exclusion's folder can be: under a base, such as the same
// app installed with Flatpak or Snap, or below the folder an environment
// variable names when it holds an absolute path. A variable with a separator
// holds a list, and each entry counts.
public sealed record ExclusionLocation( ExclusionBase Base, string RelativePath )
{
    public string? Variable { get; init; }
    public char? Separator { get; init; }

    public static ExclusionLocation Under( ExclusionBase root, string relativePath ) => new( root, relativePath );

    public static ExclusionLocation FromVariable( string variable, string relativePath, char? separator = null ) =>
        new( ExclusionBase.Home, relativePath ) { Variable = variable, Separator = separator };
}

public sealed record BuiltInExclusion( string Id, string Category, ExclusionBase Base, string RelativePath, string Description, bool EnabledByDefault )
{
    public BuiltInExclusionKind Kind { get; init; } = BuiltInExclusionKind.Folder;

    // Paths inside a Folder that stay indexed: files you write. Each is
    // relative to the folder, and * stands for any one name.
    public IReadOnlyList<string> Keep { get; init; } = [];

    // NamedFolder: the trailing path segments. AppCaches: the folder names.
    public IReadOnlyList<string> Names { get; init; } = [];

    // Other places the same folder can be. Folder and AppCaches only.
    public IReadOnlyList<ExclusionLocation> Also { get; init; } = [];

    // AppCaches: only folders directly inside each app folder.
    public bool TopLevel { get; init; }
}

public static class BuiltInExclusions
{
    private const string AnyFolder = "**";
    private const string AnyApp = "<app>";
    private const string TransientFilePatterns = "**/*-wal, *-shm, *-journal beside SQLite databases, LevelDB files, *.pid, .*.swp, .*.swo";
    private const string CacheDirectoryTagPattern = "**/<folder with CACHEDIR.TAG>";

    public static IReadOnlyList<BuiltInExclusion> All { get; } =
    [
        .. BuiltInExclusionCatalog.Caches,
        .. BuiltInExclusionCatalog.Packages,
        .. BuiltInExclusionCatalog.Agents,
        .. BuiltInExclusionCatalog.Containers,
        .. BuiltInExclusionCatalog.Applications
    ];

    public static BuiltInExclusion? Find( string id ) =>
        All.FirstOrDefault( exclusion => string.Equals( exclusion.Id, id, StringComparison.Ordinal ) );

    // The folder for this user (the scope folder for app caches), or null for
    // kinds without one, and when a misconfigured base would make it the home
    // folder, an ancestor of it, or the filesystem root.
    public static string? Resolve( BuiltInExclusion exclusion, string home )
    {
        ArgumentNullException.ThrowIfNull( exclusion );
        ArgumentException.ThrowIfNullOrWhiteSpace( home );

        if ( !HasFolder( exclusion ) )
        {
            return null;
        }

        return BelowHome( Path.Join( ResolveBase( exclusion.Base, home ), exclusion.RelativePath ), home );
    }

    // Every folder the exclusion covers for this user: its own, then each other
    // location that applies, once each.
    public static IReadOnlyList<string> ResolveAll( BuiltInExclusion exclusion, string home )
    {
        List<string> folders = [];
        AddFolder( folders, Resolve( exclusion, home ) );

        if ( !HasFolder( exclusion ) )
        {
            return folders;
        }

        foreach ( var location in exclusion.Also )
        {
            foreach ( var baseDirectory in ResolveLocationBases( location, home ) )
            {
                AddFolder( folders, BelowHome( Path.Join( baseDirectory, location.RelativePath ), home ) );
            }
        }

        return folders;
    }

    // Where Settings says the exclusion applies, or null when it cannot apply.
    public static string? DescribeLocation( BuiltInExclusion exclusion, string home )
    {
        ArgumentNullException.ThrowIfNull( exclusion );

        return exclusion.Kind switch
        {
            BuiltInExclusionKind.NamedFolder => AnyFolder + "/" + string.Join( "/", exclusion.Names ),
            BuiltInExclusionKind.TransientFiles => TransientFilePatterns,
            BuiltInExclusionKind.CacheDirectoryTags => CacheDirectoryTagPattern,
            BuiltInExclusionKind.AppCaches => Resolve( exclusion, home ) is { } scope ? DescribeScope( exclusion, scope ) : null,
            _ => Resolve( exclusion, home )
        };
    }

    // The app cache folders one scope folder covers, such as ~/.config/<app>/**/Cache.
    public static string DescribeScope( BuiltInExclusion exclusion, string scope )
    {
        ArgumentNullException.ThrowIfNull( exclusion );

        var depth = exclusion.TopLevel ? "/" : "/" + AnyFolder + "/";
        var names = exclusion.Names.Count == 1 ? exclusion.Names[0] : $"<{exclusion.Names.Count} cache folders>";

        return scope + "/" + AnyApp + depth + names;
    }

    public static bool IsEnabled( BuiltInExclusion exclusion, IReadOnlyList<BuiltInExclusionOverride>? overrides )
    {
        var setting = overrides?.LastOrDefault( value => string.Equals( value.Id, exclusion.Id, StringComparison.Ordinal ) );

        return setting?.Enabled ?? exclusion.EnabledByDefault;
    }

    private static string ResolveBase( ExclusionBase kind, string home )
    {
        return kind switch
        {
            ExclusionBase.CacheHome => AbsoluteOr( XdgEnvironmentNames.CacheHome, Path.Join( home, ".cache" ) ),
            ExclusionBase.DataHome => AbsoluteOr( XdgEnvironmentNames.DataHome, Path.Join( home, ".local", "share" ) ),
            ExclusionBase.ConfigHome => AbsoluteOr( XdgEnvironmentNames.ConfigHome, Path.Join( home, ".config" ) ),
            ExclusionBase.CodexHome => AbsoluteOr( EnvironmentVariableNames.CodexHome, Path.Join( home, ".codex" ) ),
            ExclusionBase.ClaudeHome => AbsoluteOr( EnvironmentVariableNames.ClaudeConfigDirectory, Path.Join( home, ".claude" ) ),
            _ => home
        };
    }

    private static string AbsoluteOr( string variable, string fallback )
    {
        var value = Environment.GetEnvironmentVariable( variable );

        return string.IsNullOrEmpty( value ) || !Path.IsPathFullyQualified( value ) ? fallback : value;
    }

    private static bool HasFolder( BuiltInExclusion exclusion ) =>
        exclusion.Kind is BuiltInExclusionKind.Folder or BuiltInExclusionKind.AppCaches;

    // A base folder, or each absolute entry of the variable that names one.
    private static IEnumerable<string> ResolveLocationBases( ExclusionLocation location, string home )
    {
        if ( location.Variable is null )
        {
            return [ResolveBase( location.Base, home )];
        }

        var value = Environment.GetEnvironmentVariable( location.Variable );
        if ( string.IsNullOrEmpty( value ) )
        {
            return [];
        }

        var entries = location.Separator is { } separator ? value.Split( separator, StringSplitOptions.RemoveEmptyEntries ) : [value];

        return entries.Where( Path.IsPathFullyQualified );
    }

    private static string? BelowHome( string path, string home )
    {
        var folder = Path.TrimEndingDirectorySeparator( Path.GetFullPath( path ) );

        return IsHomeOrAncestor( folder, home ) ? null : folder;
    }

    private static void AddFolder( List<string> folders, string? folder )
    {
        if ( folder is not null && !folders.Contains( folder, StringComparer.Ordinal ) )
        {
            folders.Add( folder );
        }
    }

    private static bool IsHomeOrAncestor( string folder, string home )
    {
        var normalizedHome = Path.TrimEndingDirectorySeparator( Path.GetFullPath( home ) );

        return ExcludedFolderPaths.IsSameOrDescendant( folder, normalizedHome );
    }
}
