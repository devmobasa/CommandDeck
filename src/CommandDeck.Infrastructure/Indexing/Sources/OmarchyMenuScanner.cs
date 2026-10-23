using System.Runtime.CompilerServices;

namespace CommandDeck;

public static class OmarchyMenuScanner
{
    private const string MenuFileName = "omarchy-menu.jsonc";

    public static async IAsyncEnumerable<IndexedItem> ScanAsync( [EnumeratorCancellation] CancellationToken cancellationToken )
    {
        var documents = await ReadDocumentsAsync( GetCandidates( ), cancellationToken )
            .ConfigureAwait( false );

        foreach ( var item in ParseDocuments( documents ) )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            yield return item;
        }
    }

    public static IReadOnlyList<IndexedItem> ParseDocuments( IEnumerable<OmarchyMenuDocument> documents ) => OmarchyMenu.Parse( documents ).Rows;

    // The menu documents that exist, in merge order; unreadable ones are skipped.
    public static async ValueTask<IReadOnlyList<OmarchyMenuDocument>> ReadDocumentsAsync( IEnumerable<string> candidates, CancellationToken cancellationToken )
    {
        List<OmarchyMenuDocument> documents = [];

        foreach ( var candidate in candidates.Distinct( StringComparer.Ordinal ) )
        {
            if ( !File.Exists( candidate ) )
            {
                continue;
            }

            try
            {
                documents.Add( new OmarchyMenuDocument( candidate, await File
                    .ReadAllTextAsync( candidate, cancellationToken )
                    .ConfigureAwait( false ) ) );
            }
            catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
            {
            }
        }

        return documents;
    }

    public static IEnumerable<string> GetCandidates( )
    {
        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        var configHome = PathExpansion.GetConfigHome( home );
        var omarchyPath = Environment.GetEnvironmentVariable( "OMARCHY_PATH" );
        if ( !string.IsNullOrWhiteSpace( omarchyPath ) )
        {
            yield return Path.Combine( omarchyPath, "default", "omarchy", MenuFileName );

            yield return Path.Combine( omarchyPath, "config", "omarchy", "extensions", MenuFileName );
        }

        yield return "/usr/share/omarchy/default/omarchy/omarchy-menu.jsonc";

        yield return "/usr/share/omarchy/config/omarchy/extensions/omarchy-menu.jsonc";

        yield return Path.Combine( home, ".local", "share", "omarchy", "default", "omarchy",
            MenuFileName );

        yield return Path.Combine( home, ".local", "share", "omarchy", "config", "omarchy",
            "extensions", MenuFileName );

        yield return Path.Combine( configHome, "omarchy", "extensions", MenuFileName );
    }
}

public sealed record OmarchyMenuDocument( string Path, string Jsonc );
