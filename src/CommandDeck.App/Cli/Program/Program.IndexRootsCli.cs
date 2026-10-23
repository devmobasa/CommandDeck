using System.Net.Sockets;
using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const string RootsCommand = "roots";
    private const string AddCommand = "add";
    private const string DisableCommand = "disable";
    private const string EnableCommand = "enable";
    private const string ImportCommand = "import";
    private const int FileStreamBufferBytes = 4096;
    private const string IndexRootsKey = "indexRoots";

    private static bool IsIndexRootsCommand( CliArguments arguments )
    {
        var values = arguments.PositionalsAfterCommand( );

        return values.Count > 0 && values[0] is RootsCommand or AddCommand or RemoveCommand or DisableCommand or EnableCommand or ImportCommand;
    }

    private static async Task<int> RunIndexRootsCliAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        var values = arguments.PositionalsAfterCommand( );

        var operation = values[0] switch
        {
            RootsCommand => ProtocolOperations.IndexRoots,
            AddCommand => ProtocolOperations.IndexAdd,
            RemoveCommand => ProtocolOperations.IndexRemove,
            DisableCommand => ProtocolOperations.IndexDisable,
            EnableCommand => ProtocolOperations.IndexEnable,
            ImportCommand => ProtocolOperations.IndexRootsImport,
            _ => throw new InvalidOperationException( "Unknown index roots command." )
        };

        if ( values.Count != (operation == ProtocolOperations.IndexRoots ? 1 : 2) )
        {
            Console.Error.WriteLine( "Use index roots, add <path>, remove/disable/enable <root-id>, or import <export-file>." );

            return 2;
        }

        IndexRootPreferences? import = null;
        if ( operation == ProtocolOperations.IndexRootsImport )
        {
            import = await ReadIndexRootsImportAsync( values[1], cancellationToken )
                .ConfigureAwait( false );
        }

        var request = new ProtocolRequest
        {
            RequestId = InitialRequestId,
            Operation = operation,
            Path = operation == ProtocolOperations.IndexAdd ? IndexRootPaths.Normalize( values[1] ) : null,
            RootId = operation is ProtocolOperations.IndexRemove or ProtocolOperations.IndexDisable or ProtocolOperations.IndexEnable ? values[1] : null,
            IndexRoots = import
        };

        var client = await ConnectPreferenceDaemonAsync( arguments, cancellationToken )
            .ConfigureAwait( false );

        IndexRootsResult? result = null;
        if ( client is not null )
        {
            await using ( client )
            {
                await client
                    .SendAsync( request, cancellationToken )
                    .ConfigureAwait( false );

                await foreach ( var line in client
                                   .ReadRawMessagesAsync( cancellationToken )
                                   .ConfigureAwait( false ) )
                {
                    var message = JsonSerializer.Deserialize( line, CommandDeckJsonContext.Default.ProtocolMessage );
                    if ( message?.Type == ProtocolMessageTypes.Error )
                    {
                        throw new InvalidOperationException( message.Error );
                    }

                    result ??= message?.IndexRoots;

                    if ( message?.Type == ProtocolMessageTypes.Done )
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            await using var runtime = await CommandDeckRuntime
                .CreateAsync( arguments.GetOption( CliOptionNames.Config ), new RuntimeConstructionOptions( ), includeClipboardHistory: false,
                    cancellationToken )
                .ConfigureAwait( false );

            using var shutdown = cancellationToken.Register( runtime.CancelBackgroundIndexing );

            result = await runtime
                .Indexer.ManageRootsAsync( operation, request.Path, request.RootId, import, cancellationToken )
                .ConfigureAwait( false );

            await (( IndexCoordinator ) runtime.Indexer)
                .WaitForBackgroundReindexAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        if ( result is null )
        {
            throw new InvalidOperationException( "The daemon did not return indexed folders." );
        }

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( result, CommandDeckJsonContext.Default.IndexRootsResult ) );

            return 0;
        }

        foreach ( var root in result.Roots )
        {
            Console.WriteLine( $"{root.Id}  source={root.Source} enabled={root.Enabled}  " + PreferencesTextFormatter.EscapeTerminalText( root.Path ) );
            Console.WriteLine( "  " + PreferencesTextFormatter.EscapeTerminalText( root.Issue ?? root.Status ) );
        }

        return 0;
    }

    private static async ValueTask<IndexRootPreferences> ReadIndexRootsImportAsync( string path, CancellationToken cancellationToken )
    {
        await using FileStream stream = new( PathExpansion.Expand( path ), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: FileStreamBufferBytes,
            FileOptions.Asynchronous );

        var buffer = new byte[ProtocolLimits.MaximumCustomizationExportBytes + 1];
        var count = 0;
        while ( count < buffer.Length )
        {
            var read = await stream
                .ReadAsync( buffer.AsMemory( count ), cancellationToken )
                .ConfigureAwait( false );

            if ( read == 0 )
            {
                break;
            }

            count += read;
        }

        if ( count > ProtocolLimits.MaximumCustomizationExportBytes )
        {
            throw new InvalidOperationException( "Customization import exceeds 256 KiB." );
        }

        using var document = JsonDocument.Parse( buffer.AsMemory( 0, count ) );
        if ( !document.RootElement.TryGetProperty( IndexRootsKey, out var roots ) )
        {
            throw new InvalidOperationException( "Customization export must contain indexRoots." );
        }

        var preferences = JsonSerializer.Deserialize( roots, CommandDeckJsonContext.Default.IndexRootPreferences );
        if ( preferences is null )
        {
            throw new InvalidOperationException( "Indexed folder preferences are required." );
        }

        return preferences;
    }

    private static async ValueTask<UnixProtocolClient?> ConnectPreferenceDaemonAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        UnixProtocolClient? client = null;
        if ( UsesExplicitPreferenceDaemon( arguments ) )
        {
            try
            {
                client = await UnixProtocolClient
                    .ConnectAsync( arguments.GetOption( CliOptionNames.Socket ), cancellationToken )
                    .ConfigureAwait( false );
            }
            catch ( SocketException exception ) when ( arguments.GetOption( CliOptionNames.Socket ) is null &&
                                                       exception.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable )
            {
                // No daemon: the local runtime takes the same index journal lease.
            }
        }

        return client;
    }

    private static bool UsesExplicitPreferenceDaemon( CliArguments arguments )
    {
        return arguments.GetOption( CliOptionNames.Config ) is null || arguments.GetOption( CliOptionNames.Socket ) is not null;
    }

    private static async Task<int> RunReindexAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var quiet = arguments.HasFlag( CliOptionNames.Quiet );
        Progress<IndexProgress>? progress;
        if ( quiet )
        {
            progress = null;
        }
        else
        {
            progress = new Progress<IndexProgress>( value =>
            {
                if ( value.Stage is IndexProgressStages.ScanFiles or IndexProgressStages.Write )
                {
                    Console.Error.Write( $"\r{value.Stage,-12} indexed={value.Indexed,8} " + $"skipped={value.Skipped,8} failed={value.Failed,6}" );
                }
            } );
        }

        var scopeKind = arguments.GetOption( CliOptionNames.Scope ) ?? (GetReindexScopeKind( arguments ));

        var report = await runtime
            .Indexer.ReindexAsync(
                new IndexReindexScope
                {
                    Kind = scopeKind,
                    RootId = arguments.GetOption( CliOptionNames.Root ),
                    Path = arguments.GetOption( CliOptionNames.Path )
                }, progress, cancellationToken )
            .ConfigureAwait( false );

        if ( !quiet )
        {
            Console.Error.WriteLine( );
        }

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( ProtocolJson.SerializeReport( report ) );
        }
        else
        {
            var elapsed = report.CompletedAt - report.StartedAt;

            Console.WriteLine( $"Indexed {report.Indexed} items; skipped {report.Skipped}; " + $"failed {report.Failed}; elapsed {elapsed.TotalSeconds:F2}s." );
        }

        return report.Failed > 0 ? 2 : 0;
    }

    private static async Task<int> RunIndexControlAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var positionals = arguments.PositionalsAfterCommand( );
        var action = ReadCliAction( positionals, 0, StatusCommand );
        var rootId = arguments.GetOption( CliOptionNames.Root );

        switch ( action )
        {
            case PauseCommand:
                await runtime
                    .Indexer.PauseAsync( rootId, cancellationToken )
                    .ConfigureAwait( false );

                break;
            case ResumeCommand:
                await runtime
                    .Indexer.ResumeAsync( rootId, cancellationToken )
                    .ConfigureAwait( false );

                break;
            case StatusCommand:
                break;
            default:
                Console.Error.WriteLine( "Use 'index status', 'index pause', or 'index resume'." );

                return 2;
        }

        var status = await runtime
            .Indexer.GetIncrementalStatusAsync( cancellationToken )
            .ConfigureAwait( false );

        foreach ( var root in status.Roots )
        {
            Console.WriteLine( $"{root.Id}  paused={root.Paused} watched={root.Watched} " +
                               $"pending={root.PendingEventCount} indexed={root.IndexedItemCount}  {root.Path}" );

            if ( !string.IsNullOrWhiteSpace( root.LastError ) )
            {
                Console.WriteLine( $"  Error: {root.LastError}" );
                Console.WriteLine( $"  Next: {root.Remediation}" );
            }
        }

        var maintenance = status.Maintenance;

        Console.WriteLine( $"maintenance enabled={maintenance.Enabled} running={maintenance.Running} " +
                           $"next={maintenance.NextRunAt?.ToString( TimestampFormat, System.Globalization.CultureInfo.InvariantCulture ) ?? NoneValue}" );

        if ( !string.IsNullOrWhiteSpace( maintenance.LastError ) )
        {
            Console.WriteLine( $"  Maintenance error: {maintenance.LastError}" );
        }

        return 0;
    }

    private static string GetReindexScopeKind( CliArguments arguments )
    {
        if ( arguments.GetOption( CliOptionNames.Root ) is null )
        {
            if ( arguments.GetOption( CliOptionNames.Path ) is null )
            {
                return IndexReindexScopeKinds.Full;
            }
            else
            {
                if ( Directory.Exists( PathExpansion.Expand( arguments.GetOption( CliOptionNames.Path )! ) ) )
                {
                    return IndexReindexScopeKinds.Directory;
                }
                else
                {
                    return IndexReindexScopeKinds.File;
                }
            }
        }
        else
        {
            return IndexReindexScopeKinds.Root;
        }
    }
}
