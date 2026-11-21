using System.Text;
using Microsoft.Data.Sqlite;

namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    // Live windows carry only their class. The indexed application with that ID
    // or startup class supplies the icon; otherwise the lowercased class names it.
    public async ValueTask<IReadOnlyDictionary<string, string>> ResolveApplicationIconsAsync( IReadOnlyList<string> windowClasses,
        CancellationToken cancellationToken )
    {
        var requested = NormalizeWindowClasses( windowClasses );
        if ( requested.Length == 0 )
        {
            return new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        }

        await InitializeAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var command = CreateApplicationIconsCommand( connection, requested );
        Dictionary<string, string> icons = new( StringComparer.OrdinalIgnoreCase );

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            icons[reader.GetString( 0 )] = reader.GetString( 1 );
        }

        return icons;
    }

    // Every window search runs this. The partial applications index keeps it
    // off the file rows, which it would otherwise scan: the OR of
    // case-insensitive matches cannot use an index of its own. The kind
    // predicate is written as the index defines it.
    internal static SqliteCommand CreateApplicationIconsCommand( SqliteConnection connection, IReadOnlyList<string> windowClasses )
    {
        var command = connection.CreateCommand( );
        StringBuilder sql = new( "WITH requested(window_class) AS (VALUES " );

        for ( var index = 0; index < windowClasses.Count; index++ )
        {
            if ( index > 0 )
            {
                sql.Append( ',' );
            }

            var parameter = $"$window_class_{index.ToString( System.Globalization.CultureInfo.InvariantCulture )}";

            sql
                .Append( '(' )
                .Append( parameter )
                .Append( ')' );

            command.Parameters.AddWithValue( parameter, windowClasses[index] );
        }

        sql.Append( """
                    )
                    SELECT r.window_class,
                           COALESCE((
                               SELECT i.icon
                               FROM catalog_items i
                               WHERE i.kind = 'application'
                                 AND i.icon IS NOT NULL
                                 AND i.icon <> ''
                                 AND (
                                   (i.stable_id COLLATE NOCASE) = ('app:' || r.window_class)
                                   OR (i.startup_wm_class COLLATE NOCASE) = r.window_class
                                   OR (i.stable_id COLLATE NOCASE) =
                                      ('app:' || r.window_class || $desktop_extension))
                               ORDER BY CASE
                                   WHEN (i.stable_id COLLATE NOCASE) =
                                        ('app:' || r.window_class) THEN 1
                                   WHEN (i.startup_wm_class COLLATE NOCASE) =
                                        r.window_class THEN 2
                                   ELSE 3
                               END,
                               i.stable_id
                               LIMIT 1
                           ), lower(r.window_class))
                    FROM requested r;
                    """ );

        command.CommandText = sql.ToString( );
        command.Parameters.AddWithValue( "$desktop_extension", FileExtensions.Desktop );

        return command;
    }

    private static string[] NormalizeWindowClasses( IReadOnlyList<string> windowClasses )
    {
        return windowClasses
            .Select( static windowClass => windowClass.Trim( ) )
            .Where( static windowClass => windowClass.Length > 0 )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToArray( );
    }
}
