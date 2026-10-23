namespace CommandDeck;

/// <summary>
/// Section IDs that are not a source ID. A provider's own section, and the
/// section that groups a source's ranked rows, use the source ID instead.
/// </summary>
public static class ResultSectionIds
{
    public const string TopMatch = "top-match";
    public const string SavedSearches = "saved-searches";
    public const string Bookmarks = "bookmarks";
    public const string RecentFrequent = "recent-frequent";
    public const string Omarchy = "omarchy";
    public const string OmarchyMenu = "omarchy-menu";
    public const string Assistant = "assistant";
    public const string Fallback = "fallback";
    public const string HeyFallback = "hey-fallback";
}

/// <summary>The sections with a fixed title and order.</summary>
public static class ResultSections
{
    public static readonly ResultSection TopMatch = new( ) { Id = ResultSectionIds.TopMatch, Title = "Top Match", Order = 0 };
    public static readonly ResultSection TopMatches = new( ) { Id = ResultSectionIds.TopMatch, Title = "Top Matches", Order = 0 };
    public static readonly ResultSection OmarchyMenu = new( ) { Id = ResultSectionIds.OmarchyMenu, Title = "Omarchy", Order = 0 };

    // Home
    public static readonly ResultSection SavedSearches = new( ) { Id = ResultSectionIds.SavedSearches, Title = "Saved Searches", Order = 0 };
    public static readonly ResultSection Bookmarks = new( ) { Id = ResultSectionIds.Bookmarks, Title = "Bookmarks", Order = 1 };
    public static readonly ResultSection RecentFrequent = new( ) { Id = ResultSectionIds.RecentFrequent, Title = "Recent & Frequent", Order = 2 };
    public static readonly ResultSection WindowsWorkspaces = new( ) { Id = SourceIds.WindowsWorkspaces, Title = "Windows & Workspaces", Order = 3 };
    public static readonly ResultSection Herdr = new( ) { Id = SourceIds.Herdr, Title = "Herdr", Order = 3 };
    public static readonly ResultSection Omarchy = new( ) { Id = ResultSectionIds.Omarchy, Title = "Omarchy", Order = 4 };

    // Provider rows
    public static readonly ResultSection Assistant = new( ) { Id = ResultSectionIds.Assistant, Title = "Assistant", Order = 1 };
    public static readonly ResultSection Projects = new( ) { Id = SourceIds.Projects, Title = "Projects", Order = 6 };
    public static readonly ResultSection Snippets = new( ) { Id = SourceIds.Snippets, Title = "Snippets", Order = 6 };
    public static readonly ResultSection Scripts = new( ) { Id = SourceIds.Scripts, Title = "Script commands", Order = 6 };
    public static readonly ResultSection Clipboard = new( ) { Id = SourceIds.Clipboard, Title = "Clipboard history", Order = 6 };
    public static readonly ResultSection SystemdUnits = new( ) { Id = SourceIds.SystemdUnits, Title = "Services & timers", Order = 6 };
    public static readonly ResultSection Timezone = new( ) { Id = SourceIds.Timezone, Title = "Timezone", Order = 7 };
    public static readonly ResultSection WebSearch = new( ) { Id = SourceIds.WebSearch, Title = "Web Search", Order = 10 };
    public static readonly ResultSection Fallback = new( ) { Id = ResultSectionIds.Fallback, Title = "Search the Web", Order = 100 };
    public static readonly ResultSection HeyFallback = new( ) { Id = ResultSectionIds.HeyFallback, Title = "HEY", Order = 101 };
}
