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
        if ( !client.IsInstalled )
        {
            return [];
        }

        var inventoryTask = client
            .ListAsync( cancellationToken )
            .AsTask( );

        var agentsTask = client
            .ListAgentsAsync( cancellationToken )
            .AsTask( );

        await Task
            .WhenAll( inventoryTask, agentsTask )
            .ConfigureAwait( false );

        cancellationToken.ThrowIfCancellationRequested( );

        if ( inventoryTask.Result is not { } inventory || agentsTask.Result is not { } agents )
        {
            return [];
        }

        var spaces = inventory
            .Workspaces.GroupBy( static workspace => workspace.Id, StringComparer.Ordinal )
            .ToDictionary( static group => group.Key, static group => group.First( ), StringComparer.Ordinal );

        var tabs = inventory
            .Tabs.GroupBy( static tab => tab.Id, StringComparer.Ordinal )
            .ToDictionary( static group => group.Key, static group => group.First( ), StringComparer.Ordinal );

        var clauses = query.Expression.Clauses;

        return agents
            .Where( agent => request.HerdrWorkspaceId is null || agent.WorkspaceId == request.HerdrWorkspaceId )
            .Select( agent => (Agent: agent, Tab: tabs.GetValueOrDefault( agent.TabId ), Space: spaces.GetValueOrDefault( agent.WorkspaceId )) )
            .Select( entry => (entry.Agent, entry.Tab, entry.Space, Result: AgentResult( entry.Agent, entry.Tab, entry.Space )) )
            .Where( entry => AgentMatches( entry.Result, entry.Agent, clauses ) )
            .OrderBy( static entry => AgentUrgency( entry.Agent.AgentStatus ) )
            .ThenBy( static entry => entry.Space?.Number ?? long.MaxValue )
            .ThenBy( static entry => entry.Tab?.Number ?? long.MaxValue )
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

    private static SearchResult AgentResult( HerdrAgent agent, HerdrTab? tab, HerdrWorkspace? space )
    {
        var name = agent.Name.Length > 0 ? agent.Name : "Agent";
        var tabTitle = AgentTabTitle( tab );
        var spaceLabel = AgentSpaceLabel( space );
        var status = DescribeAgentStatus( agent.AgentStatus );

        return new SearchResult
        {
            Id = HerdrSearch.AgentResultPrefix + agent.PaneId,
            Kind = ResultKinds.Tab,
            Title = tabTitle,
            Subtitle = $"{name} · {status} · {spaceLabel}" + (agent.Focused ? " · focused" : string.Empty),
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
                new DetailField( "Herdr pane ID", agent.PaneId, Monospace: true )
            ],
            Actions = [FocusAction( SwitchTabActionId, "Switch Herdr Tab", HerdrTargetKinds.Tab, agent.TabId )],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [TargetKindMetadata] = HerdrTargetKinds.Tab,
                [TargetIdMetadata] = agent.TabId,
                [WorkspaceIdMetadata] = agent.WorkspaceId,
                [WorkspaceLabelMetadata] = space?.Label ?? string.Empty,
                [FocusedMetadata] = agent.Focused ? "true" : "false",
                [AgentStatusMetadata] = agent.AgentStatus,
                [AgentNameMetadata] = agent.Name,
                [PaneIdMetadata] = agent.PaneId
            }
        };
    }

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
