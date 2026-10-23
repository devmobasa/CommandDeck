using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CommandDeck;

internal sealed partial class SqliteLauncherPreferenceStore
{
    private const string SavedSearchIdPrefix = "saved-search:";
    private const int MaximumSavedSearchIdCharacters = 80;

    public async ValueTask<IReadOnlyList<SavedSearch>> GetSavedSearchesAsync( CancellationToken cancellationToken )
    {
        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var command = connection.CreateCommand( );

        command.CommandText = """
                              SELECT id, name, expression_json, shortcut, show_on_home, position
                              FROM saved_searches
                              ORDER BY position, name COLLATE NOCASE, id;
                              """;

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        List<SavedSearch> searches = [];

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            var expression = JsonSerializer.Deserialize( reader.GetString( 2 ), CommandDeckJsonContext.Default.QueryExpression );
            if ( expression is null )
            {
                throw new InvalidOperationException( "A saved search contains no query expression." );
            }

            var unavailable = PrivateSources.ReferencesPrivateSource( expression );
            var shortcut = unavailable || reader.IsDBNull( 3 ) ? null : reader.GetString( 3 );
            var reserved = shortcut is not null && SavedSearchValidator.IsReserved( shortcut );
            searches.Add( ReadSavedSearch( reader, unavailable, expression, reserved, shortcut ) );
        }

        return searches;
    }

    private static SavedSearch ReadSavedSearch( SqliteDataReader reader, bool unavailable, QueryExpression expression, bool reserved, string? shortcut )
    {
        return new SavedSearch
        {
            Id = reader.GetString( 0 ),
            Name = SavedSearchTitle( unavailable, reader, expression ),
            Expression = unavailable ? new QueryExpression( ) : QueryParser.ValidateShape( expression ),
            Shortcut = reserved ? null : shortcut,
            ReservedShortcut = reserved ? shortcut : null,
            ShowOnHome = !unavailable && reader.GetInt64( 4 ) != 0,
            Position = reader.GetInt32( 5 ),
            Unavailable = unavailable
        };
    }

    private static string SavedSearchTitle( bool unavailable, SqliteDataReader reader, QueryExpression expression )
    {
        if ( !unavailable )
        {
            return reader.GetString( 1 );
        }

        return ClipboardPrivacy.ReferencesHistory( expression ) ? "Unsupported clipboard saved search" : "Unsupported HEY saved search";
    }

    public async ValueTask<IReadOnlyList<SavedSearch>> SaveSavedSearchAsync( SavedSearch search, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( search );

        var id = ResolveSavedSearchId( search );
        if ( IsInvalidSavedSearchId( id ) )
        {
            throw new InvalidOperationException( "Saved-search IDs are invalid." );
        }

        var name = SavedSearchValidator.ValidateName( search.Name );
        var expression = SavedSearchValidator.ValidateExpression( search.Expression, ContributionSnapshot );
        var shortcut = SavedSearchValidator.NormalizeShortcut( search.Shortcut );
        var expressionJson = JsonSerializer.Serialize( expression, CommandDeckJsonContext.Default.QueryExpression );

        var now = _timeProvider
            .GetUtcNow( )
            .ToUnixTimeMilliseconds( );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var transaction = ( SqliteTransaction ) await connection
            .BeginTransactionAsync( cancellationToken )
            .ConfigureAwait( false );

        await EnsureNotClipboardShortcutAsync( connection, transaction, id, shortcut, cancellationToken )
            .ConfigureAwait( false );

        await using var capacity = connection.CreateCommand( );
        capacity.Transaction = transaction;

        capacity.CommandText = """
                               SELECT COUNT(*), EXISTS(SELECT 1 FROM saved_searches WHERE id = $id)
                               FROM saved_searches;
                               """;

        capacity.Parameters.AddWithValue( "$id", id );

        await using ( var reader = await capacity
                         .ExecuteReaderAsync( cancellationToken )
                         .ConfigureAwait( false ) )
        {
            _ = await reader
                .ReadAsync( cancellationToken )
                .ConfigureAwait( false );

            if ( ExceedsSavedSearchCapacity( reader ) )
            {
                throw new InvalidOperationException( $"Saved searches are limited to {SavedSearchValidator.MaximumSearches} entries." );
            }
        }

        var position = await ResolveSavedSearchPositionAsync( search, connection, transaction, cancellationToken )
            .ConfigureAwait( false );

        await using var command = connection.CreateCommand( );
        command.Transaction = transaction;

        command.CommandText = """
                              INSERT INTO saved_searches(
                                  id, name, expression_json, shortcut, show_on_home, position,
                                  created_unix_ms, updated_unix_ms)
                              VALUES($id, $name, $expression, $shortcut, $home, $position, $now, $now)
                              ON CONFLICT(id) DO UPDATE SET
                                  name = excluded.name,
                                  expression_json = excluded.expression_json,
                                  shortcut = excluded.shortcut,
                                  show_on_home = excluded.show_on_home,
                                  position = excluded.position,
                                  updated_unix_ms = excluded.updated_unix_ms;
                              """;

        command.Parameters.AddWithValue( "$id", id );
        command.Parameters.AddWithValue( "$name", name );
        command.Parameters.AddWithValue( "$expression", expressionJson );
        command.Parameters.AddWithValue( "$shortcut", ( object? ) shortcut ?? DBNull.Value );
        command.Parameters.AddWithValue( "$home", search.ShowOnHome ? 1 : 0 );
        command.Parameters.AddWithValue( "$position", position );
        command.Parameters.AddWithValue( "$now", now );

        try
        {
            await command
                .ExecuteNonQueryAsync( cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( SqliteException exception ) when ( exception.SqliteErrorCode == SQLitePCL.raw.SQLITE_CONSTRAINT )
        {
            throw new InvalidOperationException( "A saved search already uses that name or shortcut.", exception );
        }

        await transaction
            .CommitAsync( cancellationToken )
            .ConfigureAwait( false );

        return await GetSavedSearchesAsync( cancellationToken )
            .ConfigureAwait( false );
    }

    private static string ResolveSavedSearchId( SavedSearch search )
    {
        if ( string.IsNullOrWhiteSpace( search.Id ) )
        {
            return $"{SavedSearchIdPrefix}{Guid.NewGuid( ):N}";
        }

        return search.Id.Trim( );
    }

    private static bool IsInvalidSavedSearchId( string id )
    {
        return !id.StartsWith( SavedSearchIdPrefix, StringComparison.Ordinal ) || id.Length > MaximumSavedSearchIdCharacters;
    }

    private static bool ExceedsSavedSearchCapacity( SqliteDataReader reader )
    {
        return reader.GetInt64( 0 ) >= SavedSearchValidator.MaximumSearches && reader.GetInt64( 1 ) == 0;
    }

    private static async ValueTask<int> ResolveSavedSearchPositionAsync( SavedSearch search, SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken )
    {
        if ( search.Position > 0 )
        {
            return search.Position;
        }

        return await NextSavedSearchPositionAsync( connection, transaction, cancellationToken )
            .ConfigureAwait( false );
    }

    public async ValueTask<IReadOnlyList<SavedSearch>> DeleteSavedSearchAsync( string id, CancellationToken cancellationToken )
    {
        if ( string.IsNullOrWhiteSpace( id ) )
        {
            throw new InvalidOperationException( "A saved-search ID is required." );
        }

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var command = connection.CreateCommand( );
        command.CommandText = "DELETE FROM saved_searches WHERE id = $id;";
        command.Parameters.AddWithValue( "$id", id );

        if ( await command
                 .ExecuteNonQueryAsync( cancellationToken )
                 .ConfigureAwait( false ) ==
             0 )
        {
            throw new InvalidOperationException( $"Saved search '{id}' was not found." );
        }

        return await GetSavedSearchesAsync( cancellationToken )
            .ConfigureAwait( false );
    }

    private static async ValueTask<int> NextSavedSearchPositionAsync( SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken )
    {
        await using var command = connection.CreateCommand( );
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(position), 0) + 1 FROM saved_searches;";

        return Convert.ToInt32( await command
            .ExecuteScalarAsync( cancellationToken )
            .ConfigureAwait( false ), System.Globalization.CultureInfo.InvariantCulture );
    }
}
