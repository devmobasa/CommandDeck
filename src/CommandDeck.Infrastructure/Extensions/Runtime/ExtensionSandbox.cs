using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck;

/// <summary>Host-owned Linux runtime closure. Package manifests cannot add mounts or environment variables.</summary>
public static class ExtensionSandbox
{
    public const string RuntimeProfile = ExtensionTargets.LinuxGlibcInvariantV1;

    private static readonly string[] Libraries =
    [
        "libc.so.6", "libm.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1",
        "libstdc++.so.6", "libgcc_s.so.1", "libz.so.1", "libcrypto.so.3", "libssl.so.3"
    ];

    public static IReadOnlyList<string> BuildArguments( string artifactDirectory, string entrypoint, string? debugTerminal = null )
    {
        var root = Path.GetFullPath( artifactDirectory );
        if ( IsTargetPathInvalid( artifactDirectory, entrypoint, root ) )
        {
            throw new InvalidDataException( "The extension target path is invalid." );
        }

        var loader = GetDynamicLoader( );
        List<string> arguments = BuildSandboxArguments( root );

        foreach ( var library in Libraries.Prepend( loader ) )
        {
            var path = Path.Combine( "/usr/lib", library );
            if ( !File.Exists( path ) )
            {
                throw new PlatformNotSupportedException( $"Extension runtime library {library} is unavailable." );
            }

            arguments.AddRange( ["--ro-bind", path, path] );
        }

        if ( debugTerminal is not null )
        {
            if ( IsDebuggerUnavailable( debugTerminal ) )
            {
                throw new PlatformNotSupportedException( "Native debugging requires gdbserver and a private terminal." );
            }

            arguments.AddRange( [
                "--ro-bind", "/usr/bin/gdbserver", "/run/gdbserver", "--dev-bind", debugTerminal, "/run/debug-tty",
                "--", "/run/gdbserver", "--once", "--no-startup-with-shell", "--no-disable-randomization", "/run/debug-tty", "/extension/" + entrypoint
            ] );
        }
        else
        {
            arguments.AddRange( ["--", "/extension/" + entrypoint] );
        }

        return arguments;
    }

    /// <summary>Runs inside the already-created service, before package code. Owner loss ends the namespace.</summary>
    public static async Task<int> RunWorkerAsync( string artifactDirectory, string entrypoint, int ownerPid, long ownerStartTicks, string? debugSocket,
        CancellationToken cancellationToken )
    {
        if ( !OwnerIsAlive( ownerPid, ownerStartTicks ) )
        {
            return 1;
        }

        using var debugger = debugSocket is null ? null : new ExtensionDebugTransport( debugSocket );
        using var debugLifetime = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        var relay = debugger?.RelayAsync( debugLifetime.Token );
        ProcessStartInfo info = CreateStartInfo( );
        info.Environment.Clear( );

        foreach ( var argument in BuildArguments( artifactDirectory, entrypoint, debugger?.SlavePath ) )
        {
            info.ArgumentList.Add( argument );
        }

        using var child = Process.Start( info );
        if ( child is null )
        {
            throw new IOException( "The restricted extension could not start." );
        }

        try
        {
            while ( !child.HasExited && OwnerIsAlive( ownerPid, ownerStartTicks ) )
            {
                if ( relay?.IsCompleted == true )
                {
                    break;
                }

                await Task
                    .Delay( 100, cancellationToken )
                    .ConfigureAwait( false );
            }

            return child.HasExited ? child.ExitCode : 1;
        }
        finally
        {
            if ( !child.HasExited )
            {
                child.Kill( entireProcessTree: true );
            }

            await child
                .WaitForExitAsync( CancellationToken.None )
                .WaitAsync( TimeSpan.FromSeconds( 1 ), CancellationToken.None )
                .ConfigureAwait( false );

            await debugLifetime
                .CancelAsync( )
                .ConfigureAwait( false );

            if ( relay is not null )
            {
                try
                {
                    await relay.ConfigureAwait( false );
                }
                catch ( Exception exception ) when ( exception is OperationCanceledException or IOException or SocketException ) { }
            }
        }
    }

    private static bool OwnerIsAlive( int pid, long startTicks )
    {
        try
        {
            return ReadProcessStartTicks( pid ) == startTicks;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException )
        {
            return false;
        }
    }

    internal static long ReadProcessStartTicks( int pid )
    {
        var stat = File.ReadAllText( $"/proc/{pid}/stat" );

        var fields = stat[(stat.LastIndexOf( ')' ) + 2)..]
            .Split( ' ' );

        return fields[0] == "Z" ? -1 : long.Parse( fields[19], System.Globalization.CultureInfo.InvariantCulture );
    }

    private static bool IsTargetPathInvalid( string artifactDirectory, string entrypoint, string root )
    {
        return !Directory.Exists( root ) ||
               !Path.IsPathFullyQualified( artifactDirectory ) ||
               string.IsNullOrWhiteSpace( entrypoint ) ||
               Path.IsPathRooted( entrypoint ) ||
               entrypoint
                   .Split( '/' )
                   .Any( part => part is "" or "." or ".." ) ||
               entrypoint.Contains( '\\', StringComparison.Ordinal );
    }

    private static string GetDynamicLoader( )
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "ld-linux-x86-64.so.2",
            Architecture.Arm64 => "ld-linux-aarch64.so.1",
            _ => throw new PlatformNotSupportedException( "This extension runtime architecture is unavailable." )
        };
    }

    private static List<string> BuildSandboxArguments( string root )
    {
        return
        [
            "--unshare-all", "--unshare-user", "--disable-userns", "--die-with-parent", "--new-session",
            "--cap-drop", "ALL", "--clearenv",
            "--dir", "/usr", "--dir", "/usr/lib", "--symlink", "usr/lib", "/lib", "--symlink", "usr/lib", "/lib64",
            "--ro-bind", root, "/extension", "--proc", "/proc", "--dev", "/dev",
            "--size", "67108864", "--tmpfs", "/tmp", "--dir", "/run", "--dir", "/home", "--dir", "/home/extension",
            "--setenv", EnvironmentVariableNames.Home, "/home/extension", "--setenv", EnvironmentVariableNames.TemporaryDirectory, "/tmp",
            "--setenv", EnvironmentVariableNames.Language, ProcessEnvironmentDefaults.Utf8Locale, "--setenv", EnvironmentVariableNames.EnableDiagnostics, "0",
            "--setenv", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1",
            "--setenv", EnvironmentVariableNames.GcHeapHardLimit, "10000000",
            "--chdir", "/extension"
        ];
    }

    private static bool IsDebuggerUnavailable( string debugTerminal )
    {
        return !debugTerminal.StartsWith( "/dev/pts/", StringComparison.Ordinal ) || !File.Exists( "/usr/bin/gdbserver" );
    }

    private static ProcessStartInfo CreateStartInfo( )
    {
        return new( LinuxExecutablePaths.Bubblewrap ) { UseShellExecute = false };
    }
}
