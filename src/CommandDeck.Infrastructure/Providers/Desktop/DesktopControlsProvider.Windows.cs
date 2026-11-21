namespace CommandDeck;

public sealed partial class DesktopControlsProvider
{
    private const int MinimumMonitors = 2;
    private const int MaximumWindowLabelCharacters = 80;

    // Hyprland's fullscreen bitmask: 1 is maximized, 2 is fullscreen.
    private const int FullscreenMode = 2;
    private static readonly TimeSpan HyprctlTimeout = TimeSpan.FromMilliseconds( 250 );

    // The launcher is a layer-shell overlay, so opening it does not change
    // Hyprland's active window: activewindow still names the window the user was
    // in. Each row binds that window's literal address when the search runs, and
    // names it in the subtitle. No window, no hyprctl, or no reply means no rows.
    private async ValueTask AddWindowRowsAsync( string text, Dictionary<string, bool> tools, List<SearchResult> results, CancellationToken cancellationToken )
    {
        var matches = FindWindowCommands( text );
        if ( matches.Length == 0 || !GetOrCacheToolAvailability( ToolExecutables.Hyprctl, tools ) )
        {
            return;
        }

        var needsMonitors = matches.Any( static match => match.Command.NeedsSecondMonitor );
        var activeTask = ReadHyprlandAsync( "activewindow", cancellationToken );

        var monitorsTask = needsMonitors ? ReadHyprlandAsync( "monitors", cancellationToken ) : Task.FromResult<string?>( null );

        await Task
            .WhenAll( activeTask, monitorsTask )
            .ConfigureAwait( false );

        var window = activeTask.Result is { } active ? HyprlandWindowState.ParseActiveWindow( active ) : null;
        if ( window is null )
        {
            return;
        }

        var monitors = CountEnabledMonitors( monitorsTask );

        foreach ( (var command, var score) in matches )
        {
            if ( CanRunWindowCommand( monitors, command ) && WindowCommandTitle( command, window ) is { } title )
            {
                results.Add( BuildWindowRow( command, title, window, score ) );
            }
        }
    }

    // Toggles are named for what they will do to this window. A floating window
    // already has Tile Window, so the floating toggle only floats a tiled one.
    private static string? WindowCommandTitle( WindowCommand command, HyprlandWindow window )
    {
        return command.Operation switch
        {
            HyprlandActionOperations.ToggleFloating => window.Floating ? null : "Float Window",
            HyprlandActionOperations.ToggleFullscreen => (window.Fullscreen & FullscreenMode) != 0 ? "Exit Fullscreen" : "Enter Fullscreen",
            _ => command.Title
        };
    }

    // Through the session's socket when it answers, so a matching keystroke
    // starts no hyprctl process; otherwise through hyprctl itself.
    private async Task<string?> ReadHyprlandAsync( string request, CancellationToken cancellationToken )
    {
        var reply = await HyprlandSocket
            .QueryOrRunAsync( request, _hyprlandEnvironment, _hyprland, HyprctlTimeout, cancellationToken )
            .ConfigureAwait( false );

        return reply.Length > 0 ? reply : null;
    }

    private static SearchResult BuildWindowRow( WindowCommand command, string title, HyprlandWindow window, double score )
    {
        var label = string.IsNullOrWhiteSpace( window.Title ) ? window.Class : $"{window.Class} — {window.Title}";
        if ( label.Length > MaximumWindowLabelCharacters )
        {
            label = label[..(MaximumWindowLabelCharacters - 1)] + "…";
        }

        Dictionary<string, string> arguments = CreateWindowArguments( command, window );

        if ( command.Parameter is not null )
        {
            arguments[HyprlandActionOperations.ParameterKey] = command.Parameter;
        }

        return new SearchResult
        {
            Id = DesktopControlResultIds.For( WindowResultPrefix + command.Id ),
            Kind = ResultKinds.CustomCommand,
            Title = title,
            Subtitle = "Window · " + label,
            Icon = command.Icon,
            ProviderId = ProviderIds.DesktopControls,
            SourceId = SourceIds.DesktopControls,
            Score = score,
            Details =
            [
                new DetailField( "Window", string.IsNullOrWhiteSpace( window.Title ) ? window.Class : window.Title ),
                new DetailField( "Class", window.Class, Monospace: true ),
                new DetailField( "Address", window.Address, Monospace: true )
            ],
            Actions =
            [
                new CommandAction
                {
                    Id = RunActionId,
                    Title = title,
                    Kind = ActionKinds.HyprlandDispatch,
                    Arguments = arguments
                }
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                ["control"] = WindowResultPrefix + command.Id,
                [ResultMetadataKeys.Address] = window.Address
            }
        };
    }

    private static ValueTask<ProcessResult> DefaultRunAsync( string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken ) =>
        ProcessRunner.RunAsync( fileName, arguments, null, null, timeout, cancellationToken );

    private static (WindowCommand Command, double Score)[] FindWindowCommands( string text )
    {
        return WindowCommandCatalog
            .All.Select( command => (Command: command, Score: Score( text, command.Title, command.Keywords )) )
            .Where( static match => match.Score > 0 )
            .ToArray( );
    }

    private static int CountEnabledMonitors( Task<string?> monitorsTask )
    {
        if ( monitorsTask.Result is { } json )
        {
            return HyprlandWindowState
                .ParseMonitors( json )
                .Count( static monitor => !monitor.Disabled );
        }

        return 0;
    }

    private static bool CanRunWindowCommand( int monitors, WindowCommand command )
    {
        return !command.NeedsSecondMonitor || monitors >= MinimumMonitors;
    }

    private static string? GetSuccessfulOutput( ProcessResult result )
    {
        if ( result is { ExitCode: 0, TimedOut: false, AnyOutputLimitExceeded: false } )
        {
            return result.StandardOutput;
        }

        return null;
    }

    private static Dictionary<string, string> CreateWindowArguments( WindowCommand command, HyprlandWindow window )
    {
        return new( StringComparer.Ordinal )
        {
            [HyprlandActionOperations.OperationKey] = command.Operation,
            [HyprlandActionOperations.ArgumentKey] = window.Selector
        };
    }
}
