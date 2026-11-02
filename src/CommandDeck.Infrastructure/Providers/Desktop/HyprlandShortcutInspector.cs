using System.Globalization;
using System.Text.Json;

namespace CommandDeck;

public static class HyprlandShortcutInspector
{
    private const string CodePrefix = "code:";

    private const int MaximumSubmapCharacters = 200;
    private const int SuperModifierMask = 64;
    private const int ControlModifierMask = 4;
    private const int AltModifierMask = 8;

    private static readonly ShortcutTarget[] Targets =
    [
        new( "commanddeck", "CommandDeck", CommandDeckIdentity.PluginId, ["CommandDeck"] ),
        new( "omarchy-control", "Omarchy Control", "omarchy-menu toggle root", ["Omarchy menu"] ),
        new( "applications", "Applications", "omarchy-menu toggle apps", ["Installed applications"] ),
        new( "system-menu", "System Menu", "omarchy-menu toggle system", ["System menu"] )
    ];

    public const int MaximumKeybindings = 1024;
    private const string Xf86Prefix = "XF86";

    public static async ValueTask<IReadOnlyList<ShortcutBinding>> InspectAsync( CancellationToken cancellationToken ) =>
        (await InspectWithKeybindingsAsync( cancellationToken )
            .ConfigureAwait( false )).Shortcuts;

    // One hyprctl call answers both the launcher targets and the full binding list.
    public static async ValueTask<(IReadOnlyList<ShortcutBinding> Shortcuts, IReadOnlyList<Keybinding> Keybindings)> InspectWithKeybindingsAsync(
        CancellationToken cancellationToken )
    {
        var output = await ReadBindsAsync( cancellationToken )
            .ConfigureAwait( false );

        return output.Json is { } json ? (Parse( json ), ParseKeybindings( json )) : (output.Failure!, []);
    }

    private static async ValueTask<(string? Json, IReadOnlyList<ShortcutBinding>? Failure)> ReadBindsAsync( CancellationToken cancellationToken )
    {
        if ( !ProcessRunner.ExistsOnPath( ToolExecutables.Hyprctl ) )
        {
            return (null, EmptyBindings( ShortcutStatuses.NotConfigured, "hyprctl is unavailable on PATH." ));
        }

        var result = await ProcessRunner
            .RunAsync( ToolExecutables.Hyprctl, ["-j", "binds"], standardInput: null, workingDirectory: null, TimeSpan.FromMilliseconds( 600 ),
                cancellationToken )
            .ConfigureAwait( false );

        if ( result.TimedOut )
        {
            return (null, EmptyBindings( ShortcutStatuses.NotConfigured, "Shortcut inspection timed out." ));
        }

        if ( result.ExitCode != 0 )
        {
            return (null, EmptyBindings( ShortcutStatuses.NotConfigured, DescribeFailure( result ) ));
        }

        return (result.StandardOutput, null);
    }

    // Every keyboard binding with its chord and description. The chord uses the
    // launcher's modifier order; XF86 media keys keep Hyprland's own spelling.
    public static IReadOnlyList<Keybinding> ParseKeybindings( string json )
    {
        try
        {
            using var document = JsonDocument.Parse( json );
            if ( document.RootElement.ValueKind != JsonValueKind.Array )
            {
                return [];
            }

            List<Keybinding> bindings = [];
            foreach ( var bind in document.RootElement.EnumerateArray( ) )
            {
                if ( bindings.Count == MaximumKeybindings )
                {
                    break;
                }

                var chord = FormatDisplayChord( bind );
                if ( bind.ValueKind != JsonValueKind.Object || chord.Length == 0 || IsMouseBind( bind ) )
                {
                    continue;
                }

                bindings.Add( new Keybinding
                {
                    Chord = chord,
                    Description = CleanText( JsonElementReader.ReadString( bind, "description" ), ShortcutLimits.MaximumDescriptionCharacters ),
                    Submap = CleanText( JsonElementReader.ReadString( bind, "submap" ), MaximumSubmapCharacters )
                } );
            }

            return bindings;
        }
        catch ( JsonException )
        {
            return [];
        }
    }

    private static string FormatDisplayChord( JsonElement bind )
    {
        var chord = bind.ValueKind == JsonValueKind.Object ? FormatChord( bind ) : string.Empty;
        if ( chord.Length == 0 )
        {
            return chord;
        }

        var key = JsonElementReader.ReadString( bind, "key" );
        if ( key is null || !key.StartsWith( Xf86Prefix, StringComparison.OrdinalIgnoreCase ) )
        {
            return chord;
        }

        var plus = chord.LastIndexOf( '+' );

        return plus < 0 ? key : chord[..(plus + 1)] + key;
    }

    private static bool IsMouseBind( JsonElement bind ) =>
        bind.TryGetProperty( "mouse", out var mouse ) && mouse.ValueKind == JsonValueKind.True;

    private static string CleanText( string? value, int limit )
    {
        var clean = new string( (value ?? string.Empty)
            .Where( static character => !char.IsControl( character ) )
            .ToArray( ) ).Trim( );

        return clean.Length > limit ? clean[..limit] : clean;
    }

    public static async ValueTask<ShortcutInventory> InspectInventoryAsync( CancellationToken cancellationToken )
    {
        if ( !ProcessRunner.ExistsOnPath( ToolExecutables.Hyprctl ) )
        {
            return new ShortcutInventory { Detail = "hyprctl is unavailable on PATH." };
        }

        var result = await ProcessRunner
            .RunAsync( ToolExecutables.Hyprctl, ["-j", "binds"], standardInput: null, workingDirectory: null, TimeSpan.FromMilliseconds( 600 ),
                cancellationToken )
            .ConfigureAwait( false );

        if ( HasInspectionFailed( result ) )
        {
            return new ShortcutInventory { Detail = DescribeInspectionFailure( result ) };
        }

        return ParseInventory( result.StandardOutput );
    }

    public static ShortcutInventory ParseInventory( string json )
    {
        try
        {
            using var document = JsonDocument.Parse( json );
            if ( document.RootElement.ValueKind != JsonValueKind.Array )
            {
                return new ShortcutInventory { Detail = "hyprctl returned an unexpected response." };
            }

            return new ShortcutInventory
            {
                Available = true,
                Chords = document
                    .RootElement.EnumerateArray( )
                    .Select( FormatChord )
                    .Where( static value => value.Length > 0 )
                    .Select( TryNormalizeInventoryChord )
                    .Where( static value => value is not null )
                    .Select( static value => value! )
                    .Distinct( StringComparer.OrdinalIgnoreCase )
                    .Order( StringComparer.OrdinalIgnoreCase )
                    .ToList( )
            };
        }
        catch ( JsonException )
        {
            return new ShortcutInventory { Detail = "hyprctl returned malformed JSON." };
        }
    }

    private static string? TryNormalizeInventoryChord( string chord )
    {
        try
        {
            return SavedSearchValidator.NormalizeShortcut( chord );
        }
        catch ( InvalidOperationException )
        {
            return null;
        }
    }

    public static IReadOnlyList<ShortcutBinding> Parse( string json )
    {
        var chords = Targets.ToDictionary( static target => target.Id, static _ => new HashSet<string>( StringComparer.OrdinalIgnoreCase ),
            StringComparer.Ordinal );

        try
        {
            using var document = JsonDocument.Parse( json );
            if ( document.RootElement.ValueKind != JsonValueKind.Array )
            {
                return EmptyBindings( ShortcutStatuses.NotConfigured, "hyprctl returned an unexpected response." );
            }

            foreach ( var bind in document.RootElement.EnumerateArray( ) )
            {
                var dispatcher = JsonElementReader.ReadString( bind, "dispatcher" ) ?? string.Empty;
                var argument = JsonElementReader.ReadString( bind, "arg" ) ?? string.Empty;
                var command = $"{dispatcher} {argument}";
                var description = JsonElementReader.ReadString( bind, "description" ) ?? string.Empty;
                var chord = FormatChord( bind );
                if ( chord.Length == 0 )
                {
                    continue;
                }

                foreach ( var target in Targets )
                {
                    if ( MatchesTarget( command, description, target ) )
                    {
                        chords[target.Id].Add( chord );
                    }
                }
            }
        }
        catch ( JsonException )
        {
            return EmptyBindings( ShortcutStatuses.NotConfigured, "hyprctl returned malformed JSON." );
        }

        return CreateBindings( chords );
    }

    private static string FormatChord( JsonElement bind )
    {
        var modifiers = GetInt64( bind, "modmask" );
        List<string> parts = [];

        if ( (modifiers & SuperModifierMask) != 0 )
        {
            parts.Add( "Super" );
        }

        if ( (modifiers & ControlModifierMask) != 0 )
        {
            parts.Add( "Ctrl" );
        }

        if ( (modifiers & AltModifierMask) != 0 )
        {
            parts.Add( "Alt" );
        }

        if ( (modifiers & 1) != 0 )
        {
            parts.Add( "Shift" );
        }

        var key = JsonElementReader.ReadString( bind, "key" );
        if ( string.IsNullOrWhiteSpace( key ) )
        {
            var keyCode = GetInt64( bind, "keycode" );
            if ( keyCode > 0 )
            {
                key = $"{CodePrefix}{keyCode.ToString( CultureInfo.InvariantCulture )}";
            }
        }

        if ( string.IsNullOrWhiteSpace( key ) )
        {
            return string.Empty;
        }

        parts.Add( NormalizeKey( key ) );

        return string.Join( '+', parts );
    }

    private static string NormalizeKey( string key ) => key.ToUpperInvariant( ) switch
    {
        "ESCAPE" => "Escape",
        "SPACE" => "Space",
        "RETURN" => "Enter",
        _ when key.Length == 1 => key.ToUpperInvariant( ),
        _ => string.Concat( key[..1].ToUpperInvariant( ), key[1..].ToLowerInvariant( ) )
    };

    private static long GetInt64( JsonElement element, string property )
    {
        if ( TryReadNumberProperty( element, property, out var parsed ) )
        {
            return parsed;
        }
        else
        {
            return 0;
        }
    }

    private static ShortcutBinding[] EmptyBindings( string status, string? detail ) =>
        Targets
            .Select( target => new ShortcutBinding
            {
                SurfaceId = target.Id,
                Label = target.Label,
                Status = status,
                Detail = detail,
                IsAvailable = false
            } )
            .ToArray( );

    private sealed record ShortcutTarget( string Id, string Label, string Command, IReadOnlyList<string> Descriptions );

    private static string DescribeFailure( ProcessResult result )
    {
        if ( string.IsNullOrWhiteSpace( result.StandardError ) )
        {
            return $"hyprctl exited with status {result.ExitCode}.";
        }

        return result.StandardError.Trim( );
    }

    private static bool HasInspectionFailed( ProcessResult result )
    {
        return result.TimedOut || result.ExitCode != 0;
    }

    private static string DescribeInspectionFailure( ProcessResult result )
    {
        if ( result.TimedOut )
        {
            return "Shortcut inspection timed out.";
        }

        if ( string.IsNullOrWhiteSpace( result.StandardError ) )
        {
            return $"hyprctl exited with status {result.ExitCode}.";
        }

        return result.StandardError.Trim( );
    }

    private static bool MatchesTarget( string command, string description, ShortcutTarget target )
    {
        return command.Contains( target.Command, StringComparison.Ordinal ) || target.Descriptions.Contains( description, StringComparer.OrdinalIgnoreCase );
    }

    private static ShortcutBinding[] CreateBindings( Dictionary<string, HashSet<string>> chords )
    {
        return Targets
            .Select( target => new ShortcutBinding
            {
                SurfaceId = target.Id,
                Label = target.Label,
                Chords = chords[target.Id]
                    .Order( StringComparer.OrdinalIgnoreCase )
                    .ToList( ),
                Status = GetShortcutStatus( chords, target ),
                IsAvailable = chords[target.Id].Count > 0
            } )
            .ToArray( );
    }

    private static string GetShortcutStatus( Dictionary<string, HashSet<string>> chords, ShortcutTarget target )
    {
        if ( chords[target.Id].Count > 0 )
        {
            return ShortcutStatuses.Configured;
        }

        return ShortcutStatuses.NotConfigured;
    }

    private static bool TryReadNumberProperty( JsonElement element, string property, out long parsed )
    {
        parsed = default;

        return element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64( out parsed );
    }
}
