using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

static partial class WorkflowSmoke
{
    private static readonly string[] EchoArguments = ["%s", "{value}"];

    private static async Task<string> WriteWorkflowConfigurationAsync( string root, string project )
    {
        var scripts = Path.Combine( root, "scripts" );
        Directory.CreateDirectory( scripts );

        await File.WriteAllTextAsync( Path.Combine( scripts, "echo.commanddeck.json" ), JsonSerializer.Serialize( new
        {
            title = "Echo smoke argument",
            description = "Isolated argv fixture",
            executable = "/usr/bin/printf",
            arguments = EchoArguments,
            inputs = new[] { new { name = "value" } }
        } ) );

        var config = Path.Combine( root, "config.json" );

        await File.WriteAllTextAsync( config, JsonSerializer.Serialize( new
        {
            storage = new { databasePath = Path.Combine( root, "catalog.db" ) },
            indexing = new { roots = Array.Empty<string>( ), indexDesktopApplications = false, indexOmarchyCommands = false },
            privacy = new { storeUsageHistory = false, storeQueryHistory = false, neverIndex = Array.Empty<string>( ) },
            ai = new { enabled = false, providers = Array.Empty<string>( ) },
            agent = new { enabled = false },
            projects = new[] { new { title = "Workflow project", path = project } },
            snippets = new[] { new { id = "hello", title = "Hello smoke", text = "Hello {name}" } },
            scriptDirectories = new[] { scripts }
        } ) );

        return config;
    }

    private static async Task CheckProjectAsync( SmokeClient client, string project )
    {
        var row = (await client.SearchAsync( "proj: Workflow" ))[0];

        Require( ( string? ) row["subtitle"] == project && (( string? ) row["detailMarkdown"])!.Contains( "workflow-test", StringComparison.Ordinal ), "Project checkout and Git status" );

        Console.WriteLine( "PASS project checkout and Git status" );
    }

    private static async Task CheckSnippetsAsync( SmokeClient client, string clipboard )
    {
        var row = (await client.SearchAsync( "snip: Hello" ))[0];

        var outcome = await client.InvokeAsync( row, "snippet-preview", new JsonObject { ["inputValues"] = new JsonObject { ["name"] = "Ada {date}" } } );

        Require( Succeeded( outcome ) && ( string? ) outcome["detailMarkdown"] == "Hello Ada {date}", "Snippet preview" );
        Require( Succeeded( await client.InvokeAsync( row, "snippet-copy", new JsonObject { ["inputValues"] = new JsonObject { ["name"] = "Ada" } } ) ), "Snippet copy" );
        Require( await File.ReadAllTextAsync( clipboard ) == "Hello Ada", "Snippet clipboard contents" );
        Console.WriteLine( "PASS snippet inputs, preview, and copy" );
    }

    private static async Task CheckScriptsAsync( SmokeClient client )
    {
        var row = (await client.SearchAsync( "script: Echo" ))[0];

        Require( !Succeeded( await client.InvokeAsync( row, "run", new JsonObject { ["inputValues"] = new JsonObject { ["value"] = "blocked" } } ) ), "Script requires confirmation" );

        var outcome = await client.InvokeAsync( row, "run", new JsonObject { ["confirmed"] = true, ["inputValues"] = new JsonObject { ["value"] = "literal $(not-a-command)" } } );

        Require( Succeeded( outcome ) && ( string? ) outcome["detailMarkdown"] == "literal $(not-a-command)", "Literal script output" );

        Console.WriteLine( "PASS confirmed scripts and literal output" );
    }

    private static async Task CheckClipboardAsync( SmokeClient client, WaylandFixture wayland, string clipboard )
    {
        var entry = await CaptureClipboardEntryAsync( client, wayland );
        Require( Succeeded( await client.InvokeAsync( entry, "pin" ) ), "Pin clipboard entry" );
        Require( Succeeded( await client.InvokeAsync( entry, "copy" ) ), "Copy clipboard entry" );
        Require( await File.ReadAllTextAsync( clipboard ) == "synthetic\r\n😀é\n", "Native clipboard capture and copy contents" );

        var clipboardRows = await client.SearchAsync( "clip:" );
        var controls = clipboardRows.First( value => ( string? ) value["id"] == "clipboard:controls" );
        entry = clipboardRows.First( value => ( string? ) value["id"] == ( string? ) entry["id"] );
        Require( Succeeded( await client.InvokeAsync( controls, "disable" ) ), "Pause clipboard capture" );
        var outcome = await client.InvokeAsync( entry, "delete" );
        Require( Succeeded( outcome ), "Delete clipboard entry: " + outcome.ToJsonString( ) );
        Require( !Succeeded( await client.InvokeAsync( entry, "copy" ) ), "Deleted clipboard entry is unavailable" );

        Console.WriteLine( "PASS opt-in clipboard capture, pin, copy, pause, and deletion" );
    }

    private static async Task<JsonObject> CaptureClipboardEntryAsync( SmokeClient client, WaylandFixture wayland )
    {
        var controls = (await client.SearchAsync( "clip:" )).First( value => ( string? ) value["id"] == "clipboard:controls" );

        Require( (( string? ) controls["title"])!.Contains( "off", StringComparison.Ordinal ), "Clipboard starts off" );
        Require( Succeeded( await client.InvokeAsync( controls, "enable", new JsonObject { ["confirmed"] = true } ) ), "Enable clipboard capture" );

        var workflowDeadline = Stopwatch.StartNew( );

        do
        {
            controls = (await client.SearchAsync( "clip:" )).First( value => ( string? ) value["id"] == "clipboard:controls" );

            Require( workflowDeadline.Elapsed < TimeSpan.FromSeconds( 5 ), "Native clipboard capture did not start: " + controls.ToJsonString( ) );

            await Task.Delay( 50 );
        } while ( ( string? ) controls["subtitle"] != "Capturing text" );

        Require( await wayland.CommandAsync( "raw" ) == "sent", "Synthetic clipboard selection" );
        workflowDeadline.Restart( );
        List<JsonObject> entries;

        do
        {
            entries = (await client.SearchAsync( "clip: synthetic" ))
                .Where( value => ( string? ) value["id"] != "clipboard:controls" )
                .ToList( );

            Require( workflowDeadline.Elapsed < TimeSpan.FromSeconds( 5 ), "Clipboard capture timed out" );

            if ( entries.Count == 0 )
            {
                await Task.Delay( 100 );
            }
        } while ( entries.Count == 0 );

        return entries[0];
    }

    private static async Task CheckFileSelectionAsync( SmokeClient client, string project, string first, string second, string clipboard, string opened )
    {
        var rows = await client.SearchAsync( "browse:", new JsonObject { ["browseDirectory"] = project } );

        var files = rows
            .Where( value => new[] { first, second }.Contains( ( string? ) value["metadata"]?["path"] ) )
            .ToList( );

        Require( files.Count == 2, "Folder browsing returns both files" );
        List<JsonObject> selected = [];

        foreach ( var file in files )
        {
            selected = await client.RequestAsync( "files.select", new JsonObject { ["resultId"] = ( string? ) file["id"], ["enabled"] = true } );

            Require( selected.Any( message => ( string? ) message["type"] == "file-selection" ), "Select file" );
        }

        Require( selected[0]["fileSelection"]!.AsArray( ).Count == 2, "Retain both selections" );
        Require( Succeeded( (await client.RequestAsync( "files.batch", new JsonObject { ["actionId"] = "copy-paths" } ))[0]["actionResult"]!.AsObject( ) ), "Copy selected paths" );
        Require( new HashSet<string>( await File.ReadAllLinesAsync( clipboard ) ).SetEquals( [first, second] ), "Literal selected paths" );
        Require( Succeeded( (await client.RequestAsync( "files.batch", new JsonObject { ["actionId"] = "open" } ))[0]["actionResult"]!.AsObject( ) ), "Open selected files" );

        Require( (await File.ReadAllLinesAsync( opened ))
            .Select( line => ( string ) JsonNode.Parse( line )![0]! )
            .ToHashSet( )
            .SetEquals( [first, second] ), "Opened file arguments" );

        await client.RequestAsync( "files.clear" );

        Require( !Succeeded( (await client.RequestAsync( "files.batch", new JsonObject { ["actionId"] = "open" } ))[0]["actionResult"]!.AsObject( ) ), "Cleared selection cannot open files" );
        Require( ( string? ) (await client.RequestAsync( "files.select", new JsonObject { ["resultId"] = "forged", ["enabled"] = true } ))[0]["type"] == "error", "Reject forged selection" );

        Console.WriteLine( "PASS folder browsing and retained multi-file actions over the socket" );
    }

    static void WriteFixtureLauncher( string path, string name )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new PlatformNotSupportedException( "Workflow smoke fixtures require Linux." );
        }

        var executable = Environment.ProcessPath;
        if ( executable is null )
        {
            throw new InvalidOperationException( "The smoke fixture executable is unavailable" );
        }

        static string Quote( string value ) => "'" + value.Replace( "'", "'\"'\"'", StringComparison.Ordinal ) + "'";

        var command = Quote( executable );

        if ( Path.GetFileNameWithoutExtension( executable ) == ScriptExecutables.Dotnet )
        {
            command += " " + Quote( Assembly.GetExecutingAssembly( ).Location );
        }

        File.WriteAllText( path, $"#!/bin/sh\nexec {command} --fixture {Quote( name )} \"$@\"\n" );

        File.SetUnixFileMode( path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
    }

    public static async Task RunFixtureAsync( string name, string[] arguments )
    {
        var root = Environment.GetEnvironmentVariable( RootEnvironmentVariable );
        if ( root is null )
        {
            throw new InvalidOperationException( "The smoke fixture directory is unavailable" );
        }

        switch ( name )
        {
            case CommandDeck.ToolExecutables.ClipboardCopy:
                await using ( var file = File.Create( Path.Combine( root, "copied" ) ) )
                {
                    await Console
                        .OpenStandardInput( )
                        .CopyToAsync( file );
                }

                break;
            case CommandDeck.ToolExecutables.DesktopOpener:
                await File.AppendAllTextAsync( Path.Combine( root, "opened" ), JsonSerializer.Serialize( arguments ) + "\n" );

                break;
            case "qs":
                var pickerOutput = Environment.GetEnvironmentVariable( "COMMANDDECK_PICKER_OUTPUT" );

                if ( pickerOutput is null )
                {
                    throw new InvalidOperationException( "The picker fixture output is unavailable" );
                }

                await File.WriteAllTextAsync( pickerOutput, "1" );
                Console.WriteLine( "ignored UI log" );

                break;
            case "uwsm-app":
                var separator = Array.IndexOf( arguments, "--" );
                Require( separator >= 0 && separator + 1 < arguments.Length, "Invalid uwsm-app fixture arguments" );
                ProcessStartInfo start = new( arguments[separator + 1] ) { UseShellExecute = false };

                foreach ( var argument in arguments[(separator + 2)..] )
                {
                    start.ArgumentList.Add( argument );
                }

                using ( var child = Process.Start( start ) )
                {
                    if ( child is null )
                    {
                        throw new InvalidOperationException( "Could not start the desktop fixture" );
                    }

                    await child.WaitForExitAsync( );
                    Environment.ExitCode = child.ExitCode;
                }

                break;
            default:
                throw new ArgumentException( "Unknown fixture: " + name );
        }
    }

    private static async Task<WaylandFixture> StartWaylandFixtureAsync(
        Dictionary<string, string> environment, [CallerFilePath] string source = "" )
    {
        var project = Path.Combine( Path.GetDirectoryName( source )!, "CommandDeck.Specs", "CommandDeck.Specs.csproj" );

        var build = await RunProcessAsync( ScriptExecutables.Dotnet, ["build", project, "-c", "Release", "--nologo", "--verbosity", "quiet"],
            environment, timeoutSeconds: 120 );

        Require( build.ExitCode == 0, "Synthetic Wayland fixture build failed: " + build.Output + build.Error );

        var binary = Path.Combine( Path.GetDirectoryName( project )!, "bin", "Release", "net11.0", "linux-x64", "CommandDeck.Specs" );

        WaylandFixture fixture = new( StartProcess( binary, ["--clipboard-wayland-server"], environment ) );

        try
        {
            var name = await fixture.ReadAsync( );

            Require( name.StartsWith( "wayland-", StringComparison.Ordinal ), "Synthetic Wayland server did not start: " + name );

            environment[CommandDeck.EnvironmentVariableNames.WaylandDisplay] = Path.Combine( environment[CommandDeck.XdgEnvironmentNames.RuntimeDirectory], name );

            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync( );

            throw;
        }
    }

    private sealed class WaylandFixture( Process process ) : IAsyncDisposable
    {
        private readonly Task<string> _error = process.StandardError.ReadToEndAsync( );

        public async Task<string> ReadAsync( )
        {
            using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 5 ) );
            var line = await process.StandardOutput.ReadLineAsync( timeout.Token );
            if ( line is null )
            {
                throw new IOException( "Synthetic Wayland server disconnected: " + await _error );
            }

            return line;
        }

        public async Task<string> CommandAsync( string command )
        {
            await process.StandardInput.WriteLineAsync( command );

            return await ReadAsync( );
        }

        public async ValueTask DisposeAsync( )
        {
            process.StandardInput.Close( );
            using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 5 ) );

            try
            {
                await process.WaitForExitAsync( timeout.Token );
            }
            catch ( OperationCanceledException )
            {
                process.Kill( entireProcessTree: true );
                await process.WaitForExitAsync( );
            }

            var exitCode = process.ExitCode;
            var error = await _error;
            process.Dispose( );
            Require( exitCode == 0, "Synthetic Wayland server failed: " + error );
        }
    }
}
