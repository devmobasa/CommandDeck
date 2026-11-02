using System.Text.Json;

namespace CommandDeck;

public sealed partial class ShortcutEditorService
{
    // Planning only: the installer writes the full pair in one file transaction
    // and owns the later session reload. Source files supply the original binds.
    internal IReadOnlyDictionary<string, string?> PlanInstallation( string? requested )
    {
        var changes = ReadChanges( );
        var previous = changes.ToList( );
        var loader = File.Exists( _paths.HotkeyFile ) ? File.ReadAllText( _paths.HotkeyFile ) : "";
        var chord = RequestedChord( requested, changes, previous, loader );
        if ( chord.Length == 0 && loader.Length == 0 && changes.Count == 0 )
        {
            chord = HyprlandHotkeyConfig.DefaultChord;
        }

        var main = HyprlandHotkeyConfig.EnsureRequire( File.ReadAllText( _paths.MainConfig ) );
        var inventory = _sources.Read( main ).Select( static source => new Keybinding
        {
            Chord = source.Call.Chord,
            Description = source.Call.Description
        } ).ToArray( );
        if ( requested is not null )
        {
            chord = ApplyInstallationHotkey( requested, changes, inventory, loader );
        }

        var preserveManual = requested is null && loader.Contains( ShortcutLua.SupportOnlyMarker, StringComparison.Ordinal );
        var planningLoader = preserveManual ? loader : HyprlandHotkeyConfig.GeneratedHeader;
        var plan = PlanModules( changes, previous, inventory, chord, planningLoader );
        Dictionary<string, string?> updates = new( StringComparer.Ordinal ) { [_paths.HotkeyFile] = plan.Loader };
        if ( changes.Count > 0 || previous.Count > 0 || File.Exists( ModuleFile ) )
        {
            updates[ModuleFile] = changes.Count == 0 ? null : plan.Keys;
        }

        if ( !changes.SequenceEqual( previous ) )
        {
            updates[_stateFile] = JsonSerializer.Serialize( changes, CommandDeckJsonContext.Default.ListShortcutOverride );
        }

        return updates;
    }

    private string ApplyInstallationHotkey( string requested, List<ShortcutOverride> changes, IReadOnlyList<Keybinding> inventory, string loader )
    {
        var chord = HyprlandHotkeyConfig.NormalizeChord( requested );
        var snapshot = Snapshot( inventory, changes );
        var conflicts = snapshot.Bindings.Where( binding => !binding.LauncherHotkey && binding.Submap.Length == 0 &&
            binding.Chord.Length > 0 && SameChord( binding.Chord, chord ) ).ToArray( );
        if ( changes.Count > 0 || conflicts.Length > 0 )
        {
            var original = changes.FirstOrDefault( static change => change.HotkeyEdit )?.OriginalChord ??
                HyprlandHotkeyConfig.ReadChord( loader ) ?? HyprlandHotkeyConfig.DefaultChord;
            ApplyHotkeyChange( changes, conflicts, snapshot, original, chord );
        }

        return chord;
    }

}
