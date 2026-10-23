namespace CommandDeck;

public sealed partial class DesktopControlsProvider
{
    private const int MinimumMonitors = 2;
    private const int MaximumWindowLabelCharacters = 80;
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
            if ( CanRunWindowCommand( monitors, command ) )
            {
                results.Add( BuildWindowRow( command, window, score ) );
            }
        }
    }

    private async Task<string?> ReadHyprlandAsync( string request, CancellationToken cancellationToken )
    {
        ProcessResult result;

        try
        {
            result = await _hyprland( ToolExecutables.Hyprctl, ["-j", request], HyprctlTimeout, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return null;
        }

        return GetSuccessfulOutput( result );
    }

    private static SearchResult BuildWindowRow( WindowCommand command, HyprlandWindow window, double score )
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
            Title = command.Title,
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
                    Title = command.Title,
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

    private static ValueTask<ProcessResult> DefaultHyprlandRunAsync( string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
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
