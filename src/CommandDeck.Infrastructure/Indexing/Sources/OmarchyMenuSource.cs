using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

// The parsed Omarchy menu for submenu browsing. It is read from the same files
// the index scans and parsed again only when one of them appears, disappears,
// or changes size or modification time. Two searches that both find it stale
// may both parse; either result is current.
public sealed class OmarchyMenuSource
{
    private readonly Func<IEnumerable<string>> _candidates;
    private Parsed? _parsed;

    private sealed record Parsed( string Signature, OmarchyMenu Menu );

    public OmarchyMenuSource( Func<IEnumerable<string>>? candidates = null )
    {
        _candidates = candidates ?? OmarchyMenuScanner.GetCandidates;
    }

    public async ValueTask<OmarchyMenu> LoadAsync( CancellationToken cancellationToken )
    {
        var candidates = GetDistinctCandidates( );
        var signature = Signature( candidates );
        var parsed = Volatile.Read( ref _parsed );
        if ( IsCachedSignature( signature, parsed ) )
        {
            return parsed.Menu;
        }

        var documents = await OmarchyMenuScanner
            .ReadDocumentsAsync( candidates, cancellationToken )
            .ConfigureAwait( false );

        var menu = OmarchyMenu.Parse( documents );
        Volatile.Write( ref _parsed, new Parsed( signature, menu ) );

        return menu;
    }

    private static string Signature( IEnumerable<string> candidates ) =>
        string.Join( '\n', candidates.Select( static path =>
        {
            FileInfo file = new( path );
            if ( file.Exists )
            {
                return $"{path}\u001f{file.Length}\u001f{file.LastWriteTimeUtc.Ticks}";
            }

            return $"{path}\u001f-";
        } ) );

    private static bool IsCachedSignature( string signature, [NotNullWhen( true )] Parsed? parsed )
    {
        return parsed is not null && parsed.Signature == signature;
    }

    private string[] GetDistinctCandidates( )
    {
        return _candidates( )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );
    }
}
