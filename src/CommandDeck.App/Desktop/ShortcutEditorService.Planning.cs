using System.Text.Json;

namespace CommandDeck;

public sealed partial class ShortcutEditorService
{
    private sealed record ModulePlan( string Keys, string Loader );

    private ModulePlan PlanModules( List<ShortcutOverride> changes, List<ShortcutOverride> previous,
        IReadOnlyList<Keybinding> inventory, string? requestedHotkey, string? installationLoader = null )
    {
        var oldLoader = installationLoader ?? (File.Exists( _paths.HotkeyFile ) ? File.ReadAllText( _paths.HotkeyFile ) : "");
        if ( oldLoader.Length > 0 && !HyprlandHotkeyConfig.IsGenerated( oldLoader ) )
        {
            throw new InvalidOperationException( "The CommandDeck module has custom changes. Run commanddeck hotkey install before editing shortcuts." );
        }

        var managed = oldLoader.Length > 0 && !oldLoader.Contains( ShortcutLua.SupportOnlyMarker, StringComparison.Ordinal );
        var chord = RequestedChord( requestedHotkey, changes, previous, oldLoader );
        if ( !managed || ShortcutLua.IsHotkeySuppressed( changes, chord ) )
        {
            chord = "";
        }

        var unbound = changes.Where( static change => !change.HotkeyEdit && change.OriginalChord.Length > 0 )
            .Select( static change => ShortcutLua.CanonicalChord( change.OriginalChord ) ).ToHashSet( StringComparer.Ordinal );
        var touched = new HashSet<string>( unbound, StringComparer.Ordinal );
        if ( chord.Length > 0 )
        {
            touched.Add( ShortcutLua.CanonicalChord( chord ) );
        }

        var preserved = PreserveOriginals( OriginalInventory( inventory, previous ), changes, touched, managed, chord );
        var companions = changes.Where( change => !change.LauncherHotkey && change.Chord.Length > 0 && SameChord( change.Chord, chord ) )
            .Concat( preserved.Where( change => SameChord( change.Chord, chord ) ) ).ToArray( );
        var keys = ShortcutLua.Generate( changes, preservedBindings: preserved.Where( change =>
             unbound.Contains( ShortcutLua.CanonicalChord( change.Chord ) ) ).ToArray( ) );
        var loader = CreateLoader( managed, chord, companions );

        return new( keys, loader );
    }

    private static string CreateLoader( bool managed, string chord, IReadOnlyList<ShortcutOverride> companions )
    {
        if ( managed && chord.Length > 0 )
        {
            return HyprlandHotkeyConfig.GenerateFile( chord, companions );
        }

        var marker = managed ? "" : ShortcutLua.SupportOnlyMarker + "\n";

        return HyprlandHotkeyConfig.GeneratedHeader + "\n" + marker + ShortcutLua.KeysRequire + "\n" + ShortcutLua.ModuleDefinitions + "\n";
    }

    private string OriginalLauncherChord( string fallback ) => File.Exists( _paths.HotkeyFile )
        ? HyprlandHotkeyConfig.ReadChord( File.ReadAllText( _paths.HotkeyFile ) ) ?? fallback : fallback;

    private static string RequestedChord( string? requested, List<ShortcutOverride> changes, List<ShortcutOverride> previous, string loader ) =>
        requested ?? changes.FirstOrDefault( static change => change.HotkeyEdit )?.Chord ?? HyprlandHotkeyConfig.ReadChord( loader ) ??
        previous.FirstOrDefault( static change => change.LauncherHotkey && !change.HotkeyEdit )?.OriginalChord ?? "";

    private List<ShortcutOverride> PreserveOriginals( IReadOnlyList<Keybinding> inventory, List<ShortcutOverride> changes,
        HashSet<string> touched, bool managed, string launcherChord )
    {
        var sources = ReadSources( );
        List<ShortcutOverride> preserved = [];
        foreach ( var binding in inventory.Where( binding => touched.Contains( ShortcutLua.CanonicalChord( binding.Chord ) ) ) )
        {
            if ( binding.Submap == ShortcutLua.RecordingSubmap && SameChord( binding.Chord, "ESCAPE" ) ||
                 managed && binding.Description == HyprlandHotkeyConfig.BindingDescription )
            {
                continue;
            }

            var matches = sources.Where( source => source.Call.Description == binding.Description && SameChord( source.Call.Chord, binding.Chord ) ).ToArray( );
            var source = matches.LastOrDefault( );
            var duplicate = inventory.Count( row => row.Description == binding.Description && SameChord( row.Chord, binding.Chord ) &&
                 row.Submap == binding.Submap ) > 1;
            if ( changes.Any( change => IsOriginal( change, binding ) ) && !duplicate )
            {
                continue;
            }

            if ( GivesWayToLauncher( binding, matches, launcherChord ) )
            {
                continue;
            }

            if ( duplicate || binding.Submap.Length > 0 || matches.Length != 1 || source is not { BeforeModule: true, Conditional: false, Call.Literal: true } )
            {
                throw new InvalidOperationException( ($"{DisplayChord( binding.Chord )} also runs {binding.Description}, " +
                    "which can't be recreated here. Change it in " +
                    $"{source?.File ?? _paths.MainConfig}.") );
            }

            preserved.Add( new( )
            {
                Id = source.Id,
                Chord = source.Call.Chord,
                Description = binding.Description,
                Callee = source.Call.Callee,
                Dispatcher = source.Call.Dispatcher,
                Options = source.Call.Options,
                SourceFile = source.File
            } );
        }

        return preserved;
    }

    // An Omarchy default that may not even be loaded, in a file nobody edits,
    // gives way to the launcher, as the module's unbind would at run time.
    // A chord that only moves keeps every other binding on it.
    private bool GivesWayToLauncher( Keybinding binding, ResolvedShortcut[] matches, string launcherChord ) =>
        launcherChord.Length > 0 && SameChord( binding.Chord, launcherChord ) && matches.Length > 0 &&
        matches.All( IsConditionalOmarchyDefault );

    private bool IsConditionalOmarchyDefault( ResolvedShortcut source )
    {
        return source is { BeforeModule: true, Conditional: true } && IsOmarchyOwned( source.File );
    }

    // Inside Omarchy's directory once every link is followed: a file that links
    // out of it is someone else's. The directory itself may be a link.
    private bool IsOmarchyOwned( string file )
    {
        if ( !file.StartsWith( _omarchyDirectory, StringComparison.Ordinal ) )
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator( RealPath( _omarchyDirectory ) ) + Path.DirectorySeparatorChar;

            return RealPath( file ).StartsWith( root, StringComparison.Ordinal );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            return false;
        }
    }

    // The path with every symbolic link followed, as realpath(3) gives it: one
    // name at a time, so a ".." after a link leaves the link's target.
    private static string RealPath( string path )
    {
        var full = Path.Combine( Environment.CurrentDirectory, path );
        var root = Path.GetPathRoot( full ) ?? "/";
        List<string> remaining = [.. full[root.Length..].Split( Path.DirectorySeparatorChar )];
        var current = root;
        for ( var links = 0; remaining.Count > 0; )
        {
            var name = remaining[0];
            remaining.RemoveAt( 0 );
            if ( name is "" or "." )
            {
                continue;
            }

            if ( name == ".." )
            {
                current = Path.GetDirectoryName( current ) ?? root;
                continue;
            }

            var next = Path.Combine( current, name );
            if ( new FileInfo( next ).LinkTarget is not { } target )
            {
                current = next;
                continue;
            }

            if ( ++links > 40 )
            {
                throw new IOException( "Too many levels of symbolic links." );
            }

            remaining.InsertRange( 0, target.Split( Path.DirectorySeparatorChar ) );
            if ( Path.IsPathRooted( target ) )
            {
                current = root;
            }
        }

        return current;
    }

    private static List<Keybinding> OriginalInventory( IReadOnlyList<Keybinding> inventory, List<ShortcutOverride> previous )
    {
        var originals = inventory.Where( binding => !previous.Any( change => !change.HotkeyEdit && change.Description == binding.Description &&
            change.Chord.Length > 0 && SameChord( change.Chord, binding.Chord ) ) ).ToList( );
        foreach ( var change in previous.Where( static change => !change.Added && !change.HotkeyEdit && change.OriginalChord.Length > 0 ) )
        {
            var description = change.LauncherHotkey ? HyprlandHotkeyConfig.BindingDescription : change.Description;
            if ( !originals.Any( row => row.Description == description && SameChord( row.Chord, change.OriginalChord ) && row.Submap.Length == 0 ) )
            {
                originals.Add( new( ) { Chord = change.OriginalChord, Description = description } );
            }
        }

        return originals;
    }

    private static bool IsOriginal( ShortcutOverride change, Keybinding binding ) => !change.HotkeyEdit && binding.Submap.Length == 0 &&
        SameChord( change.OriginalChord, binding.Chord ) && (change.LauncherHotkey ? binding.Description ==
             HyprlandHotkeyConfig.BindingDescription : change.Description == binding.Description);

    private static bool SameChord( string first, string second ) => ShortcutLua.CanonicalChord( first ) == ShortcutLua.CanonicalChord( second );

    private static string DisplayChord( string chord ) => string.Join( '+', ShortcutLua.CanonicalChord( chord ).Split( '+' ).Select( static part =>
        part switch
        {
            "SUPER" => "Super",
            "CTRL" => "Ctrl",
            "ALT" => "Alt",
            "SHIFT" => "Shift",
            "ESCAPE" => "Escape",
            "ENTER" => "Enter",
            "SPACE" => "Space",
            _ => part
        } ) );

    private async ValueTask SaveAsync( List<ShortcutOverride> changes, List<ShortcutOverride> previous, IReadOnlyList<Keybinding> inventory,
        CancellationToken cancellationToken, string? requestedHotkey = null )
    {
        var plan = PlanModules( changes, previous, inventory, requestedHotkey );
        var updates = new Dictionary<string, string?>( StringComparer.Ordinal )
        {
            [_stateFile] = JsonSerializer.Serialize( changes, CommandDeckJsonContext.Default.ListShortcutOverride ),
            [ModuleFile] = changes.Count == 0 ? null : plan.Keys,
            [_paths.HotkeyFile] = plan.Loader
        };
        if ( !File.Exists( _paths.HotkeyFile ) )
        {
            if ( !File.Exists( _paths.MainConfig ) )
            {
                throw new InvalidOperationException( "The Hyprland Lua configuration is missing." );
            }

            updates[_paths.MainConfig] = HyprlandHotkeyConfig.EnsureRequire( File.ReadAllText( _paths.MainConfig ) );
        }

        await ShortcutFileTransaction.ApplyAsync( updates, ReloadAsync, cancellationToken ).ConfigureAwait( false );
    }

    private string ExportChanges( List<ShortcutOverride> changes )
    {
        var keys = File.Exists( ModuleFile ) ? File.ReadAllText( ModuleFile ) : ShortcutLua.Generate( changes );
        if ( !File.Exists( _paths.HotkeyFile ) )
        {
            return keys + ShortcutLua.ModuleDefinitions + "\n";
        }

        var loader = File.ReadAllText( _paths.HotkeyFile );
        var chord = HyprlandHotkeyConfig.ReadChord( loader );
        if ( chord is not null && (changes.Any( static change => change.HotkeyEdit ) || changes.Any( change => SameChord( change.OriginalChord, chord ) )) )
        {
            keys += loader.Replace( ShortcutLua.KeysRequire, "", StringComparison.Ordinal ).Replace( ShortcutLua.ModuleDefinitions, "",
                 StringComparison.Ordinal );
        }

        return keys + ShortcutLua.ModuleDefinitions + "\n";
    }
}
