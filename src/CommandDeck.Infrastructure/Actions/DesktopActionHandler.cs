using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CommandDeck;

// Opening paths and URLs, launching desktop entries and typed programs, editing
// at a line, and running commands and scripts: the actions of indexed,
// configured and command-mode rows.
internal sealed class DesktopActionHandler( EditorLauncher editor ) : ILinuxActionHandler
{
    private const int ShellTimeoutMinutes = 5;

    private const string TrueValue = "true";

    private static readonly string[] OwnedKinds =
    [
        ActionKinds.OpenPath,
        ActionKinds.RevealPath,
        ActionKinds.EditAtLine,
        ActionKinds.OpenUrl,
        ActionKinds.LaunchDesktop,
        ActionKinds.LaunchArgv,
        ActionKinds.RunArgv,
        ActionKinds.RunTrustedShell,
        ActionKinds.RunScript
    ];

    public IReadOnlyList<string> Kinds => OwnedKinds;

    public async ValueTask<ActionExecutionResult> ExecuteAsync(
        SearchResult result, CommandAction action, string query, CancellationToken cancellationToken ) => action.Kind switch
        {
            ActionKinds.OpenPath => await OpenPathAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.RevealPath => await RevealPathAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.EditAtLine => await editor
                .EditAtLineAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.OpenUrl => await OpenUrlAsync( query, action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.LaunchDesktop => await LaunchDesktopAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.LaunchArgv => await LaunchArgvAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.RunArgv => await RunArgvAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.RunTrustedShell => await RunTrustedShellAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.RunScript => await ScriptCommandProvider
                .ExecuteAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            _ => LinuxActionArguments.UnknownKind( action )
        };

    private static async ValueTask<ActionExecutionResult> OpenPathAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var path = PathActionPayload.Read( action.Arguments ).Path;

        return await ManagedLaunch
            .LaunchApplicationAsync( new DesktopEntryCommand { Executable = ToolExecutables.DesktopOpener, Arguments = [path] }, terminal: false,
                cancellationToken )
            .ConfigureAwait( false );
    }

    private static async ValueTask<ActionExecutionResult> RevealPathAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var path = PathActionPayload.Read( action.Arguments ).Path;
        var directory = ResolveRevealDirectory( path );

        return await ManagedLaunch
            .LaunchApplicationAsync( new DesktopEntryCommand { Executable = ToolExecutables.DesktopOpener, Arguments = [directory] }, terminal: false,
                cancellationToken )
            .ConfigureAwait( false );
    }

    private static async ValueTask<ActionExecutionResult> OpenUrlAsync( string query, CommandAction action, CancellationToken cancellationToken )
    {
        var url = UrlActionPayload
            .Read( action.Arguments )
            .Expand( query );

        return await ManagedLaunch
            .LaunchApplicationAsync( new DesktopEntryCommand { Executable = ToolExecutables.DesktopOpener, Arguments = [url] }, terminal: false,
                cancellationToken )
            .ConfigureAwait( false );
    }

    private static async ValueTask<ActionExecutionResult> LaunchDesktopAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var encodedArguments = action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.LaunchArguments );
        if ( !string.IsNullOrEmpty( encodedArguments ) )
        {
            var exec = LinuxActionArguments.Require( action, ResultMetadataKeys.Application.Exec );
            var desktopFileWithArguments = LinuxActionArguments.Require( action, ResultMetadataKeys.Application.DesktopFile );

            var applicationName = action.Arguments.GetValueOrDefault(
                ResultMetadataKeys.Application.Name, Path.GetFileNameWithoutExtension( desktopFileWithArguments ) );

            var userArguments = LaunchArgumentCodec.Decode( encodedArguments );

            var command = DesktopEntryCommand.Create( exec, applicationName, action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.Icon ),
                desktopFileWithArguments, userArguments );

            if ( string.Equals( action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.Terminal ), TrueValue, StringComparison.OrdinalIgnoreCase ) )
            {
                return await ManagedLaunch
                    .LaunchApplicationAsync( command, terminal: true, cancellationToken )
                    .ConfigureAwait( false );
            }

            return await ManagedLaunch
                .LaunchApplicationAsync( command, terminal: false, cancellationToken )
                .ConfigureAwait( false );
        }

        var desktopFile = action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.DesktopFile );
        if ( IsGioLaunchAvailable( desktopFile ) )
        {
            return await ManagedLaunch
                .LaunchApplicationAsync( new DesktopEntryCommand { Executable = "gio", Arguments = ["launch", desktopFile] }, terminal: false,
                    cancellationToken )
                .ConfigureAwait( false );
        }

        var desktopId = LinuxActionArguments.Require( action, ResultMetadataKeys.Application.DesktopId );

        if ( !ProcessRunner.ExistsOnPath( "gtk-launch" ) )
        {
            return ActionExecutionResult.Failure( "Neither 'gio launch' nor 'gtk-launch' could launch the desktop entry." );
        }

        var idWithoutSuffix = RemoveDesktopSuffix( desktopId );

        return await ManagedLaunch
            .LaunchApplicationAsync( new DesktopEntryCommand { Executable = "gtk-launch", Arguments = [idWithoutSuffix] }, terminal: false, cancellationToken )
            .ConfigureAwait( false );
    }

    // Detached like an application launch, through the same uwsm-app or
    // systemd-run path; "terminal" uses the terminal of Terminal=true entries.
    private static async ValueTask<ActionExecutionResult> LaunchArgvAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var argv = LaunchArgumentCodec.Decode( LinuxActionArguments.Require( action, ActionArgumentNames.Argv ) );
        if ( IsEmptyCommand( argv ) )
        {
            return ActionExecutionResult.Failure( "The command has no executable." );
        }

        return await ManagedLaunch
            .LaunchApplicationAsync( new DesktopEntryCommand
            {
                Executable = argv[0],
                Arguments = argv
                    .Skip( 1 )
                    .ToArray( )
            }, terminal: action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.Terminal ) == TrueValue, cancellationToken )
            .ConfigureAwait( false );
    }

    private static async ValueTask<ActionExecutionResult> RunArgvAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var encoded = LinuxActionArguments.Require( action, ActionArgumentNames.Argv );
        var argv = encoded.Split( '\u001f', StringSplitOptions.RemoveEmptyEntries );
        if ( argv.Length == 0 )
        {
            return ActionExecutionResult.Failure( "The command has no executable." );
        }

        var workingDirectory = action.Arguments.GetValueOrDefault( ActionArgumentNames.WorkingDirectory );

        return await ManagedLaunch
            .RunAndReportAsync( argv[0], argv.Skip( 1 ), workingDirectory, cancellationToken )
            .ConfigureAwait( false );
    }

    // An Omarchy menu command runs in the user session, as from Omarchy's own menu:
    // in the daemon, NoNewPrivileges would block sudo and pkexec, the root is
    // read-only, files would get UMask=0077, and a daemon restart would kill it.
    private static async ValueTask<ActionExecutionResult> RunTrustedShellAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var command = LinuxActionArguments.Require( action, ActionArgumentNames.Command );

        var result = await ManagedLaunch
            .RunInSessionAsync( new DesktopEntryCommand { Executable = "/bin/sh", Arguments = ["-lc", command] }, TimeSpan.FromMinutes( ShellTimeoutMinutes ),
                cancellationToken )
            .ConfigureAwait( false );

        var exceeded = result.OutputLimitExceeded || result.StandardErrorLimitExceeded;
        if ( result is { ExitCode: 0, TimedOut: false } && !exceeded )
        {
            return ActionExecutionResult.Success( );
        }

        return ActionExecutionResult.Failure( GetShellFailureMessage( result, exceeded ), $"```\n{result.StandardError}\n```" );
    }

    private static bool IsGioLaunchAvailable( [NotNullWhen( true )] string? desktopFile )
    {
        return !string.IsNullOrWhiteSpace( desktopFile ) && ProcessRunner.ExistsOnPath( "gio" );
    }

    private static bool IsEmptyCommand( IReadOnlyList<string> argv )
    {
        return argv.Count == 0 || string.IsNullOrEmpty( argv[0] );
    }

    private static string ResolveRevealDirectory( string path )
    {
        if ( Directory.Exists( path ) )
        {
            return path;
        }

        return Path.GetDirectoryName( path ) ?? path;
    }

    private static string RemoveDesktopSuffix( string desktopId )
    {
        if ( desktopId.EndsWith( FileExtensions.Desktop, StringComparison.OrdinalIgnoreCase ) )
        {
            return desktopId[..^FileExtensions.Desktop.Length];
        }

        return desktopId;
    }

    private static string GetShellFailureMessage( ProcessResult result, bool exceeded )
    {
        if ( exceeded )
        {
            return "Command exceeded its output limit.";
        }

        if ( result.TimedOut )
        {
            return "Command timed out.";
        }

        return $"Command exited with {result.ExitCode.ToString( CultureInfo.InvariantCulture )}.";
    }
}
