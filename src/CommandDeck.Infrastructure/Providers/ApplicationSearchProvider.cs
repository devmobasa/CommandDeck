namespace CommandDeck;

public sealed class ApplicationSearchProvider( ICatalogQueryStore catalog ) : ICommandProvider
{
    public string Id => ProviderIds.ApplicationsFastPath;

    public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        if ( AreApplicationsDisabled( request ) )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        return catalog.SearchSourcesAsync( query, [SourceIds.Applications], request.Limit, cancellationToken );
    }

    private static bool AreApplicationsDisabled( SearchRequest request )
    {
        return request.EnabledSearchSources is { } sources && !sources.Contains( SourceIds.Applications, StringComparer.Ordinal );
    }
}
