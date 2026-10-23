using System.Diagnostics;
using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task IndexedFolderProcessRestartServesDuringRecoveryAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "The blocked extractor fixture requires Linux." );
        }

        var root = CreateCliSpecRoot( );
        var originalPath = Environment.GetEnvironmentVariable( "PATH" );
        List<Process> processes = [];

        try
        {
            var retained = Directory.CreateDirectory( Path.Combine( root, "retained" ) ).FullName;
            var added = Directory.CreateDirectory( Path.Combine( root, "added" ) ).FullName;
            var bin = Directory.CreateDirectory( Path.Combine( root, "bin" ) ).FullName;
            var started = Path.Combine( root, "extractor-started" );
            var release = Path.Combine( root, "extractor-release" );
            var extractor = Path.Combine( bin, ToolExecutables.PdfToText );

            static string Quote( string value ) => "'" + value.Replace( "'", "'\\''", StringComparison.Ordinal ) + "'";

            await File.WriteAllTextAsync( extractor,
                "#!/bin/sh\n: > " + Quote( started ) + "\nwhile [ ! -f " + Quote( release ) + " ]; do sleep 0.05; done\nprintf 'recoveredcontentsignal'\n" );

            File.SetUnixFileMode( extractor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

            Environment.SetEnvironmentVariable( "PATH", bin + Path.PathSeparator + originalPath );
            await File.WriteAllTextAsync( Path.Combine( retained, "retainedsignal.txt" ), "existing" );
            await File.WriteAllTextAsync( Path.Combine( added, "blocked.pdf" ), "%PDF fixture" );
            var initial = CreateIncrementalIndexOptions( Path.Combine( root, "catalog.db" ), retained );
            var options = CreateOptions( initial );
            var config = Path.Combine( root, "config.json" );
            var socket = SpecTemporaryFiles.SocketPath( root, "restart.sock" );

            await File.WriteAllTextAsync( config, JsonSerializer.Serialize( options, CommandDeckJsonContext.Default.CommandDeckOptions ) );

            await using ( var seed = await CommandDeckRuntime.CreateAsync( config, default ) )
            {
                await seed.Indexer.ReindexAsync( null, default );
            }

            using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 20 ) );
            var daemon = StartCommandDeckProcess( processes, "daemon", "--config", config, "--socket", socket );

            await using ( var client = await ConnectWithRetryAsync( socket ) )
            {
                await client.SendAsync( new ProtocolRequest { RequestId = 1, Operation = ProtocolOperations.IndexAdd, Path = added }, deadline.Token );

                await foreach ( var line in client.ReadRawMessagesAsync( deadline.Token ) )
                {
                    var message = DeserializeMessage( line );
                    True( message?.Type != "error" );

                    if ( message is { RequestId: 1, Type: "done" } )
                    {
                        break;
                    }
                }

                True( await WaitUntilAsync( ( ) => Task.FromResult( File.Exists( started ) ), TimeSpan.FromSeconds( 5 ) ) );
            }

            StopProcess( daemon );
            await using SqliteCatalog persisted = new( options.Storage );
            True( await persisted.RequiresReindexAsync( default ) );
            Equal( added, (await persisted.GetIndexRootsAsync( default )).Added.Single( ).Options.Path );
            File.Delete( started );
            StartCommandDeckProcess( processes, "daemon", "--config", config, "--socket", socket );

            True( await WaitUntilAsync( ( ) => Task.FromResult( File.Exists( started ) ), TimeSpan.FromSeconds( 5 ) ) );

            // Exercise Program's startup path, with extraction still blocked.
            await using var restarted = await ConnectWithRetryAsync( socket );

            await restarted.SendAsync( new ProtocolRequest { RequestId = 2, Operation = ProtocolOperations.Search, Query = "retainedsignal" }, deadline.Token );
            await restarted.SendAsync( new ProtocolRequest { RequestId = 3, Operation = ProtocolOperations.IndexRoots }, deadline.Token );

            HashSet<long> done = [];
            bool searched = false;
            bool listed = false;
            await foreach ( var line in restarted.ReadRawMessagesAsync( deadline.Token ) )
            {
                var message = DeserializeMessage( line );
                if ( message is null )
                {
                    continue;
                }

                searched |= message is { RequestId: 2, Result.Title: "retainedsignal.txt" };

                listed |= message is { RequestId: 3, IndexRoots: { ReindexPending: true } roots } &&
                          roots.Roots.Any( row => row.Path == added && row.Status.StartsWith( "Indexing", StringComparison.Ordinal ) );

                if ( message.Type == "done" )
                {
                    done.Add( message.RequestId );
                }

                if ( done.IsSupersetOf( new long[] { 2, 3 } ) )
                {
                    break;
                }
            }

            True( searched && listed );
            True( !File.Exists( release ) );
            await File.WriteAllTextAsync( release, "release" );

            True( await WaitUntilAsync( async ( ) => !await persisted.RequiresReindexAsync( default ), TimeSpan.FromSeconds( 5 ) ) );

            Equal( 1, (await SearchAsync( persisted, "blocked" )).Count );
        }
        finally
        {
            foreach ( var process in processes )
            {
                StopProcess( process );
                process.Dispose( );
            }

            Environment.SetEnvironmentVariable( "PATH", originalPath );
            Directory.Delete( root, true );
        }

        static CommandDeckOptions CreateOptions( CommandDeckOptions initial )
        {
            return new CommandDeckOptions
            {
                Storage = initial.Storage,
                Privacy = initial.Privacy,
                Indexing = initial.Indexing with { EnablePdfExtraction = true, ExtractorTimeoutMilliseconds = 60_000, EnableScheduledMaintenance = false }
            };
        }
    }
}
