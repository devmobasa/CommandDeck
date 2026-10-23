#!/usr/bin/env dotnet
#:include ../src/CommandDeck.Core/Utilities/ByteUnits.cs
#:include ../src/CommandDeck.Core/Protocol/ProtocolLimits.cs
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

// Manual performance check for a published (AOT) daemon. It starts the daemon on
// an isolated fixture and times two endpoints from process start: the first
// accepted socket connection, and the first successful Home search. Each run
// gets its own state, configuration and socket, and none of the desktop's
// session variables, so it never touches the installed daemon or the desktop.
// `--self-test` checks reply parsing and percentiles.

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

return await DaemonStartup.RunAsync( args );

static class DaemonStartup
{
    private const string Usage = """
        Usage:
          dotnet scripts/measure-daemon-startup.cs --binary PATH [--compare PATH] [--samples N] [--directories N] [--timeout-ms MS]
          dotnet scripts/measure-daemon-startup.cs --self-test

        --binary       The published commanddeck executable to start (required).
        --compare      A second executable; samples alternate between the two.
        --samples      Timed starts per executable after one warm-up, 1-200 (default 10).
        --directories  Empty directories in the fixture's one index root, 0-50000 (default 0: no root).
        --timeout-ms   Longest wait for readiness and for the Home search, 100-60000 (default 10000).
        """;

    private static readonly string[] SessionVariables =
        ["WAYLAND_DISPLAY", "HYPRLAND_INSTANCE_SIGNATURE", "SWAYSOCK", "DISPLAY", "DBUS_SESSION_BUS_ADDRESS"];

    public static async Task<int> RunAsync( string[] args )
    {
        if ( args is ["--self-test"] )
        {
            return SelfTest( );
        }

        Options options;
        try
        {
            options = Options.Parse( args );
        }
        catch ( ArgumentException exception )
        {
            Console.Error.WriteLine( exception.Message );
            Console.Error.WriteLine( Usage );

            return 2;
        }

        var fixture = Directory.CreateTempSubdirectory( "cd-startup-" ).FullName;
        try
        {
            var config = await PrepareFixtureAsync( fixture, options );
            List<string> binaries = [options.Binary, .. options.Compare is null ? Array.Empty<string>( ) : [options.Compare]];
            Dictionary<string, (List<double> Ready, List<double> Home)> results = binaries.ToDictionary( static binary => binary, static _ => (new List<double>( ), new List<double>( )) );

            foreach ( var binary in binaries )
            {
                await PrepareCatalogAsync( binary, fixture, config, options );
                _ = await StartOnceAsync( binary, fixture, config, options );
            }

            for ( var sample = 1; sample <= options.Samples; sample++ )
            {
                // Alternate the order so neither build always runs on a warmer cache.
                foreach ( var binary in sample % 2 == 0 ? Enumerable.Reverse( binaries ) : binaries )
                {
                    var (ready, home) = await StartOnceAsync( binary, fixture, config, options );
                    results[binary].Ready.Add( ready );
                    results[binary].Home.Add( home );
                    Console.WriteLine( string.Create( CultureInfo.InvariantCulture,
                        $"sample {sample,3} {Label( binary, options )}: ready {ready,7:F1} ms   home {home,7:F1} ms" ) );
                }
            }

            foreach ( var binary in binaries )
            {
                Console.WriteLine( $"{Label( binary, options )} {binary}" );
                Console.WriteLine( Describe( "  spawn -> socket accepted    ", results[binary].Ready ) );
                Console.WriteLine( Describe( "  spawn -> Home search done   ", results[binary].Home ) );
            }

            return 0;
        }
        catch ( InvalidOperationException exception )
        {
            Console.Error.WriteLine( exception.Message );

            return 1;
        }
        finally
        {
            Directory.Delete( fixture, recursive: true );
        }
    }

    private static string Label( string binary, Options options ) => binary == options.Binary ? "A" : "B";

    private static async Task<string> PrepareFixtureAsync( string fixture, Options options )
    {
        var tree = Path.Combine( fixture, "tree" );
        for ( var index = 0; index < options.Directories; index++ )
        {
            Directory.CreateDirectory( Path.Combine( tree, "d" + index.ToString( CultureInfo.InvariantCulture ) ) );
        }

        object[] roots = options.Directories == 0
            ? []
            : [new { path = tree, includeHidden = true, includeContentInSearch = false, followSymbolicLinks = false, respectGitIgnore = false }];
        var config = Path.Combine( fixture, "config.json" );
        var document = new
        {
            storage = new { databasePath = Path.Combine( fixture, "catalog.db" ) },
            indexing = new { roots, indexDesktopApplications = false, indexOmarchyCommands = false, enableScheduledMaintenance = false },
            privacy = new { storeQueryHistory = false, storeUsageHistory = false, persistClipboard = false },
            ai = new { enabled = false }
        };
        await File.WriteAllTextAsync( config, JsonSerializer.Serialize( document ) );

        return config;
    }

    private static async Task PrepareCatalogAsync( string binary, string fixture, string config, Options options )
    {
        using var process = new Process { StartInfo = CreateStart( binary, fixture, ["reindex", "--config", config, "--quiet", "--json"] ) };
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 120 ) );

        try
        {
            var errors = StartDrained( process );
            await process.WaitForExitAsync( timeout.Token );
            if ( process.ExitCode != 0 )
            {
                throw new InvalidOperationException( $"reindex with {binary} exited with {process.ExitCode}: {Tail( errors )}" );
            }
        }
        catch ( OperationCanceledException ) when ( timeout.IsCancellationRequested )
        {
            throw new InvalidOperationException( $"reindex with {binary} did not finish within 120 s." );
        }
        finally
        {
            await StopAsync( process );
        }
    }

    // Starts the child and drains both output pipes, so a chatty child never
    // blocks on a full pipe. The last error lines are kept for failure messages.
    private static Queue<string> StartDrained( Process process )
    {
        Queue<string> errors = new( );
        process.OutputDataReceived += static ( _, _ ) => { };
        process.ErrorDataReceived += ( _, line ) =>
        {
            if ( line.Data is null )
            {
                return;
            }

            lock ( errors )
            {
                errors.Enqueue( line.Data );
                if ( errors.Count > 10 )
                {
                    errors.Dequeue( );
                }
            }
        };
        process.Start( );
        process.BeginOutputReadLine( );
        process.BeginErrorReadLine( );

        return errors;
    }

    private static string Tail( Queue<string> errors )
    {
        lock ( errors )
        {
            return string.Join( " | ", errors );
        }
    }

    // Milliseconds from process start to the first accepted connection, and to
    // the end of a successful Home search on that connection.
    private static async Task<(double Ready, double Home)> StartOnceAsync( string binary, string fixture, string config, Options options )
    {
        var socketPath = Path.Combine( fixture, "d.sock" );
        File.Delete( socketPath );
        using var process = new Process { StartInfo = CreateStart( binary, fixture, ["daemon", "--config", config, "--socket", socketPath] ) };
        using CancellationTokenSource deadline = new( options.TimeoutMilliseconds );

        try
        {
            var started = Stopwatch.GetTimestamp( );
            var errors = StartDrained( process );
            using var socket = await ConnectAsync( process, socketPath, deadline.Token );
            var ready = Stopwatch.GetElapsedTime( started ).TotalMilliseconds;

            await using NetworkStream stream = new( socket );
            using StreamReader reader = new( stream, Encoding.UTF8 );
            var request = JsonSerializer.Serialize( new { version = CommandDeck.ProtocolLimits.CurrentVersion, requestId = 1, operation = "search", query = "", limit = 50 } );
            await stream.WriteAsync( Encoding.UTF8.GetBytes( request + "\n" ), deadline.Token );

            while ( true )
            {
                var line = await reader.ReadLineAsync( deadline.Token ) ?? throw new InvalidOperationException( "The daemon closed the connection before the Home search finished." );
                var reply = Reply.Classify( line, requestId: 1 );
                if ( reply == ReplyKind.Done )
                {
                    return (ready, Stopwatch.GetElapsedTime( started ).TotalMilliseconds);
                }

                if ( reply == ReplyKind.Failed )
                {
                    throw new InvalidOperationException( $"The Home search failed or was incomplete: {line} {Tail( errors )}" );
                }
            }
        }
        catch ( OperationCanceledException ) when ( deadline.IsCancellationRequested )
        {
            throw new InvalidOperationException( $"No ready daemon and finished Home search within {options.TimeoutMilliseconds} ms." );
        }
        finally
        {
            await StopAsync( process );
        }
    }

    private static async Task<Socket> ConnectAsync( Process process, string socketPath, CancellationToken cancellationToken )
    {
        while ( true )
        {
            if ( process.HasExited )
            {
                throw new InvalidOperationException( $"The daemon exited with {process.ExitCode} before it accepted a connection." );
            }

            Socket socket = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
            try
            {
                await socket.ConnectAsync( new UnixDomainSocketEndPoint( socketPath ), cancellationToken );

                return socket;
            }
            catch ( SocketException )
            {
                socket.Dispose( );
                await Task.Delay( 1, cancellationToken );
            }
        }
    }

    private static async Task StopAsync( Process process )
    {
        try
        {
            if ( process.HasExited )
            {
                return;
            }

            Process.Start( "kill", ["-TERM", process.Id.ToString( CultureInfo.InvariantCulture )] )?.WaitForExit( );
            using CancellationTokenSource grace = new( TimeSpan.FromSeconds( 5 ) );
            await process.WaitForExitAsync( grace.Token );
        }
        catch ( OperationCanceledException )
        {
            process.Kill( entireProcessTree: true );
            await process.WaitForExitAsync( );
        }
        catch ( InvalidOperationException )
        {
            // Never started.
        }
    }

    private static ProcessStartInfo CreateStart( string binary, string fixture, string[] arguments )
    {
        ProcessStartInfo start = new( binary ) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach ( var argument in arguments )
        {
            start.ArgumentList.Add( argument );
        }

        foreach ( var name in new[] { "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME" } )
        {
            var directory = Directory.CreateDirectory( Path.Combine( fixture, name ) ).FullName;
            start.Environment[name] = directory;
        }

        foreach ( var name in SessionVariables )
        {
            start.Environment.Remove( name );
        }

        return start;
    }

    private static string Describe( string label, List<double> samples ) =>
        string.Create( CultureInfo.InvariantCulture,
            $"{label} p50 {NearestRank( samples, 0.50 ):F1} ms  p95 {NearestRank( samples, 0.95 ):F1} ms  " +
            $"min {samples.Min( ):F1} ms  max {samples.Max( ):F1} ms  ({samples.Count} samples)" );

    // Nearest rank: the smallest sample with at least q of the samples at or below it.
    internal static double NearestRank( IReadOnlyList<double> samples, double quantile )
    {
        var sorted = samples.Order( ).ToArray( );
        var rank = ( int ) Math.Ceiling( (quantile * sorted.Length) - 1e-9 );

        return sorted[Math.Max( rank, 1 ) - 1];
    }

    private static int SelfTest( )
    {
        List<string> failures = [];
        void Check( bool condition, string name )
        {
            if ( !condition )
            {
                failures.Add( name );
            }
        }

        Check( Reply.Classify( """{"type":"done","requestId":1,"searchCompletion":{"complete":true,"incompleteProviderCount":0}}""", 1 ) == ReplyKind.Done,
            "complete done for the request" );
        Check( Reply.Classify( """{"type":"done","requestId":1,"searchCompletion":{"complete":false,"incompleteProviderCount":1}}""", 1 ) == ReplyKind.Failed,
            "incomplete done" );
        Check( Reply.Classify( """{"type":"done","requestId":1}""", 1 ) == ReplyKind.Failed, "done without a completion" );
        Check( Reply.Classify( """{"type":"error","requestId":1,"error":"x"}""", 1 ) == ReplyKind.Failed, "error for the request" );
        Check( Reply.Classify( """{"type":"done","requestId":2}""", 1 ) == ReplyKind.Other, "done for another request" );
        Check( Reply.Classify( """{"type":"result","requestId":1}""", 1 ) == ReplyKind.Other, "a result row" );
        Check( Reply.Classify( "not json", 1 ) == ReplyKind.Failed, "malformed reply" );
        Check( NearestRank( [5, 1, 3, 2, 4], 0.95 ) == 5, "p95 of five is the largest" );
        Check( NearestRank( [.. Enumerable.Range( 1, 100 ).Select( static value => ( double ) value )], 0.95 ) == 95, "p95 of 100" );
        Check( NearestRank( [4, 1, 3, 2], 0.50 ) == 2, "median rank" );
        Check( Options.Parse( ["--binary", "a", "--samples", "3"] ).Samples == 3, "samples option" );
        Check( Throws( ( ) => Options.Parse( ["--samples", "3"] ) ), "binary is required" );
        Check( Throws( ( ) => Options.Parse( ["--binary", "a", "--samples", "0"] ) ), "samples range" );

        foreach ( var failure in failures )
        {
            Console.Error.WriteLine( $"FAIL {failure}" );
        }

        Console.WriteLine( failures.Count == 0 ? "self-test passed" : $"{failures.Count} self-test check(s) failed" );

        return failures.Count == 0 ? 0 : 1;

        static bool Throws( Action action )
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
}

enum ReplyKind { Other, Done, Failed }

static class Reply
{
    // The end of the given request's search, its failure, or anything else. A
    // line that is not a protocol object counts as a failure, and so does a
    // search that finished with failed or late providers.
    public static ReplyKind Classify( string line, int requestId )
    {
        try
        {
            using var document = JsonDocument.Parse( line );
            var root = document.RootElement;
            if ( !root.TryGetProperty( "requestId", out var id ) || id.ValueKind != JsonValueKind.Number || id.GetInt32( ) != requestId )
            {
                return ReplyKind.Other;
            }

            return root.TryGetProperty( "type", out var type ) ? type.GetString( ) switch
            {
                "done" => IsComplete( root ) ? ReplyKind.Done : ReplyKind.Failed,
                "error" => ReplyKind.Failed,
                _ => ReplyKind.Other
            } : ReplyKind.Other;
        }
        catch ( JsonException )
        {
            return ReplyKind.Failed;
        }
    }

    private static bool IsComplete( JsonElement done ) =>
        done.TryGetProperty( "searchCompletion", out var completion ) &&
        completion.TryGetProperty( "complete", out var complete ) && complete.ValueKind == JsonValueKind.True &&
        completion.TryGetProperty( "incompleteProviderCount", out var incomplete ) && incomplete.ValueKind == JsonValueKind.Number &&
        incomplete.GetInt32( ) == 0;
}

sealed record Options( string Binary, string? Compare, int Samples, int Directories, int TimeoutMilliseconds )
{
    public static Options Parse( string[] args )
    {
        string? binary = null;
        string? compare = null;
        int samples = 10, directories = 0, timeout = 10_000;

        for ( var index = 0; index < args.Length; index++ )
        {
            var value = index + 1 < args.Length ? args[index + 1] : throw new ArgumentException( $"{args[index]} needs a value." );
            switch ( args[index] )
            {
                case "--binary":
                    binary = Path.GetFullPath( value );
                    break;
                case "--compare":
                    compare = Path.GetFullPath( value );
                    break;
                case "--samples":
                    samples = ParseRange( value, 1, 200, "--samples" );
                    break;
                case "--directories":
                    directories = ParseRange( value, 0, 50_000, "--directories" );
                    break;
                case "--timeout-ms":
                    timeout = ParseRange( value, 100, 60_000, "--timeout-ms" );
                    break;
                default:
                    throw new ArgumentException( $"Unknown option {args[index]}." );
            }

            index++;
        }

        return new( binary ?? throw new ArgumentException( "--binary is required." ), compare, samples, directories, timeout );
    }

    private static int ParseRange( string value, int minimum, int maximum, string name ) =>
        int.TryParse( value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed ) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ArgumentException( $"{name} must be {minimum}-{maximum}." );
}
