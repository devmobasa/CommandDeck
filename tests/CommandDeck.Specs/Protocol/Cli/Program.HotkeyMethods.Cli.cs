using System.Diagnostics;
using System.Globalization;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task HotkeyCliInstallsAndRemovesThroughXdgPathsAsync( )
    {
        using HotkeyFixture fixture = new( );
        var configHome = Path.GetDirectoryName( fixture.Paths.HyprDirectory )!;
        var stateHome = Path.Combine( fixture.Root, "state" );
        File.WriteAllText( fixture.Paths.MainConfig, StockHyprlandLua );

        var installed = await RunHotkeyCliAsync( configHome, stateHome, "hotkey", "install", "--chord", "ALT + SPACE" );

        Equal( 0, installed.ExitCode );
        Equal( "ALT + SPACE", HyprlandHotkeyConfig.ReadChord( File.ReadAllText( fixture.Paths.HotkeyFile ) ) );
        Equal( 1, Directory.GetFiles( Path.Combine( stateHome, "commanddeck", "binding-backups" ) ).Length );

        var invalid = await RunHotkeyCliAsync( configHome, stateHome, "hotkey", "install", "--chord", "HYPER + K" );

        Equal( 2, invalid.ExitCode );
        True( invalid.StandardError.Contains( "HYPER", StringComparison.Ordinal ) );
        Equal( 2, (await RunHotkeyCliAsync( configHome, stateHome, "hotkey", "status" )).ExitCode );
        var removed = await RunHotkeyCliAsync( configHome, stateHome, "hotkey", "remove" );
        Equal( 0, removed.ExitCode );
        True( !File.Exists( fixture.Paths.HotkeyFile ) );
        Equal( StockHyprlandLua, File.ReadAllText( fixture.Paths.MainConfig ) );
    }

    // install.sh runs `hotkey check` before it stops anything, so a bad chord or
    // a missing Lua configuration fails while the old installation still runs.
    private static async Task HotkeyCheckValidatesWithoutWritingAsync( )
    {
        using HotkeyFixture fixture = new( );
        var configHome = Path.GetDirectoryName( fixture.Paths.HyprDirectory );
        if ( configHome is null )
        {
            throw new InvalidOperationException( "The fixture's Hyprland directory has no parent." );
        }

        var stateHome = Path.Combine( fixture.Root, "state" );
        var missing = await RunHotkeyCliAsync( configHome, stateHome, "hotkey", "check" );
        Equal( 2, missing.ExitCode );

        True( missing.StandardError.Contains( "hyprland.lua does not exist", StringComparison.Ordinal ), missing.StandardError );

        File.WriteAllText( fixture.Paths.MainConfig, StockHyprlandLua );

        var valid = await RunHotkeyCliAsync( configHome, stateHome, "hotkey", "check", "--chord", "super+space" );

        Equal( 0, valid.ExitCode );

        True( valid.StandardOutput.Contains( "SUPER + SPACE hotkey can be installed", StringComparison.Ordinal ), valid.StandardOutput );

        var invalid = await RunHotkeyCliAsync( configHome, stateHome, "hotkey", "check", "--chord", "HYPER + K" );

        Equal( 2, invalid.ExitCode );
        True( invalid.StandardError.Contains( "HYPER", StringComparison.Ordinal ) );

        Equal( 2, (await RunHotkeyCliAsync( configHome, stateHome, "hotkey", "check", "--chord", "" )).ExitCode );

        True( !File.Exists( fixture.Paths.HotkeyFile ), "check writes no module" );
        Equal( StockHyprlandLua, File.ReadAllText( fixture.Paths.MainConfig ) );
        True( !Directory.Exists( Path.Combine( stateHome, "commanddeck" ) ), "check makes no backup" );
    }

    private static void OmarchyShortcutMatchesTheGeneratedBinding( )
    {
        var shortcut = File.ReadAllText( Path.Combine( FindRepositoryRoot( ), "qml", "omarchy", "hotkey", "LauncherShortcut.qml" ) );

        True( shortcut.Contains( $"appid: \"{"commanddeck"}\"", StringComparison.Ordinal ) );
        True( shortcut.Contains( $"name: \"{"toggle"}\"", StringComparison.Ordinal ) );

        True( HyprlandHotkeyConfig
            .GenerateFile( "CTRL + SPACE" )
            .Contains( "hl.dsp.global(\"commanddeck:toggle\")", StringComparison.Ordinal ) );

        var host = File.ReadAllText( Path.Combine( FindRepositoryRoot( ), "qml", "omarchy", "CommandDeck.qml" ) );

        True( host.Contains( "\"hotkey/LauncherShortcut.qml\"", StringComparison.Ordinal ) );
    }

    private static Task<ProcessOutcome> RunHotkeyCliAsync( string configHome, string stateHome, params string[] arguments ) =>
        RunCommandDeckWithEnvironmentAsync( new Dictionary<string, string?> { ["XDG_CONFIG_HOME"] = configHome, ["XDG_STATE_HOME"] = stateHome }, arguments );

    // A null value removes the variable from the child's environment.
    private static async Task<ProcessOutcome> RunCommandDeckWithEnvironmentAsync( IReadOnlyDictionary<string, string?> environment, params string[] arguments )
    {
        ProcessStartInfo start = CreateStart( );
        start.ArgumentList.Add( typeof( CommandDeck.Program ).Assembly.Location );

        foreach ( var argument in arguments )
        {
            start.ArgumentList.Add( argument );
        }

        foreach ( (var name, var value) in environment )
        {
            if ( value is null )
            {
                start.Environment.Remove( name );
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using Process process = new( ) { StartInfo = start };
        True( process.Start( ) );
        var output = process.StandardOutput.ReadToEndAsync( );
        var error = process.StandardError.ReadToEndAsync( );
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 15 ) );
        await process.WaitForExitAsync( timeout.Token );

        return new ProcessOutcome( process.ExitCode, (await output).Trim( ), (await error).Trim( ) );

        static ProcessStartInfo CreateStart( )
        {
            return new( )
            {
                FileName = DotnetHost.ExecutableName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
        }
    }

    private sealed class HotkeyFixture : IDisposable
    {
        public HotkeyFixture( )
        {
            Root = SpecTemporaryFiles.PathFor( $"commanddeck-hotkey-spec-{Guid.NewGuid( ):N}" );

            Paths = new HyprlandHotkeyPaths
            {
                HyprDirectory = Path.Combine( Root, "config", "hypr" ),
                BackupDirectory = Path.Combine( Root, "state", "commanddeck", "binding-backups" )
            };

            Directory.CreateDirectory( Paths.HyprDirectory );
            Installer = new HyprlandHotkeyInstaller( Paths, new FixedTimeProvider( HotkeyClock ) );
        }

        public string Root { get; }
        public HyprlandHotkeyPaths Paths { get; }
        public HyprlandHotkeyInstaller Installer { get; }

        // The installer stamps backups in local time, like `date +%Y%m%d%H%M%S`.
        public string Backup( string file ) => Path.Combine( Paths.BackupDirectory, file +
                                                                                    "." +
                                                                                    TimeZoneInfo
                                                                                        .ConvertTime( HotkeyClock, TimeZoneInfo.Local )
                                                                                        .ToString( "yyyyMMddHHmmss", CultureInfo.InvariantCulture ) );

        public string[] Backups( )
        {
            if ( Directory.Exists( Paths.BackupDirectory ) )
            {
                return Directory.GetFiles( Paths.BackupDirectory );
            }

            return [];
        }

        public void Dispose( ) => Directory.Delete( Root, recursive: true );
    }
}
