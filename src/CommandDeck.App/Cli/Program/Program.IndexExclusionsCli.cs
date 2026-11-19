using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const string ExclusionsCommand = "exclusions";
    private const string ExcludeCommand = "exclude";
    private const string ExclusionCommand = "exclusion";
    private const string ExclusionUsage =
        "Use index exclusions, index exclude <path>, or index exclusion edit <id> <path> | remove <id> | enable <id> | disable <id>.";

    private static bool IsIndexExclusionsCommand( CliArguments arguments )
    {
        var values = arguments.PositionalsAfterCommand( );

        return values.Count > 0 && values[0] is ExclusionsCommand or ExcludeCommand or ExclusionCommand;
    }

    private static async Task<int> RunIndexExclusionsCliAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        var request = CreateExclusionRequest( arguments.PositionalsAfterCommand( ) );
        if ( request is null )
        {
            Console.Error.WriteLine( ExclusionUsage );

            return 2;
        }

        var result = await ExecuteIndexPreferenceRequestAsync( arguments, request,
                runtime => ManageExclusionsLocallyAsync( runtime, request, cancellationToken ), cancellationToken )
            .ConfigureAwait( false );

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( result, CommandDeckJsonContext.Default.IndexRootsResult ) );

            return 0;
        }

        foreach ( var exclusion in result.Exclusions )
        {
            Console.WriteLine( FormatExclusion( exclusion ) );

            if ( exclusion.Issue is not null )
            {
                Console.WriteLine( "  " + PreferencesTextFormatter.EscapeTerminalText( exclusion.Issue ) );
            }
        }

        return 0;
    }

    private static ValueTask<IndexRootsResult> ManageExclusionsLocallyAsync( CommandDeckRuntime runtime, ProtocolRequest request,
        CancellationToken cancellationToken )
    {
        if ( request.Operation == ProtocolOperations.IndexRoots )
        {
            return runtime.Indexer.ManageRootsAsync( request.Operation, null, null, null, cancellationToken );
        }

        return runtime.Indexer.ManageExclusionsAsync( request.Operation, request.Path, request.ExclusionId, cancellationToken );
    }

    // index exclusions | exclude <path> | exclusion edit <id> <path> | exclusion remove|enable|disable <id>
    private static ProtocolRequest? CreateExclusionRequest( IReadOnlyList<string> values )
    {
        if ( values is [ExclusionsCommand] )
        {
            return new ProtocolRequest { RequestId = InitialRequestId, Operation = ProtocolOperations.IndexRoots };
        }

        if ( values is [ExcludeCommand, var path] )
        {
            return new ProtocolRequest { RequestId = InitialRequestId, Operation = ProtocolOperations.IndexExclusionAdd, Path = path };
        }

        if ( values is [ExclusionCommand, EditCommand, var editedId, var editedPath] )
        {
            return new ProtocolRequest
            {
                RequestId = InitialRequestId, Operation = ProtocolOperations.IndexExclusionUpdate, ExclusionId = editedId, Path = editedPath
            };
        }

        if ( values is not [ExclusionCommand, var action, var id] )
        {
            return null;
        }

        string? operation = action switch
        {
            RemoveCommand => ProtocolOperations.IndexExclusionRemove,
            EnableCommand => ProtocolOperations.IndexExclusionEnable,
            DisableCommand => ProtocolOperations.IndexExclusionDisable,
            _ => null
        };

        return operation is null ? null : new ProtocolRequest { RequestId = InitialRequestId, Operation = operation, ExclusionId = id };
    }

    private static string FormatExclusion( ManagedIndexExclusion exclusion )
    {
        var state = exclusion.Enabled ? "excluded" : "included";
        var presence = exclusion.Exists ? "" : "  (not present)";

        return $"{exclusion.Id}  source={exclusion.Source} {state}  " + PreferencesTextFormatter.EscapeTerminalText( exclusion.Path ) + presence;
    }
}
