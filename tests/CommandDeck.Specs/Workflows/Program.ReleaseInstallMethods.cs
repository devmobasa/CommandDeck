using System.Text.RegularExpressions;

namespace CommandDeck.Specs;

public static partial class Program
{
    private const string FakeVersion = "v9.9.9";
    private const string OldBinary = "#!/bin/sh\necho old\n";

    // A release folder must carry install.sh and every script it calls, and its
    // dry run must pass there, also through a symlink, while it changes nothing.
    private static async Task ReleasePackageInstallsFromItsOwnFolderAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var repository = FindRepositoryRoot( );
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-release-" ).FullName;

        try
        {
            var calls = Path.Combine( root, "tool-calls.log" );
            var release = await PackageFakeReleaseAsync( root, calls );
            var installer = Path.Combine( release, "install.sh" );

            var calledScripts = Regex
                .Matches( await File.ReadAllTextAsync( installer ), @"""\$root_dir/scripts/(?<name>[\w.-]+)""" )
                .Select( match => "scripts/" + match.Groups["name"].Value );

            var plugin = Directory
                .EnumerateFiles( Path.Combine( repository, "qml" ), "*", SearchOption.AllDirectories )
                .Select( path => "qml/" + Path.GetRelativePath( Path.Combine( repository, "qml" ), path ) );

            string[] fixedFiles = CreateFixedFiles( );

            var expected = fixedFiles
                .Concat( calledScripts )
                .Concat( plugin )
                .Distinct( )
                .Order( StringComparer.Ordinal );

            var actual = Directory
                .EnumerateFiles( release, "*", SearchOption.AllDirectories )
                .Select( path => Path.GetRelativePath( release, path ) )
                .Order( StringComparer.Ordinal );

            Equal( string.Join( "\n", expected ), string.Join( "\n", actual ) );

            True( File
                .GetUnixFileMode( installer )
                .HasFlag( UnixFileMode.UserExecute ), "install.sh is executable in the archive" );

            var home = Path.Combine( root, "home" );
            var environment = InstallerEnvironment( home, await WriteDesktopStubsAsync( root, calls ) );
            var linked = Path.Combine( root, "linked-install.sh" );
            File.CreateSymbolicLink( linked, installer );
            File.Delete( calls );

            var dryRun = await ProcessRunner.RunAsync( "env", [.. environment, linked, "--dry-run"], null, root, TimeSpan.FromSeconds( 30 ), default );

            True( dryRun.ExitCode == 0, "install.sh --dry-run failed: " + dryRun.StandardError );

            True(
                dryRun.StandardOutput.Contains( "Install commanddeck 9.9.9", StringComparison.Ordinal ) &&
                dryRun.StandardOutput.Contains( "from     " + Path.Combine( release, "bin" ), StringComparison.Ordinal ) &&
                dryRun.StandardOutput.Contains( "Nothing was changed.", StringComparison.Ordinal ), dryRun.StandardOutput );

            Equal(
                string.Join( "\n", "hyprctl -j version", "systemctl --user show-environment", "commanddeck version",
                    "commanddeck hotkey check --chord CTRL + SPACE", "omarchy plugin validate " + Path.Combine( release, "qml" ) ),
                (await File.ReadAllTextAsync( calls )).TrimEnd( ) );

            True( !Directory.Exists( home ), "a dry run creates no installed files" );
            var missing = Path.Combine( root, "missing" );

            var rejected = await ProcessRunner.RunAsync( "env", [.. environment, installer, "--from=" + missing], null, root, TimeSpan.FromSeconds( 30 ),
                default );

            Equal( 1, rejected.ExitCode );

            True(
                rejected.StandardError.Contains( $"No commanddeck binary in {missing}.", StringComparison.Ordinal ) &&
                rejected.StandardError.Contains( "Nothing was changed.", StringComparison.Ordinal ), rejected.StandardError );

            True( !Directory.Exists( home ), "a rejected bundle creates no installed files" );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static string[] CreateFixedFiles( )
        {
            return
            [
                "INSTALL.txt", "LICENSE", "install.sh", "packaging/systemd/commanddeck.service",
                "bin/commanddeck", "bin/commanddeck.schema.json", "bin/libe_sqlite3.so", "bin/picker/shell.qml"
            ];
        }
    }

    // A failure late in an update puts back the previous program, link,
    // service, configuration, plugin, and hotkey files, including the target of
    // a symlinked hyprland.lua, and restarts the old daemon. A successful rerun
    // with an unchanged plugin makes no second plugin backup.
    private static async Task ReleaseInstallRollsBackAFailedUpdateAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-rollback-" ).FullName;

        try
        {
            var calls = Path.Combine( root, "tool-calls.log" );
            var release = await PackageFakeReleaseAsync( root, calls );
            var installer = Path.Combine( release, "install.sh" );
            var tools = await WriteDesktopStubsAsync( root, calls );
            var home = Path.Combine( root, "home" );
            await CreatePreviousInstallationAsync( home );

            string[] failing = [.. InstallerEnvironment( home, tools ), "COMMANDDECK_SPEC_CONFIG_ERRORS=error: bad bind"];

            File.Delete( calls );

            var failed = await ProcessRunner.RunAsync( "env", [.. failing, installer, "--reset-config"], null, root, TimeSpan.FromSeconds( 60 ), default );

            Equal( 1, failed.ExitCode );

            True( failed.StandardError.Contains( "Restored the previous CommandDeck installation.", StringComparison.Ordinal ), failed.StandardError );

            await AssertPreviousInstallationAsync( home );

            Equal( "systemctl --user daemon-reload,systemctl --user enable commanddeck.service,systemctl --user restart commanddeck.service,hyprctl reload",
                await CallsAfterAsync( calls, "hyprctl configerrors" ) );

            // The first run replaces the old plugin and backs it up; the second
            // finds the plugin current and adds no backup.
            var environment = InstallerEnvironment( home, tools );
            var backups = Path.Combine( home, "state", "commanddeck", "plugin-backups" );
            List<int> added = [];
            foreach ( var run in new[] { "first", "second" } )
            {
                var before = Directory.GetDirectories( backups ).Length;

                var installed = await ProcessRunner.RunAsync( "env", [.. environment, installer], null, root, TimeSpan.FromSeconds( 60 ), default );

                True( installed.ExitCode == 0, $"{run} install failed: " + installed.StandardError );
                True( installed.StandardOutput.Contains( "CommandDeck is installed.", StringComparison.Ordinal ), installed.StandardOutput );

                AssertNoRollbackLeftovers( home );

                added.Add( Directory.GetDirectories( backups )
                               .Length -
                           before );
            }

            Equal( "1,0", string.Join( ",", added ) );

            Equal( await File.ReadAllTextAsync( Path.Combine( release, "bin", "commanddeck" ) ),
                await File.ReadAllTextAsync( Path.Combine( home, "data", "commanddeck", "commanddeck" ) ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    // SIGTERM during an update rolls back like a failure; a second signal
    // during the rollback does not cut it short.
    private static async Task ReleaseInstallRollsBackWhenInterruptedAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-signal-" ).FullName;

        try
        {
            var calls = Path.Combine( root, "tool-calls.log" );
            var release = await PackageFakeReleaseAsync( root, calls );
            var tools = await WriteDesktopStubsAsync( root, calls );
            var home = Path.Combine( root, "home" );
            await CreatePreviousInstallationAsync( home );

            string[] interrupted = [.. InstallerEnvironment( home, tools ), "COMMANDDECK_SPEC_SIGNAL_AT=hyprctl reload"];

            File.Delete( calls );

            var result = await ProcessRunner.RunAsync( "env", [.. interrupted, Path.Combine( release, "install.sh" )], null, root, TimeSpan.FromSeconds( 60 ),
                default );

            Equal( 143, result.ExitCode );

            True( result.StandardError.Contains( "Restored the previous CommandDeck installation.", StringComparison.Ordinal ), result.StandardError );

            await AssertPreviousInstallationAsync( home );

            Equal( "systemctl --user daemon-reload,systemctl --user enable commanddeck.service,systemctl --user restart commanddeck.service,hyprctl reload",
                await CallsAfterAsync( calls, "hyprctl reload" ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
