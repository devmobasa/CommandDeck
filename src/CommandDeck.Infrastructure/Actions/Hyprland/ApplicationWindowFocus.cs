using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommandDeck;

/// <summary>
/// Launching an application that already has a window focuses that window
/// instead, the most recently used one when it has several; Open New Window
/// launches anyway. A window belongs to the entry when its class or initial
/// class is the entry's StartupWMClass or desktop ID. Chromium-based web app
/// classes end in a profile name, so that suffix is ignored on both sides.
/// Terminal entries, launches that open files, and sessions without Hyprland
/// always launch.
/// </summary>
internal sealed partial class ApplicationWindowFocus
{
    private static readonly TimeSpan ClientsTimeout = TimeSpan.FromMilliseconds( 400 );

    private readonly ExternalProcessRunner _run;
    private readonly Func<string, string?> _environment;
    private readonly Func<bool> _inHyprland;

    public ApplicationWindowFocus( ExternalProcessRunner? hyprlandRunner = null, Func<bool>? inHyprland = null )
    {
        _run = hyprlandRunner ?? DefaultRunAsync;
        _environment = HyprlandSocket.EnvironmentFor( hyprlandRunner );
        _inHyprland = inHyprland ?? (static ( ) =>
            !string.IsNullOrEmpty( Environment.GetEnvironmentVariable( EnvironmentVariableNames.HyprlandInstanceSignature ) ) &&
            ProcessRunner.ExistsOnPath( ToolExecutables.Hyprctl ));
    }

    // Null means launch: no window, no Hyprland, or a focus Hyprland did not acknowledge.
    public async ValueTask<ActionExecutionResult?> TryFocusAsync( CommandAction action, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( action );

        var keys = WindowKeys( action );
        if ( keys.Count == 0 || AlwaysLaunches( action ) || !_inHyprland( ) )
        {
            return null;
        }

        // The window list comes from the session's socket when it answers, so
        // only the focus itself starts hyprctl.
        var clients = await HyprlandSocket
            .QueryOrRunAsync( "clients", _environment, _run, ClientsTimeout, cancellationToken )
            .ConfigureAwait( false );

        if ( clients.Length == 0 || FindWindow( clients, keys ) is not { } address )
        {
            return null;
        }

        var lua = $"hl.dsp.focus({{ window = {HyprlandDispatch.QuoteLuaString( "address:" + address )} }})";
        var focused = await _run( ToolExecutables.Hyprctl, [HyprlandDispatch.Command, lua], HyprlandDispatch.Timeout, cancellationToken )
            .ConfigureAwait( false );

        if ( !HyprlandDispatch.WasAccepted( focused ) )
        {
            return null;
        }

        var name = action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.Name );

        return ActionExecutionResult.Success( string.IsNullOrWhiteSpace( name ) ? "Switched to the open window" : $"Switched to {name}" );
    }

    private static bool AlwaysLaunches( CommandAction action )
    {
        return action.Arguments.GetValueOrDefault( ActionArgumentNames.NewInstance ) == "true" ||
               action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.Terminal ) == "true" ||
               !string.IsNullOrEmpty( action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.LaunchArguments ) );
    }

    internal static HashSet<string> WindowKeys( CommandAction action )
    {
        HashSet<string> keys = new( StringComparer.Ordinal );
        if ( action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.StartupWmClass ) is { Length: > 0 } wmClass )
        {
            keys.Add( Normalize( wmClass ) );
        }

        if ( action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.DesktopId ) is { Length: > 0 } desktopId )
        {
            var stem = desktopId.EndsWith( FileExtensions.Desktop, StringComparison.Ordinal ) ? desktopId[..^FileExtensions.Desktop.Length] : desktopId;
            if ( stem.Length > 0 )
            {
                keys.Add( Normalize( stem ) );
            }
        }

        return keys;
    }

    // The matching window that was focused most recently; Hyprland numbers
    // focus history from 0, the window focused last.
    internal static string? FindWindow( string json, IReadOnlySet<string> keys )
    {
        try
        {
            using var document = JsonDocument.Parse( json );
            if ( document.RootElement.ValueKind != JsonValueKind.Array )
            {
                return null;
            }

            string? best = null;
            var bestHistory = long.MaxValue;
            foreach ( var client in document.RootElement.EnumerateArray( ) )
            {
                if ( client.ValueKind != JsonValueKind.Object || IsFalse( client, "mapped" ) || IsTrue( client, "hidden" ) ||
                     Text( client, "address" ) is not { Length: > 0 } address || !Matches( client, keys ) )
                {
                    continue;
                }

                var history = client.TryGetProperty( "focusHistoryID", out var value ) && value.TryGetInt64( out var number ) && number >= 0
                    ? number
                    : long.MaxValue - 1;

                if ( history < bestHistory )
                {
                    best = address;
                    bestHistory = history;
                }
            }

            return best;
        }
        catch ( JsonException )
        {
            return null;
        }
    }

    private static bool Matches( JsonElement client, IReadOnlySet<string> keys )
    {
        return Text( client, "class" ) is { Length: > 0 } windowClass && keys.Contains( Normalize( windowClass ) ) ||
               Text( client, "initialClass" ) is { Length: > 0 } initialClass && keys.Contains( Normalize( initialClass ) );
    }

    // chrome-discord.com__channels_@me-Default and its -Profile_1 twin are one app.
    internal static string Normalize( string windowClass )
    {
        var lower = windowClass.Trim( ).ToLowerInvariant( );
        var webApp = WebAppClass( ).Match( lower );

        return webApp.Success ? webApp.Groups["app"].Value : lower;
    }

    private static string? Text( JsonElement element, string property ) =>
        element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.String ? value.GetString( ) : null;

    private static bool IsTrue( JsonElement element, string property ) =>
        element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.True;

    private static bool IsFalse( JsonElement element, string property ) =>
        element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.False;

    private static ValueTask<ProcessResult> DefaultRunAsync( string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken ) =>
        ProcessRunner.RunAsync( fileName, arguments, null, null, timeout, cancellationToken );

    [GeneratedRegex( "^(?<app>(?:chrome|chromium|brave|msedge)-.+)-(?:default|profile[_ ]?\\d+)$" )]
    private static partial Regex WebAppClass( );
}
