using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace CommandDeck;

public static class DesktopEntryScanner
{
    private const string TrueValue = "true";
    private const string FalseValue = "false";
    private const string DefaultApplicationIcon = "application-x-executable";
    private const string ApplicationIdPrefix = "app:";

    public static async IAsyncEnumerable<IndexedItem> ScanAsync( [EnumeratorCancellation] CancellationToken cancellationToken )
    {
        HashSet<string> seen = new( StringComparer.Ordinal );

        foreach ( var directory in GetApplicationDirectories( ) )
        {
            if ( !Directory.Exists( directory ) )
            {
                continue;
            }

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles( directory, "*" + FileExtensions.Desktop,
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false } );
            }
            catch ( UnauthorizedAccessException )
            {
                continue;
            }

            foreach ( var file in files )
            {
                cancellationToken.ThrowIfCancellationRequested( );

                var entry = await ParseAsync( file, directory, cancellationToken )
                    .ConfigureAwait( false );

                if ( entry is null || !seen.Add( entry.DesktopId ) || !entry.IsVisible )
                {
                    continue;
                }

                yield return CreateApplicationItem( file, entry );
            }
        }
    }

    internal static IEnumerable<string> GetApplicationDirectories( )
    {
        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        var dataHome = Environment.GetEnvironmentVariable( XdgEnvironmentNames.DataHome ) ?? Path.Combine( home, ".local", "share" );

        yield return Path.Combine( dataHome, "applications" );

        var dataDirectories = Environment.GetEnvironmentVariable( XdgEnvironmentNames.DataDirectories ) ?? "/usr/local/share:/usr/share";
        foreach ( var directory in dataDirectories.Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries ) )
        {
            yield return Path.Combine( directory, "applications" );
        }
    }

    private static async ValueTask<DesktopEntry?> ParseAsync( string path, string applicationsDirectory, CancellationToken cancellationToken )
    {
        string[] lines;

        try
        {
            lines = await File
                .ReadAllLinesAsync( path, Encoding.UTF8, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
        {
            return null;
        }

        var inDesktopEntry = false;
        Dictionary<string, string> values = new( StringComparer.Ordinal );

        foreach ( var raw in lines )
        {
            var line = raw.Trim( );
            if ( line.StartsWith( '[' ) )
            {
                inDesktopEntry = line.Equals( "[Desktop Entry]", StringComparison.Ordinal );

                continue;
            }

            if ( ShouldSkipLine( inDesktopEntry, line ) )
            {
                continue;
            }

            var equals = line.IndexOf( '=' );
            if ( equals <= 0 )
            {
                continue;
            }

            var key = line[..equals];
            if ( key.Contains( '[', StringComparison.Ordinal ) )
            {
                continue;
            }

            values[key] = line[(equals + 1)..];
        }

        var desktopId = Path
            .GetRelativePath( applicationsDirectory, path )
            .Replace( Path.DirectorySeparatorChar, '-' );

        values.TryGetValue( DesktopEntryFields.Name, out var name );
        var exec = values.GetValueOrDefault( DesktopEntryFields.Exec );

        return CreateDesktopEntry( values, desktopId, name, exec );
    }

    private static bool IsVisibleOnCurrentDesktop( IReadOnlyDictionary<string, string> values )
    {
        HashSet<string> currentDesktops = new( StringComparer.OrdinalIgnoreCase );
        AddDesktopNames( currentDesktops, Environment.GetEnvironmentVariable( XdgEnvironmentNames.CurrentDesktop ) );
        AddDesktopNames( currentDesktops, Environment.GetEnvironmentVariable( "DESKTOP_SESSION" ) );
        var onlyShowIn = SplitList( values.GetValueOrDefault( "OnlyShowIn" ) );
        if ( IsExcludedFromCurrentDesktop( currentDesktops, onlyShowIn ) )
        {
            return false;
        }

        var notShowIn = SplitList( values.GetValueOrDefault( "NotShowIn" ) );

        return !notShowIn.Any( currentDesktops.Contains );
    }

    private static void AddDesktopNames( HashSet<string> names, string? value )
    {
        if ( string.IsNullOrWhiteSpace( value ) )
        {
            return;
        }

        foreach ( var name in value.Split( [':', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
        {
            names.Add( name );
        }
    }

    private static string[] SplitList( string? value )
    {
        if ( string.IsNullOrWhiteSpace( value ) )
        {
            return [];
        }

        return value.Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
    }

    private static bool IsTryExecAvailable( string? tryExec ) =>
        string.IsNullOrWhiteSpace( tryExec ) || ProcessRunner.ExistsOnPath( tryExec );

    private static bool IsTrue( string? value ) =>
        string.Equals( value, TrueValue, StringComparison.OrdinalIgnoreCase );

    private sealed class DesktopEntry
    {
        public required string DesktopId { get; init; }
        public required string Name { get; init; }
        public string? GenericName { get; init; }
        public string? Comment { get; init; }
        public string? Icon { get; init; }
        public string? StartupWmClass { get; init; }
        public string? Exec { get; init; }
        public bool Terminal { get; init; }
        public bool IsVisible { get; init; }
        public IReadOnlyList<string> Keywords { get; init; } = [];
        public IReadOnlyList<string> MimeTypes { get; init; } = [];
    }

    private static bool ShouldSkipLine( bool inDesktopEntry, string line )
    {
        return !inDesktopEntry || line.Length == 0 || line.StartsWith( '#' );
    }

    private static bool IsExcludedFromCurrentDesktop( HashSet<string> currentDesktops, string[] onlyShowIn )
    {
        return onlyShowIn.Length > 0 && !onlyShowIn.Any( currentDesktops.Contains );
    }

    private static IndexedItem CreateApplicationItem( string file, DesktopEntry entry )
    {
        return new IndexedItem
        {
            StableId = $"{ApplicationIdPrefix}{entry.DesktopId}",
            Kind = ResultKinds.Application,
            Title = entry.Name,
            Subtitle = entry.Comment ?? entry.GenericName,
            Path = file,
            Icon = entry.Icon ?? DefaultApplicationIcon,
            StartupWmClass = entry.StartupWmClass,

            // The program name and desktop ID are low-weight keywords; the
            // full Exec line would match every wrapper word and argument.
            Body = string.Join( ' ', entry
                .Keywords.Append( DesktopEntryCommand.ProgramName( entry.Exec ) ?? string.Empty )
                .Append( entry.DesktopId[..^FileExtensions.Desktop.Length] )
                .Append( entry.StartupWmClass ?? string.Empty ) ),
            PayloadJson = PayloadWriter.Create( (ResultMetadataKeys.Application.DesktopFile, file), (ResultMetadataKeys.Application.DesktopId, entry.DesktopId),
                (ResultMetadataKeys.Application.Exec, entry.Exec), (ResultMetadataKeys.Application.Icon, entry.Icon),
                (ResultMetadataKeys.Application.StartupWmClass, entry.StartupWmClass),
                (ResultMetadataKeys.Application.MimeTypes, string.Join( ';', entry.MimeTypes )),
                (ResultMetadataKeys.Application.Terminal, entry.Terminal ? TrueValue : FalseValue) ),
            ProviderId = ProviderIds.Applications,
            SourceId = SourceIds.Applications
        };
    }

    private static DesktopEntry CreateDesktopEntry( Dictionary<string, string> values, string desktopId, [NotNullWhen( true )] string? name,
        [NotNullWhen( true )] string? exec )
    {
        return new DesktopEntry
        {
            DesktopId = desktopId,
            Name = name ?? desktopId[..^FileExtensions.Desktop.Length],
            GenericName = values.GetValueOrDefault( "GenericName" ),
            Comment = values.GetValueOrDefault( "Comment" ),
            Icon = values.GetValueOrDefault( DesktopEntryFields.Icon ),
            StartupWmClass = values.GetValueOrDefault( "StartupWMClass" ),
            Exec = exec,
            Terminal = IsTrue( values.GetValueOrDefault( DesktopEntryFields.Terminal ) ),
            IsVisible = !string.IsNullOrWhiteSpace( name ) &&
                        !string.IsNullOrWhiteSpace( exec ) &&
                        string.Equals( values.GetValueOrDefault( DesktopEntryFields.Type, DesktopEntryFields.ApplicationType ),
                            DesktopEntryFields.ApplicationType, StringComparison.Ordinal ) &&
                        !IsTrue( values.GetValueOrDefault( DesktopEntryFields.Hidden ) ) &&
                        !IsTrue( values.GetValueOrDefault( "NoDisplay" ) ) &&
                        IsVisibleOnCurrentDesktop( values ) &&
                        IsTryExecAvailable( values.GetValueOrDefault( DesktopEntryFields.TryExec ) ),
            Keywords = values
                .GetValueOrDefault( "Keywords", string.Empty )
                .Split( ';', StringSplitOptions.RemoveEmptyEntries ),
            MimeTypes = values
                .GetValueOrDefault( DesktopEntryFields.MimeType, string.Empty )
                .Split( ';', StringSplitOptions.RemoveEmptyEntries )
        };
    }
}
