using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditIndexDaemonControlsAsync( )
    {
        var root = CreateCliSpecRoot( );
        try
        {
            var documents = Directory.CreateDirectory( Path.Combine( root, "documents" ) ).FullName;
            var note = Path.Combine( documents, "note.txt" );
            await File.WriteAllTextAsync( note, "beforepausemarker" );
            var config = Path.Combine( root, "config.json" );
            var options = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), documents );
            await File.WriteAllTextAsync( config, JsonSerializer.Serialize( options, CommandDeckJsonContext.Default.CommandDeckOptions ) );
            var localConfig = Directory.CreateDirectory( Path.Combine( root, "local-config", "commanddeck" ) ).FullName;
            File.Copy( config, Path.Combine( localConfig, "commanddeck.jsonc" ) );
            var socketDirectory = Path.GetDirectoryName( SpecTemporaryFiles.SocketPath( root, "daemon.sock" ) )!;
            var localEnvironment = new Dictionary<string, string?>
            {
                ["XDG_CONFIG_HOME"] = Path.GetDirectoryName( localConfig ),
                ["XDG_RUNTIME_DIR"] = socketDirectory
            };
            var localPause = await RunCommandDeckWithEnvironmentAsync( localEnvironment, "index", "pause" );
            True( localPause.ExitCode == 0, localPause.StandardError );
            True( localPause.StandardOutput.Contains( "paused=True", StringComparison.Ordinal ) );
            Equal( 0, (await RunCommandDeckWithEnvironmentAsync( localEnvironment, "index", "resume" )).ExitCode );
            await using var runtime = await CommandDeckRuntime.CreateAsync( config, default );
            await runtime.Indexer.ReindexAsync( null, default );
            using CancellationTokenSource stop = new( TimeSpan.FromSeconds( 30 ) );
            var socket = Path.Combine( socketDirectory, "commanddeck.sock" );
            UnixProtocolServer server = new( runtime, socket );
            var serving = server.RunAsync( stop.Token );
            try
            {
                await using ( await ConnectWithRetryAsync( socket ) )
                {
                }

                var paused = await RunCommandDeckProcessAsync( "index", "pause", "--config", config, "--socket", socket );
                Equal( 0, paused.ExitCode );
                True( (await runtime.Indexer.GetIncrementalStatusAsync( default )).Roots.Single( ).Paused );
                var daemonEnvironment = new Dictionary<string, string?> { ["XDG_RUNTIME_DIR"] = socketDirectory };
                var status = await RunCommandDeckWithEnvironmentAsync( daemonEnvironment, "index", "status" );
                Equal( 0, status.ExitCode );
                True( status.StandardOutput.Contains( "paused=True watched=True", StringComparison.Ordinal ) );
                True( !status.StandardOutput.Contains( "owned by another", StringComparison.Ordinal ) );
                await File.WriteAllTextAsync( note, "afterresumemarker" );
                await Task.Delay( 400 );
                Equal( 0, (await SearchAsync( runtime.Catalog, "afterresumemarker" )).Count );
                True( (await SearchAsync( runtime.Catalog, "beforepausemarker" )).Count > 0 );
                var resumed = await RunCommandDeckProcessAsync( "index", "resume", "--config", config, "--socket", socket );
                Equal( 0, resumed.ExitCode );
                var timeout = TimeSpan.FromSeconds( 5 );
                True( await WaitForCatalogResultAsync( runtime.Catalog, "afterresumemarker", result => result.Title == "note.txt", timeout ) );
                True( !(await runtime.Indexer.GetIncrementalStatusAsync( default )).Roots.Single( ).Paused );
            }
            finally
            {
                stop.Cancel( );
                await serving;
            }
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
