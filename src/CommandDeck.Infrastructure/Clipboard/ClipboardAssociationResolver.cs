using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

/// <summary>Startup-captured inputs for association resolution; callers never supply them.</summary>
public sealed record ClipboardResolverEnvironment(
    string Home,
    string? ConfigHome,
    string? ConfigDirs,
    string? DataHome,
    string? DataDirs,
    string? CurrentDesktop,
    string Path,
    string? Locale )
{
    public static ClipboardResolverEnvironment Capture( ) => new( Environment.GetEnvironmentVariable( EnvironmentVariableNames.Home ) ?? "/",
        Environment.GetEnvironmentVariable( XdgEnvironmentNames.ConfigHome ), Environment.GetEnvironmentVariable( XdgEnvironmentNames.ConfigDirectories ),
        Environment.GetEnvironmentVariable( XdgEnvironmentNames.DataHome ), Environment.GetEnvironmentVariable( XdgEnvironmentNames.DataDirectories ),
        Environment.GetEnvironmentVariable( XdgEnvironmentNames.CurrentDesktop ),
        (Environment.GetEnvironmentVariable( EnvironmentVariableNames.ExecutablePath ) ?? ""), ReadLocale( ) );

    private static string? ReadLocale( )
    {
        if ( Environment.GetEnvironmentVariable( EnvironmentVariableNames.LocaleAll ) is { Length: > 0 } all )
        {
            return all;
        }

        if ( Environment.GetEnvironmentVariable( "LC_MESSAGES" ) is { Length: > 0 } messages )
        {
            return messages;
        }

        return Environment.GetEnvironmentVariable( EnvironmentVariableNames.Language );
    }
}

/// <summary>Exact-key XDG MIME Applications resolution with no catalog input and no association writes.</summary>
public sealed partial class ClipboardAssociationResolver( ClipboardResolverEnvironment environment )
{
    public const int Version = 3;
    public const int MaximumHandlers = 12;
    private const int MaximumRoots = 64;
    private const int MaximumEntries = 4096;
    private const long MaximumAssociationFileBytes = ByteUnits.BytesPerMebibyte;
    private const long MaximumDesktopFileBytes = 256 * ByteUnits.BytesPerKibibyte;
    private const long MaximumAggregateBytes = 32L * ByteUnits.BytesPerMebibyte;
    private readonly IncrementalHash _revision = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
    private readonly Dictionary<string, DesktopEntry?> _entries = new( StringComparer.Ordinal );
    private long _aggregate;
    private int _entryCount;

    public ClipboardHandlerResolution Resolve( string key, ClipboardLaunchTarget targetKind, string target )
    {
        ArgumentException.ThrowIfNullOrEmpty( key );
        ArgumentNullException.ThrowIfNull( target );

        try
        {
            Record( "resolver", Version.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
            Record( "grammar", ClipboardDesktopExec.GrammarVersion.ToString( System.Globalization.CultureInfo.InvariantCulture ) );

            foreach ( var value in new[]
                     {
                         environment.Home, environment.ConfigHome, environment.ConfigDirs, environment.DataHome, environment.DataDirs,
                         environment.CurrentDesktop, environment.Path, environment.Locale
                     } )
            {
                Record( "environment", value ?? "" );
            }

            var layout = Layout.Create( environment );
            if ( layout.AssociationLocations.Count + layout.ApplicationRoots.Count > MaximumRoots )
            {
                return Unavailable( ClipboardHandlerStatuses.HandlerDiscoveryUnavailable );
            }

            var applications = IndexApplications( layout );
            var associations = CollectAssociations( key, layout, applications );
            if ( associations is null )
            {
                return Unavailable( ClipboardHandlerStatuses.InvalidAssociations );
            }

            var choices = BuildChoices( key, layout, applications, associations, targetKind, target );
            var total = choices.Count;

            var handlers = choices
                .Take( MaximumHandlers )
                .ToArray( );

            return new( total == 0 ? ClipboardHandlerStatuses.NoAssociations : ClipboardHandlerStatuses.Resolved, handlers, total, total > handlers.Length,
                Convert.ToHexStringLower( _revision.GetHashAndReset( ) ), handlers
                    .FirstOrDefault( static choice => choice.Rank == ClipboardHandlerRanks.Default )
                    ?.DesktopId );
        }
        catch ( DiscoveryUnavailableException )
        {
            return Unavailable( ClipboardHandlerStatuses.HandlerDiscoveryUnavailable );
        }
        finally
        {
            _revision.Dispose( );
        }
    }

    private Associations? CollectAssociations( string key, Layout layout, ApplicationIndex applications )
    {
        Associations associations = new( );

        foreach ( var location in layout.AssociationLocations )
        {
            foreach ( var file in location.Files )
            {
                var parsed = ReadAssociationFile( file.Path );
                if ( parsed is null )
                {
                    continue;
                }

                associations.Defaults.AddRange( parsed.Defaults( key ) );

                if ( file.DesktopSpecific )
                {
                    continue;
                }

                var added = parsed
                    .Added( key )
                    .Where( id => !associations.Shadowed.Contains( id ) )
                    .ToArray( );

                var removed = parsed
                    .Removed( key )
                    .Where( id => !associations.Shadowed.Contains( id ) )
                    .ToArray( );

                if ( added
                    .Intersect( removed, StringComparer.Ordinal )
                    .Any( ) )
                {
                    return null;
                }

                foreach ( var id in removed )
                {
                    associations.Removed.Add( id );
                }

                foreach ( var id in added.Where( id => !associations.Removed.Contains( id ) ) )
                {
                    associations.Preferred.Add( id );
                }
            }

            if ( location.ApplicationsRoot is not null )
            {
                foreach ( var id in DeclaredIds( applications, layout, location.ApplicationsRoot, key )
                             .Where( id => !associations.Removed.Contains( id ) ) )
                {
                    associations.Declared.Add( id );
                }

                associations.Shadowed.UnionWith( applications.Inventory( location.ApplicationsRoot ) );
            }
        }

        return associations;
    }

    private List<ClipboardHandlerChoice> BuildChoices( string key, Layout layout, ApplicationIndex applications, Associations associations,
        ClipboardLaunchTarget targetKind, string target )
    {
        var effective = ResolvePreferredEntries( layout, applications, associations );

        var defaultId = associations.Defaults.FirstOrDefault( id => effective.Contains( id, StringComparer.Ordinal ) );
        List<ClipboardHandlerChoice> choices = [];

        if ( defaultId is not null )
        {
            choices.Add( Describe( defaultId, ClipboardHandlerRanks.Default, ResolveEntry( applications, layout, defaultId )!, key, targetKind, target ) );
        }

        foreach ( var id in effective.Where( id => id != defaultId ) )
        {
            var rank = associations.Preferred.Contains( id, StringComparer.Ordinal ) ? ClipboardHandlerRanks.Preferred : ClipboardHandlerRanks.Declared;

            choices.Add( Describe( id, rank, ResolveEntry( applications, layout, id )!, key, targetKind, target ) );
        }

        return choices;
    }

    private ClipboardHandlerChoice Describe( string id, string rank, DesktopEntry entry, string key, ClipboardLaunchTarget targetKind, string target )
    {
        var reason = Evaluate( entry, targetKind, target );
        Record( "choice", id + "\n" + rank + "\n" + key + "\n" + (reason ?? "available") );

        return new( id, entry.LocalizedName( environment.Locale ), rank, reason is null, reason );
    }

    private void Record( string kind, string value )
    {
        _revision.AppendData( Encoding.UTF8.GetBytes( kind ) );
        _revision.AppendData( [0] );
        _revision.AppendData( Encoding.UTF8.GetBytes( value ) );
        _revision.AppendData( [0] );
    }

    private byte[] ReadBounded( string path, long maximum, bool required )
    {
        FileInfo info = new( path );
        if ( !info.Exists )
        {
            Record( "missing", path );

            return [];
        }

        if ( ExceedsReadPolicy( maximum, required, info ) )
        {
            throw new DiscoveryUnavailableException( );
        }

        try
        {
            var bytes = File.ReadAllBytes( path );
            if ( bytes.Length > maximum )
            {
                throw new DiscoveryUnavailableException( );
            }

            _aggregate += bytes.Length;
            Record( "file", path + "\n" + Convert.ToHexStringLower( SHA256.HashData( bytes ) ) );

            return bytes;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            throw new DiscoveryUnavailableException( );
        }
    }

    private AssociationFile? ReadAssociationFile( string path )
    {
        var bytes = ReadBounded( path, MaximumAssociationFileBytes, required: false );
        if ( bytes.Length == 0 )
        {
            return null;
        }

        var text = DecodeUtf8( bytes );
        if ( text is null )
        {
            throw new DiscoveryUnavailableException( );
        }

        return AssociationFile.Parse( text );
    }

    private static string? DecodeUtf8( byte[] bytes )
    {
        try
        {
            return new UTF8Encoding( false, true ).GetString( bytes );
        }
        catch ( DecoderFallbackException )
        {
            return null;
        }
    }

    private sealed class Associations
    {
        internal List<string> Defaults { get; } = [];
        internal List<string> Preferred { get; } = [];
        internal List<string> Declared { get; } = [];
        internal HashSet<string> Removed { get; } = new( StringComparer.Ordinal );
        internal HashSet<string> Shadowed { get; } = new( StringComparer.Ordinal );
    }

    private sealed class AssociationFile
    {
        private readonly Dictionary<string, Dictionary<string, string[]>> _sections = new( StringComparer.Ordinal );

        internal string[] Defaults( string key ) => Lookup( "Default Applications", key );
        internal string[] Added( string key ) => Lookup( "Added Associations", key );
        internal string[] Removed( string key ) => Lookup( "Removed Associations", key );

        private string[] Lookup( string section, string key ) =>
            _sections.TryGetValue( section, out var values ) && values.TryGetValue( key, out var ids ) ? ids : [];

        internal static AssociationFile Parse( string text )
        {
            AssociationFile file = new( );
            Dictionary<string, string[]>? current = null;

            foreach ( var raw in text.Split( '\n' ) )
            {
                var line = raw
                    .TrimEnd( '\r' )
                    .Trim( );

                if ( IsCommentOrEmpty( line ) )
                {
                    continue;
                }

                if ( IsSectionHeader( line ) )
                {
                    var name = line[1..^1];
                    if ( !file._sections.TryGetValue( name, out current ) )
                    {
                        current = new( StringComparer.Ordinal );
                        file._sections.Add( name, current );
                    }

                    continue;
                }

                var equals = line.IndexOf( '=' );
                if ( IsOutsideSection( current, equals ) )
                {
                    continue;
                }

                var ids = ReadDesktopIds( line, equals );

                var mime = line[..equals]
                    .Trim( );

                current[mime] = MergeDesktopIds( current, mime, ids );
            }

            return file;
        }

        private static string[] MergeDesktopIds( Dictionary<string, string[]> current, string mime, string[] ids )
        {
            if ( !current.TryGetValue( mime, out var existing ) )
            {
                return ids;
            }

            return existing
                .Concat( ids )
                .ToArray( );
        }

        private static bool IsSectionHeader( string line )
        {
            return line.StartsWith( '[' ) && line.EndsWith( ']' );
        }

        private static bool IsOutsideSection( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] Dictionary<string, string[]>? current, int equals )
        {
            return current is null || equals <= 0;
        }

        private static string[] ReadDesktopIds( string line, int equals )
        {
            return line[(equals + 1)..]
                .Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                .Where( static id => id.EndsWith( FileExtensions.Desktop, StringComparison.Ordinal ) && !id.Contains( '/' ) && !id.Contains( '\\' ) )
                .ToArray( );
        }
    }

    private sealed class DiscoveryUnavailableException : Exception;

    private List<string> ResolvePreferredEntries( Layout layout, ApplicationIndex applications, Associations associations )
    {
        return associations
            .Preferred.Concat( associations.Declared.OrderBy( static id => id, StringComparer.Ordinal ) )
            .Distinct( StringComparer.Ordinal )
            .Where( id => ResolveEntry( applications, layout, id ) is not null )
            .ToList( );
    }

    private bool ExceedsReadPolicy( long maximum, bool required, FileInfo info )
    {
        return info.LinkTarget is not null && !required || info.Length > maximum || _aggregate + info.Length > MaximumAggregateBytes;
    }

    private static bool IsCommentOrEmpty( string line )
    {
        return line.Length == 0 || line.StartsWith( '#' );
    }

    private static ClipboardHandlerResolution Unavailable( string status ) => new( status, [], 0, false, "", null );
}
