using Microsoft.Data.Sqlite;

namespace CommandDeck;

internal sealed partial class SqliteUsageHistoryStore
{
    private const string UsageIdParameterPrefix = "$usage_id_";

    private static string UsageIdParameterName( int index ) => UsageIdParameterPrefix + index;

    public async ValueTask<RankingLearningSnapshot> GetRankingLearningAsync( int limit, CancellationToken cancellationToken )
    {
        await initialize( cancellationToken )
            .ConfigureAwait( false );

        var boundedLimit = Math.Clamp( limit, 1, MaximumSnapshotEntries );
        var cutoff = AdaptiveCutoffUnixMilliseconds( );

        await using var connection = await open( cancellationToken )
            .ConfigureAwait( false );

        await using var command = connection.CreateCommand( );

        command.CommandText = """
                              SELECT stable_id, action_id, query_text, invoked_unix_ms,
                                     (SELECT COUNT(*) FROM invocation_events WHERE invoked_unix_ms >= $cutoff)
                              FROM invocation_events
                              WHERE invoked_unix_ms >= $cutoff
                              ORDER BY invoked_unix_ms DESC, id DESC
                              LIMIT $limit;
                              """;

        command.Parameters.AddWithValue( "$cutoff", cutoff );
        command.Parameters.AddWithValue( "$limit", boundedLimit );
        List<RankingLearningEvent> events = [];
        long total = 0;

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            total = reader.GetInt64( 4 );
            events.Add( ReadRankingLearningEvent( reader ) );
        }

        return CreateRankingLearningSnapshot( ranking, total, events );
    }

    private static RankingLearningEvent ReadRankingLearningEvent( SqliteDataReader reader )
    {
        return new RankingLearningEvent
        {
            StableId = reader.GetString( 0 ),
            ActionId = reader.GetString( 1 ),
            Query = reader.IsDBNull( 2 ) ? null : reader.GetString( 2 ),
            InvokedUnixMilliseconds = reader.GetInt64( 3 )
        };
    }

    private RankingLearningSnapshot CreateRankingLearningSnapshot( RankingOptions ranking, long total, List<RankingLearningEvent> events )
    {
        return new RankingLearningSnapshot
        {
            Enabled = LearnsUsage,
            RetentionDays = ranking.Adaptive.RetentionDays,
            MaximumEvents = ranking.Adaptive.MaximumEvents,
            TotalEvents = total,
            Truncated = total > events.Count,
            Events = events
        };
    }

    public async ValueTask<IReadOnlyDictionary<string, UsageSignal>> GetUsageSignalsAsync( IReadOnlyList<string> stableIds,
        CancellationToken cancellationToken )
    {
        var ids = BoundUsageSignalIds( stableIds );
        if ( ShouldSkipUsageSignals( ids ) )
        {
            return new Dictionary<string, UsageSignal>( StringComparer.Ordinal );
        }

        await initialize( cancellationToken )
            .ConfigureAwait( false );

        await using var connection = await open( cancellationToken )
            .ConfigureAwait( false );

        await using var command = connection.CreateCommand( );
        var parameters = string.Join( ", ", ids.Select( ( _, index ) => UsageIdParameterName( index ) ) );

        command.CommandText = $"""
                               SELECT stable_id, use_count, last_used_unix_ms
                               FROM usage
                               WHERE stable_id IN ({parameters})
                                 AND last_used_unix_ms >= $cutoff;
                               """;

        for ( var index = 0; index < ids.Length; index++ )
        {
            command.Parameters.AddWithValue( UsageIdParameterName( index ), ids[index] );
        }

        command.Parameters.AddWithValue( "$cutoff", AdaptiveCutoffUnixMilliseconds( ) );
        Dictionary<string, UsageSignal> signals = new( StringComparer.Ordinal );

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            signals[reader.GetString( 0 )] = new UsageSignal( reader.GetInt64( 1 ), reader.GetInt64( 2 ) );
        }

        return signals;
    }

    private static string[] BoundUsageSignalIds( IReadOnlyList<string> stableIds )
    {
        return stableIds
            .Where( static value => !string.IsNullOrWhiteSpace( value ) )
            .Distinct( StringComparer.Ordinal )
            .Take( MaximumUsageSignalIds )
            .ToArray( );
    }

    private bool ShouldSkipUsageSignals( string[] ids )
    {
        return ids.Length == 0 || !LearnsUsage;
    }

    // Query history is bounded by its maximum entry count, so every distinct
    // query and result pair since the cutoff is read and matched here.
    public async ValueTask<IReadOnlyDictionary<string, LearnedQuery>> GetLearnedQueriesAsync( string query, long sinceUnixMilliseconds,
        CancellationToken cancellationToken )
    {
        var typed = LearnedQueries.Normalize( query );
        Dictionary<string, LearnedQuery> learned = new( StringComparer.Ordinal );

        if ( ShouldSkipLearnedQueries( typed, history ) )
        {
            return learned;
        }

        // Entries past query-history retention never promote, even before
        // the next write purges them.
        var retained = timeProvider
                           .GetUtcNow( )
                           .ToUnixTimeMilliseconds( ) -
                       checked(( long ) privacy.QueryHistoryRetentionDays * ( long ) FrecencyPolicy.MillisecondsPerDay);

        await initialize( cancellationToken )
            .ConfigureAwait( false );

        await using var connection = await open( cancellationToken )
            .ConfigureAwait( false );

        await using var command = connection.CreateCommand( );

        command.CommandText = """
                              SELECT stable_id, query_text, MAX(invoked_unix_ms)
                              FROM query_history
                              WHERE invoked_unix_ms >= $since
                              GROUP BY stable_id, query_text
                              LIMIT $limit;
                              """;

        command.Parameters.AddWithValue( "$since", Math.Max( sinceUnixMilliseconds, retained ) );
        command.Parameters.AddWithValue( "$limit", Math.Max( 1, privacy.MaximumQueryHistoryEntries ) );

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            var kind = LearnedQueries.Match( LearnedQueries.Normalize( reader.GetString( 1 ) ), typed );
            if ( kind is null )
            {
                continue;
            }

            LearnedQuery candidate = new( kind.Value, reader.GetInt64( 2 ) );
            var stableId = reader.GetString( 0 );
            if ( !learned.TryGetValue( stableId, out var existing ) || LearnedQueries.Outranks( candidate, existing ) )
            {
                learned[stableId] = candidate;
            }
        }

        return learned;
    }

    private static bool ShouldSkipLearnedQueries( string typed, HistoryPolicy history )
    {
        return typed.Length == 0 || !history.RememberSearches;
    }
}
