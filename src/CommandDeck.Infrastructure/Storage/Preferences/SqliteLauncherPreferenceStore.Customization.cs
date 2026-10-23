using Microsoft.Data.Sqlite;

namespace CommandDeck;

internal sealed partial class SqliteLauncherPreferenceStore
{
    private const string CustomizationIdPrefix = "customization:";
    private const string BookmarkAliasNamespace = "bookmark";
    private const double ExactAliasScore = 1.4;

    public async ValueTask<CustomizationResult> SetAliasAsync( SearchResult result, string? aliasValue, CancellationToken cancellationToken )
    {
        EnsureCustomizable( result );
        var stableId = StableCustomizationId( result );
        var value = NormalizeOptionalAlias( aliasValue );

        var now = _timeProvider
            .GetUtcNow( )
            .ToUnixTimeMilliseconds( );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var transaction = ( SqliteTransaction ) await connection
            .BeginTransactionAsync( cancellationToken )
            .ConfigureAwait( false );

        var primary = await GetPrimaryAliasAsync( connection, transaction, stableId, cancellationToken )
            .ConfigureAwait( false );

        if ( value is not null )
        {
            await SqliteCatalog
                .EnsureAliasAvailableAsync( connection, transaction, value, BookmarkAliasNamespace, stableId, _configuredQuicklinks( ),
                    cancellationToken )
                .ConfigureAwait( false );

            await using var conflict = connection.CreateCommand( );
            conflict.Transaction = transaction;
            conflict.CommandText = "SELECT stable_id FROM aliases WHERE alias = $alias COLLATE NOCASE;";
            conflict.Parameters.AddWithValue( "$alias", value );

            var owner = ( string? ) await conflict
                .ExecuteScalarAsync( cancellationToken )
                .ConfigureAwait( false );

            if ( IsAliasOwnedByAnotherItem( owner, stableId ) )
            {
                throw new InvalidOperationException( $"Alias '{value}' is already assigned." );
            }
        }

        if ( HasDifferentPrimaryAlias( primary, value ) )
        {
            await using var delete = connection.CreateCommand( );
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM aliases WHERE alias = $alias COLLATE NOCASE;";
            delete.Parameters.AddWithValue( "$alias", primary );

            await delete
                .ExecuteNonQueryAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        if ( value is not null )
        {
            await InsertFavoriteAsync( connection, transaction, result, now, cancellationToken )
                .ConfigureAwait( false );

            await using var insert = connection.CreateCommand( );
            insert.Transaction = transaction;

            insert.CommandText = """
                                 INSERT INTO aliases(alias, stable_id, created_unix_ms, updated_unix_ms)
                                 VALUES($alias, $id, $now, $now)
                                 ON CONFLICT(alias) DO UPDATE SET updated_unix_ms = excluded.updated_unix_ms;
                                 """;

            insert.Parameters.AddWithValue( "$alias", value );
            insert.Parameters.AddWithValue( "$id", stableId );
            insert.Parameters.AddWithValue( "$now", now );

            await insert
                .ExecuteNonQueryAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        await transaction
            .CommitAsync( cancellationToken )
            .ConfigureAwait( false );

        return await ReadCustomizationResultAsync( stableId, value is null ? "Alias removed" : "Alias saved", cancellationToken )
            .ConfigureAwait( false );
    }

    private static string? NormalizeOptionalAlias( string? aliasValue )
    {
        if ( string.IsNullOrWhiteSpace( aliasValue ) )
        {
            return null;
        }

        return AliasValidator.NormalizeAndValidate( aliasValue );
    }

    private static bool IsAliasOwnedByAnotherItem( string? owner, string stableId )
    {
        return owner is not null && !string.Equals( owner, stableId, StringComparison.Ordinal );
    }

    private static bool HasDifferentPrimaryAlias( string? primary, string? value )
    {
        return primary is not null && !string.Equals( primary, value, StringComparison.Ordinal );
    }

    public async ValueTask<CustomizationResult> RemoveOrphanAsync( string customizationId, CancellationToken cancellationToken )
    {
        if ( IsInvalidCustomizationId( customizationId, CustomizationIdPrefix ) )
        {
            throw new InvalidOperationException( "Unknown customization ID." );
        }

        var stableId = customizationId[CustomizationIdPrefix.Length..];

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var transaction = ( SqliteTransaction ) await connection
            .BeginTransactionAsync( cancellationToken )
            .ConfigureAwait( false );

        await using ( var verify = connection.CreateCommand( ) )
        {
            verify.Transaction = transaction;

            verify.CommandText = """
                                 SELECT
                                     EXISTS(SELECT 1 FROM favorites WHERE stable_id = $id),
                                     EXISTS(SELECT 1 FROM customizable_items WHERE stable_id = $id);
                                 """;

            verify.Parameters.AddWithValue( "$id", stableId );

            await using var reader = await verify
                .ExecuteReaderAsync( cancellationToken )
                .ConfigureAwait( false );

            await reader
                .ReadAsync( cancellationToken )
                .ConfigureAwait( false );

            if ( IsOrphanRemovalUnavailable( reader ) )
            {
                throw new InvalidOperationException( "Only an unavailable customization can be removed by ID." );
            }
        }

        await using var delete = connection.CreateCommand( );
        delete.Transaction = transaction;

        delete.CommandText = """
                             DELETE FROM aliases WHERE stable_id = $id;
                             DELETE FROM favorites WHERE stable_id = $id;
                             """;

        delete.Parameters.AddWithValue( "$id", stableId );

        await delete
            .ExecuteNonQueryAsync( cancellationToken )
            .ConfigureAwait( false );

        await transaction
            .CommitAsync( cancellationToken )
            .ConfigureAwait( false );

        return new CustomizationResult { StableId = stableId, Message = "Unavailable customization removed" };
    }

    private static bool IsInvalidCustomizationId( string customizationId, string prefix )
    {
        return !customizationId.StartsWith( prefix, StringComparison.Ordinal ) || customizationId.Length <= prefix.Length;
    }

    private static bool IsOrphanRemovalUnavailable( SqliteDataReader reader )
    {
        return reader.GetInt64( 0 ) == 0 || reader.GetInt64( 1 ) != 0;
    }

    public async ValueTask<SearchResult?> FindAliasAsync( string aliasValue, CancellationToken cancellationToken )
    {
        if ( string.IsNullOrWhiteSpace( aliasValue ) )
        {
            return null;
        }

        await InitializeAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var command = connection.CreateCommand( );

        command.CommandText = $"""
                               SELECT i.stable_id, i.kind, i.title, i.subtitle, i.path, i.icon,
                                      i.payload_json, i.provider_id, i.source_id,
                                      EXISTS(SELECT 1 FROM favorites f WHERE f.stable_id = i.stable_id),
                                      COALESCE((SELECT group_concat(ordered_alias.alias, char(31))
                                                FROM (SELECT a2.alias FROM aliases a2
                                                      WHERE a2.stable_id = i.stable_id
                                                      ORDER BY a2.created_unix_ms, a2.alias COLLATE NOCASE)
                                                     ordered_alias), '')
                               FROM (
                                   SELECT stable_id FROM aliases WHERE alias = $alias COLLATE NOCASE
                                   UNION
                                   SELECT stable_id FROM catalog_items
                                   WHERE kind = 'quicklink' AND json_extract(payload_json, '$.alias') = $alias COLLATE NOCASE
                               ) a
                               JOIN customizable_items i ON i.stable_id = a.stable_id
                               WHERE 1 = 1
                                 {SqliteCatalog.HomeSearchClause( "i" )};
                               """;

        SqliteCatalog.AddHomeSearchParameters( command );
        command.Parameters.AddWithValue( "$alias", aliasValue );

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        if ( !await reader
                .ReadAsync( cancellationToken )
                .ConfigureAwait( false ) )
        {
            return null;
        }

        var result = SqliteCatalog.ReadCustomizationSearchResult( reader, score: ExactAliasScore, exactAlias: aliasValue );

        return result;
    }

    private async ValueTask<List<CustomizationSummary>> ReadCustomizationsAsync( CancellationToken cancellationToken )
    {
        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var command = CreateCustomizationsCommand( connection );
        List<CustomizationSummary> values = [];

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            var stableId = reader.GetString( 0 );
            values.Add( ReadCustomizationSummary( stableId, reader ) );
        }

        return values;
    }

    private static CustomizationSummary ReadCustomizationSummary( string stableId, SqliteDataReader reader )
    {
        return new CustomizationSummary
        {
            Id = $"{CustomizationIdPrefix}{stableId}",
            StableId = stableId,
            Title = reader.GetString( 1 ),
            SourceId = reader.GetString( 2 ),
            Icon = reader.IsDBNull( 3 ) ? null : reader.GetString( 3 ),
            Position = reader.GetInt32( 4 ),
            IsAvailable = reader.GetInt64( 5 ) != 0,
            IsFavorite = true,
            Aliases = SqliteCatalog.SplitAliases( reader.GetString( 6 ) )
        };
    }

    // A LEFT JOIN against the customizable_items view materializes both catalog
    // tables plus an automatic index on every preferences snapshot. Availability is
    // two primary-key probes per favorite instead. Specs run EXPLAIN QUERY PLAN here.
    internal static SqliteCommand CreateCustomizationsCommand( SqliteConnection connection )
    {
        var command = connection.CreateCommand( );

        command.CommandText = """
                              SELECT f.stable_id, f.title_snapshot, f.source_id, f.icon_snapshot,
                                     f.position,
                                     EXISTS(SELECT 1 FROM catalog_items i WHERE i.stable_id = f.stable_id)
                                         OR EXISTS(SELECT 1 FROM extension_commands e WHERE e.stable_id = f.stable_id),
                                     COALESCE((SELECT group_concat(ordered_alias.alias, char(31))
                                               FROM (SELECT a.alias FROM aliases a
                                                     WHERE a.stable_id = f.stable_id
                                                     ORDER BY a.created_unix_ms, a.alias COLLATE NOCASE)
                                                    ordered_alias), '')
                              FROM favorites f
                              ORDER BY f.position, f.title_snapshot COLLATE NOCASE;
                              """;

        return command;
    }

    private static void EnsureCustomizable( SearchResult result )
    {
        if ( IsNotCustomizable( result ) )
        {
            throw new InvalidOperationException( "This result cannot be customized." );
        }
    }

    private static bool IsNotCustomizable( SearchResult result )
    {
        return !result.CanCustomize || !SqliteCatalog.CanCustomize( result.Kind, result.SourceId );
    }

    private static string StableCustomizationId( SearchResult result ) => result.Metadata.GetValueOrDefault( ResultMetadataKeys.CatalogResultId, result.Id );

    private static async ValueTask<string?> GetPrimaryAliasAsync( SqliteConnection connection, SqliteTransaction transaction, string stableId,
        CancellationToken cancellationToken )
    {
        await using var command = connection.CreateCommand( );
        command.Transaction = transaction;

        command.CommandText = """
                              SELECT alias FROM aliases WHERE stable_id = $id
                              ORDER BY created_unix_ms, alias COLLATE NOCASE LIMIT 1;
                              """;

        command.Parameters.AddWithValue( "$id", stableId );

        return ( string? ) await command
            .ExecuteScalarAsync( cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask<CustomizationResult> ReadCustomizationResultAsync( string stableId, string message, CancellationToken cancellationToken )
    {
        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var command = connection.CreateCommand( );

        command.CommandText = """
                              SELECT EXISTS(SELECT 1 FROM favorites WHERE stable_id = $id),
                                     COALESCE((SELECT group_concat(ordered_alias.alias, char(31))
                                               FROM (SELECT alias FROM aliases
                                                     WHERE stable_id = $id
                                                     ORDER BY created_unix_ms, alias COLLATE NOCASE)
                                                    ordered_alias), ''),
                                     EXISTS(SELECT 1 FROM hidden_items WHERE stable_id = $id);
                              """;

        command.Parameters.AddWithValue( "$id", stableId );

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        await reader
            .ReadAsync( cancellationToken )
            .ConfigureAwait( false );

        return ReadCustomizationResult( stableId, reader, message );
    }

    private static CustomizationResult ReadCustomizationResult( string stableId, SqliteDataReader reader, string message )
    {
        return new CustomizationResult
        {
            StableId = stableId,
            IsFavorite = reader.GetInt64( 0 ) != 0,
            Aliases = SqliteCatalog.SplitAliases( reader.GetString( 1 ) ),
            IsHidden = reader.GetInt64( 2 ) != 0,
            Message = message
        };
    }
}
