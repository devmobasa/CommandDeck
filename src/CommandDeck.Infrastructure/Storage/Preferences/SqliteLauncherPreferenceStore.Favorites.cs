using Microsoft.Data.Sqlite;

namespace CommandDeck;

internal sealed partial class SqliteLauncherPreferenceStore
{
    public async ValueTask<CustomizationResult> SetFavoriteAsync( SearchResult result, bool enabled, CancellationToken cancellationToken )
    {
        EnsureCustomizable( result );

        var now = _timeProvider
            .GetUtcNow( )
            .ToUnixTimeMilliseconds( );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var transaction = ( SqliteTransaction ) await connection
            .BeginTransactionAsync( cancellationToken )
            .ConfigureAwait( false );

        if ( enabled )
        {
            await InsertFavoriteAsync( connection, transaction, result, now, cancellationToken )
                .ConfigureAwait( false );
        }
        else
        {
            await using var delete = connection.CreateCommand( );
            delete.Transaction = transaction;

            delete.CommandText = """
                                 DELETE FROM aliases WHERE stable_id = $id;
                                 DELETE FROM favorites WHERE stable_id = $id;
                                 """;

            delete.Parameters.AddWithValue( "$id", StableCustomizationId( result ) );

            await delete
                .ExecuteNonQueryAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        await transaction
            .CommitAsync( cancellationToken )
            .ConfigureAwait( false );

        return await ReadCustomizationResultAsync( StableCustomizationId( result ), enabled ? "Bookmark added" : "Bookmark and aliases removed",
                cancellationToken )
            .ConfigureAwait( false );
    }

    public async ValueTask ReorderFavoritesAsync( IReadOnlyList<string> stableIds, CancellationToken cancellationToken )
    {
        if ( HasInvalidFavoriteOrder( stableIds ) )
        {
            throw new InvalidOperationException( "Favorite order contains duplicate or excessive IDs." );
        }

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var transaction = ( SqliteTransaction ) await connection
            .BeginTransactionAsync( cancellationToken )
            .ConfigureAwait( false );

        var existing = await ReadFavoriteOrderAsync( connection, transaction, cancellationToken )
            .ConfigureAwait( false );

        if ( HasFavoriteSetChanged( existing, stableIds ) )
        {
            throw new InvalidOperationException( "Favorite order must contain every current favorite exactly once." );
        }

        await WriteFavoriteOrderAsync( connection, transaction, stableIds, cancellationToken )
            .ConfigureAwait( false );

        await transaction
            .CommitAsync( cancellationToken )
            .ConfigureAwait( false );
    }

    private static bool HasInvalidFavoriteOrder( IReadOnlyList<string> stableIds )
    {
        return stableIds.Count > CustomizationLimits.MaximumReorderItems ||
               stableIds.Count !=
               stableIds
                   .Distinct( StringComparer.Ordinal )
                   .Count( );
    }

    private static bool HasFavoriteSetChanged( List<string> existing, IReadOnlyList<string> stableIds )
    {
        return existing.Count != stableIds.Count ||
               existing
                   .Except( stableIds, StringComparer.Ordinal )
                   .Any( );
    }

    // The order is read and written in one transaction, so a move made
    // meanwhile is never overwritten.
    public async ValueTask MoveFavoriteAsync( SearchResult result, SearchResult target, CancellationToken cancellationToken )
    {
        EnsureCustomizable( result );
        EnsureCustomizable( target );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var transaction = ( SqliteTransaction ) await connection
            .BeginTransactionAsync( cancellationToken )
            .ConfigureAwait( false );

        var order = await ReadFavoriteOrderAsync( connection, transaction, cancellationToken )
            .ConfigureAwait( false );

        var index = order.IndexOf( StableCustomizationId( result ) );
        var targetIndex = order.IndexOf( StableCustomizationId( target ) );
        if ( HasInvalidFavoriteMove( index, targetIndex ) )
        {
            throw new InvalidOperationException( "Both results must be bookmarked." );
        }

        (order[index], order[targetIndex]) = (order[targetIndex], order[index]);

        await WriteFavoriteOrderAsync( connection, transaction, order, cancellationToken )
            .ConfigureAwait( false );

        await transaction
            .CommitAsync( cancellationToken )
            .ConfigureAwait( false );
    }

    private static bool HasInvalidFavoriteMove( int index, int targetIndex )
    {
        return index < 0 || targetIndex < 0;
    }

    private static async ValueTask<List<string>> ReadFavoriteOrderAsync( SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken )
    {
        List<string> order = [];
        await using var command = connection.CreateCommand( );
        command.Transaction = transaction;
        command.CommandText = "SELECT stable_id FROM favorites ORDER BY position;";

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            order.Add( reader.GetString( 0 ) );
        }

        return order;
    }

    private static async ValueTask WriteFavoriteOrderAsync( SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> order,
        CancellationToken cancellationToken )
    {
        await SqliteCatalog
            .ExecuteNonQueryAsync( connection, transaction, "UPDATE favorites SET position = -position;", cancellationToken )
            .ConfigureAwait( false );

        for ( var index = 0; index < order.Count; index++ )
        {
            await using var update = connection.CreateCommand( );
            update.Transaction = transaction;
            update.CommandText = "UPDATE favorites SET position = $position WHERE stable_id = $id;";
            update.Parameters.AddWithValue( "$position", index + 1 );
            update.Parameters.AddWithValue( "$id", order[index] );

            await update
                .ExecuteNonQueryAsync( cancellationToken )
                .ConfigureAwait( false );
        }
    }

    private static async ValueTask InsertFavoriteAsync( SqliteConnection connection, SqliteTransaction transaction, SearchResult result, long now,
        CancellationToken cancellationToken )
    {
        await using ( var capacity = connection.CreateCommand( ) )
        {
            capacity.Transaction = transaction;

            capacity.CommandText = """
                                   SELECT EXISTS(SELECT 1 FROM favorites WHERE stable_id = $id),
                                          COUNT(*)
                                   FROM favorites;
                                   """;

            capacity.Parameters.AddWithValue( "$id", StableCustomizationId( result ) );

            await using var reader = await capacity
                .ExecuteReaderAsync( cancellationToken )
                .ConfigureAwait( false );

            await reader
                .ReadAsync( cancellationToken )
                .ConfigureAwait( false );

            var alreadyFavorite = reader.GetInt64( 0 ) != 0;
            var favoriteCount = reader.GetInt64( 1 );
            if ( ExceedsFavoriteCapacity( alreadyFavorite, favoriteCount ) )
            {
                throw new InvalidOperationException( $"At most {CustomizationLimits.MaximumFavorites} bookmarks can be saved." );
            }
        }

        await using var command = connection.CreateCommand( );
        command.Transaction = transaction;

        command.CommandText = """
                              INSERT INTO favorites(
                                  stable_id, position, title_snapshot, source_id, icon_snapshot,
                                  created_unix_ms, updated_unix_ms)
                              VALUES(
                                  $id, COALESCE((SELECT MAX(position) + 1 FROM favorites), 1),
                                  $title, $source, $icon, $now, $now)
                              ON CONFLICT(stable_id) DO UPDATE SET
                                  title_snapshot = excluded.title_snapshot,
                                  source_id = excluded.source_id,
                                  icon_snapshot = excluded.icon_snapshot,
                                  updated_unix_ms = excluded.updated_unix_ms;
                              """;

        command.Parameters.AddWithValue( "$id", StableCustomizationId( result ) );
        command.Parameters.AddWithValue( "$title", result.Title );
        command.Parameters.AddWithValue( "$source", result.SourceId );
        command.Parameters.AddWithValue( "$icon", ( object? ) result.Icon ?? DBNull.Value );
        command.Parameters.AddWithValue( "$now", now );

        await command
            .ExecuteNonQueryAsync( cancellationToken )
            .ConfigureAwait( false );
    }

    private static bool ExceedsFavoriteCapacity( bool alreadyFavorite, long favoriteCount )
    {
        return !alreadyFavorite && favoriteCount >= CustomizationLimits.MaximumFavorites;
    }
}
