using System.Runtime.InteropServices;

namespace CommandDeck.Specs;

public static partial class Program
{
    // A failed first install removes everything it added, puts hyprland.lua
    // back, and leaves the service stopped and disabled as it was.
    private static async Task ReleaseInstallUndoesAFailedFirstInstallAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-first-" ).FullName;

        try
        {
            var calls = Path.Combine( root, "tool-calls.log" );
            var release = await PackageFakeReleaseAsync( root, calls );
            var tools = await WriteDesktopStubsAsync( root, calls );
            var home = Path.Combine( root, "home" );
            var main = Path.Combine( home, "config", "hypr", "hyprland.lua" );
            Directory.CreateDirectory( Path.Combine( home, "config", "hypr" ) );
            await File.WriteAllTextAsync( main, "-- stock main" );

            string[] failing =
            [
                .. InstallerEnvironment( home, tools ), "COMMANDDECK_SPEC_CONFIG_ERRORS=error: bad bind",
                "COMMANDDECK_SPEC_DAEMON_OFF=1"
            ];

            File.Delete( calls );

            var failed = await ProcessRunner.RunAsync( "env", [.. failing, Path.Combine( release, "install.sh" )], null, root, TimeSpan.FromSeconds( 60 ),
                default );

            Equal( 1, failed.ExitCode );

            True( failed.StandardError.Contains( "Removed the partial installation.", StringComparison.Ordinal ), failed.StandardError );

            Equal( "-- stock main", await File.ReadAllTextAsync( main ) );
            string[] added = CreateAdded( home );

            Equal( "", string.Join( ",", added.Where( path => Path.Exists( path ) || new FileInfo( path ).LinkTarget is not null ) ) );

            Equal( "systemctl --user stop commanddeck.service,systemctl --user disable commanddeck.service,systemctl --user daemon-reload,hyprctl reload",
                await CallsAfterAsync( calls, "hyprctl configerrors" ) );

            AssertNoRollbackLeftovers( home );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static string[] CreateAdded( string home )
        {
            return
            [
                Path.Combine( home, "data", "commanddeck" ), Path.Combine( home, ".local", "bin", "commanddeck" ),
                Path.Combine( home, "config", "systemd", "user", "commanddeck.service" ), Path.Combine( home, "config", "commanddeck" ),
                Path.Combine( home, "config", "omarchy", "plugins", CommandDeckIdentity.PluginId ), Path.Combine( home, "config", "hypr", "commanddeck.lua" )
            ];
        }
    }

    // reinstall-all.sh lets install.sh check the options first, then validates,
    // publishes, and passes every option after the RID on to install.sh.
    private static async Task ReinstallAllHandsItsOptionsToInstallScriptAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-reinstall-" ).FullName;

        try
        {
            var scripts = Path.Combine( root, "scripts" );
            var tools = Path.Combine( root, "tools" );
            var calls = Path.Combine( root, "calls.log" );
            Directory.CreateDirectory( scripts );
            Directory.CreateDirectory( tools );

            File.Copy( Path.Combine( FindRepositoryRoot( ), "scripts", "reinstall-all.sh" ), Path.Combine( scripts, "reinstall-all.sh" ) );

            await WriteExecutableAsync( Path.Combine( scripts, "validate-repository.sh" ), $"#!/bin/sh\necho validate >>'{calls}'\n" );
            await WriteExecutableAsync( Path.Combine( scripts, "publish-aot.sh" ), $"#!/bin/sh\necho \"publish $*\" >>'{calls}'\n" );
            await WriteExecutableAsync( Path.Combine( scripts, "install.sh" ), $"#!/bin/sh\necho \"install $*\" >>'{calls}'\n" );
            await WriteExecutableAsync( Path.Combine( tools, DotnetHost.ExecutableName ), "#!/bin/sh\nexit 0\n" );
            var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            string[] environment = ["-u", "COMMANDDECK_SKIP_VALIDATE", "PATH=" + tools + ":" + Environment.GetEnvironmentVariable( "PATH" )];

            var result = await ProcessRunner.RunAsync( "env",
                [.. environment, "bash", Path.Combine( scripts, "reinstall-all.sh" ), rid, "--reset-config", "--hotkey=SUPER + SPACE"], null, root,
                TimeSpan.FromSeconds( 30 ), default );

            True( result.ExitCode == 0, "reinstall-all.sh failed: " + result.StandardError );

            Equal(
                string.Join( "\n", "install --reset-config --hotkey=SUPER + SPACE --help", "validate", "publish " + rid,
                    $"install --from={Path.Combine( root, "artifacts", "publish", rid )} --reset-config --hotkey=SUPER + SPACE" ),
                (await File.ReadAllTextAsync( calls )).TrimEnd( ) );

            // With the real install.sh, a wrong option stops before validation.
            File.Delete( calls );

            File.Copy( Path.Combine( FindRepositoryRoot( ), "scripts", "install.sh" ), Path.Combine( scripts, "install.sh" ), overwrite: true );

            var rejected = await ProcessRunner.RunAsync( "env", [.. environment, "bash", Path.Combine( scripts, "reinstall-all.sh" ), "--hotkey", "K"], null,
                root, TimeSpan.FromSeconds( 30 ), default );

            Equal( 2, rejected.ExitCode );

            True( rejected.StandardError.Contains( "Unknown argument: --hotkey", StringComparison.Ordinal ), rejected.StandardError );
            True( !File.Exists( calls ), "a wrong option stops before validation and publishing" );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    // Stubs stand in for the desktop tools and log every call. `hyprctl
    // configerrors` prints COMMANDDECK_SPEC_CONFIG_ERRORS, a call that matches
    // COMMANDDECK_SPEC_SIGNAL_AT sends SIGTERM to the installer, and
    // COMMANDDECK_SPEC_DAEMON_OFF reports the service as stopped and disabled.
    [System.Runtime.Versioning.SupportedOSPlatform( "linux" )]
    private static async Task<string> WriteDesktopStubsAsync( string root, string calls )
    {
        var tools = Directory.CreateDirectory( Path.Combine( root, "tools" ) ).FullName;

        foreach ( var tool in new[] { "systemctl", "omarchy", "omarchy-shell", "hyprctl", "jq" } )
        {
            await WriteExecutableAsync( Path.Combine( tools, tool ),
                $"#!/bin/sh\ncall=\"{tool} $*\"\necho \"$call\" >>'{calls}'\n" +
                "if [ \"$call\" = \"$COMMANDDECK_SPEC_SIGNAL_AT\" ]; then kill -TERM \"$PPID\"; fi\n" +
                "if [ \"$call\" = 'hyprctl configerrors' ]; then printf '%s' \"$COMMANDDECK_SPEC_CONFIG_ERRORS\"; fi\n" +
                "if [ -n \"$COMMANDDECK_SPEC_DAEMON_OFF\" ]; then\n  case \"$call\" in\n" +
                "    'systemctl --user is-active'*) exit 3 ;;\n    'systemctl --user is-enabled'*) exit 1 ;;\n  esac\nfi\n" );
        }

        return tools;
    }

    // An earlier installation whose hyprland.lua is a symlink into a dotfiles
    // folder, as some people keep it.
    [System.Runtime.Versioning.SupportedOSPlatform( "linux" )]
    private static async Task CreatePreviousInstallationAsync( string home )
    {
        var program = Path.Combine( home, "data", "commanddeck" );
        Directory.CreateDirectory( Path.Combine( program, "picker" ) );
        await WriteExecutableAsync( Path.Combine( program, "commanddeck" ), OldBinary );
        await File.WriteAllTextAsync( Path.Combine( program, "picker", "old.qml" ), "old" );
        Directory.CreateDirectory( Path.Combine( home, ".local", "bin" ) );

        File.CreateSymbolicLink( Path.Combine( home, ".local", "bin", "commanddeck" ), Path.Combine( program, "commanddeck" ) );

        foreach ( (var path, var text) in PreviousFiles( home ) )
        {
            Directory.CreateDirectory( Path.GetDirectoryName( path ) ?? home );
            await File.WriteAllTextAsync( path, text );
        }

        File.CreateSymbolicLink( Path.Combine( home, "config", "hypr", "hyprland.lua" ), Path.Combine( home, "dotfiles", "hyprland.lua" ) );
    }

    private static async Task AssertPreviousInstallationAsync( string home )
    {
        var program = Path.Combine( home, "data", "commanddeck" );
        Equal( OldBinary, await File.ReadAllTextAsync( Path.Combine( program, "commanddeck" ) ) );

        Equal( "old.qml", string.Join( ",", Directory
            .EnumerateFiles( Path.Combine( program, "picker" ) )
            .Select( Path.GetFileName ) ) );

        Equal( Path.Combine( program, "commanddeck" ), new FileInfo( Path.Combine( home, ".local", "bin", "commanddeck" ) ).LinkTarget );
        Equal( Path.Combine( home, "dotfiles", "hyprland.lua" ), new FileInfo( Path.Combine( home, "config", "hypr", "hyprland.lua" ) ).LinkTarget );

        foreach ( (var path, var text) in PreviousFiles( home ) )
        {
            Equal( text, await File.ReadAllTextAsync( path ) );
        }

        Equal( "old.txt", string.Join( ",", Directory
            .EnumerateFiles( Path.Combine( home, "config", "omarchy", "plugins", "local.commanddeck" ) )
            .Select( Path.GetFileName ) ) );

        AssertNoRollbackLeftovers( home );
    }

    private static (string Path, string Text)[] PreviousFiles( string home ) =>
    [
        (Path.Combine( home, "config", "systemd", "user", "commanddeck.service" ), "old service"),
        (Path.Combine( home, "config", "commanddeck", "commanddeck.jsonc" ), "old config"),
        (Path.Combine( home, "config", "omarchy", "plugins", CommandDeckIdentity.PluginId, "old.txt" ), "old plugin"),
        (Path.Combine( home, "config", "hypr", "commanddeck.lua" ), "-- old hotkey"),
        (Path.Combine( home, "dotfiles", "hyprland.lua" ), "-- old main")
    ];

    // The stub calls after the first line equal to `marker`, comma-separated.
    private static async Task<string> CallsAfterAsync( string calls, string marker ) =>
        string.Join( ",", (await File.ReadAllLinesAsync( calls ))
            .SkipWhile( line => line != marker )
            .Skip( 1 ) );

    private static string[] InstallerEnvironment( string home, string tools ) =>
    [
        "HOME=" + home, "XDG_CONFIG_HOME=" + Path.Combine( home, "config" ), "XDG_DATA_HOME=" + Path.Combine( home, "data" ),
        "XDG_STATE_HOME=" + Path.Combine( home, "state" ), "PATH=" + tools + ":" + Environment.GetEnvironmentVariable( "PATH" )
    ];

    private static void AssertNoRollbackLeftovers( string home ) =>
        Equal( "", string.Join( ",", Directory.EnumerateDirectories( Path.Combine( home, "state", "commanddeck" ), "install-rollback.*" ) ) );
}
