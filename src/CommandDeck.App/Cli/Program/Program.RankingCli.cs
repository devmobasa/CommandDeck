using System.Text;
using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const int DefaultRankingEntries = 50;
    private const int MaximumRankingEntries = 256;
    private const int MaximumRankingExportBytes = 256 * ByteUnits.BytesPerKibibyte;

    private static async Task<int> RunRankingAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var positionals = arguments.PositionalsAfterCommand( );
        var action = ReadCliAction( positionals, 0, RankingCliActions.Inspect );
        switch ( action )
        {
            case RankingCliActions.Inspect:
            case RankingCliActions.Export:
                int limit;

                if ( action == RankingCliActions.Export )
                {
                    limit = MaximumRankingEntries;
                }
                else
                {
                    limit = ParseInteger( arguments.GetOption( CliOptionNames.Limit ), DefaultRankingEntries, 1, MaximumRankingEntries );
                }

                var snapshot = await runtime
                    .GetRankingLearningAsync( limit, cancellationToken )
                    .ConfigureAwait( false );

                if ( ShouldExportRanking( arguments, action ) )
                {
                    var json = JsonSerializer.Serialize( snapshot, CommandDeckJsonContext.Default.RankingLearningSnapshot );
                    if ( Encoding.UTF8.GetByteCount( json ) > MaximumRankingExportBytes )
                    {
                        throw new InvalidOperationException( "Ranking export exceeds the 256 KiB UTF-8 output limit." );
                    }

                    Console.WriteLine( json );

                    return 0;
                }

                Console.WriteLine( $"Adaptive ranking: {(snapshot.Enabled ? EnabledValue : DisabledValue)}" );
                Console.WriteLine( $"Retention: {snapshot.RetentionDays} days" );
                Console.WriteLine( $"Stored events: {snapshot.TotalEvents}" );

                foreach ( var rankingEvent in snapshot.Events )
                {
                    string? query;
                    if ( rankingEvent.Query is null )
                    {
                        query = "query not retained";
                    }
                    else
                    {
                        query = $"query {PreferencesTextFormatter.EscapeTerminalText( rankingEvent.Query )}";
                    }

                    Console.WriteLine( $"  {PreferencesTextFormatter.EscapeTerminalText( rankingEvent.StableId )} · " +
                                       $"{PreferencesTextFormatter.EscapeTerminalText( rankingEvent.ActionId )} · {query}" );
                }

                return 0;

            case RankingCliActions.Reset:
                if ( !arguments.HasFlag( CliOptionNames.Yes ) )
                {
                    Console.Error.WriteLine( $"ranking {RankingCliActions.Reset} requires {CliOptionNames.Yes}." );

                    return 2;
                }

                await runtime
                    .ResetRankingLearningAsync( cancellationToken )
                    .ConfigureAwait( false );

                Console.WriteLine( "Adaptive ranking history reset." );

                return 0;

            default:
                Console.Error.WriteLine( $"Use 'ranking {RankingCliActions.Inspect}', 'ranking {RankingCliActions.Export}', " +
                                         $"or 'ranking {RankingCliActions.Reset} {CliOptionNames.Yes}'." );

                return 2;
        }
    }

    private static bool ShouldExportRanking( CliArguments arguments, string action )
    {
        return action == RankingCliActions.Export || arguments.HasFlag( CliOptionNames.Json );
    }
}
