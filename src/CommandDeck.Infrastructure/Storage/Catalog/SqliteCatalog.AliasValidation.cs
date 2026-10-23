using Microsoft.Data.Sqlite;

namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    private const string QuicklinkAliasNamespace = "quicklink";

    internal static async ValueTask EnsureAliasAvailableAsync( SqliteConnection connection, SqliteTransaction transaction, string alias, string aliasNamespace,
        string? ownerId, IReadOnlyList<QuicklinkOptions> configured, CancellationToken cancellationToken )
    {
        AliasValidator.NormalizeAndValidate( alias );
        var configuredConflict = HasConfiguredAliasConflict( configured, alias, aliasNamespace, ownerId );
        await using var command = connection.CreateCommand( );
        command.Transaction = transaction;

        command.CommandText = """
                              SELECT EXISTS(SELECT 1 FROM aliases WHERE alias=$alias COLLATE NOCASE
                                            AND ($namespace!='bookmark' OR stable_id!=$owner))
                                  OR EXISTS(SELECT 1 FROM filter_aliases WHERE alias=$alias COLLATE NOCASE AND $namespace!='filter')
                                  OR EXISTS(SELECT 1 FROM user_quicklinks WHERE alias=$alias COLLATE NOCASE
                                            AND ($namespace!='quicklink' OR id!=$owner COLLATE NOCASE));
                              """;

        command.Parameters.AddWithValue( "$alias", alias );
        command.Parameters.AddWithValue( "$namespace", aliasNamespace );
        command.Parameters.AddWithValue( "$owner", ownerId ?? string.Empty );

        var conflict = ( long ) (await command
            .ExecuteScalarAsync( cancellationToken )
            .ConfigureAwait( false ))!;

        if ( HasAliasConflict( configuredConflict, conflict ) )
        {
            throw new InvalidOperationException( $"Alias '{alias}' is already assigned." );
        }
    }

    private static bool HasConfiguredAliasConflict( IReadOnlyList<QuicklinkOptions> configured, string alias, string aliasNamespace, string? ownerId )
    {
        return configured.Any( link =>
            string.Equals( link.Alias, alias, StringComparison.OrdinalIgnoreCase ) &&
            (aliasNamespace != QuicklinkAliasNamespace || !string.Equals( link.Id, ownerId, StringComparison.OrdinalIgnoreCase )) );
    }

    private static bool HasAliasConflict( bool configuredConflict, long conflict )
    {
        return configuredConflict || conflict != 0;
    }
}
