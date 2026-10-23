using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const string RemovedState = "removed";

    private static async Task<int> RunExtensionsCliAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        var values = arguments.PositionalsAfterCommand( );
        var action = values.Count > 0 ? values[0] : ExtensionManagementActions.List;
        if ( action == CliArguments.HelpCommand )
        {
            PrintExtensionsHelp( );

            return 0;
        }

        var key = values.Count > 1 ? values[1] : null;
        Dictionary<string, JsonElement>? settings = null;
        if ( action == ExtensionManagementActions.Configure )
        {
            settings = await ReadExtensionSettingsFileAsync( arguments, cancellationToken )
                .ConfigureAwait( false );
        }

        ExtensionConnectionSetup? connection = null;
        if ( action == ExtensionManagementActions.ConnectionSet )
        {
            connection = await ReadExtensionConnectionAsync( arguments, cancellationToken )
                .ConfigureAwait( false );
        }

        long? settingsRevision = action == ExtensionManagementActions.Configure ? ReadExtensionSettingsRevision( arguments ) : null;

        if ( RequiresExtensionManagementConfirmation( arguments, action ) )
        {
            throw new InvalidOperationException( "Review the extension and repeat with --yes to confirm this change." );
        }

        await using var client = await UnixProtocolClient
            .ConnectAsync( arguments.GetOption( CliOptionNames.Socket ), cancellationToken )
            .ConfigureAwait( false );

        var current = await RequestExtensionInventoryAsync( client,
                new ProtocolRequest
                {
                    RequestId = InitialRequestId,
                    Operation = ProtocolOperations.ExtensionsManage,
                    Extension = new( ),
                    Limit = ExtensionInventoryProjector.MaximumInventoryPageSize
                }, cancellationToken )
            .ConfigureAwait( false );

        var request = CreateExtensionManagementRequest( arguments, action, key, settingsRevision ?? current.Revision, settings, connection );

        var result = await RequestExtensionInventoryAsync( client, request, cancellationToken )
            .ConfigureAwait( false );

        WriteExtensionInventory( result, arguments.HasFlag( CliOptionNames.Json ) );

        return 0;
    }

    private static void PrintExtensionsHelp( )
    {
        Console.WriteLine(
            "extensions list|inspect|install|attach|logs|debug|debug-stop|enable|disable|remove|rollback|grants|configure [package-id] [--archive path] [--digest sha256] [--allow capability,...] [--yes] [--json]" );

        Console.WriteLine(
            "Debug: development packages only; --command ID --digest SHA256 --seconds 1..300 --yes. Open that command, then connect GDB to the returned socket. debug-stop revokes the lease. logs --include-stderr explicitly includes untrusted child text that may contain sensitive data." );

        Console.WriteLine(
            "Configure: --settings-file FILE.json --expected-revision REVISION. Inspect first; the file replaces non-secret settings. An empty object restores declared defaults." );

        Console.WriteLine(
            "Connections: connections|connection-set|connection-remove|connection-login PACKAGE. Set uses --connection-file FILE.json --yes; remove/login use --connection NAME --yes. Remove optionally uses --remote-revoke. Credentials and roots last only for this daemon session." );

        Console.WriteLine(
            "effects-reset PACKAGE --yes [--include-legacy] clears remote-write history and permits previously used request keys to be sent again. --include-legacy also clears unattributed legacy history for all packages." );

        Console.WriteLine(
            "Inspect an archive, then install its reviewed digest with --yes. New packages are disabled. Enable the package and the master switch separately. Omit package-id only for the master enable or disable switch." );
    }

    private static ProtocolRequest CreateExtensionManagementRequest( CliArguments arguments, string action, string? key, long expectedRevision,
        Dictionary<string, JsonElement>? settings, ExtensionConnectionSetup? connection )
    {
        var archive = arguments.GetOption( CliOptionNames.Archive );

        return new ProtocolRequest
        {
            RequestId = FollowUpRequestId,
            Operation = ProtocolOperations.ExtensionsManage,
            Query = arguments.GetOption( CliOptionNames.Query ) ?? "",
            Offset = int.Parse( arguments.GetOption( CliOptionNames.Offset ) ?? "0", System.Globalization.CultureInfo.InvariantCulture ),
            Limit = ExtensionInventoryProjector.MaximumInventoryPageSize,
            Extension = new( )
            {
                Action = action,
                Key = key,
                ArchivePath = archive is null ? null : Path.GetFullPath( archive ),
                Digest = arguments.GetOption( CliOptionNames.Digest ),
                ExpectedRevision = expectedRevision,
                Settings = settings,
                Connection = connection,
                ConnectionName = arguments.GetOption( CliOptionNames.Connection ),
                RemoteRevoke = arguments.HasFlag( CliOptionNames.RemoteRevoke ),
                IncludeStderr = arguments.HasFlag( CliOptionNames.IncludeStderr ),
                IncludeLegacy = arguments.HasFlag( CliOptionNames.IncludeLegacy ),
                CommandId = arguments.GetOption( CliOptionNames.Command ),
                DebugSeconds = int.Parse( arguments.GetOption( CliOptionNames.Seconds ) ?? "0", System.Globalization.CultureInfo.InvariantCulture ),
                Confirmed = arguments.HasFlag( CliOptionNames.Yes ),
                EraseSettings = arguments.HasFlag( CliOptionNames.EraseSettings ),
                Grants = arguments
                    .GetOption( CliOptionNames.Allow )
                    ?.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            }
        };
    }

    private static void WriteExtensionInventory( ExtensionInventory result, bool json )
    {
        if ( json )
        {
            Console.WriteLine( JsonSerializer.Serialize( result, CommandDeckJsonContext.Default.ExtensionInventory ) );
        }
        else
        {
            Console.WriteLine(
                $"Extensions {(result.Enabled ? EnabledValue : DisabledValue)}; registry revision {result.Revision}; {result.Total} retained packages." );

            if ( result.Error is not null )
            {
                Console.WriteLine( result.Error );
            }

            foreach ( var package in result.Packages )
            {
                Console.WriteLine( $"{package.Id} {package.Version} {(DescribeExtensionPackageState( package ))} {package.Digest}" );
            }

            if ( result.Manifest is { } manifest )
            {
                Console.WriteLine( manifest.GetRawText( ) );
            }

            if ( result.ArchiveDigest is not null )
            {
                Console.WriteLine( "Reviewed archive digest: " + result.ArchiveDigest );
            }

            foreach ( var connection in result.Connections )
            {
                Console.WriteLine( $"{connection.Name} · {connection.Kind} · {connection.Scope} · {connection.CredentialStatus}" );
            }

            if ( result.ConnectionMessage is not null )
            {
                Console.WriteLine( result.ConnectionMessage );
            }

            if ( result.Debug is { } debug )
            {
                Console.WriteLine( $"Debug lease: {debug.CommandId}, expires {debug.ExpiresAt:O}, socket {debug.SocketPath}, consumed {debug.Consumed}" );
            }

            foreach ( var diagnostic in result.Diagnostics )
            {
                Console.WriteLine( $"{diagnostic.At:O} {diagnostic.CommandId} {diagnostic.SessionId} {diagnostic.Digest} {diagnostic.Status}" );

                if ( diagnostic.Stderr is not null )
                {
                    Console.WriteLine( "Untrusted child stderr:\n" + diagnostic.Stderr );
                }
            }
        }
    }

    private static async Task<ExtensionInventory> RequestExtensionInventoryAsync( UnixProtocolClient client, ProtocolRequest request,
        CancellationToken cancellationToken )
    {
        ProtocolRequestValidator.Validate( request, deferFilterResolution: true );

        await client
            .SendAsync( request, cancellationToken )
            .ConfigureAwait( false );

        ExtensionInventory? result = null;
        string? error = null;
        await foreach ( var line in client
                           .ReadRawMessagesAsync( cancellationToken )
                           .ConfigureAwait( false ) )
        {
            var message = ProtocolJson.DeserializeMessage( line );
            if ( message is null || message.RequestId != request.RequestId )
            {
                continue;
            }

            if ( message.Type == ProtocolMessageTypes.Error )
            {
                error = message.Error;
            }

            if ( message.Extensions is not null )
            {
                result = message.Extensions;
            }

            if ( message.Type == ProtocolMessageTypes.Done )
            {
                break;
            }
        }

        if ( error is not null )
        {
            throw new InvalidOperationException( error );
        }

        if ( result is null )
        {
            throw new InvalidOperationException( "The daemon did not return an extension inventory." );
        }

        return result;
    }

    private static bool RequiresExtensionManagementConfirmation( CliArguments arguments, string action )
    {
        return ExtensionManagementPolicy.RequiresConfirmation( action ) && !arguments.HasFlag( CliOptionNames.Yes );
    }

    private static string DescribeExtensionPackageState( ExtensionPackageSummary package )
    {
        if ( package.Removed )
        {
            return RemovedState;
        }
        else
        {
            if ( package.Enabled )
            {
                return EnabledValue;
            }
            else
            {
                return DisabledValue;
            }
        }
    }
}
