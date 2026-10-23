using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

// Run the actual measurement entry point in a child whose only desktop is two
// private sockets. A fake shell records hide by creating files in its directory.
static class CleanupProcessTest
{
    public static async Task<bool> RunAsync( string signal, string via = "global" )
    {
        var directory = Directory.CreateTempSubdirectory( "cd-latency-" ).FullName;
        var socketDirectory = Path.Combine( directory, "hypr", "test" );
        Directory.CreateDirectory( socketDirectory );
        var groupSignal = signal == "group-SIGINT";
        var shell = Path.Combine( directory, CommandDeck.ToolExecutables.OmarchyShell );

        await File.WriteAllTextAsync( shell, """
            #!/bin/sh
            if [ "$2" = toggle ]; then
              echo $$ > toggle-pid
              /usr/bin/touch opened
              exec /usr/bin/sleep 30
            fi
            if [ -e toggle-pid ] && /usr/bin/kill -0 "$(/usr/bin/cat toggle-pid)" 2>/dev/null; then
              /usr/bin/touch toggle-alive-at-hide
            fi
            trap '/usr/bin/touch interrupted; exit 130' INT
            if [ ! -e attempted ]; then
              /usr/bin/touch attempted
              case "$CLEANUP_CASE" in
                retry) exit 7 ;;
                retry-timeout) exec /usr/bin/sleep 30 ;;
              esac
            else
              /usr/bin/touch retried
            fi
            /usr/bin/touch hide-started
            while [ ! -e release-hide ]; do /usr/bin/sleep 0.01; done
            /usr/bin/touch "$@"
            """ );

        File.SetUnixFileMode( shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

        if ( !groupSignal )
        {
            await File.WriteAllTextAsync( Path.Combine( directory, "release-hide" ), "" );
        }

        using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 15 ) );
        using var requests = Listen( Path.Combine( socketDirectory, ".socket.sock" ) );
        using var events = Listen( Path.Combine( socketDirectory, ".socket2.sock" ) );
        TaskCompletionSource dispatched = new( TaskCreationOptions.RunContinuationsAsynchronously );
        var serve = ServeAsync( requests, dispatched, deadline.Token );
        var timesOut = signal is "timeout" or "retry" or "retry-timeout" || groupSignal;
        var childStart = ChildStart( directory, timesOut, via );
        childStart.Environment["CLEANUP_CASE"] = signal;
        using var child = Process.Start( childStart );
        if ( child is null )
        {
            throw new InvalidOperationException( "The isolated measurement process did not start." );
        }

        var output = child.StandardOutput.ReadToEndAsync( );
        var error = child.StandardError.ReadToEndAsync( );

        try
        {
            using var eventClient = await events.AcceptAsync( deadline.Token );
            await AwaitToggleAsync( eventClient, dispatched, directory, via, deadline.Token );

            if ( groupSignal || !timesOut )
            {
                if ( groupSignal )
                {
                    while ( !File.Exists( Path.Combine( directory, "hide-started" ) ) )
                    {
                        await Task.Delay( 10, deadline.Token );
                    }
                }

                await SendSignalAsync( groupSignal ? "SIGINT" : signal, groupSignal ? -child.Id : child.Id, deadline.Token );

                if ( groupSignal )
                {
                    await Task.Delay( 100, deadline.Token );
                    await File.WriteAllTextAsync( Path.Combine( directory, "release-hide" ), "", deadline.Token );
                }
            }

            await child.WaitForExitAsync( deadline.Token );

            var cleaned = File.Exists( Path.Combine( directory, "shell" ) )
                && File.Exists( Path.Combine( directory, "hide" ) )
                && File.Exists( Path.Combine( directory, "local.commanddeck" ) );
            var retried = File.Exists( Path.Combine( directory, "retried" ) );
            var interrupted = File.Exists( Path.Combine( directory, "interrupted" ) );
            var toggleAlive = File.Exists( Path.Combine( directory, "toggle-alive-at-hide" ) );
            var passed = child.ExitCode == (timesOut ? 1 : 130) && cleaned && !interrupted;
            passed &= !toggleAlive && retried == (signal is "retry" or "retry-timeout");

            if ( !passed )
            {
                Console.Error.WriteLine( $"Isolated {via} {signal}: exit {child.ExitCode}, cleanup {cleaned}, toggle alive at hide {toggleAlive}; {await output}{await error}" );
            }

            return passed;
        }
        catch ( OperationCanceledException ) when ( deadline.IsCancellationRequested )
        {
            Console.Error.WriteLine( $"Isolated {via} {signal}: measurement did not finish cleanup before the test deadline." );

            return false;
        }
        finally
        {
            if ( !child.HasExited )
            {
                child.Kill( entireProcessTree: true );
                await child.WaitForExitAsync( );
            }

            await deadline.CancelAsync( );
            await serve;
            Directory.Delete( directory, recursive: true );
        }
    }

    private static async Task AwaitToggleAsync( Socket events, TaskCompletionSource dispatched, string directory, string via,
        CancellationToken cancellationToken )
    {
        if ( via == "global" )
        {
            // No openlayer is delivered: the measurement is waiting on a delayed open.
            await dispatched.Task.WaitAsync( cancellationToken );

            return;
        }

        while ( !File.Exists( Path.Combine( directory, "opened" ) ) )
        {
            await Task.Delay( 10, cancellationToken );
        }

        await events.SendAsync( Encoding.UTF8.GetBytes( $"{ToggleLatency.OpenLayerEvent}>>{ToggleLatency.Namespace}\n" ), SocketFlags.None, cancellationToken );
    }

    private static async Task SendSignalAsync( string signal, int target, CancellationToken cancellationToken )
    {
        ProcessStartInfo start = new( "/usr/bin/kill" );
        start.ArgumentList.Add( "-s" );
        start.ArgumentList.Add( signal );
        start.ArgumentList.Add( "--" );
        start.ArgumentList.Add( target.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
        using var sender = Process.Start( start );
        if ( sender is null )
        {
            throw new InvalidOperationException( "The signal sender did not start." );
        }

        await sender.WaitForExitAsync( cancellationToken );

        if ( sender.ExitCode != 0 )
        {
            throw new InvalidOperationException( "The signal sender failed." );
        }
    }

    private static ProcessStartInfo ChildStart( string directory, bool timesOut, string via )
    {
        var executable = Environment.ProcessPath;
        if ( executable is null )
        {
            throw new InvalidOperationException( "The current executable path is unavailable." );
        }

        ProcessStartInfo start = CreateChildStartInfo( directory );
        start.ArgumentList.Add( "--wait" );
        start.ArgumentList.Add( executable );

        if ( Path.GetFileNameWithoutExtension( executable ) == ScriptExecutables.Dotnet )
        {
            start.ArgumentList.Add( Assembly.GetExecutingAssembly( ).Location );
        }

        var timeout = TimeoutArgument( timesOut, via );
        foreach ( var argument in new[] { Options.LiveOption, Options.SamplesOption, "1", Options.TimeoutOption, timeout, Options.PathOption, via } )
        {
            start.ArgumentList.Add( argument );
        }

        start.Environment[CommandDeck.XdgEnvironmentNames.RuntimeDirectory] = directory;
        start.Environment[CommandDeck.EnvironmentVariableNames.HyprlandInstanceSignature] = "test";
        start.Environment[CommandDeck.EnvironmentVariableNames.ExecutablePath] = directory;
        start.Environment.Remove( CommandDeck.EnvironmentVariableNames.WaylandDisplay );
        start.Environment.Remove( "DISPLAY" );

        return start;
    }

    private static string TimeoutArgument( bool timesOut, string via )
    {
        if ( !timesOut )
        {
            return "10000";
        }

        return via == Options.ShellTogglePath ? "500" : "100";
    }

    private static Socket Listen( string path )
    {
        Socket socket = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
        socket.Bind( new UnixDomainSocketEndPoint( path ) );
        socket.Listen( 4 );

        return socket;
    }

    private static async Task ServeAsync( Socket listener, TaskCompletionSource dispatched, CancellationToken cancellationToken )
    {
        try
        {
            while ( true )
            {
                using var client = await listener.AcceptAsync( cancellationToken );
                var buffer = new byte[4096];
                var length = await client.ReceiveAsync( buffer, SocketFlags.None, cancellationToken );
                var request = Encoding.UTF8.GetString( buffer, 0, length );
                var reply = request switch
                {
                    "j/globalshortcuts" => """[{"name":"commanddeck:toggle"}]""",
                    "j/layers" => "{}",
                    "dispatch hl.dsp.global(\"commanddeck:toggle\")" => "ok",
                    _ => throw new InvalidOperationException( $"Unexpected isolated request: {request}" )
                };

                await client.SendAsync( Encoding.UTF8.GetBytes( reply ), SocketFlags.None, cancellationToken );
                client.Shutdown( SocketShutdown.Send );

                if ( request == "dispatch hl.dsp.global(\"commanddeck:toggle\")" )
                {
                    dispatched.SetResult( );
                }
            }
        }
        catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
        {
        }
    }

    private static ProcessStartInfo CreateChildStartInfo( string directory )
    {
        return new( "/usr/bin/setsid" )
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }


}

static class SelfTest
{
    private const string RecordedEvents = """
        activewindow>>kitty,~
        openlayer>>commanddeck-hud
        openlayer>>commanddeck
        workspacev2>>2,2
        closelayer>>commanddeck
        openlayer>>quickshell
        openlayer>>commanddeck
        garbage
        """;
    private const string RecordedLayers = """
        {"DP-1":{"levels":{"0":[{"namespace":"omarchy-background"}],"3":[{"address":"0x1","namespace":"commanddeck"}]}}}
        """;

    public static async Task<int> RunAsync( )
    {
        List<string> failures = [];

        void Check( bool condition, string name )
        {
            Console.WriteLine( $"{(condition ? "PASS" : "FAIL")} {name}" );

            if ( !condition )
            {
                failures.Add( name );
            }
        }

        var lines = RecordedEvents.Split( '\n' );

        Check( lines.Count( line => LayerEvents.Matches( line, ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ) ) == 2, "openlayer matches only the launcher namespace" );
        Check( lines.Count( line => LayerEvents.Matches( line, ToggleLatency.CloseLayerEvent, ToggleLatency.Namespace ) ) == 1, "closelayer matches the launcher namespace" );
        Check( !LayerEvents.Matches( "openlayer>>commanddeck-hud", ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ), "the HUD namespace is not the launcher" );
        Check( LayerEvents.Matches( "openlayer>>commanddeck\r", ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ), "a trailing carriage return is ignored" );
        Check( !LayerEvents.Matches( ">>commanddeck", ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ) && !LayerEvents.Matches( "", ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ), "malformed lines never match" );
        Check( LayerEvents.HasLayer( RecordedLayers, ToggleLatency.Namespace ), "an open launcher is found in hyprctl layers" );
        Check( !LayerEvents.HasLayer( RecordedLayers, "commanddeck-hud" ), "an absent namespace is not found" );
        Check( LayerEvents.HasShortcut( """[{"name":"commanddeck:toggle","description":"Toggle CommandDeck"}]""" ), "the registered shortcut is found" );
        Check( !LayerEvents.HasShortcut( """[{"name":"other:toggle"}]""" ), "another shortcut does not count" );
        Check( ToggleLatency.DispatchRequest == "dispatch hl.dsp.global(\"commanddeck:toggle\")", "the toggle uses the hotkey's global dispatcher" );

        double[] twenty = [.. Enumerable
            .Range( 1, 20 )
            .Select( static value => value * 10.0 )
            .Reverse( )];

        Check( Percentiles.NearestRank( twenty, 0.50 ) == 100 && Percentiles.NearestRank( twenty, 0.95 ) == 190, "nearest-rank p50 and p95 of 20 unsorted samples" );
        Check( Percentiles.NearestRank( [42.5], 0.95 ) == 42.5, "one sample is every percentile" );

        double[] hundred = [.. Enumerable
            .Range( 1, 100 )
            .Select( static value => ( double ) value )];

        Check( Percentiles.NearestRank( hundred, 0.95 ) == 95, "p95 of 100 samples is rank 95 despite float rounding" );

        Check( Percentiles
            .Describe( "open ", [1, 2, 3] )
            .StartsWith( "open  p50 2.0 ms  p95 3.0 ms  min 1.0 ms  max 3.0 ms", StringComparison.Ordinal ), "the summary line" );

        Check( Throws( ( ) => Percentiles.NearestRank( [], 0.5 ) ), "no samples is an error" );
        Check( Throws( ( ) => Options.Parse( [] ) ), "measurement requires --live" );
        Check( Options.Parse( ["--live", "--samples", "5", "--path", "shell"] ) == new Options( 5, 2000, "shell" ), "options parse" );
        Check( HasExpectedOptionRejections( ), "invalid options are rejected" );
        Check( HyprlandSession.Refusal( static _ => null, static _ => true ) is not null, "no Hyprland session is refused" );
        Check( HyprlandSession.Refusal( static name => name == CommandDeck.EnvironmentVariableNames.HyprlandInstanceSignature ? "sig" : "/run/user/1", static _ => false ) is not null, "missing sockets are refused" );
        Check( HyprlandSession.Refusal( static name => name == CommandDeck.EnvironmentVariableNames.HyprlandInstanceSignature ? "sig" : "/run/user/1", static _ => true ) is null, "a live session is accepted" );

        var window = Path.Combine( "qml", "common", "Launcher", "CommandDeckWindow.qml" );

        Check( File.Exists( window ) && File
            .ReadAllText( window )
            .Contains( "WlrLayershell.namespace: \"commanddeck\"", StringComparison.Ordinal ),
            "the launcher window declares the measured namespace (run from the repository root)" );

        foreach ( var signal in new[] { "SIGINT", "SIGTERM", "timeout", "group-SIGINT", "retry", "retry-timeout" } )
        {
            Check( await CleanupProcessTest.RunAsync( signal ), $"{signal} during a pending open awaits launcher cleanup" );
        }

        foreach ( var signal in new[] { "SIGINT", "SIGTERM", "timeout" } )
        {
            Check( await CleanupProcessTest.RunAsync( signal, Options.ShellTogglePath ), $"{signal} during a hung shell toggle terminates it before cleanup" );
        }

        Console.WriteLine( failures.Count == 0 ? "Self-test passed." : $"{failures.Count} self-test check(s) failed." );

        return failures.Count == 0 ? 0 : 1;
    }

    private static bool HasExpectedOptionRejections( )
    {
        return Throws( ( ) => Options.Parse( ["--live", "--samples", "0"] ) ) &&
               Throws( ( ) => Options.Parse( ["--live", "--path", "keys"] ) );
    }

    private static bool Throws( Action action )
    {
        try
        {
            action( );

            return false;
        }
        catch ( ArgumentException )
        {
            return true;
        }
    }
}
