namespace CommandDeck;

/// <summary>Chosen by trusted in-process callers; never a wire request field.</summary>
public enum SearchPurpose
{
    Interactive, Suggestion, AssistantTool
}

public static class ClipboardPrivacy
{
    public static bool ReferencesHistory( QueryExpression expression ) =>
        expression
            .IncludeFilterIds.Concat( expression.ExcludeFilterIds )
            .Contains( SearchFilterIds.Clipboard, StringComparer.OrdinalIgnoreCase );

    public static bool IsHistory( SearchResult result ) =>
        result.SourceId == SourceIds.Clipboard ||
        result.ProviderId == ProviderIds.Clipboard ||
        result.Id.StartsWith( ResultIdPrefixes.Clipboard, StringComparison.Ordinal ) ||
        result
            .Metadata.GetValueOrDefault( ResultMetadataKeys.CatalogResultId, "" )
            .StartsWith( ResultIdPrefixes.Clipboard, StringComparison.Ordinal ) ||
        result.Actions.Any( static action => action.Kind == ActionKinds.ClipboardHistory || SnippetActionPayload.IsHistoryReader( action ) );

    public static bool ReferencesHistory( ParsedQuery query ) =>
        string.Equals( query.ProviderFilter, ProviderIds.Clipboard, StringComparison.OrdinalIgnoreCase ) ||
        query
            .IncludedFilters.Concat( query.ExcludedFilters )
            .Any( static filter => filter.SourceId == SourceIds.Clipboard || filter.ProviderId == ProviderIds.Clipboard );
}
