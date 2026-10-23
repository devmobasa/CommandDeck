namespace CommandDeck;

public sealed partial class ClipboardAssociationResolver
{
    private const string InvalidDesktopExecReason = "InvalidDesktopExec";
    private const string WorkingDirectoryUnavailableReason = "WorkingDirectoryUnavailable";

    private DesktopEntry? ResolveEntry( ApplicationIndex applications, Layout layout, string id )
    {
        if ( _entries.TryGetValue( id, out var cached ) )
        {
            return cached;
        }

        DesktopEntry? entry = null;

        foreach ( var root in layout.ApplicationRoots )
        {
            var candidates = applications.Candidates( root, id );
            if ( candidates.Count == 0 )
            {
                continue;
            }

            // The highest-priority root decides: a same-root collision or an invalid/hidden entry never revives a lower file.
            if ( candidates.Count == 1 )
            {
                var bytes = ReadBounded( candidates[0], MaximumDesktopFileBytes, required: true );
                var text = bytes.Length == 0 ? null : DecodeUtf8( bytes );
                entry = text is null ? null : DesktopEntry.Parse( text, candidates[0], environment.Path );
            }

            break;
        }

        _entries[id] = entry;

        return entry;
    }

    private string? Evaluate( DesktopEntry entry, ClipboardLaunchTarget targetKind, string target )
    {
        if ( entry.DBusActivatable )
        {
            return ClipboardHandlerReasons.UnsupportedActivation;
        }

        if ( entry.Exec is null )
        {
            return InvalidDesktopExecReason;
        }

        var rejection = ClipboardDesktopExec.TryExpand( entry.Exec, entry.LocalizedName( environment.Locale ), entry.Icon, entry.Path, targetKind, target,
            out var launch );

        if ( rejection != ClipboardLaunchRejection.None )
        {
            return rejection.ToString( );
        }

        var executable = ResolveExecutable( launch!.Executable );
        if ( executable is null )
        {
            return ClipboardHandlerReasons.ExecutableUnavailable;
        }

        Record( "executable", executable + "\n" + FileIdentity( executable ) );

        if ( entry.Terminal )
        {
            return ClipboardHandlerReasons.UnsupportedTerminalLaunch;
        }

        var directory = entry.WorkingDirectory;
        if ( HasInvalidWorkingDirectory( directory ) )
        {
            return WorkingDirectoryUnavailableReason;
        }

        Record( "working-directory", directory ?? "/" );

        return null;
    }

    private string? ResolveExecutable( string token )
    {
        if ( token.StartsWith( '/' ) )
        {
            return IsExecutable( token ) ? token : null;
        }

        foreach ( var directory in environment.Path.Split( ':', StringSplitOptions.RemoveEmptyEntries ) )
        {
            if ( !directory.StartsWith( '/' ) )
            {
                continue;
            }

            var candidate = System.IO.Path.Combine( directory, token );
            if ( IsExecutable( candidate ) )
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsExecutable( string path )
    {
        try
        {
            FileInfo info = new( path );

            return OperatingSystem.IsLinux( ) &&
                   info.Exists &&
                   (File.GetUnixFileMode( path ) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            return false;
        }
    }

    private static string FileIdentity( string path )
    {
        FileInfo info = new( path );

        return GetFileRevision( info );
    }

    private static bool ValidWorkingDirectory( string directory )
    {
        if ( HasInvalidDirectorySyntax( directory ) )
        {
            return false;
        }

        var current = "";

        foreach ( var segment in directory.Split( '/', StringSplitOptions.RemoveEmptyEntries ) )
        {
            current += "/" + segment;
            DirectoryInfo info = new( current );
            if ( IsDirectoryUnavailable( info ) )
            {
                return false;
            }
        }

        return true;
    }

    private ApplicationIndex IndexApplications( Layout layout )
    {
        ApplicationIndex index = new( );

        foreach ( var root in layout.ApplicationRoots )
        {
            if ( !Directory.Exists( root ) )
            {
                Record( "missing-root", root );

                continue;
            }

            try
            {
                foreach ( var file in Directory.EnumerateFiles( root, "*" + FileExtensions.Desktop, SearchOption.AllDirectories ) )
                {
                    if ( ++_entryCount > MaximumEntries )
                    {
                        throw new DiscoveryUnavailableException( );
                    }

                    var id = System
                        .IO.Path.GetRelativePath( root, file )
                        .Replace( System.IO.Path.DirectorySeparatorChar, '-' );

                    index.Add( root, id, file );
                }
            }
            catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
            {
                throw new DiscoveryUnavailableException( );
            }

            Record( "root", root + "\n" + string.Join( '\n', index.Inventory( root ) ) );
        }

        return index;
    }

    private IEnumerable<string> DeclaredIds( ApplicationIndex applications, Layout layout, string root, string key )
    {
        foreach ( var id in applications.Inventory( root ) )
        {
            var entry = ResolveEntry( applications, layout, id );
            if ( IsDeclaredInRoot( root, key, entry ) )
            {
                yield return id;
            }
        }
    }

    private sealed class ApplicationIndex
    {
        private readonly Dictionary<string, Dictionary<string, List<string>>> _roots = new( StringComparer.Ordinal );

        internal void Add( string root, string id, string file )
        {
            if ( !_roots.TryGetValue( root, out var ids ) )
            {
                ids = new( StringComparer.Ordinal );
                _roots.Add( root, ids );
            }

            if ( !ids.TryGetValue( id, out var files ) )
            {
                files = [];
                ids.Add( id, files );
            }

            files.Add( file );
        }

        internal List<string> Candidates( string root, string id ) =>
            _roots.TryGetValue( root, out var ids ) && ids.TryGetValue( id, out var files ) ? files : [];

        internal IEnumerable<string> Inventory( string root ) =>
            _roots.TryGetValue( root, out var ids ) ? ids.Keys.Order( StringComparer.Ordinal ) : [];
    }

    private sealed class Layout
    {
        internal List<AssociationLocation> AssociationLocations { get; } = [];
        internal List<string> ApplicationRoots { get; } = [];

        internal static Layout Create( ClipboardResolverEnvironment environment )
        {
            var configHome = AbsoluteOrDefault( environment.ConfigHome, System.IO.Path.Combine( environment.Home, ".config" ) );

            var dataHome = AbsoluteOrDefault( environment.DataHome, System.IO.Path.Combine( environment.Home, ".local", "share" ) );

            var configDirs = SplitDirectories( environment.ConfigDirs, ["/etc/xdg"] );
            var dataDirs = SplitDirectories( environment.DataDirs, ["/usr/local/share", "/usr/share"] );
            var desktops = ReadDesktops( environment );
            Layout layout = new( );

            foreach ( var directory in new[] { configHome }
                         .Concat( configDirs )
                         .Distinct( StringComparer.Ordinal ) )
            {
                layout.AssociationLocations.Add( new( directory, null, desktops ) );
            }

            foreach ( var directory in new[] { dataHome }
                         .Concat( dataDirs )
                         .Distinct( StringComparer.Ordinal ) )
            {
                var applications = System.IO.Path.Combine( directory, "applications" );
                layout.AssociationLocations.Add( new( applications, applications, desktops ) );
                layout.ApplicationRoots.Add( applications );
            }

            return layout;
        }

        private static string AbsoluteOrDefault( string? value, string fallback ) =>
            value is { Length: > 0 } && value.StartsWith( '/' ) ? System.IO.Path.TrimEndingDirectorySeparator( value ) : fallback;

        private static string[] SplitDirectories( string? value, string[] fallback )
        {
            if ( string.IsNullOrEmpty( value ) )
            {
                return fallback;
            }

            var directories = ReadDirectories( value );

            return directories.Length == 0 ? fallback : directories;
        }

        private static string[] ReadDesktops( ClipboardResolverEnvironment environment )
        {
            return (environment.CurrentDesktop ?? "")
                .Split( ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                .Select( static desktop => desktop.ToLowerInvariant( ) )
                .Where( static desktop => desktop.All( static character => char.IsAsciiLetterOrDigit( character ) || character is '-' or '_' ) )
                .Distinct( StringComparer.Ordinal )
                .ToArray( );
        }

        private static string[] ReadDirectories( string value )
        {
            return value
                .Split( ':', StringSplitOptions.RemoveEmptyEntries )
                .Where( static directory => directory.StartsWith( '/' ) )
                .Select( System.IO.Path.TrimEndingDirectorySeparator )
                .Distinct( StringComparer.Ordinal )
                .ToArray( );
        }
    }

    private sealed class AssociationLocation( string directory, string? applicationsRoot, string[] desktops )
    {
        internal string? ApplicationsRoot { get; } = applicationsRoot;

        internal IReadOnlyList<(string Path, bool DesktopSpecific)> Files { get; } = desktops
            .Select( desktop => (System.IO.Path.Combine( directory, desktop + "-mimeapps.list" ), true) )
            .Append( (System.IO.Path.Combine( directory, "mimeapps.list" ), false) )
            .ToArray( );
    }

    private sealed class DesktopEntry( string path, Dictionary<string, string> values )
    {
        internal string Path { get; } = path;
        internal string? Exec => values.GetValueOrDefault( DesktopEntryFields.Exec );
        internal string? Icon => values.GetValueOrDefault( DesktopEntryFields.Icon );
        internal bool Terminal => IsTrue( values.GetValueOrDefault( DesktopEntryFields.Terminal ) );
        internal bool DBusActivatable => IsTrue( values.GetValueOrDefault( DesktopEntryFields.DBusActivatable ) );
        internal string? WorkingDirectory => values.GetValueOrDefault( "Path" ) is { Length: > 0 } directory ? directory : null;

        internal string[] MimeTypes =>
            (values.GetValueOrDefault( DesktopEntryFields.MimeType ) ?? "").Split( ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

        internal string LocalizedName( string? locale )
        {
            foreach ( var suffix in LocaleSuffixes( locale ) )
            {
                if ( values.TryGetValue( DesktopEntryFields.Name + "[" + suffix + "]", out var localized ) && localized.Length > 0 )
                {
                    return localized;
                }
            }

            return values[DesktopEntryFields.Name];
        }

        private static IEnumerable<string> LocaleSuffixes( string? locale )
        {
            if ( string.IsNullOrEmpty( locale ) )
            {
                yield break;
            }

            var modifierIndex = locale.IndexOf( '@' );
            var modifier = modifierIndex >= 0 ? locale[modifierIndex..] : "";
            var withoutModifier = modifierIndex >= 0 ? locale[..modifierIndex] : locale;
            var encodingIndex = withoutModifier.IndexOf( '.' );
            var languageCountry = encodingIndex >= 0 ? withoutModifier[..encodingIndex] : withoutModifier;
            var underscore = languageCountry.IndexOf( '_' );
            var language = underscore >= 0 ? languageCountry[..underscore] : languageCountry;

            if ( HasLocaleModifierAndTerritory( modifier, underscore ) )
            {
                yield return languageCountry + modifier;
            }

            if ( underscore >= 0 )
            {
                yield return languageCountry;
            }

            if ( modifier.Length > 0 )
            {
                yield return language + modifier;
            }

            yield return language;
        }

        internal static DesktopEntry? Parse( string text, string path, string searchPath )
        {
            Dictionary<string, string> values = new( StringComparer.Ordinal );
            var inGroup = false;
            var sawGroup = false;

            foreach ( var raw in text.Split( '\n' ) )
            {
                var line = raw.TrimEnd( '\r' );
                if ( IsCommentOrEmpty( line ) )
                {
                    continue;
                }

                if ( line.StartsWith( '[' ) )
                {
                    inGroup = line.Trim( ) == "[Desktop Entry]";

                    if ( IsDuplicateGroup( inGroup, sawGroup ) )
                    {
                        return null;
                    }

                    sawGroup |= inGroup;

                    continue;
                }

                var equals = line.IndexOf( '=' );
                if ( IsOutsideGroup( inGroup, equals ) )
                {
                    continue;
                }

                values.TryAdd( line[..equals]
                    .Trim( ), line[(equals + 1)..]
                    .Trim( ) );
            }

            var valid = sawGroup &&
                        values.GetValueOrDefault( DesktopEntryFields.Type ) == DesktopEntryFields.ApplicationType &&
                        !IsTrue( values.GetValueOrDefault( DesktopEntryFields.Hidden ) ) &&
                        values.GetValueOrDefault( DesktopEntryFields.Name ) is { Length: > 0 } &&
                        (values.GetValueOrDefault( DesktopEntryFields.Exec ) is { Length: > 0 } ||
                         IsTrue( values.GetValueOrDefault( DesktopEntryFields.DBusActivatable ) )) &&
                        TryExecAvailable( values.GetValueOrDefault( DesktopEntryFields.TryExec ), searchPath );

            return valid ? new( path, values ) : null;
        }

        private static bool TryExecAvailable( string? tryExec, string searchPath )
        {
            if ( string.IsNullOrEmpty( tryExec ) )
            {
                return true;
            }

            if ( tryExec.StartsWith( '/' ) )
            {
                return IsExecutable( tryExec );
            }

            return searchPath
                .Split( ':', StringSplitOptions.RemoveEmptyEntries )
                .Where( static directory => directory.StartsWith( '/' ) )
                .Any( directory => IsExecutable( System.IO.Path.Combine( directory, tryExec ) ) );
        }

        private static bool IsTrue( string? value ) => string.Equals( value, "true", StringComparison.OrdinalIgnoreCase );

        private static bool HasLocaleModifierAndTerritory( string modifier, int underscore )
        {
            return modifier.Length > 0 && underscore >= 0;
        }

        private static bool IsDuplicateGroup( bool inGroup, bool sawGroup )
        {
            return inGroup && sawGroup;
        }

        private static bool IsOutsideGroup( bool inGroup, int equals )
        {
            return !inGroup || equals <= 0;
        }
    }

    private static bool HasInvalidWorkingDirectory( string? directory )
    {
        return directory is not null && !ValidWorkingDirectory( directory );
    }

    private static string GetFileRevision( FileInfo info )
    {
        return info.Length.ToString( System.Globalization.CultureInfo.InvariantCulture ) +
               "\n" +
               info.LastWriteTimeUtc.Ticks.ToString( System.Globalization.CultureInfo.InvariantCulture );
    }

    private static bool HasInvalidDirectorySyntax( string directory )
    {
        return !directory.StartsWith( '/' ) ||
               directory.Contains( "//", StringComparison.Ordinal ) ||
               directory
                   .Split( '/' )
                   .Any( static segment => segment is "." or ".." ) ||
               directory.Length > 1 && directory.EndsWith( '/' );
    }

    private static bool IsDirectoryUnavailable( DirectoryInfo info )
    {
        return !info.Exists || info.LinkTarget is not null;
    }

    private static bool IsDeclaredInRoot( string root, string key, DesktopEntry? entry )
    {
        return entry is not null && entry.MimeTypes.Contains( key, StringComparer.Ordinal ) && entry.Path.StartsWith( root, StringComparison.Ordinal );
    }
}
