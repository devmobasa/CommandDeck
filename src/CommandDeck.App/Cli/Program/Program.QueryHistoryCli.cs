using System.Text;
using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const int DefaultQueryHistoryEntries = 50;
    private const int MaximumQueryHistoryExportBytes = 256 * ByteUnits.BytesPerKibibyte;

    private static async Task<int> RunQueryHistoryAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var positionals = arguments.PositionalsAfterCommand( );
        var action = ReadCliAction( positionals, 0, QueryHistoryCliActions.Inspect );
        switch ( action )
        {
            case QueryHistoryCliActions.Inspect:
            case QueryHistoryCliActions.Export:
                int limit;

                if ( action == QueryHistoryCliActions.Export )
                {
                    limit = AppSearchLimits.MaximumQueryHistoryEntries;
                }
                else
                {
                    limit = ParseInteger( arguments.GetOption( CliOptionNames.Limit ), DefaultQueryHistoryEntries, 1,
                        AppSearchLimits.MaximumQueryHistoryEntries );
                }

                var snapshot = await runtime
                    .QueryHistory.GetAsync( limit, cancellationToken )
                    .ConfigureAwait( false );

                if ( ShouldExportQueryHistory( arguments, action ) )
                {
                    var json = JsonSerializer.Serialize( snapshot, CommandDeckJsonContext.Default.QueryHistorySnapshot );
                    if ( Encoding.UTF8.GetByteCount( json ) > MaximumQueryHistoryExportBytes )
                    {
                        throw new InvalidOperationException( "Query history export exceeds the 256 KiB UTF-8 output limit." );
                    }

                    Console.WriteLine( json );

                    return 0;
                }

                Console.WriteLine( $"Query history: {(snapshot.Enabled ? EnabledValue : DisabledValue)}" );
                Console.WriteLine( $"Retention: {snapshot.RetentionDays} days" );
                Console.WriteLine( $"Stored entries: {snapshot.TotalEntries}" );

                foreach ( var entry in snapshot.Entries )
                {
                    Console.WriteLine( $"  {entry.Id}: " + PreferencesTextFormatter.EscapeTerminalText( entry.Query ) );
                }

                return 0;

            case QueryHistoryCliActions.Delete:
                if ( positionals.Count < 2 || !long.TryParse( positionals[1], out var historyId ) || historyId <= 0 )
                {
                    Console.Error.WriteLine( $"history {QueryHistoryCliActions.Delete} requires a positive entry ID." );

                    return 2;
                }

                if ( !arguments.HasFlag( CliOptionNames.Yes ) )
                {
                    Console.Error.WriteLine( $"history {QueryHistoryCliActions.Delete} requires {CliOptionNames.Yes}." );

                    return 2;
                }

                if ( !await runtime
                        .QueryHistory.DeleteAsync( new QueryHistoryDeleteInput( historyId, true ), cancellationToken )
                        .ConfigureAwait( false ) )
                {
                    Console.Error.WriteLine( $"Query history entry {historyId} was not found." );

                    return 2;
                }

                Console.WriteLine( $"Query history entry {historyId} deleted." );

                return 0;

            case QueryHistoryCliActions.Reset:
                if ( !arguments.HasFlag( CliOptionNames.Yes ) )
                {
                    Console.Error.WriteLine( $"history {QueryHistoryCliActions.Reset} requires {CliOptionNames.Yes}." );

                    return 2;
                }

                await runtime
                    .QueryHistory.ResetAsync( new QueryHistoryResetInput( true ), cancellationToken )
                    .ConfigureAwait( false );

                Console.WriteLine( "Query history reset." );

                return 0;

            default:
                Console.Error.WriteLine( $"Use 'history {QueryHistoryCliActions.Inspect}', 'history {QueryHistoryCliActions.Export}', " +
                                         $"'history {QueryHistoryCliActions.Delete} ID {CliOptionNames.Yes}', " +
                                         $"or 'history {QueryHistoryCliActions.Reset} {CliOptionNames.Yes}'." );

                return 2;
        }
    }

    private static bool ShouldExportQueryHistory( CliArguments arguments, string action )
    {
        return action == QueryHistoryCliActions.Export || arguments.HasFlag( CliOptionNames.Json );
    }
}
