namespace CommandDeck;

public static class ExcludedFolderLimits
{
    public const int MaximumUserFolders = 32;
    public const int MaximumPathCharacters = 512;
    public const int MaximumOverrides = 256;
    public const int MaximumIdCharacters = 64;
}

// A folder the user excluded in Settings or with the CLI. The id survives an
// edit of the path, so focus and queued changes follow the same row.
public sealed class ExcludedFolder
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// A built-in exclusion turned on or off against its default. Unknown ids are
// kept, so a newer backup restores into an older list without loss.
public sealed class BuiltInExclusionOverride
{
    public required string Id { get; init; }
    public bool Enabled { get; init; }
}

public sealed class ManagedIndexExclusion
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string Source { get; init; }
    public string? Category { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public bool EnabledByDefault { get; init; }
    public bool Exists { get; init; }
    public string? Issue { get; init; }
}

public static class ExcludedFolderPaths
{
    private const string HomePrefix = "~/";

    // True when path is folder or lies below it, compared by whole components.
    public static bool IsSameOrDescendant( string folder, string path )
    {
        if ( !path.StartsWith( folder, StringComparison.Ordinal ) )
        {
            return false;
        }

        return path.Length == folder.Length || path[folder.Length] == '/' || folder == "/";
    }

    // Accepts an absolute path or one that starts with ~/, and returns it
    // expanded, without a trailing separator. Throws with a message for the user.
    public static string Normalize( string? raw, string home )
    {
        var value = raw?.Trim( ) ?? "";
        if ( value.Length == 0 )
        {
            throw new InvalidOperationException( "Enter a folder path." );
        }

        if ( value.Length > ExcludedFolderLimits.MaximumPathCharacters )
        {
            throw new InvalidOperationException( $"Folder paths are limited to {ExcludedFolderLimits.MaximumPathCharacters} characters." );
        }

        if ( !IsAbsoluteOrHomeRelative( value ) )
        {
            throw new InvalidOperationException( "Use an absolute path or one that starts with ~/." );
        }

        if ( HasRelativeSegment( value ) || value.Contains( '\0' ) )
        {
            throw new InvalidOperationException( "Folder paths cannot contain . or .. segments." );
        }

        var expanded = value.StartsWith( HomePrefix, StringComparison.Ordinal ) ? Path.Join( home, value[HomePrefix.Length..] ) : value;
        var normalized = Path.TrimEndingDirectorySeparator( Path.GetFullPath( expanded ) );
        var normalizedHome = Path.TrimEndingDirectorySeparator( Path.GetFullPath( home ) );

        if ( IsSameOrDescendant( normalized, normalizedHome ) )
        {
            throw new InvalidOperationException( "Excluding the home folder or a folder above it would hide everything." );
        }

        return normalized;
    }

    private static bool IsAbsoluteOrHomeRelative( string value )
    {
        return value.StartsWith( '/' ) || value.StartsWith( HomePrefix, StringComparison.Ordinal );
    }

    private static bool HasRelativeSegment( string value )
    {
        return value
            .Split( '/' )
            .Any( static segment => segment is "." or ".." );
    }
}
