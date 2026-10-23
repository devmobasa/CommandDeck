using System.Text.Json;

namespace CommandDeck;

// The live half of the desktop wiring checks: what the running compositor
// actually binds and which global shortcuts the shell has registered.
internal sealed partial class DesktopWiringDoctor
{
    internal static readonly TimeSpan HyprctlTimeout = TimeSpan.FromSeconds( 2 );

    internal static readonly ProcessExecutionLimits HyprctlLimits = new( )
    {
        MaximumStandardOutputBytes = 4 * ByteUnits.BytesPerMebibyte,
        MaximumStandardErrorBytes = 16 * ByteUnits.BytesPerKibibyte,
        MaximumMemoryBytes = 256L * ByteUnits.BytesPerMebibyte
    };

    // Hyprland's modmask bits in the order Omarchy writes modifiers.
    private static readonly (int Bit, string Name)[] ModifierBits =
    [
        (64, ShortcutModifiers.Super), (4, ShortcutModifiers.Control), (8, ShortcutModifiers.Alt),
        (1, ShortcutModifiers.Shift), (16, "MOD2"), (32, "MOD3"), (128, "MOD5")
    ];

    private async ValueTask<IReadOnlyList<DesktopCheck>> InspectHyprlandAsync( string? expectedChord, string? userChord, CancellationToken cancellationToken )
    {
        const string binding = "Hyprland binding";
        const string shortcut = "Global shortcut";
        string? unavailable;
        if ( string.IsNullOrEmpty( environment( EnvironmentVariableNames.HyprlandInstanceSignature ) ) )
        {
            unavailable = "no Hyprland session in this environment";
        }
        else
        {
            if ( !commandExists( ToolExecutables.Hyprctl ) )
            {
                unavailable = "hyprctl is not on PATH";
            }
            else
            {
                unavailable = null;
            }
        }

        if ( unavailable is not null )
        {
            return [new( binding, DesktopCheckState.Skipped, unavailable ), new( shortcut, DesktopCheckState.Skipped, unavailable )];
        }

        var binds = await QueryAsync( "binds", cancellationToken )
            .ConfigureAwait( false );

        var shortcuts = await QueryAsync( "globalshortcuts", cancellationToken )
            .ConfigureAwait( false );

        return
        [
            binds.Document is null ? new( binding, DesktopCheckState.Skipped, binds.Problem! ) : InspectBinding( binds.Document, expectedChord, userChord ),
            shortcuts.Document is null ? new( shortcut, DesktopCheckState.Skipped, shortcuts.Problem! ) : InspectShortcut( shortcuts.Document )
        ];
    }

    // CommandDeck's Lua bind reports dispatcher "__lua", so its description identifies it.
    // A binding the user wrote carries no CommandDeck description, so it is
    // found by its chord.
    private static DesktopCheck InspectBinding( JsonDocument document, string? expectedChord, string? userChord )
    {
        const string name = "Hyprland binding";

        using ( document )
        {
            var binds = GetBindingObjects( document );
            var chords = GetCommandDeckChords( binds );
            if ( chords.Count == 0 && userChord is not null )
            {
                var canonical = Canonical( userChord );
                if ( binds.Any( bind => FormatChord( bind ) == canonical ) )
                {
                    return new( name, DesktopCheckState.Passed, $"{canonical} is active, from your binding" );
                }
                else
                {
                    return new( name, DesktopCheckState.Warning, $"Hyprland does not bind {canonical}", "Run `hyprctl reload`, then `hyprctl configerrors`." );
                }
            }

            if ( chords.Count == 0 )
            {
                return new( name, DesktopCheckState.Warning, "Hyprland has no active CommandDeck binding",
                    "Run `hyprctl reload`, then `hyprctl configerrors`; if it is still missing, " + "run `commanddeck hotkey install`." );
            }

            var active = string.Join( ", ", chords );
            if ( IsExpectedChordMissing( expectedChord, chords ) )
            {
                return new( name, DesktopCheckState.Warning, $"Hyprland binds {active}, but commanddeck.lua binds {expectedChord}", "Run `hyprctl reload`." );
            }

            return new( name, DesktopCheckState.Passed, $"{active} is active" );
        }
    }

    private static DesktopCheck InspectShortcut( JsonDocument document )
    {
        const string name = "Global shortcut";

        using ( document )
        {
            var registered = document
                .RootElement.EnumerateArray( )
                .Any( static entry => entry.ValueKind == JsonValueKind.Object && GetString( entry, "name" ) == HyprlandHotkeyConfig.ShortcutId );

            if ( registered )
            {
                return new( name, DesktopCheckState.Passed, $"{HyprlandHotkeyConfig.ShortcutId} is registered by the running shell" );
            }
            else
            {
                return new( name, DesktopCheckState.Warning, $"{HyprlandHotkeyConfig.ShortcutId} is not registered, so the hotkey reaches nothing",
                    "Enable the plugin, then run `omarchy restart shell`." );
            }
        }
    }

    private async ValueTask<(JsonDocument? Document, string? Problem)> QueryAsync( string request, CancellationToken cancellationToken )
    {
        ProcessResult result;

        try
        {
            result = await run( ToolExecutables.Hyprctl, ["-j", request], HyprctlTimeout, HyprctlLimitsFor( environment ), cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception )
        {
            return (null, $"hyprctl could not start: {exception.Message}");
        }

        if ( HasHyprctlFailed( result ) )
        {
            string? reason;
            if ( result.TimedOut )
            {
                reason = "timed out";
            }
            else
            {
                if ( HasHyprctlExceededLimits( result ) )
                {
                    reason = "exceeded its output or memory limit";
                }
                else
                {
                    reason = $"exited with status {result.ExitCode}";
                }
            }

            return (null, $"`hyprctl -j {request}` {reason}");
        }

        try
        {
            var document = JsonDocument.Parse( result.StandardOutput );
            if ( document.RootElement.ValueKind == JsonValueKind.Array )
            {
                return (document, null);
            }

            document.Dispose( );
        }
        catch ( JsonException )
        {
        }

        return (null, $"`hyprctl -j {request}` returned an unexpected response");
    }

    // hyprctl finds the compositor through these variables; nothing else is passed.
    internal static ProcessExecutionLimits HyprctlLimitsFor( Func<string, string?> environment )
    {
        Dictionary<string, string> variables = new( StringComparer.Ordinal )
        {
            [EnvironmentVariableNames.ExecutablePath] =
                environment( EnvironmentVariableNames.ExecutablePath ) ?? ProcessEnvironmentDefaults.SystemExecutablePath,
            [EnvironmentVariableNames.Language] = ProcessEnvironmentDefaults.Utf8Locale,
            [EnvironmentVariableNames.LocaleAll] = ProcessEnvironmentDefaults.Utf8Locale
        };

        foreach ( var name in new[] { EnvironmentVariableNames.HyprlandInstanceSignature, XdgEnvironmentNames.RuntimeDirectory } )
        {
            if ( environment( name ) is { Length: > 0 } value )
            {
                variables[name] = value;
            }
        }

        return new ProcessExecutionLimits( )
        {
            MaximumStandardOutputBytes = HyprctlLimits.MaximumStandardOutputBytes,
            MaximumStandardErrorBytes = HyprctlLimits.MaximumStandardErrorBytes,
            MaximumMemoryBytes = HyprctlLimits.MaximumMemoryBytes,
            EnvironmentVariables = variables
        };
    }

    private static string FormatChord( JsonElement bind )
    {
        var key = GetString( bind, "key" );
        if ( string.IsNullOrWhiteSpace( key ) )
        {
            if ( bind.TryGetProperty( "keycode", out var code ) && code.TryGetInt32( out var number ) && number > 0 )
            {
                key = "code:" + number.ToString( System.Globalization.CultureInfo.InvariantCulture );
            }
            else
            {
                key = null;
            }
        }

        if ( key is null )
        {
            return string.Empty;
        }

        var modifiers = bind.TryGetProperty( "modmask", out var mask ) && mask.TryGetInt32( out var bits ) ? bits : 0;

        var names = GetModifierNames( modifiers );

        return string.Join( " + ", names.Append( key.ToUpperInvariant( ) ) );
    }

    // A chord in the modifier order FormatChord uses, so the two compare equal.
    private static string Canonical( string chord )
    {
        var parts = chord.Split( '+', StringSplitOptions.TrimEntries );
        HashSet<string> given = new( parts.Take( parts.Length - 1 ), StringComparer.OrdinalIgnoreCase );
        var modifiers = OrderModifierNames( given );

        return string.Join( " + ", modifiers.Append( parts[^1]
            .ToUpperInvariant( ) ) );
    }

    private static ValueTask<ProcessResult> RunBoundedAsync( string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, ProcessExecutionLimits limits,
        CancellationToken cancellationToken ) => ProcessRunner.RunBoundedAsync( fileName, arguments, null, timeout, limits, cancellationToken );

    private static List<JsonElement> GetBindingObjects( JsonDocument document )
    {
        return document
            .RootElement.EnumerateArray( )
            .Where( static bind => bind.ValueKind == JsonValueKind.Object )
            .ToList( );
    }

    private static List<string> GetCommandDeckChords( List<JsonElement> binds )
    {
        return binds
            .Where( static bind => GetString( bind, "description" ) == HyprlandHotkeyConfig.BindingDescription )
            .Select( FormatChord )
            .Where( static chord => chord.Length > 0 )
            .Distinct( StringComparer.Ordinal )
            .ToList( );
    }

    private static bool IsExpectedChordMissing( string? expectedChord, List<string> chords )
    {
        return expectedChord is not null && !chords.Contains( Canonical( expectedChord ), StringComparer.Ordinal );
    }

    private static bool HasHyprctlFailed( ProcessResult result )
    {
        return result.TimedOut || result.ExitCode != 0 || result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static bool HasHyprctlExceededLimits( ProcessResult result )
    {
        return result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static IEnumerable<string> GetModifierNames( int modifiers )
    {
        return ModifierBits
            .Where( modifier => (modifiers & modifier.Bit) != 0 )
            .Select( static modifier => modifier.Name );
    }

    private static IEnumerable<string> OrderModifierNames( HashSet<string> given )
    {
        return ModifierBits
            .Select( static modifier => modifier.Name )
            .Where( given.Contains );
    }
}
