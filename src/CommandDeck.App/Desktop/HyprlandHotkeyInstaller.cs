using System.Globalization;

namespace CommandDeck;

public sealed class HyprlandHotkeyPaths
{
    private const string StateKey = "state";
    public const string DefaultOmarchyDirectory = "/usr/share/omarchy";

    public required string HyprDirectory { get; init; }
    public required string BackupDirectory { get; init; }
    // Omarchy's own configuration, whose default bindings load before CommandDeck's.
    public string OmarchyDirectory { get; init; } = DefaultOmarchyDirectory;
    public string HotkeyFile => Path.Combine( HyprDirectory, HyprlandHotkeyConfig.FileName );
    public string MainConfig => Path.Combine( HyprDirectory, "hyprland.lua" );
    public string Bindings => Path.Combine( HyprDirectory, "bindings.lua" );

    // The same XDG resolution the installer scripts use for the Hyprland
    // directory and for CommandDeck's binding backups.
    public static HyprlandHotkeyPaths FromEnvironment( Func<string, string?> environment )
    {
        var home = environment( EnvironmentVariableNames.Home );
        if ( string.IsNullOrEmpty( home ) )
        {
            throw new InvalidOperationException( "HOME is not set; cannot locate the Hyprland configuration." );
        }

        var configHome = NonEmpty( environment( XdgEnvironmentNames.ConfigHome ) ) ?? Path.Combine( home, ".config" );

        var stateHome = NonEmpty( environment( XdgEnvironmentNames.StateHome ) ) ?? Path.Combine( home, ".local", StateKey );

        var omarchy = NonEmpty( environment( EnvironmentVariableNames.OmarchyPath ) ) ?? DefaultOmarchyDirectory;

        return new( )
        {
            HyprDirectory = Path.Combine( configHome, "hypr" ),
            BackupDirectory = Path.Combine( stateHome, "commanddeck", "binding-backups" ),
            OmarchyDirectory = omarchy
        };
    }

    private static string? NonEmpty( string? value ) => string.IsNullOrEmpty( value ) ? null : value;
}

// Writes CommandDeck's own Hyprland module and its one require line, migrates
// the block earlier installers appended to bindings.lua, and removes both again.
// Every edited user file is backed up first; an unchanged file is not rewritten.
public sealed class HyprlandHotkeyInstaller( HyprlandHotkeyPaths paths, TimeProvider? time = null )
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Makes the checks that Install makes before it writes, and writes nothing.
    public string Check( string? chord = null )
    {
        var current = File.Exists( paths.HotkeyFile ) ? HyprlandHotkeyConfig.ReadChord( File.ReadAllText( paths.HotkeyFile ) ) : null;
        var normalized = HyprlandHotkeyConfig.NormalizeChord( chord ?? current ?? HyprlandHotkeyConfig.DefaultChord );

        if ( !File.Exists( paths.MainConfig ) )
        {
            throw new InvalidOperationException( $"{paths.MainConfig} does not exist; the CommandDeck hotkey needs Omarchy's Lua Hyprland configuration." );
        }

        return normalized;
    }

    public IReadOnlyList<string> Install( string? chord = null )
    {
        var normalized = Check( chord );
        List<string> messages = [];
        var bindings = File.Exists( paths.Bindings ) ? File.ReadAllText( paths.Bindings ) : string.Empty;
        bindings = RemoveLegacyBlock( bindings, messages );

        if ( HyprlandHotkeyConfig.HasUserBinding( bindings, normalized ) )
        {
            messages.Add( $"{paths.Bindings} already binds {normalized} to CommandDeck; leaving your binding in place." );

            return messages;
        }

        if ( chord is not null && HyprlandHotkeyConfig.MentionsChord( bindings, normalized ) )
        {
            messages.Add( $"Note: {paths.Bindings} also binds {normalized}; the CommandDeck binding loads later and replaces it." );
        }

        var updates = PlanInstallation( chord );
        var generated = updates[paths.HotkeyFile]!;
        var current = File.Exists( paths.HotkeyFile ) ? File.ReadAllText( paths.HotkeyFile ) : null;
        if ( current != generated )
        {
            if ( OmarchyDefaultBinding( normalized ) is { } defaults )
            {
                messages.Add( $"Note: Omarchy's {defaults} also binds {normalized}." );
            }

            if ( current is not null && !HyprlandHotkeyConfig.IsGenerated( current ) )
            {
                messages.Add( $"Backed up an unrelated {paths.HotkeyFile} to {Backup( paths.HotkeyFile )}" );
            }

            messages.Add( $"Regenerated {paths.HotkeyFile} with saved shortcuts and {HyprlandHotkeyConfig.ShortcutId}." );
        }

        var changed = updates.Where( update => !HasContent( update.Key, update.Value ) )
            .ToDictionary( static update => update.Key, static update => update.Value, StringComparer.Ordinal );
        ShortcutFileTransaction.ApplyAsync( changed, static _ => ValueTask.CompletedTask, CancellationToken.None ).AsTask( ).GetAwaiter( ).GetResult( );

        var main = File.ReadAllText( paths.MainConfig );
        var required = HyprlandHotkeyConfig.EnsureRequire( main );
        if ( required != main )
        {
            var backup = Backup( paths.MainConfig );
            WriteAtomically( paths.MainConfig, required );
            messages.Add( $"Added the CommandDeck require line to {paths.MainConfig}; backup: {backup}" );
        }

        if ( messages.Count == 0 )
        {
            var message = chord is null ? "Saved shortcut choices are already installed." : $"The {normalized} CommandDeck hotkey is already installed.";
            messages.Add( message );
        }

        return messages;
    }

    private IReadOnlyDictionary<string, string?> PlanInstallation( string? chord )
    {
        var editor = new ShortcutEditorService( paths, paths.OmarchyDirectory, inHyprland: static ( ) => false );
        try
        {
            return editor.PlanInstallation( chord );
        }
        finally
        {
            editor.DisposeAsync( ).AsTask( ).GetAwaiter( ).GetResult( );
        }
    }

    // The first of Omarchy's default configuration files that binds the chord.
    private string? OmarchyDefaultBinding( string chord )
    {
        var defaults = Path.Combine( paths.OmarchyDirectory, "default", "hypr" );
        if ( !Directory.Exists( defaults ) )
        {
            return null;
        }

        return Directory
            .EnumerateFiles( defaults, "*.lua", SearchOption.AllDirectories )
            .Order( StringComparer.Ordinal )
            .FirstOrDefault( file => HyprlandHotkeyConfig.MentionsChord( File.ReadAllText( file ), chord ) );
    }

    private static bool HasContent( string path, string? content ) =>
        content is null ? !File.Exists( path ) && new FileInfo( path ).LinkTarget is null : File.Exists( path ) && File.ReadAllText( path ) == content;

    // Removes the require line before the module, so Hyprland never requires a
    // missing file. A module CommandDeck did not generate is left in place.
    public IReadOnlyList<string> Remove( )
    {
        List<string> messages = [];
        if ( File.Exists( paths.Bindings ) )
        {
            RemoveLegacyBlock( File.ReadAllText( paths.Bindings ), messages );
        }

        if ( File.Exists( paths.MainConfig ) )
        {
            var main = File.ReadAllText( paths.MainConfig );
            var removed = HyprlandHotkeyConfig.RemoveRequire( main );
            if ( removed != main )
            {
                var backup = Backup( paths.MainConfig );
                WriteAtomically( paths.MainConfig, removed );
                messages.Add( $"Removed the CommandDeck require line from {paths.MainConfig}; backup: {backup}" );
            }
        }

        if ( File.Exists( paths.HotkeyFile ) )
        {
            if ( HyprlandHotkeyConfig.IsGenerated( File.ReadAllText( paths.HotkeyFile ) ) )
            {
                File.Delete( paths.HotkeyFile );
                messages.Add( $"Deleted {paths.HotkeyFile}" );
            }
            else
            {
                messages.Add( $"Left {paths.HotkeyFile} in place; CommandDeck did not generate it." );
            }
        }

        if ( messages.Count == 0 )
        {
            messages.Add( "No CommandDeck hotkey is installed." );
        }

        return messages;
    }

    private string RemoveLegacyBlock( string bindings, List<string> messages )
    {
        if ( !HyprlandHotkeyConfig.HasLegacyBlock( bindings ) )
        {
            return bindings;
        }

        var migrated = HyprlandHotkeyConfig.RemoveLegacyBlock( bindings );
        var backup = Backup( paths.Bindings );
        WriteAtomically( paths.Bindings, migrated );
        messages.Add( $"Removed the earlier CommandDeck block from {paths.Bindings}; backup: {backup}" );

        return migrated;
    }

    private string Backup( string path )
    {
        Directory.CreateDirectory( paths.BackupDirectory );

        var stamp = _time
            .GetLocalNow( )
            .ToString( "yyyyMMddHHmmss", CultureInfo.InvariantCulture );

        var backup = Path.Combine( paths.BackupDirectory, $"{Path.GetFileName( path )}.{stamp}" );
        for ( var suffix = 1; File.Exists( backup ); suffix++ )
        {
            backup = Path.Combine( paths.BackupDirectory, $"{Path.GetFileName( path )}.{stamp}-{suffix.ToString( CultureInfo.InvariantCulture )}" );
        }

        File.Copy( path, backup );

        return backup;
    }

    // Replaces a symlink's target rather than the link, and keeps the file mode.
    private static void WriteAtomically( string path, string content )
    {
        string? target;
        if ( File.Exists( path ) )
        {
            target = File.ResolveLinkTarget( path, returnFinalTarget: true )
                         ?.FullName ??
                     path;
        }
        else
        {
            target = path;
        }

        var directory = Path.GetDirectoryName( target ) ?? ".";
        Directory.CreateDirectory( directory );

        var temporary = Path.Combine( directory, $".{Path.GetFileName( target )}.commanddeck-{Guid.NewGuid( ):N}" );

        try
        {
            File.WriteAllText( temporary, content );

            if ( File.Exists( target ) && !OperatingSystem.IsWindows( ) )
            {
                File.SetUnixFileMode( temporary, File.GetUnixFileMode( target ) );
            }

            File.Move( temporary, target, overwrite: true );
        }
        finally
        {
            File.Delete( temporary );
        }
    }
}
