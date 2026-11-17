namespace CommandDeck;

public static class SearchFilterRegistry
{
    private static readonly FilterDescriptor[] Descriptors =
    [
        Filter( SearchFilterIds.Projects, "Projects", "proj:", ProviderIds.Projects, SourceIds.Projects ),
        Filter( SearchFilterIds.Snippets, "Snippets", "snip:", ProviderIds.Snippets, SourceIds.Snippets ),
        Filter( SearchFilterIds.Scripts, "Script commands", "script:", ProviderIds.Scripts, SourceIds.Scripts ),
        Filter( SearchFilterIds.Clipboard, "Clipboard history", "clip:", ProviderIds.Clipboard, SourceIds.Clipboard ),
        Filter( SearchFilterIds.Folders, "Browse folders", "browse:", ProviderIds.Folders, SourceIds.Folders ),
        Filter( SearchFilterIds.OmarchyControl, "Omarchy Control", "oma:", ProviderIds.Catalog, SourceIds.OmarchyControl ),
        Filter( SearchFilterIds.Applications, "Applications", "app:", ProviderIds.Catalog, SourceIds.Applications, ResultKinds.Application ),
        Filter( SearchFilterIds.Files, "Files", "file:", ProviderIds.Catalog, SourceIds.Files, ResultKinds.File,
            "text:", "files:" ),
        Filter( SearchFilterIds.Windows, "Windows", "win:", ProviderIds.Hyprland, SourceIds.WindowsWorkspaces, ResultKinds.Window,
            "window:" ),
        Filter( SearchFilterIds.Workspaces, "Workspaces", "workspace:", ProviderIds.Hyprland, SourceIds.WindowsWorkspaces, ResultKinds.Workspace,
            "ws:", "workspaces:" ),
        Filter( SearchFilterIds.Herdr, "Herdr", "herdr:", ProviderIds.Herdr, SourceIds.Herdr ),
        Filter( SearchFilterIds.Hey, "HEY", "hey:", ProviderIds.Hey, SourceIds.Hey, null,
            "mail:" ),
        Filter( SearchFilterIds.HerdrSpaces, "Herdr spaces", "hspace:", ProviderIds.Herdr, SourceIds.Herdr, ResultKinds.Workspace,
            "herdr-space:", "hspaces:" ),
        Filter( SearchFilterIds.HerdrTabs, "Herdr tabs", "htab:", ProviderIds.Herdr, SourceIds.Herdr, ResultKinds.Tab,
            "herdr-tab:", "htabs:" ),
        Filter( HerdrSearch.AttentionFilterId, "Needs attention", "hattention:", ProviderIds.Herdr, SourceIds.Herdr, ResultKinds.Tab,
            "herdr-attention:" ),
        Filter( HerdrSearch.AgentsFilterId, "Herdr agents", "agents:", ProviderIds.Herdr, SourceIds.Herdr, ResultKinds.Tab,
            "hagents:", "herdr-agents:" ),
        Filter( SearchFilterIds.Configuration, "Configuration", "cfg:", ProviderIds.Catalog, SourceIds.Configuration, ResultKinds.Configuration,
            "config:" ),
        Filter( SearchFilterIds.Commands, "Commands", "cmd:", ProviderIds.Catalog, SourceIds.CommandsQuicklinks, ResultKinds.CustomCommand ),
        Filter( SearchFilterIds.Quicklinks, "Quicklinks", "link:", ProviderIds.Catalog, SourceIds.CommandsQuicklinks, ResultKinds.Quicklink ),
        Filter( SearchFilterIds.SystemMenu, "System Menu", "system:", ProviderIds.Catalog, SourceIds.OmarchySystem ),
        Filter( SearchFilterIds.Ai, "AI", "ai:", ProviderIds.Assistant, SourceIds.Ai, null,
            AgentQueryPrefixes.Codex, "ask:", AgentQueryPrefixes.Claude, AgentQueryPrefixes.Cursor, AgentQueryPrefixes.Grok ),
        Filter( SearchFilterIds.Services, "Services", "svc:", ProviderIds.SystemdUnits, SourceIds.SystemdUnits, null,
            "unit:", "service:" ),
        Filter( SearchFilterIds.Timezone, "Timezone", "tz:", ProviderIds.Timezone, SourceIds.Timezone, null,
            "time:" ),
        Filter( SearchFilterIds.WebSearch, "Web Search", "web:", ProviderIds.WebSearch, SourceIds.WebSearch ),
        Filter( SearchFilterIds.Emoji, "Emoji", "emoji:", ProviderIds.Emoji, SourceIds.Emoji, ResultKinds.Emoji,
            "e:" ),
        Filter( SearchFilterIds.DesktopControls, "Desktop controls", "ctl:", ProviderIds.DesktopControls, SourceIds.DesktopControls, null,
            "control:", "controls:" ),
        Filter( ProcessResultIds.FilterId, "Processes", "kill:", ProviderIds.Processes, SourceIds.Processes, ResultKinds.Process,
            "proc:", "process:", "processes:" ),
        Filter( SearchFilterIds.Themes, "Themes", "theme:", ProviderIds.Themes, SourceIds.Themes, ResultKinds.Theme,
            "themes:" )
    ];

    private static readonly Dictionary<string, FilterDescriptor> ById = Descriptors.ToDictionary( static value => value.Id, StringComparer.OrdinalIgnoreCase );

    private static readonly Dictionary<string, FilterDescriptor> ByToken = Descriptors
        .SelectMany( static value => value.Aliases.Prepend( value.Token ) )
        .ToDictionary( static token => token.TrimEnd( ':' ),
            token => Descriptors.First( value =>
                value.Token.Equals( token, StringComparison.OrdinalIgnoreCase ) || value.Aliases.Contains( token, StringComparer.OrdinalIgnoreCase ) ),
            StringComparer.OrdinalIgnoreCase );

    private static readonly string[] ReservedKeywords = ["home", "settings"];

    // The names the first release, v0.1.0-preview.1, reserved. Each later
    // release reserves the names of the filters it adds, and an alias saved
    // before then may already use one: "agents" in preview.2, "theme" after it.
    // Saved aliases, backups, and the configuration file are checked against
    // this list, so they keep loading; a new or changed alias may not use any
    // reserved name. A built-in filter still wins over a filter alias it shadows.
    private static readonly string[] FirstReleaseReservedAliases =
    [
        "ai", "app", "ask", "browse", "cfg", "claude", "clip", "cmd", "codex", "config", "control", "controls", "ctl", "cursor", "e", "emoji",
        "file", "files", "grok", "hattention", "herdr", "herdr-attention", "herdr-space", "herdr-tab", "hey", "home", "hspace", "hspaces", "htab",
        "htabs", "kill", "link", "mail", "oma", "proc", "process", "processes", "proj", "script", "service", "settings", "snip", "svc", "system",
        "text", "time", "tz", "unit", "web", "win", "window", "workspace", "workspaces", "ws"
    ];

    public static IReadOnlyList<FilterDescriptor> All => Descriptors;

    public static IReadOnlyList<string> ReservedAliases { get; } = ByToken
        .Keys.Concat( ReservedKeywords )
        .ToArray( );

    public static FilterDescriptor? FindById( string? id )
    {
        if ( string.IsNullOrWhiteSpace( id ) )
        {
            return null;
        }

        return ById.GetValueOrDefault( id );
    }

    public static FilterDescriptor? FindByToken( string? token )
    {
        if ( string.IsNullOrWhiteSpace( token ) )
        {
            return null;
        }

        var normalized = NormalizeToken( token );

        return ByToken.GetValueOrDefault( normalized );
    }

    private static string NormalizeToken( string token ) => token
        .Trim( )
        .TrimEnd( ':' );

    public static bool IsReserved( string value ) =>
        FindByToken( value ) is not null || ReservedKeywords.Contains( value, StringComparer.OrdinalIgnoreCase );

    // Whether an alias that was already saved, or written in the configuration
    // file, clashes with launcher syntax: only a name reserved since the first release does.
    public static bool IsReservedForSavedAlias( string value ) =>
        IsReserved( value ) && FirstReleaseReservedAliases.Contains( NormalizeToken( value ), StringComparer.OrdinalIgnoreCase );

    public static bool Matches( FilterDescriptor filter, SearchResult result ) =>
        string.Equals( filter.SourceId, result.SourceId, StringComparison.Ordinal ) &&
        (filter.Kind is null || string.Equals( filter.Kind, result.Kind, StringComparison.Ordinal ));

    public static IReadOnlyList<FilterDescriptor> MergeUserAliases( IReadOnlyList<FilterAlias> userAliases ) => Descriptors
        .Select( descriptor => WithUserAliases( descriptor, userAliases ) )
        .ToArray( );

    private static FilterDescriptor WithUserAliases( FilterDescriptor descriptor, IReadOnlyList<FilterAlias> userAliases ) => new( )
    {
        Id = descriptor.Id,
        Label = descriptor.Label,
        Token = descriptor.Token,
        ProviderId = descriptor.ProviderId,
        SourceId = descriptor.SourceId,
        Kind = descriptor.Kind,
        Aliases = [.. descriptor.Aliases, .. MatchingAliases( descriptor, userAliases )]
    };

    private static IEnumerable<string> MatchingAliases( FilterDescriptor descriptor, IReadOnlyList<FilterAlias> userAliases ) => userAliases
        .Where( alias => string.Equals( alias.FilterId, descriptor.Id, StringComparison.OrdinalIgnoreCase ) )
        .Select( static alias => alias.Alias + ":" );

    private static FilterDescriptor Filter( string id, string label, string token, string provider, string source, string? kind = null,
        params string[] aliases ) => new( )
        {
            Id = id,
            Label = label,
            Token = token,
            ProviderId = provider,
            SourceId = source,
            Kind = kind,
            Aliases = [.. aliases]
        };
}
