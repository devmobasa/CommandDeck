using System.Globalization;

namespace CommandDeck;

public sealed partial class HerdrProvider
{
    public const string AgentNameMetadata = "herdrAgent";
    public const string PaneIdMetadata = "herdrPaneId";

    private const string UnknownTabLabel = "Unknown tab";

    // agents: lists every agent across spaces, most urgent first: blocked,
    // working, done, idle, then unknown. Text narrows the list by tab, agent, or
    // space name, but the order stays by urgency so a waiting agent is never
    // buried. Each row switches to its agent's tab.
    private async ValueTask<IReadOnlyList<SearchResult>> SearchAgentsAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        if ( !IsInstalled )
        {
            return [];
        }

        (var inventory, var agents) = await ListWithAgentsAsync( request,
                ( partial, listed ) => ProjectAgents( partial, listed, query, request, ContextFor( partial, request.HerdrWorkspaceId ) ).Length > 0,
                cancellationToken )
            .ConfigureAwait( false );

        cancellationToken.ThrowIfCancellationRequested( );

        return inventory is null || agents is null ? [] : ProjectAgents( inventory, agents, query, request, ContextFor( inventory, request.HerdrWorkspaceId ) );
    }

    private static SearchResult[] ProjectAgents( HerdrInventory inventory, IReadOnlyList<HerdrAgent> agents, ParsedQuery query, SearchRequest request,
        RowContext context )
    {
        // Each app names its agents' spaces and tabs from its own lists.
        var spaces = inventory
            .Workspaces.GroupBy( static workspace => (workspace.App, workspace.Id) )
            .ToDictionary( static group => group.Key, static group => group.First( ) );

        var tabs = inventory
            .Tabs.GroupBy( static tab => (tab.App, tab.Id) )
            .ToDictionary( static group => group.Key, static group => group.First( ) );

        // Tabs whose names repeat within a space are told apart by their number.
        var repeated = inventory
            .Tabs.GroupBy( static tab => (tab.App, tab.WorkspaceId, Title: AgentTabTitle( tab )) )
            .Where( static group => group.Count( ) > 1 )
            .SelectMany( static group => group )
            .Select( static tab => (tab.App, tab.Id) )
            .ToHashSet( );

        var clauses = query.Expression.Clauses;

        return agents
            .Where( agent => request.HerdrWorkspaceId is null || HerdrScopes.Of( agent.App, agent.WorkspaceId ) == request.HerdrWorkspaceId )
            .Select( agent => (Agent: agent, Tab: tabs.GetValueOrDefault( (agent.App, agent.TabId) ),
                Space: spaces.GetValueOrDefault( (agent.App, agent.WorkspaceId) )) )
            .Select( entry => (entry.Agent, entry.Tab, entry.Space, Result: AgentResult( entry.Agent, entry.Tab, entry.Space, context,
                    entry.Tab is not null && repeated.Contains( (entry.Tab.App, entry.Tab.Id) ) )) )
            .Where( entry => AgentMatches( entry.Result, entry.Agent, clauses ) && HerdrSearch.MatchesAttention( query, entry.Result ) )
            .OrderBy( static entry => AgentUrgency( entry.Agent.AgentStatus ) )
            .ThenBy( static entry => entry.Space?.Number ?? long.MaxValue )
            .ThenBy( static entry => entry.Tab?.Number ?? long.MaxValue )
            .ThenBy( static entry => entry.Agent.Position )
            .ThenBy( static entry => entry.Agent.PaneId, StringComparer.Ordinal )
            .Take( Math.Clamp( request.Limit, 1, MaximumResults ) )
            .Select( static ( entry, ordinal ) => new SearchResult( entry.Result ) { Score = BrowseBaseline - (ordinal * BrowseStep) } )
            .ToArray( );
    }

    // Unqualified text also matches the agent's name; fields and exclusions
    // follow the same rules as tab rows.
    private static bool AgentMatches( SearchResult result, HerdrAgent agent, List<QueryClause> clauses )
    {
        foreach ( var clause in clauses )
        {
            var score = ClauseScore( result, clause );
            if ( IsUnqualifiedIncludedClause( clause ) && agent.Name.Length > 0 )
            {
                score = Math.Max( score, LabelScore( clause.Text, agent.Name ) );
            }

            if ( clause.Excluded ? score > 0 : score <= 0 )
            {
                return false;
            }
        }

        return true;
    }

    private static string AgentSpaceLabel( HerdrWorkspace? space )
    {
        if ( space is null )
        {
            return UnknownSpaceLabel;
        }

        return space.Label.Length > 0 ? space.Label : $"Space {Number( space.Number )}";
    }

    // The title starts with what tells two agents apart, so a narrow row that
    // cuts its subtitle still keeps it. A desktop agent leads with its space
    // and tab numbers, then its terminal's position in a split tab, then the
    // tab's name unless that name is only its number. A terminal-app agent
    // shows its tab, numbered when another tab in its space has the same name.
    private static string AgentTitle( HerdrAgent agent, HerdrTab? tab, HerdrWorkspace? space, bool repeatedTab )
    {
        var tabTitle = AgentTabTitle( tab );
        var ownNumber = tab is null ? null : $"Tab {Number( tab.Number )}";

        if ( DesktopAgentPosition( agent, tab, space ) is { Length: > 0 } position )
        {
            return tabTitle == ownNumber ? position : $"{position} · {tabTitle}";
        }

        var plain = repeatedTab && tab is not null && tabTitle != ownNumber ? $"{ownNumber} · {tabTitle}" : tabTitle;

        return agent.Position > 0 ? $"Terminal {Number( agent.Position )} · {plain}" : plain;
    }

    // A desktop agent's space, tab, and terminal numbers, which lead its title;
    // a compact row keeps room for them. Empty for terminal-app agents.
    private static string DesktopAgentPosition( HerdrAgent agent, HerdrTab? tab, HerdrWorkspace? space )
    {
        if ( !agent.App || tab is null || space is null )
        {
            return string.Empty;
        }

        var position = $"Space {Number( space.Number )} · Tab {Number( tab.Number )}";

        return agent.Position > 0 ? $"{position} · Terminal {Number( agent.Position )}" : position;
    }

    private static SearchResult AgentResult( HerdrAgent agent, HerdrTab? tab, HerdrWorkspace? space, RowContext context, bool repeatedTab )
    {
        var name = agent.Name.Length > 0 ? agent.Name : "Agent";
        var tabTitle = AgentTabTitle( tab );
        var title = AgentTitle( agent, tab, space, repeatedTab );
        var spaceLabel = AgentSpaceLabel( space );
        var status = DescribeAgentStatus( agent.AgentStatus );

        return new SearchResult
        {
            Id = HerdrSearch.AgentResultPrefix + (agent.App ? HerdrScopes.AppPanePrefix : string.Empty) + agent.PaneId,
            Kind = ResultKinds.Tab,
            Title = title,
            Subtitle = context.Prefix( agent.App, space?.Machine ) + $"{name} · {status} · {spaceLabel}" + (agent.Focused ? " · focused" : string.Empty),
            Icon = Icon,
            ProviderId = ProviderIds.Herdr,
            SourceId = SourceIds.Herdr,
            Section = ResultSections.HerdrAgents,
            Details =
            [
                new DetailField( "Agent", name ),
                new DetailField( "Status", status ),
                new DetailField( "Space", spaceLabel ),
                new DetailField( "Tab", tabTitle ),
                .. AgentPositionDetail( agent ),
                new DetailField( "Herdr pane ID", agent.PaneId, Monospace: true )
            ],
            Actions = [AgentFocusAction( agent )],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [TargetKindMetadata] = AgentTargetKind( agent ),
                [TargetIdMetadata] = AgentTargetId( agent ),
                [WorkspaceIdMetadata] = HerdrScopes.Of( agent.App, agent.WorkspaceId ),
                [WorkspaceLabelMetadata] = space?.Label ?? string.Empty,
                [MachineMetadata] = space?.Machine ?? string.Empty,
                [TitlePositionMetadata] = DesktopAgentPosition( agent, tab, space ),
                [FocusedMetadata] = agent.Focused ? "true" : "false",
                [AgentStatusMetadata] = agent.AgentStatus,
                [AgentNameMetadata] = agent.Name,
                [PaneIdMetadata] = agent.PaneId
            }
        };
    }

    // A desktop agent's own pane is focused, which also shows its tab and
    // space; the terminal app switches to the agent's tab.
    private static CommandAction AgentFocusAction( HerdrAgent agent ) => agent.App
        ? FocusAction( SwitchTabActionId, "Switch to agent", HerdrTargetKinds.AppPane, agent.PaneId )
        : FocusAction( SwitchTabActionId, "Switch Herdr Tab", HerdrTargetKinds.Tab, agent.TabId );

    private static DetailField[] AgentPositionDetail( HerdrAgent agent ) =>
        agent.Position > 0 ? [new DetailField( "Terminal", $"{Number( agent.Position )} in its tab" )] : [];

    private static string AgentTargetKind( HerdrAgent agent ) => agent.App ? HerdrTargetKinds.AppPane : HerdrTargetKinds.Tab;

    private static string AgentTargetId( HerdrAgent agent ) => agent.App ? agent.PaneId : agent.TabId;

    // Herdr labels an unnamed tab with its number, which says nothing on its own.
    private static string AgentTabTitle( HerdrTab? tab )
    {
        if ( tab is null )
        {
            return UnknownTabLabel;
        }

        var number = Number( tab.Number );

        return tab.Label.Length == 0 || tab.Label == number ? $"Tab {number}" : tab.Label;
    }

    private static string DescribeAgentStatus( string status ) => status switch
    {
        "blocked" => "Needs attention",
        "working" => "Working",
        "done" => "Done",
        "idle" => "Idle",
        _ => "Status unknown"
    };

    private static int AgentUrgency( string status ) => status switch
    {
        "blocked" => 0,
        "working" => 1,
        "done" => 2,
        "idle" => 3,
        _ => 4
    };

    private static string Number( long value ) => value.ToString( CultureInfo.InvariantCulture );
}
