using System.Diagnostics;
using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditDetachedEditorsAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-detached-editor-" ).FullName;
        var previousPath = Environment.GetEnvironmentVariable( "PATH" );
        var previousVisual = Environment.GetEnvironmentVariable( "VISUAL" );
        var capture = Path.Combine( root, "editor-argv" );
        var launchCapture = Path.Combine( root, "launch-argv" );
        var pidPath = Path.Combine( root, "editor-pid" );
        try
        {
            var editor = Path.Combine( root, "editor" );
            await WriteExecutableAsync( editor, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{capture}'\nexec /bin/sleep 30\n" );
            await WriteExecutableAsync( Path.Combine( root, "xdg-terminal-exec" ), "#!/bin/sh\nshift\nexec \"$@\"\n" );
            await WriteExecutableAsync( Path.Combine( root, "uwsm-app" ), $$"""
                #!/usr/bin/python3
                import json, pathlib, subprocess, sys
                args = sys.argv[1:]
                pathlib.Path({{JsonSerializer.Serialize( launchCapture )}}).write_text(json.dumps(args))
                command = args[args.index('--') + 1:]
                command = [command[0]] + [arg.replace('$$', '$') for arg in command[1:]]
                process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                           stderr=subprocess.DEVNULL, start_new_session=True)
                pathlib.Path({{JsonSerializer.Serialize( pidPath )}}).write_text(str(process.pid))
                """ );
            Environment.SetEnvironmentVariable( "PATH", root );
            Environment.SetEnvironmentVariable( "VISUAL", editor );
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );
            await catalog.InitializeAsync( default );
            var path = Path.Combine( root, "notes $HOME with spaces.txt" );
            await File.WriteAllTextAsync( path, "notes" );
            var action = new CommandAction
            {
                Id = "edit",
                Title = "Edit",
                Kind = "edit-at-line",
                Arguments = new EditorActionPayload( path, 12, 3, "key $literal" ).ToArguments( )
            };
            foreach ( var mode in new[] { "graphical", "terminal", "environment" } )
            {
                var adapter = new EditorAdapter { Executable = editor, Arguments = ["{path}", "{line}", "{column}", "{key}"] };
                var terminal = new EditorAdapter { Executable = editor, Arguments = adapter.Arguments, Terminal = true };
                var defaults = LauncherPreferences.CreateDefault( );
                await catalog.ReplacePreferencesAsync( new LauncherPreferences
                {
                    Sources = defaults.Sources,
                    PreferredEditor = mode == "terminal" ? "terminal" : "graphical",
                    GraphicalEditor = adapter,
                    TerminalEditor = terminal
                }, default );
                LinuxActionExecutor executor = new( mode == "environment" ? null : catalog );
                using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 2 ) );
                try
                {
                    var result = await executor.ExecuteAsync( CacheResult( "editor" ), action, "", deadline.Token );
                    True( result.Succeeded, result.Message );
                    True( await WaitUntilAsync( ( ) => Task.FromResult( File.Exists( capture ) ), TimeSpan.FromSeconds( 2 ) ) );
                    var launcher = JsonSerializer.Deserialize<string[]>( await File.ReadAllTextAsync( launchCapture ) )!;
                    var prefix = mode == "terminal" ? new[] { "-t", "service", "-S", "both", "-T", "--", editor }
                        : ["-t", "service", "-S", "both", "--", editor];
                    var arguments = mode == "environment" ? new[] { "+12", path } : [path, "12", "3", "key $literal"];
                    True( launcher.SequenceEqual( prefix.Concat( arguments.Select( word => word.Replace( "$", "$$", StringComparison.Ordinal ) ) ) ) );
                    var captured = await File.ReadAllLinesAsync( capture );
                    True( arguments.SequenceEqual( captured ) );
                    deadline.Cancel( );
                    var pid = int.Parse( await File.ReadAllTextAsync( pidPath ), System.Globalization.CultureInfo.InvariantCulture );
                    using var child = Process.GetProcessById( pid );
                    True( !child.HasExited, "the detached editor survives invocation cancellation" );
                }
                finally
                {
                    deadline.Cancel( );
                    if ( File.Exists( pidPath ) )
                    {
                        var pid = int.Parse( await File.ReadAllTextAsync( pidPath ), System.Globalization.CultureInfo.InvariantCulture );
                        using var child = Process.GetProcessById( pid );
                        child.Kill( );
                        File.Delete( pidPath );
                    }

                    File.Delete( capture );
                    File.Delete( launchCapture );
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable( "PATH", previousPath );
            Environment.SetEnvironmentVariable( "VISUAL", previousVisual );
            Directory.Delete( root, recursive: true );
        }
    }
}
