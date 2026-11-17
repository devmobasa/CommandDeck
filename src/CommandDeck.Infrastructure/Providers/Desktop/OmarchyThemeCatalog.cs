using System.Text;
using System.Text.RegularExpressions;

namespace CommandDeck;

/// <summary>
/// One installed Omarchy theme: its folder name, which <c>omarchy-theme-set</c>
/// takes, the name Omarchy shows, where it lives, and its preview image, if any.
/// </summary>
public sealed record OmarchyTheme( string Name, string DisplayName, string Directory, string? PreviewPath, bool IsUserTheme, bool IsCurrent );

/// <summary>
/// The folders Omarchy keeps themes in. A theme in the user folder replaces the
/// Omarchy theme of the same name, as <c>omarchy-theme-dir</c> chooses it.
/// </summary>
public sealed record OmarchyThemePaths( string UserThemes, string OmarchyThemes, string CurrentThemeName )
{
    private const string SystemOmarchyPath = "/usr/share/omarchy";

    public static OmarchyThemePaths Default( )
    {
        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        // OMARCHY_PATH names the install; a packaged Omarchy lives in /usr/share,
        // and an install from git in ~/.local/share.
        var omarchy = Environment.GetEnvironmentVariable( EnvironmentVariableNames.OmarchyPath );
        if ( string.IsNullOrWhiteSpace( omarchy ) || !Path.IsPathFullyQualified( omarchy ) )
        {
            omarchy = Directory.Exists( Path.Combine( SystemOmarchyPath, "themes" ) ) ? SystemOmarchyPath : Path.Combine( home, ".local", "share", "omarchy" );
        }

        return new OmarchyThemePaths( Path.Combine( home, ".config", "omarchy", "themes" ), Path.Combine( omarchy, "themes" ),
            Path.Combine( home, ".local", "state", "omarchy", "current", "theme.name" ) );
    }
}

/// <summary>
/// Reads the installed Omarchy themes the way <c>omarchy-theme-list</c> and the
/// Omarchy theme picker do. It only reads folder listings and one small file.
/// </summary>
public static partial class OmarchyThemeCatalog
{
    public const int MaximumThemes = 512;
    private const int MaximumNameFileBytes = 256;
    // Files read from one theme folder, or its backgrounds folder, when looking for a preview.
    private const int MaximumThemeFiles = 1024;
    private const string BackgroundsFolder = "backgrounds";

    // The images Omarchy's picker uses as a preview, in its order. Videos are left out.
    private static readonly string[] PreviewNames = ["preview.png", "preview.jpg", "preview.jpeg", "preview.webp", "preview.gif", "preview.bmp"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

    public static bool IsAvailable( OmarchyThemePaths paths ) => Directory.Exists( paths.OmarchyThemes ) || Directory.Exists( paths.UserThemes );

    public static IReadOnlyList<OmarchyTheme> Read( OmarchyThemePaths paths, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( paths );

        var current = ReadCurrentName( paths.CurrentThemeName );
        Dictionary<string, (string Directory, bool IsUserTheme)> found = new( StringComparer.Ordinal );

        foreach ( var directory in ListThemeDirectories( paths.UserThemes ) )
        {
            found.TryAdd( Path.GetFileName( directory ), (directory, true) );
        }

        foreach ( var directory in ListThemeDirectories( paths.OmarchyThemes ) )
        {
            found.TryAdd( Path.GetFileName( directory ), (directory, false) );
        }

        List<OmarchyTheme> themes = [];

        foreach ( (var name, (var directory, var isUserTheme)) in found )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            if ( themes.Count >= MaximumThemes )
            {
                break;
            }

            // A user theme without its own preview shows the Omarchy theme it replaces.
            var preview = FindPreview( directory ) ?? (isUserTheme ? FindPreview( Path.Combine( paths.OmarchyThemes, name ) ) : null);
            var isCurrent = string.Equals( name, current, StringComparison.Ordinal );
            themes.Add( new OmarchyTheme( name, DisplayName( name ), directory, preview, isUserTheme, isCurrent ) );
        }

        themes.Sort( static ( left, right ) => string.Compare( left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase ) );

        return themes;
    }

    // "catppuccin-latte" becomes "Catppuccin Latte", as omarchy-theme-list prints it.
    public static string DisplayName( string name )
    {
        ArgumentNullException.ThrowIfNull( name );

        var words = name
            .Split( '-', StringSplitOptions.RemoveEmptyEntries )
            .Select( static word => char.IsAsciiLetterLower( word[0] ) ? char.ToUpperInvariant( word[0] ) + word[1..] : word );

        return string.Join( ' ', words );
    }

    // Only folder names that omarchy-theme-set takes unchanged are listed, so a
    // name never needs quoting in the command that applies it. The pattern
    // anchors the whole string: a trailing newline, which the command's own
    // reading would drop, does not pass.
    public static bool IsValidName( string name ) => ThemeNamePattern( ).IsMatch( name );

    // The folder a preview from FindPreview must stay inside: the theme folder,
    // for preview.* there or an image in its backgrounds folder.
    public static string PreviewRoot( string previewPath )
    {
        ArgumentNullException.ThrowIfNull( previewPath );

        var directory = Path.GetDirectoryName( previewPath ) ?? previewPath;

        return string.Equals( Path.GetFileName( directory ), BackgroundsFolder, StringComparison.Ordinal )
            ? Path.GetDirectoryName( directory ) ?? directory
            : directory;
    }

    internal static string? ReadCurrentName( string path )
    {
        try
        {
            // Opened without blocking and refused unless it is a regular file, so
            // a pipe or device in its place cannot stall a search; the read is
            // bounded even if the file grows after it is opened.
            using var file = SafeRegularFile.Open( path, followLeafSymbolicLink: true );
            Span<byte> buffer = stackalloc byte[MaximumNameFileBytes + 1];
            var length = RandomAccess.Read( file.Handle, buffer, 0 );
            if ( length > MaximumNameFileBytes )
            {
                return null;
            }

            var name = Encoding.UTF8
                .GetString( buffer[..length] )
                .Trim( );

            return IsValidName( name ) ? name : null;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                 or PlatformNotSupportedException )
        {
            return null;
        }
    }

    private static string[] ListThemeDirectories( string root )
    {
        if ( !Directory.Exists( root ) )
        {
            return [];
        }

        try
        {
            // A theme may be a link to a checkout elsewhere; Directory.Exists follows it.
            return Directory
                .EnumerateFileSystemEntries( root )
                .Where( static path => IsValidName( Path.GetFileName( path ) ) && Directory.Exists( path ) )
                .Order( StringComparer.Ordinal )
                .Take( MaximumThemes )
                .ToArray( );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            return [];
        }
    }

    private static string? FindPreview( string directory )
    {
        try
        {
            if ( !Directory.Exists( directory ) )
            {
                return null;
            }

            Dictionary<string, string> files = new( StringComparer.Ordinal );
            foreach ( var path in Directory
                         .EnumerateFiles( directory )
                         .Take( MaximumThemeFiles )
                         .Order( StringComparer.Ordinal ) )
            {
                files.TryAdd( Path
                    .GetFileName( path )
                    .ToLowerInvariant( ), path );
            }

            foreach ( var name in PreviewNames )
            {
                if ( files.TryGetValue( name, out var preview ) )
                {
                    return preview;
                }
            }

            var backgrounds = Path.Combine( directory, BackgroundsFolder );

            return Directory.Exists( backgrounds )
                ? Directory
                    .EnumerateFiles( backgrounds )
                    .Take( MaximumThemeFiles )
                    .Where( static path => ImageExtensions.Contains( Path
                        .GetExtension( path )
                        .ToLowerInvariant( ), StringComparer.Ordinal ) )
                    .Order( StringComparer.Ordinal )
                    .FirstOrDefault( )
                : null;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException )
        {
            return null;
        }
    }

    [GeneratedRegex( @"\A[a-z0-9][a-z0-9._-]{0,127}\z" )]
    private static partial Regex ThemeNamePattern( );
}
