namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    private const int MaximumUserQuicklinks = 100;
    private const int MaximumDisabledQuicklinks = 1000;
    private const int MaximumQuicklinkIdCharacters = 64;

    public async ValueTask<QuicklinksResult> RestoreQuicklinksAsync( QuicklinkState state, string? fallbackId, CancellationToken cancellationToken )
    {
        if ( HasInvalidQuicklinkRestore( state ) )
        {
            throw new InvalidOperationException( "Invalid quicklink backup." );
        }

        QuicklinkValidator.ValidateUnique( (_configuredQuicklinks ?? []).Concat( state.UserQuicklinks.Select( entry => entry.Quicklink ) ) );

        await InitializeAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var transaction = connection.BeginTransaction( deferred: false );
        await using var command = connection.CreateCommand( );
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM user_quicklinks; DELETE FROM disabled_configured_quicklinks;";

        await command
            .ExecuteNonQueryAsync( cancellationToken )
            .ConfigureAwait( false );

        foreach ( var entry in state.UserQuicklinks )
        {
            command.Parameters.Clear( );

            command.CommandText = """
                                  INSERT INTO user_quicklinks(id,title,url_template,alias,icon,created_unix_ms)
                                  VALUES($id,$title,$url,$alias,$icon,$created);
                                  """;

            command.Parameters.AddWithValue( "$id", entry.Quicklink.Id );
            command.Parameters.AddWithValue( "$title", entry.Quicklink.Title );
            command.Parameters.AddWithValue( "$url", entry.Quicklink.UrlTemplate );
            command.Parameters.AddWithValue( "$alias", ( object? ) entry.Quicklink.Alias ?? DBNull.Value );
            command.Parameters.AddWithValue( "$icon", ( object? ) entry.Quicklink.Icon ?? DBNull.Value );
            command.Parameters.AddWithValue( "$created", entry.CreatedUnixMs );

            await command
                .ExecuteNonQueryAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        foreach ( var id in state.DisabledConfiguredIds )
        {
            command.Parameters.Clear( );
            command.CommandText = "INSERT OR IGNORE INTO disabled_configured_quicklinks(id) VALUES($id);";
            command.Parameters.AddWithValue( "$id", id );

            await command
                .ExecuteNonQueryAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        var snapshot = await ReadQuicklinksAsync( connection, transaction, null, cancellationToken )
            .ConfigureAwait( false );

        await ValidateQuicklinkAliasesAsync( connection, transaction, snapshot.Quicklinks, cancellationToken )
            .ConfigureAwait( false );

        var fallback = snapshot.Quicklinks.Any( entry => entry.Enabled && entry.Quicklink.Id == fallbackId ) ? fallbackId : null;

        command.Parameters.Clear( );

        command.CommandText = "INSERT OR REPLACE INTO launcher_preferences(key,value,updated_unix_ms) VALUES('web_fallback_quicklink_id',$fallback,$now);";

        command.Parameters.AddWithValue( "$fallback", fallback ?? string.Empty );
        command.Parameters.AddWithValue( "$now", _timeProvider
            .GetUtcNow( )
            .ToUnixTimeMilliseconds( ) );

        await command
            .ExecuteNonQueryAsync( cancellationToken )
            .ConfigureAwait( false );

        await PublishQuicklinksAsync( connection, transaction, snapshot.Quicklinks, cancellationToken )
            .ConfigureAwait( false );

        var message = QuicklinkRestoreMessage( fallbackId, fallback );

        var result = await ReadQuicklinksAsync( connection, transaction, message, cancellationToken )
            .ConfigureAwait( false );

        await transaction
            .CommitAsync( cancellationToken )
            .ConfigureAwait( false );

        return result;
    }

    private static string QuicklinkRestoreMessage( string? fallbackId, string? fallback )
    {
        if ( fallbackId is not null && fallback is null )
        {
            return "Quicklinks restored. Web fallback reset to the default.";
        }

        return "Quicklinks restored.";
    }

    private static bool HasInvalidQuicklinkRestore( QuicklinkState state )
    {
        return state.UserQuicklinks.Count > MaximumUserQuicklinks ||
               state.DisabledConfiguredIds.Count > MaximumDisabledQuicklinks ||
               state.UserQuicklinks.Any( entry => entry.Source != QuicklinkSources.User || !entry.Enabled || entry.CreatedUnixMs < 0 ) ||
               state.DisabledConfiguredIds.Any( id => string.IsNullOrWhiteSpace( id ) || id.Length > MaximumQuicklinkIdCharacters || id.Any( char.IsControl ) );
    }
}
