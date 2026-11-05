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

        await WriteShellAsync( directory );

        if ( !groupSignal )
        {
            await File.WriteAllTextAsync( Path.Combine( directory, "release-hide" ), "" );
        }

        using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 15 ) );
        using var requests = Listen( Path.Combine( socketDirectory, ".socket.sock" ) );
        using var events = Listen( Path.Combine( socketDirectory, ".socket2.sock" ) );
        TaskCompletionSource dispatched = new( TaskCreationOptions.RunContinuationsAsynchronously );
        var serve = ServeAsync( requests, dispatched, new FakeLauncher( directory, opens: false ), deadline.Token );
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
                Console.Error.WriteLine( ($"Isolated {via} {signal}: exit {child.ExitCode}, cleanup {cleaned}, toggle alive at hide " +
                    $"{toggleAlive}; {await output}{await error}") );
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

    // A whole run against a launcher that opens on the first toggle, reports a
    // complete frame, and closes on the second.
    public static async Task<bool> RunSuccessAsync( )
    {
        var directory = Directory.CreateTempSubdirectory( "cd-latency-" ).FullName;
        var socketDirectory = Path.Combine( directory, "hypr", "test" );
        Directory.CreateDirectory( socketDirectory );

        await WriteShellAsync( directory );
        await File.WriteAllTextAsync( Path.Combine( directory, "release-hide" ), "" );

        using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 15 ) );
        using var requests = Listen( Path.Combine( socketDirectory, ".socket.sock" ) );
        using var events = Listen( Path.Combine( socketDirectory, ".socket2.sock" ) );
        TaskCompletionSource dispatched = new( TaskCreationOptions.RunContinuationsAsynchronously );
        var serve = ServeAsync( requests, dispatched, new FakeLauncher( directory, opens: true ), deadline.Token );
        using var child = Process.Start( ChildStart( directory, timesOut: false, Options.GlobalPath ) );
        if ( child is null )
        {
            throw new InvalidOperationException( "The isolated measurement process did not start." );
        }

        var output = child.StandardOutput.ReadToEndAsync( );
        var error = child.StandardError.ReadToEndAsync( );

        try
        {
            using var eventClient = await events.AcceptAsync( deadline.Token );
            await child.WaitForExitAsync( deadline.Token );

            var printed = await output;
            var cleaned = File.Exists( Path.Combine( directory, "hide" ) );
            var passed = child.ExitCode == 0 && cleaned && printed.Contains( "sample   1: card", StringComparison.Ordinal ) &&
                printed.Contains( "card        p50", StringComparison.Ordinal );

            if ( !passed )
            {
                Console.Error.WriteLine( $"Isolated success: exit {child.ExitCode}, cleanup {cleaned}; {printed}{await error}" );
            }

            return passed;
        }
        catch ( OperationCanceledException ) when ( deadline.IsCancellationRequested )
        {
            Console.Error.WriteLine( "Isolated success: the measurement did not finish before the test deadline." );

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

    private static async Task WriteShellAsync( string directory )
    {
        var shell = Path.Combine( directory, CommandDeck.ToolExecutables.OmarchyShell );

        await File.WriteAllTextAsync( shell, """
            #!/bin/sh
            if [ "$2" = call ]; then
              if [ -e report ]; then /usr/bin/cat report; else echo '{"generation":0,"opened":false}'; fi
              exit 0
            fi
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

    private static async Task ServeAsync( Socket listener, TaskCompletionSource dispatched, FakeLauncher launcher, CancellationToken cancellationToken )
    {
        try
        {
            while ( true )
            {
                using var client = await listener.AcceptAsync( cancellationToken );

                // The measurement polls every millisecond, so a signal can stop it
                // between a request and its reply; that request needs no answer.
                try
                {
                    await AnswerAsync( client, dispatched, launcher, cancellationToken );
                }
                catch ( SocketException )
                {
                }
            }
        }
        catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
        {
        }
    }

    private static async Task AnswerAsync( Socket client, TaskCompletionSource dispatched, FakeLauncher launcher, CancellationToken cancellationToken )
    {
        var buffer = new byte[4096];
        var length = await client.ReceiveAsync( buffer, SocketFlags.None, cancellationToken );
        if ( length == 0 )
        {
            return;
        }

        var request = Encoding.UTF8.GetString( buffer, 0, length );
        var reply = request switch
        {
            "j/globalshortcuts" => """[{"name":"commanddeck:toggle"}]""",
            LayerInteractivity.Request => launcher.Interactivity( ),
            ToggleLatency.DispatchRequest => "ok",
            _ => throw new InvalidOperationException( $"Unexpected isolated request: {request}" )
        };

        // Record the toggle before replying: a signal may stop the measurement mid-reply.
        if ( request == ToggleLatency.DispatchRequest )
        {
            launcher.Toggled( );
            dispatched.TrySetResult( );
        }

        await client.SendAsync( Encoding.UTF8.GetBytes( reply ), SocketFlags.None, cancellationToken );
        client.Shutdown( SocketShutdown.Send );
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

// The launcher behind the fake sockets. One that never opens keeps every open
// waiting; one that opens takes the keyboard and reports a complete frame on
// the first toggle, and gives the keyboard back on the second.
sealed class FakeLauncher( string directory, bool opens )
{
    private int _toggles;

    public string Interactivity( )
    {
        if ( !opens )
        {
            return "-1";
        }

        return _toggles == 1 ? "1" : "0";
    }

    public void Toggled( )
    {
        _toggles++;

        if ( opens && _toggles == 1 )
        {
            File.WriteAllText( Path.Combine( directory, "report" ), Report( DateTimeOffset.UtcNow.ToUnixTimeMilliseconds( ) ) );
        }
    }

    private static string Report( long now )
    {
        return $$"""
            {"generation":1,"opened":true,"openedAt":{{now + 1}},"firstFrameAt":{{now + 20}},"completeFrameAt":{{now + 30}},
             "keyboardReadyAt":{{now + 25}},"framesBeforeComplete":1,"firstFrameRatio":1,"completeFrameRatio":1,
             "screen":"TEST-1","renderer":"software"}
            """;
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
    private const string RecordedReport = """
        {"generation":4,"opened":true,"openedAt":1000,"firstFrameAt":1031,"completeFrameAt":1148,"keyboardReadyAt":1030,
         "closedAt":0,"framesBeforeComplete":9,"firstFrameRatio":2,"completeFrameRatio":1.6666666,"screen":"DP-3","renderer":"software"}
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

        Check( lines.Count( line => LayerEvents.Matches( line, ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ) ) == 2,
             "openlayer matches only the launcher namespace" );
        Check( !LayerEvents.Matches( "openlayer>>commanddeck-hud", ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ),
             "the HUD namespace is not the launcher" );
        Check( LayerEvents.Matches( "openlayer>>commanddeck\r", ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ),
             "a trailing carriage return is ignored" );
        Check( !LayerEvents.Matches( ">>commanddeck", ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ) && !LayerEvents.Matches( "",
             ToggleLatency.OpenLayerEvent, ToggleLatency.Namespace ), "malformed lines never match" );
        CheckPresentation( Check );
        Check( LayerEvents.HasShortcut( """[{"name":"commanddeck:toggle","description":"Toggle CommandDeck"}]""" ), "the registered shortcut is found" );
        Check( !LayerEvents.HasShortcut( """[{"name":"other:toggle"}]""" ), "another shortcut does not count" );
        Check( ToggleLatency.DispatchRequest == "dispatch hl.dsp.global(\"commanddeck:toggle\")", "the toggle uses the hotkey's global dispatcher" );

        double[] twenty = [.. Enumerable
            .Range( 1, 20 )
            .Select( static value => value * 10.0 )
            .Reverse( )];

        Check( Percentiles.NearestRank( twenty, 0.50 ) == 100 && Percentiles.NearestRank( twenty, 0.95 ) == 190,
             "nearest-rank p50 and p95 of 20 unsorted samples" );
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
        Check( Options.Parse( ["--live", "--monitor", "HDMI-A-1"] ).Monitor == "HDMI-A-1", "a monitor name parses" );
        Check( HyprlandSession.FocusRequest( "DP-1" ) == "dispatch hl.dsp.focus({ monitor = \"DP-1\" })",
             "the monitor is focused through Hyprland's dispatcher" );
        Check( HasExpectedOptionRejections( ), "invalid options are rejected" );
        Check( HyprlandSession.Refusal( static _ => null, static _ => true ) is not null, "no Hyprland session is refused" );
        Check( HyprlandSession.Refusal( static name => name ==
             CommandDeck.EnvironmentVariableNames.HyprlandInstanceSignature ? "sig" : "/run/user/1", static _ => false ) is not null,
             "missing sockets are refused" );
        Check( HyprlandSession.Refusal( static name => name ==
             CommandDeck.EnvironmentVariableNames.HyprlandInstanceSignature ? "sig" : "/run/user/1", static _ => true ) is null, "a live session is accepted" );

        var window = Path.Combine( "qml", "common", "Launcher", "CommandDeckWindow.qml" );
        var launcher = Path.Combine( "qml", "common", "Launcher", "CommandDeck.qml" );

        Check( File.Exists( window ) && File
            .ReadAllText( window )
            .Contains( "WlrLayershell.namespace: \"commanddeck\"", StringComparison.Ordinal ),
            "the launcher window declares the measured namespace (run from the repository root)" );
        Check( File.Exists( launcher ) && File
            .ReadAllText( launcher )
            .Contains( $"function {PresentationState.Method}()", StringComparison.Ordinal ), "the launcher answers the report that the script reads" );

        Check( await CleanupProcessTest.RunSuccessAsync( ), "a whole run times the open, the complete frame and the close" );

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

    private static void CheckPresentation( Action<bool, string> check )
    {
        var report = PresentationState.Parse( RecordedReport );

        check( report is { Generation: 4, Opened: true, FramesBeforeComplete: 9, Screen: "DP-3", Renderer: "software" },
             "the launcher's report parses" );

        check( report is not null && report.IsCompleteFor( 4 ) && !report.IsCompleteFor( 5 ), "a report counts only for its own open" );

        check( PresentationState.Parse( """{"generation":4,"completeFrameAt":1148}""" ) is { } partial && !partial.IsCompleteFor( 4 ),
             "an open without keyboard focus is not complete" );

        check( PresentationState.Parse( "unknown" ) is null && PresentationState.Parse( "{}" ) is null && PresentationState.Parse( "[]" ) is null,
             "an old plugin's reply is no report" );

        check( LayerInteractivity.Parse( "1" ) == LayerInteractivity.Exclusive && LayerInteractivity.Parse( " 0\n" ) == 0 &&
             LayerInteractivity.Parse( "-1" ) == LayerInteractivity.NoLayer && LayerInteractivity.Parse( "error: lua" ) is null,
             "Hyprland's layer reply parses" );

        check( LayerInteractivity.Request.Contains( "l.namespace == 'commanddeck'", StringComparison.Ordinal ), "the layer query asks for the launcher" );

        if ( report is null )
        {
            return;
        }

        var clock = new SampleClock( );
        var sample = OpenSample.From( report, clock, 29.8, 25.1, 3.1 );

        check( clock.Until( 0 ) is null && sample is not null && sample.Card - sample.FirstFrame == 117, "launcher times are relative to the sample" );
        check( OpenSample.From( report with { CompleteFrameAt = 0 }, clock, 29.8, null, 3.1 ) is null, "an open without a complete frame is no sample" );

        check( sample is not null && sample
            .Describe( 1 )
            .Contains( "9 frames before, scale 2 -> 1.67, DP-3", StringComparison.Ordinal ), "the sample line" );
    }

    private static bool HasExpectedOptionRejections( )
    {
        return Throws( ( ) => Options.Parse( ["--live", "--samples", "0"] ) ) &&
               Throws( ( ) => Options.Parse( ["--live", "--path", "keys"] ) ) &&
               Throws( ( ) => Options.Parse( ["--live", "--monitor", "DP-1\") os.exit(\""] ) ) &&
               Throws( ( ) => Options.Parse( ["--live", "--monitor"] ) );
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
