using Microsoft.Data.Sqlite;

namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    private const string FuzzySourceParameterPrefix = "$source_";

    private static string FuzzySourceParameterName( int index ) => FuzzySourceParameterPrefix + index;
    private const int MaximumBoundedTitleCandidates = 512;

    // Preserve the streaming title walk for dense sources, but inspect only its
    // first candidate budget. If any row is ineligible, use the source merge.
    private static async ValueTask<bool> ProbeBoundedTitleWalkAsync( SqliteConnection connection, IReadOnlyList<string> sourceIds, char firstCharacter,
        CancellationToken cancellationToken )
    {
        await using var command = connection.CreateCommand( );
        var parameters = string.Join( ", ", sourceIds.Select( ( _, index ) => FuzzySourceParameterName( index ) ) );

        command.CommandText = $"""
                               SELECT COUNT(*) FROM (
                                   SELECT stable_id, source_id, kind, path, title_in_search
                                   FROM catalog_items INDEXED BY ix_catalog_items_title_nocase
                                   WHERE title LIKE $pattern COLLATE NOCASE
                                   ORDER BY title COLLATE NOCASE LIMIT {MaximumBoundedTitleCandidates}
                               ) i
                               WHERE i.title_in_search = 1 AND i.source_id IN ({parameters})
                                 {HomeSearchClause( "i" )};
                               """;

        AddHomeSearchParameters( command );
        command.Parameters.AddWithValue( "$pattern", $"{firstCharacter}%" );

        for ( var index = 0; index < sourceIds.Count; index++ )
        {
            command.Parameters.AddWithValue( FuzzySourceParameterName( index ), sourceIds[index] );
        }

        var admitted = ( long ) (await command
            .ExecuteScalarAsync( cancellationToken )
            .ConfigureAwait( false ))!;

        return admitted == MaximumBoundedTitleCandidates;
    }
}
