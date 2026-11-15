using System.Security.Cryptography;

namespace CommandDeck.Specs;

public static partial class Program
{
    private sealed record DownloadFixture( string Root, string Tools, string Home, string Config, string Data, string State,
        string Cache, string Calls );

    [System.Runtime.Versioning.SupportedOSPlatform( "linux" )]
    private static async Task<DownloadFixture> CreateDownloadFixtureAsync( string root )
    {
        var home = Path.Combine( root, "home" );
        var fixture = new DownloadFixture( root, Directory.CreateDirectory( Path.Combine( root, "tools" ) ).FullName, home,
            Directory.CreateDirectory( Path.Combine( home, ".config" ) ).FullName,
            Directory.CreateDirectory( Path.Combine( home, ".local", "share" ) ).FullName,
            Directory.CreateDirectory( Path.Combine( home, ".local", "state" ) ).FullName,
            Directory.CreateDirectory( Path.Combine( home, ".cache" ) ).FullName,
            Path.Combine( root, "install-calls" ) );

        await WriteReleaseDownloadStubsAsync( fixture.Tools );
        await WriteDownloadArchiveAsync( root, "linux-x64" );
        await WriteDownloadArchiveAsync( root, "linux-arm64" );

        return fixture;
    }

    // Every desktop tool the script can call is a stub, and HOME and the XDG
    // folders point into the fixture, so no run reaches the real desktop. The
    // run looks like a Hyprland session, also where no Hyprland runs.
    // Later environment entries replace earlier ones. With standardInput, Bash
    // reads the script from stdin, as with curl | bash -s --. With command,
    // that command runs with the script path and the arguments after it.
    private static async Task<ProcessResult> RunDownloaderAsync( DownloadFixture fixture, string[] arguments, string cpu, string mode,
        string[]? extraEnvironment = null, string? standardInput = null, string[]? command = null )
    {
        foreach ( var suffix in new[] { "", ".pwd", ".urls", ".tools", ".started", ".xdg" } )
        {
            File.Delete( fixture.Calls + suffix );
        }

        string[] environment =
        [
            "PATH=" + fixture.Tools + ":" + Environment.GetEnvironmentVariable( "PATH" ), "HOME=" + fixture.Home,
            "XDG_CONFIG_HOME=" + fixture.Config, "XDG_DATA_HOME=" + fixture.Data, "XDG_STATE_HOME=" + fixture.State,
            "XDG_CACHE_HOME=" + fixture.Cache, "COMMANDDECK_SPEC_DOWNLOAD_ROOT=" + fixture.Root,
            "COMMANDDECK_SPEC_INSTALL_CALLS=" + fixture.Calls, "COMMANDDECK_SPEC_DOWNLOAD_CPU=" + cpu,
            "COMMANDDECK_SPEC_DOWNLOAD_MODE=" + mode, "HYPRLAND_INSTANCE_SIGNATURE=spec", .. extraEnvironment ?? []
        ];

        var script = Path.Combine( FindRepositoryRoot( ), "scripts", "install-release.sh" );
        string[] invocation = command is not null ? [.. command, script, .. arguments]
            : standardInput is not null ? ["bash", "-s", "--", .. arguments]
            : ["bash", script, .. arguments];

        return await ProcessRunner.RunAsync( "env", [.. environment, .. invocation], standardInput, fixture.Root,
            TimeSpan.FromSeconds( 30 ), default );
    }

    [System.Runtime.Versioning.SupportedOSPlatform( "linux" )]
    private static async Task WriteDownloadArchiveAsync( string root, string rid )
    {
        var name = $"commanddeck-{FakeVersion}-{rid}";
        var folder = Directory.CreateDirectory( Path.Combine( root, name ) ).FullName;
        await WriteExecutableAsync( Path.Combine( folder, "install.sh" ), """
            #!/bin/sh
            cd -- "$(dirname -- "$0")" || exit 1
            if [ "$COMMANDDECK_SPEC_DOWNLOAD_MODE" = slow ]; then : >"$COMMANDDECK_SPEC_INSTALL_CALLS.started"; sleep 1; fi
            printf '%s\n' "$@" >"$COMMANDDECK_SPEC_INSTALL_CALLS"
            pwd >"$COMMANDDECK_SPEC_INSTALL_CALLS.pwd"
            printf '%s\n' "${XDG_CONFIG_HOME-unset}" >"$COMMANDDECK_SPEC_INSTALL_CALLS.xdg"
            if [ "$COMMANDDECK_SPEC_DOWNLOAD_MODE" = installer-failure ]; then exit 23; fi
            """ );

        var archive = Path.Combine( root, name + ".tar.gz" );
        var packaged = await ProcessRunner.RunAsync( "tar", ["-czf", archive, "-C", root, name], null, root, TimeSpan.FromSeconds( 10 ), default );
        Equal( 0, packaged.ExitCode );
        var digest = Convert.ToHexStringLower( SHA256.HashData( await File.ReadAllBytesAsync( archive ) ) );
        await File.WriteAllTextAsync( archive + ".sha256", $"{digest}  {name}.tar.gz\n" );
    }

    [System.Runtime.Versioning.SupportedOSPlatform( "linux" )]
    private static async Task WriteReleaseDownloadStubsAsync( string tools )
    {
        await WriteExecutableAsync( Path.Combine( tools, "uname" ), """
            #!/bin/sh
            case "$1" in
              -s) echo Linux ;;
              -m) echo "$COMMANDDECK_SPEC_DOWNLOAD_CPU" ;;
              *) exit 1 ;;
            esac
            """ );

        await WriteExecutableAsync( Path.Combine( tools, "curl" ), """
            #!/bin/sh
            while [ "$#" -gt 0 ]; do
              case "$1" in
                https://*) url=$1 ;;
                -o) shift; output=$1 ;;
              esac
              shift
            done
            printf '%s\n' "$url" >>"$COMMANDDECK_SPEC_INSTALL_CALLS.urls"
            case "$url" in
              https://api.github.com/*) printf '[{"tag_name":"%s"}]' "${COMMANDDECK_SPEC_LATEST:-v9.9.9}"; exit 0 ;;
            esac
            if [ "$COMMANDDECK_SPEC_DOWNLOAD_MODE" = missing ]; then echo 'HTTP 404' >&2; exit 22; fi
            cp "$COMMANDDECK_SPEC_DOWNLOAD_ROOT/${url##*/}" "$output" || exit 1
            case "$COMMANDDECK_SPEC_DOWNLOAD_MODE:$output" in
              corrupt:*.tar.gz) printf corrupt >>"$output" ;;
              wrong-name:*.sha256) sed -i 's/  .*/  other.tar.gz/' "$output" ;;
            esac
            """ );

        // The uninstaller calls these. Each records its call and succeeds. The
        // service is inactive unless a case keeps it running; the user manager,
        // Hyprland, and the plugin disable work unless a case breaks them.
        await WriteExecutableAsync( Path.Combine( tools, "systemctl" ), """
            #!/bin/sh
            echo "systemctl $*" >>"$COMMANDDECK_SPEC_INSTALL_CALLS.tools"
            case "$*" in
              *show-environment*) [ "$COMMANDDECK_SPEC_SYSTEMD_DOWN" = 1 ] && exit 1; exit 0 ;;
              *is-active*) if [ "$COMMANDDECK_SPEC_SERVICE_ACTIVE" = 1 ]; then echo active; exit 0; fi; echo inactive; exit 3 ;;
            esac
            """ );
        await WriteExecutableAsync( Path.Combine( tools, "omarchy" ), """
            #!/bin/sh
            echo "omarchy $*" >>"$COMMANDDECK_SPEC_INSTALL_CALLS.tools"
            case "$*" in
              "plugin list --json")
                if [ "$COMMANDDECK_SPEC_OMARCHY_LIST_FAIL" = 1 ]; then exit 1; fi
                echo '[{"id":"local.commanddeck","enabled":true}]' ;;
              "plugin disable local.commanddeck") if [ "$COMMANDDECK_SPEC_OMARCHY_FAIL" = 1 ]; then exit 1; fi ;;
            esac
            exit 0
            """ );
        await WriteExecutableAsync( Path.Combine( tools, "hyprctl" ), """
            #!/bin/sh
            echo "hyprctl $*" >>"$COMMANDDECK_SPEC_INSTALL_CALLS.tools"
            if [ "$*" = "-j version" ] && [ "$COMMANDDECK_SPEC_HYPRLAND_DOWN" = 1 ]; then exit 1; fi
            exit 0
            """ );
    }
}
