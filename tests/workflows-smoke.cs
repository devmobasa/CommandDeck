#!/usr/bin/env dotnet
#:include ../scripts/ScriptExecutables.cs
#:include ../src/CommandDeck.Core/Configuration/ToolExecutables.cs
#:include ../src/CommandDeck.Core/Utilities/EnvironmentVariableNames.cs
#:include ../src/CommandDeck.Core/Utilities/XdgEnvironmentNames.cs
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
#:include ./workflows-smoke.Fixtures.cs
// Exercise a real daemon with isolated desktop fixtures; no user clipboard or apps are accessed.
// Usage: dotnet tests/workflows-smoke.cs /absolute/path/to/commanddeck

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

if ( IsFixtureRequest( args ) )
{
    await WorkflowSmoke.RunFixtureAsync( args[1], args[2..] );

    return;
}

if ( args.Length != 1 )
{
    throw new ArgumentException( "Usage: dotnet tests/workflows-smoke.cs /path/to/commanddeck" );
}

var binary = Path.GetFullPath( args[0] );
var root = Path.Combine( Path.GetTempPath( ), $"cd-workflows-{Guid.NewGuid( ):N}" );
Directory.CreateDirectory( root );

try
{
    await WorkflowSmoke.SmokeAsync( binary, root );
}
finally
{
    Directory.Delete( root, recursive: true );
}

static bool IsFixtureRequest( string[] args )
{
    return args.Length >= 2 && args[0] == "--fixture";
}

static partial class WorkflowSmoke
{
    private const string RootEnvironmentVariable = "COMMANDDECK_SMOKE_ROOT";

    public static async Task SmokeAsync( string binary, string root )
    {
        var (environment, clipboard, opened) = CreateWorkflowEnvironment( root );
        var (project, first, second) = await CreateProjectFixtureAsync( root, environment );
        var config = await WriteWorkflowConfigurationAsync( root, project );
        await using var wayland = await StartWaylandFixtureAsync( environment );
        var socketPath = Path.Combine( root, "daemon.sock" );

        using var daemon = StartProcess( binary, ["daemon", "--config", config, "--socket", socketPath], environment );

        var output = daemon.StandardOutput.ReadToEndAsync( );
        var error = daemon.StandardError.ReadToEndAsync( );

        try
        {
            await WaitForDaemonSocketAsync( daemon, socketPath, error );
            using Socket socket = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
            await socket.ConnectAsync( new UnixDomainSocketEndPoint( socketPath ) );
            using NetworkStream stream = new( socket );
            using StreamReader reader = new( stream, Encoding.UTF8, leaveOpen: true );
            await using StreamWriter writer = new( stream, new UTF8Encoding( false ), leaveOpen: true ) { AutoFlush = true };
            SmokeClient client = new( reader, writer );
            await CheckProjectAsync( client, project );
            await CheckSnippetsAsync( client, clipboard );
            await CheckScriptsAsync( client );
            await CheckClipboardAsync( client, wayland, clipboard );
            await CheckFileSelectionAsync( client, project, first, second, clipboard, opened );

            var picker = await RunProcessAsync( binary, ["pick", "--label-tab"], environment, "a\tAlpha\nb value\tBeta\n" );

            Require( picker.ExitCode == 0 && picker.Output == "b value\n", $"Picker failed: {picker.ExitCode}, {picker.Output}, {picker.Error}" );

            Console.WriteLine( "PASS pipe picker stdout contract and packaged assets" );
        }
        finally
        {
            await StopDaemonAsync( daemon, output, error );
        }
    }

    private static async Task StopDaemonAsync( Process daemon, Task<string> output, Task<string> error )
    {
        if ( !daemon.HasExited )
        {
            using var signal = TerminateDaemon( daemon );
            using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 5 ) );

            try
            {
                await daemon.WaitForExitAsync( timeout.Token );
            }
            catch ( OperationCanceledException )
            {
                daemon.Kill( entireProcessTree: true );
                await daemon.WaitForExitAsync( );
            }
        }

        var log = await output + await error;
        Require( daemon.ExitCode == 0, "Daemon did not shut down gracefully: " + log );
    }

    static bool Succeeded( JsonObject outcome ) => ( bool? ) outcome["succeeded"] == true;
    static string Describe( IEnumerable<JsonObject> messages ) => string.Join( '\n', messages.Select( message => message.ToJsonString( ) ) );

    static void Require( bool condition, string message )
    {
        if ( !condition )
        {
            throw new InvalidOperationException( message );
        }
    }

    static Process StartProcess( string binary, IEnumerable<string> arguments, Dictionary<string, string> environment )
    {
        ProcessStartInfo start = CreateDaemonStartInfo( binary );

        foreach ( var argument in arguments )
        {
            start.ArgumentList.Add( argument );
        }

        foreach ( var (name, value) in environment )
        {
            start.Environment[name] = value;
        }

        var process = Process.Start( start );
        if ( process is null )
        {
            throw new InvalidOperationException( "Could not start " + binary );
        }

        return process;
    }

    static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string binary, IEnumerable<string> arguments, Dictionary<string, string> environment, string? input = null, int timeoutSeconds = 10 )
    {
        using var process = StartProcess( binary, arguments, environment );
        var output = process.StandardOutput.ReadToEndAsync( );
        var error = process.StandardError.ReadToEndAsync( );

        if ( input is not null )
        {
            await process.StandardInput.WriteAsync( input );
        }

        process.StandardInput.Close( );
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( timeoutSeconds ) );

        try
        {
            await process.WaitForExitAsync( timeout.Token );
        }
        catch
        {
            if ( !process.HasExited )
            {
                process.Kill( entireProcessTree: true );
                await process.WaitForExitAsync( );
            }

            throw;
        }

        return (process.ExitCode, await output, await error);
    }

    private sealed class SmokeClient( StreamReader reader, StreamWriter writer )
    {
        private int _requestId;

        public async Task<List<JsonObject>> RequestAsync( string operation, JsonObject? values = null )
        {
            var request = values ?? new JsonObject( );
            request["version"] = 3;
            request["requestId"] = ++_requestId;
            request["operation"] = operation;
            using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 10 ) );

            await writer.WriteLineAsync( request
                .ToJsonString( )
                .AsMemory( ), timeout.Token );

            List<JsonObject> messages = [];

            while ( true )
            {
                var line = await reader.ReadLineAsync( timeout.Token );
                if ( line is null )
                {
                    throw new IOException( "Daemon disconnected" );
                }

                var message = JsonNode.Parse( line ) as JsonObject;
                if ( message is null )
                {
                    throw new IOException( "Daemon returned an invalid message" );
                }

                if ( ( int? ) message["requestId"] != _requestId )
                {
                    continue;
                }

                if ( ( string? ) message["type"] == "done" )
                {
                    return messages;
                }

                messages.Add( message );
            }
        }

        public async Task<List<JsonObject>> SearchAsync( string query, JsonObject? values = null )
        {
            var request = values ?? new JsonObject( );
            request["query"] = query;
            var messages = await RequestAsync( "search", request );
            Require( messages.All( message => ( string? ) message["type"] != "error" ), Describe( messages ) );

            return messages
                .Where( message => ( string? ) message["type"] == "result" )
                .Select( message => ( JsonObject ) message["result"]! )
                .ToList( );
        }

        public async Task<JsonObject> InvokeAsync( JsonObject result, string action, JsonObject? values = null )
        {
            var reference = ( string? ) result["invocationReference"];
            Require( !string.IsNullOrWhiteSpace( reference ), "Search result has no invocation reference" );
            var request = values ?? new JsonObject( );
            request["resultId"] = ( string? ) result["id"];
            request["actionId"] = action;
            request["invocationReference"] = reference;
            var messages = await RequestAsync( "invoke", request );
            var response = messages.FirstOrDefault( message => ( string? ) message["type"] == "action-result" );
            if ( response is null )
            {
                throw new InvalidOperationException( $"Invocation {action} failed: {Describe( messages )}" );
            }

            return ( JsonObject ) response["actionResult"]!;
        }
    }

    private static Dictionary<string, string> CreateFixtureEnvironment( string root, string tools )
    {
        return new( )
        {
            [CommandDeck.EnvironmentVariableNames.ExecutablePath] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable( CommandDeck.EnvironmentVariableNames.ExecutablePath ),
            [CommandDeck.XdgEnvironmentNames.ConfigHome] = Path.Combine( root, "config" ),
            [CommandDeck.XdgEnvironmentNames.StateHome] = Path.Combine( root, "state" ),
            [CommandDeck.XdgEnvironmentNames.CacheHome] = Path.Combine( root, "cache" ),
            [CommandDeck.XdgEnvironmentNames.RuntimeDirectory] = Path.Combine( root, "runtime" ),
            [RootEnvironmentVariable] = root,
            ["COMMANDDECK_HEY"] = "off"
        };
    }

    private static Process? TerminateDaemon( Process daemon )
    {
        return Process.Start( new ProcessStartInfo( "/usr/bin/kill" )
        {
            ArgumentList = { "-TERM", daemon.Id.ToString( ) },
            UseShellExecute = false
        } );
    }

    private static ProcessStartInfo CreateDaemonStartInfo( string binary )
    {
        return new( binary )
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private static async Task<(string Project, string First, string Second)> CreateProjectFixtureAsync( string root, Dictionary<string, string> environment )
    {
        var project = Path.Combine( root, "project checkout" );
        Directory.CreateDirectory( project );
        var git = await RunProcessAsync( "git", ["init", "-b", "workflow-test", project], environment );
        Require( git.ExitCode == 0, git.Error );
        var first = Path.Combine( project, "one file.txt" );
        var second = Path.Combine( project, "two 'quoted'.txt" );
        await File.WriteAllTextAsync( first, "first" );
        await File.WriteAllTextAsync( second, "second" );

        return (project, first, second);
    }

    private static async Task WaitForDaemonSocketAsync( Process daemon, string socketPath, Task<string> error )
    {
        var deadline = Stopwatch.StartNew( );

        while ( !File.Exists( socketPath ) )
        {
            Require( !daemon.HasExited, daemon.HasExited ? await error : "Daemon exited" );
            Require( deadline.Elapsed < TimeSpan.FromSeconds( 15 ), "Daemon did not create its socket" );
            await Task.Delay( 30 );
        }
    }

    private static (Dictionary<string, string> Environment, string Clipboard, string Opened) CreateWorkflowEnvironment( string root )
    {
        var tools = Path.Combine( root, "bin" );
        Directory.CreateDirectory( tools );
        var clipboard = Path.Combine( root, "copied" );
        var opened = Path.Combine( root, "opened" );
        Dictionary<string, string> environment = CreateFixtureEnvironment( root, tools );
        Directory.CreateDirectory( environment[CommandDeck.XdgEnvironmentNames.RuntimeDirectory] );

        foreach ( var name in new[] { CommandDeck.ToolExecutables.ClipboardCopy, CommandDeck.ToolExecutables.DesktopOpener, "uwsm-app", "qs" } )
        {
            WriteFixtureLauncher( Path.Combine( tools, name ), name );
        }

        return (environment, clipboard, opened);
    }

}
