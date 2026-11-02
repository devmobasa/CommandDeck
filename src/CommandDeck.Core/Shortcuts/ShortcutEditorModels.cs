using System.Globalization;
using System.Text;

namespace CommandDeck;

public sealed class ShortcutEditRequest
{
    public string Id { get; init; } = "";
    public string Chord { get; init; } = "";
    public string Description { get; init; } = "";
    public string Command { get; init; } = "";
    public bool Replace { get; init; }
}

public sealed record ShortcutOverride
{
    public string Id { get; init; } = "";
    public string OriginalChord { get; init; } = "";
    public string Chord { get; init; } = "";
    public string Description { get; init; } = "";
    public string Callee { get; init; } = "hl.bind";
    public string Dispatcher { get; init; } = "";
    public string Options { get; init; } = "{}";
    public string SourceFile { get; init; } = "";
    public bool Added { get; init; }
    public bool LauncherHotkey { get; init; }
    public bool HotkeyEdit { get; init; }
    public string? ReplacedBy { get; init; }
}

public sealed class ShortcutEditorSnapshot
{
    public List<Keybinding> Bindings { get; init; } = [];
    public string ConfigurationPath { get; init; } = "";
    public bool ModuleInstalled { get; init; }
    public bool HotkeyManaged { get; init; }
    public string Hotkey { get; init; } = "";
    public int ChangeCount { get; init; }
    public string Lua { get; init; } = "";
    public string? ConflictId { get; init; }
    public string? ConflictDescription { get; init; }
}

public static class ShortcutLua
{
    private static readonly string[] ModifierOrder = ["SUPER", "CTRL", "ALT", "SHIFT", "MOD2", "MOD3", "MOD5"];
    public const string KeysRequire = "if package.searchpath(\"hypr.commanddeck_keys\", package.path) then require(\"hypr.commanddeck_keys\") end";
    public const string SupportOnlyMarker = "-- The launcher hotkey is managed in your own Hyprland bindings.";
    public const string RecordingSubmap = "commanddeck_record";

    // Omarchy exempts its own panels from Hyprland's layer fades. Without the
    // same rule the launcher fades in and lingers on screen after it closes.
    public const string LayerRule = """hl.layer_rule({ match = { namespace = "^commanddeck" }, no_anim = true, animation = "none" })""";

    public const string ModuleDefinitions = LayerRule + "\n" + """
                                                                hl.define_submap("commanddeck_record", function()
                                                                  hl.bind("ESCAPE", function()
                                                                    hl.dispatch(hl.dsp.submap("reset"))
                                                                    hl.dispatch(hl.dsp.global("commanddeck:record-cancel"))
                                                                  end)
                                                                end)
                                                                """;

    public static string Quote( string value )
    {
        StringBuilder result = new( "\"" );
        foreach ( var character in value )
        {
            if ( character is '\\' or '"' )
            {
                result
                    .Append( '\\' )
                    .Append( character );
            }
            else if ( char.IsControl( character ) )
            {
                result
                    .Append( '\\' )
                    .Append( (( int ) character).ToString( "D3", CultureInfo.InvariantCulture ) );
            }
            else
            {
                result.Append( character );
            }
        }

        return result
            .Append( '"' )
            .ToString( );
    }

    public static string CanonicalChord( string chord )
    {
        var parts = chord
            .Replace( " ", "", StringComparison.Ordinal )
            .ToUpperInvariant( )
            .Replace( "CONTROL", "CTRL", StringComparison.Ordinal )
            .Replace( "RETURN", "ENTER", StringComparison.Ordinal )
            .Split( '+' );

        var modifiers = parts
            .Take( parts.Length - 1 )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );

        var known = ModifierOrder.Where( part => modifiers.Contains( part, StringComparer.Ordinal ) );
        var other = modifiers
            .Except( ModifierOrder, StringComparer.Ordinal )
            .Order( StringComparer.Ordinal );

        return string.Join( '+', known
            .Concat( other )
            .Append( parts[^1] ) );
    }

    public static string HyprChord( string chord )
    {
        var parts = chord
            .Split( '+', StringSplitOptions.TrimEntries )
            .Select( static part => part.Equals( "Enter", StringComparison.OrdinalIgnoreCase ) ? "RETURN" : part );

        return string.Join( " + ", parts );
    }

    public static bool IsHotkeySuppressed( IReadOnlyList<ShortcutOverride> changes, string chord ) => changes.Any( change =>
        change.LauncherHotkey && !change.HotkeyEdit && CanonicalChord( change.OriginalChord ) == CanonicalChord( chord ) );

    public static string Generate( IReadOnlyList<ShortcutOverride> changes, bool includeHotkeyEdits = false,
        IReadOnlyList<ShortcutOverride>? preservedBindings = null )
    {
        var emitted = BindingsToEmit( changes, includeHotkeyEdits, preservedBindings );
        StringBuilder result = new( "-- Generated by CommandDeck. Edit through Settings → Shortcuts.\n-- Delete this file or use Reset all to undo.\n" );

        // Unbind everything first: swaps and replacements must not erase a binding
        // we just installed earlier in this file.
        foreach ( var chord in OriginalChords( emitted ) )
        {
            result
                .Append( "hl.unbind(" )
                .Append( Quote( HyprChord( chord ) ) )
                .Append( ")\n" );
        }

        result.Append( BindCalls( ActiveBindings( emitted, changes ) ) );

        return result.ToString( );
    }

    private static List<ShortcutOverride> BindingsToEmit( IReadOnlyList<ShortcutOverride> changes, bool includeHotkeyEdits,
        IReadOnlyList<ShortcutOverride>? preservedBindings )
    {
        return changes
            .Where( change => includeHotkeyEdits || !change.HotkeyEdit )
            .Concat( preservedBindings ?? [] )
            .ToList( );
    }

    private static IEnumerable<string> OriginalChords( IEnumerable<ShortcutOverride> bindings )
    {
        return bindings
            .Select( static change => change.OriginalChord )
            .Where( static chord => chord.Length > 0 )
            .Distinct( StringComparer.OrdinalIgnoreCase );
    }

    private static IEnumerable<ShortcutOverride> ActiveBindings( IEnumerable<ShortcutOverride> emitted, IReadOnlyList<ShortcutOverride> changes )
    {
        return emitted.Where( change => change.Chord.Length > 0 && !(change.HotkeyEdit && IsHotkeySuppressed( changes, change.Chord )) );
    }

    public static string BindCalls( IEnumerable<ShortcutOverride> bindings )
    {
        StringBuilder result = new( );
        foreach ( var change in bindings )
        {
            result
                .Append( change.Callee )
                .Append( '(' )
                .Append( Quote( HyprChord( change.Chord ) ) )
                .Append( ", " );
            if ( change.Callee == "o.bind" )
            {
                result
                    .Append( Quote( change.Description ) )
                    .Append( ", " );
            }

            result
                .Append( change.Dispatcher )
                .Append( ", " )
                .Append( change.Options )
                .Append( ")\n" );
        }

        return result.ToString( );
    }
}
