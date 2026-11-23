namespace CommandDeck;

public static class HerdrSearch
{
    public const string AttentionFilterId = "herdr-attention";
    public const string AgentsFilterId = "herdr-agents";
    public const string AgentResultPrefix = ResultIdPrefixes.Herdr + "agent:";
    public const string NewTabResultPrefix = ResultIdPrefixes.Herdr + "new-tab:";
    public const string WorkspaceIdMetadata = "herdrWorkspaceId";
    public const string AgentStatusMetadata = "herdrAgentStatus";

    public static bool NeedsAttention( SearchResult result ) =>
        result is { SourceId: SourceIds.Herdr, Kind: ResultKinds.Tab } && result.Metadata.GetValueOrDefault( AgentStatusMetadata ) == "blocked";

    // agents: lists one row per agent pane; no other Herdr row is an agent row.
    public static bool IsAgentRow( SearchResult result ) =>
        result is { SourceId: SourceIds.Herdr, Kind: ResultKinds.Tab } && result.Id.StartsWith( AgentResultPrefix, StringComparison.Ordinal );

    // A browsed space lists its tabs, and the row that creates a tab in it.
    public static bool MatchesScope( SearchResult result, string? workspaceId ) =>
        workspaceId is null ||
        result is { SourceId: SourceIds.Herdr, Kind: ResultKinds.Tab } && result.Metadata.GetValueOrDefault( WorkspaceIdMetadata ) == workspaceId ||
        result is { SourceId: SourceIds.Herdr, Kind: ResultKinds.Workspace } && result.Id == NewTabResultPrefix + workspaceId;

    // Browsing a space applies the Herdr tabs filter, and the space's create
    // offer belongs to that view even though it is not a tab row.
    public static bool IsCreateOfferInView( SearchResult result, string? workspaceId, ParsedQuery query ) =>
        workspaceId is not null &&
        result is { SourceId: SourceIds.Herdr, Kind: ResultKinds.Workspace } &&
        result.Id == NewTabResultPrefix + workspaceId &&
        query.IncludedFilters.All( static filter => filter.Id == SearchFilterIds.HerdrTabs );

    public static bool MatchesAttention( ParsedQuery query, SearchResult result ) =>
        (query.IncludedFilters.Count == 0 ||
         query.IncludedFilters.Any( filter =>
             filter.SourceId == SourceIds.Herdr &&
             (filter.Kind is null || filter.Kind == result.Kind) &&
             (filter.Id != AttentionFilterId || NeedsAttention( result )) )) &&
        (!query.ExcludedFilters.Any( static filter => filter.Id == AttentionFilterId ) || !NeedsAttention( result ));
}
