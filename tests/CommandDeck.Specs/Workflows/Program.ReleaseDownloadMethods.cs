namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ReleaseDownloaderVerifiesAndInstallsAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-download-" ).FullName;

        try
        {
            var fixture = await CreateDownloadFixtureAsync( root );

            (string Cpu, string Mode, bool Latest, int Exit)[] cases =
            [
                ("x86_64", "ok", false, 0), ("aarch64", "ok", true, 0), ("arm64", "ok", false, 0),
                ("x86_64", "corrupt", false, 1), ("x86_64", "wrong-name", false, 1),
                ("x86_64", "missing", false, 1), ("x86_64", "installer-failure", false, 23),
                ("riscv64", "ok", false, 1)
            ];

            foreach ( var (cpu, mode, latest, exit) in cases )
            {
                string[] arguments = ["--dry-run", "--hotkey=SUPER + SPACE", "--reset-config"];
                if ( !latest )
                {
                    arguments = [FakeVersion, .. arguments];
                }

                var result = await RunDownloaderAsync( fixture, arguments, cpu, mode );

                Equal( exit, result.ExitCode );
                Equal( 0, Directory.GetFileSystemEntries( fixture.Cache ).Length );
                var installed = exit == 0 || mode == "installer-failure";
                Equal( installed, File.Exists( fixture.Calls ) );

                if ( !installed )
                {
                    True( result.StandardError.Length > 0, "a failed download reports an error: " + mode );

                    continue;
                }

                Equal( "--dry-run\n--hotkey=SUPER + SPACE\n--reset-config\n", await File.ReadAllTextAsync( fixture.Calls ) );
                var rid = cpu == "x86_64" ? "linux-x64" : "linux-arm64";
                var installerFolder = (await File.ReadAllTextAsync( fixture.Calls + ".pwd" )).TrimEnd( );
                Equal( $"commanddeck-{FakeVersion}-{rid}", Path.GetFileName( installerFolder ) );
                True( !Directory.Exists( installerFolder ), "the extracted bundle is removed after installation" );

                var urls = await File.ReadAllTextAsync( fixture.Calls + ".urls" );
                True( urls.Contains( $"/releases/download/{FakeVersion}/commanddeck-{FakeVersion}-{rid}.tar.gz", StringComparison.Ordinal ), urls );
                Equal( latest, urls.Contains( "api.github.com", StringComparison.Ordinal ) );
            }
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    // An update must not restart the daemon and the shell for nothing, and must
    // not replace a newer build with an older "latest" release. A dry run
    // always checks the release.
    private static async Task ReleaseDownloaderUpdatesOnlyWhenNeededAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-update-" ).FullName;

        try
        {
            var fixture = await CreateDownloadFixtureAsync( root );

            // The stub API reports FakeVersion (v9.9.9) as the newest release,
            // unless a case names another one.
            (string? Installed, string[] Arguments, bool Installs, string Output, string[] Environment)[] cases =
            [
                (null, [], true, "Release notes:", []),
                ("9.9.9", [], false, "already installed and up to date", []),
                ("9.9.9", ["--dry-run"], true, "The dry run checks the release anyway", []),
                ("9.9.9", ["--force"], true, "again", []),
                ("9.9.9", ["--hotkey=SUPER + SPACE"], true, "Release notes:", []),
                ("9.9.9-preview.1", [], true, "(installed: v9.9.9-preview.1)", []),
                ("9.10.0", [], false, "newer than the newest release", []),
                ("9.10.0", ["--force"], false, "newer than the newest release", []),
                ("9.10.0", ["--hotkey=SUPER + SPACE"], false, "commanddeck hotkey install --chord", []),
                ("9.10.0", ["--dry-run"], true, "Without VERSION, a real run changes nothing", []),
                ("9.10.0", [FakeVersion], true, "(installed: v9.10.0)", []),
                ("9.9.99999999999999999999", [], false, "newer than the newest release", []),
                ("9.9.9-alpha", [], false, "newer than the newest release", ["COMMANDDECK_SPEC_LATEST=v9.9.9-Beta", "LC_ALL=en_US.UTF-8"]),
                ("9.9.9-preview.1", ["--check"], false, "Update with:", []),
                ("9.9.9", ["--check"], false, "up to date", []),
                (null, ["--check"], false, "Installed: not installed", [])
            ];

            foreach ( var (installedVersion, arguments, installs, output, environment) in cases )
            {
                var program = Path.Combine( fixture.Data, "commanddeck" );
                if ( Directory.Exists( program ) )
                {
                    Directory.Delete( program, recursive: true );
                }

                if ( installedVersion is not null )
                {
                    Directory.CreateDirectory( program );
                    await WriteExecutableAsync( Path.Combine( program, "commanddeck" ),
                        $"#!/bin/sh\n[ \"$1\" = version ] && echo 'commanddeck {installedVersion}+0123abc' && echo '.NET 11.0.0'\n" );
                }

                var result = await RunDownloaderAsync( fixture, arguments, "x86_64", "ok", environment );
                var label = $"{installedVersion ?? "none"} {string.Join( ' ', arguments )}";

                Equal( 0, result.ExitCode );
                Equal( installs, File.Exists( fixture.Calls ) );
                True( result.StandardOutput.Contains( output, StringComparison.Ordinal ), label + ": " + result.StandardOutput );
                Equal( 0, Directory.GetFileSystemEntries( fixture.Cache ).Length );

                var urls = File.Exists( fixture.Calls + ".urls" ) ? await File.ReadAllTextAsync( fixture.Calls + ".urls" ) : "";
                Equal( installs, urls.Contains( "/releases/download/", StringComparison.Ordinal ) );
            }
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    // The README pipes the script into Bash. A download that stops early must
    // run nothing, and above all must not run main without its options.
    private static async Task ReleaseDownloaderRunsPipedAndRefusesBadInputAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-piped-" ).FullName;

        try
        {
            var fixture = await CreateDownloadFixtureAsync( root );
            var script = await File.ReadAllTextAsync( Path.Combine( FindRepositoryRoot( ), "scripts", "install-release.sh" ) );

            var piped = await RunDownloaderAsync( fixture, [FakeVersion, "--hotkey=SUPER + SPACE"], "x86_64", "ok", standardInput: script );
            Equal( 0, piped.ExitCode );
            Equal( "--hotkey=SUPER + SPACE\n", await File.ReadAllTextAsync( fixture.Calls ) );
            Equal( 0, Directory.GetFileSystemEntries( fixture.Cache ).Length );

            // An empty XDG variable means the default, so the installer and the
            // binary it runs must not see it as a relative path.
            var emptyXdg = await RunDownloaderAsync( fixture, [FakeVersion], "x86_64", "ok", ["XDG_CONFIG_HOME="] );
            Equal( 0, emptyXdg.ExitCode );
            Equal( "unset\n", await File.ReadAllTextAsync( fixture.Calls + ".xdg" ) );

            var call = script.LastIndexOf( "{ main", StringComparison.Ordinal );
            True( call > 0, "the script ends with one braced call of main" );
            int[] cuts = [script.Length / 2, call, call + "{ main".Length, script.LastIndexOf( '}' )];
            foreach ( var cut in cuts )
            {
                var truncated = await RunDownloaderAsync( fixture, ["--dry-run"], "x86_64", "ok", standardInput: script[..cut] );
                var label = "cut at " + cut + ": " + truncated.StandardOutput + truncated.StandardError;
                True( !File.Exists( fixture.Calls + ".urls" ) && !File.Exists( fixture.Calls ), label );
                Equal( 0, Directory.GetFileSystemEntries( fixture.Cache ).Length );
            }

            string[][] conflicts = [["--check", "--uninstall"], ["--uninstall", "--check"]];
            foreach ( var arguments in conflicts )
            {
                var conflict = await RunDownloaderAsync( fixture, arguments, "x86_64", "ok" );
                Equal( 2, conflict.ExitCode );
                True( !File.Exists( fixture.Calls + ".urls" ) && !File.Exists( fixture.Calls + ".tools" ), string.Join( ' ', arguments ) );
            }

            foreach ( var mode in new[] { "--check", "--uninstall" } )
            {
                var relative = await RunDownloaderAsync( fixture, [mode], "x86_64", "ok", ["XDG_DATA_HOME=relative/share"] );
                Equal( 1, relative.ExitCode );
                True( relative.StandardError.Contains( "absolute path", StringComparison.Ordinal ), relative.StandardError );
                True( !File.Exists( fixture.Calls + ".urls" ) && !File.Exists( fixture.Calls + ".tools" ), mode );
            }
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    // Bash runs a trap only after its foreground command ends. A signal sent
    // only to the downloader therefore cannot stop the installer, so the
    // downloader reports the finished installation instead of failing.
    private static async Task ReleaseDownloaderReportsASignalAfterTheInstallerFinishedAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-signal-" ).FullName;

        try
        {
            var fixture = await CreateDownloadFixtureAsync( root );
            const string driver = """
                bash "$1" v9.9.9 --hotkey=SUPER+SPACE & downloader=$!
                attempts=0
                while [ ! -e "$COMMANDDECK_SPEC_INSTALL_CALLS.started" ] && [ "$attempts" -lt 200 ]; do
                  sleep 0.05
                  attempts=$((attempts + 1))
                done
                kill -TERM "$downloader"
                wait "$downloader"
                echo "downloader exit=$?"
                """;

            var result = await RunDownloaderAsync( fixture, [], "x86_64", "slow", command: ["bash", "-c", driver, "driver"] );

            Equal( 0, result.ExitCode );
            True( result.StandardOutput.Contains( "downloader exit=0", StringComparison.Ordinal ), result.StandardOutput + result.StandardError );
            True( result.StandardOutput.Contains( "Received SIGTERM", StringComparison.Ordinal ), result.StandardOutput );
            True( File.Exists( fixture.Calls ), "the installer finished" );
            Equal( 0, Directory.GetFileSystemEntries( fixture.Cache ).Length );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
