using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

/// <summary>
/// Opportunistic live search over Herdr spaces (workspaces) and tabs. Runs no
/// process unless the Herdr source is enabled, the query can include Herdr
/// rows, and the CLI is on PATH. Matching and ranking are local; Herdr only
/// supplies the inventory and, on activation, the focus switch.
/// </summary>
public sealed partial class HerdrProvider( HerdrClient client ) : ICommandProvider
{
    private const string NameInput = "Name";
    private const string BrowseTabsActionId = "browse-tabs";
    private const string RenameTabActionId = "rename-tab";
    private const string NewTabActionId = "new-tab";

    public const string Icon = "utilities-terminal";
    public const string SwitchSpaceActionId = "switch-space";
    public const string SwitchTabActionId = "switch-tab";
    public const string TargetKindMetadata = "herdrTargetKind";
    public const string TargetIdMetadata = "herdrTargetId";
    public const string WorkspaceIdMetadata = HerdrSearch.WorkspaceIdMetadata;
    public const string WorkspaceLabelMetadata = "herdrWorkspaceLabel";
    public const string FocusedMetadata = "herdrFocused";
    public const string AgentStatusMetadata = HerdrSearch.AgentStatusMetadata;
    public const string UnknownSpaceLabel = "Unknown space";
    public const int MaximumResults = 512;

    private const double ParentSpaceWeight = 0.84;
    private const double FocusedBonus = 0.03;

    public string Id => ProviderIds.Herdr;

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( query );
        ArgumentNullException.ThrowIfNull( request );

        cancellationToken.ThrowIfCancellationRequested( );

        if ( request.EnabledSearchSources?.Contains( SourceIds.Herdr, StringComparer.Ordinal ) != true )
        {
            return [];
        }

        (var includeSpaces, var includeTabs) = IncludedKinds( query );

        if ( request.HerdrWorkspaceId is not null )
        {
            if ( !HerdrClient.IsValidIdentifier( request.HerdrWorkspaceId ) )
            {
                return [];
            }

            includeSpaces = false;
        }

        if ( !includeSpaces && !includeTabs )
        {
            return [];
        }

        // An empty query is only a browse through herdr: or the Home section; a
        // query that is empty because it only excludes something is not.
        var text = query.Text.Trim( );
        var explicitlyScoped = IsBrowseRequest( query, request );
        var browse = text.Length == 0 && (explicitlyScoped || query.ExcludedFilters.Count == 0);
        if ( IsEmptySearch( text, browse ) || !client.IsInstalled )
        {
            return [];
        }

        var inventory = await client
            .ListAsync( cancellationToken )
            .ConfigureAwait( false );

        cancellationToken.ThrowIfCancellationRequested( );

        if ( inventory is null )
        {
            return [];
        }

        var results = Project( inventory, query, includeSpaces, includeTabs, request.HerdrWorkspaceId );

        return OrderSearchResults( request, results );
    }

    private static (bool Spaces, bool Tabs) IncludedKinds( ParsedQuery query )
    {
        var herdrIncluded = query
            .IncludedFilters.Where( static filter => filter.SourceId == SourceIds.Herdr )
            .ToArray( );

        if ( HasOnlyOtherFilters( query, herdrIncluded ) )
        {
            return (false, false);
        }

        var spaces = IncludesWorkspaces( herdrIncluded );
        var tabs = IncludesTabs( herdrIncluded );

        foreach ( var filter in query.ExcludedFilters.Where( static filter =>
                     filter.SourceId == SourceIds.Herdr && filter.Id != HerdrSearch.AttentionFilterId ) )
        {
            spaces &= filter.Kind is not null and not ResultKinds.Workspace;
            tabs &= filter.Kind is not null and not ResultKinds.Tab;
        }

        return (spaces, tabs);
    }

    private static List<SearchResult> Project( HerdrInventory inventory, ParsedQuery query, bool includeSpaces, bool includeTabs, string? workspaceId )
    {
        var browse = query.Text.Trim( )
                         .Length ==
                     0;

        var clauses = query.Expression.Clauses;
        if ( IsEmptyBrowseQuery( browse, clauses ) )
        {
            clauses = [new QueryClause { Text = query.Text }];
        }

        var labels = IndexWorkspaces( inventory );
        List<Candidate> candidates = [];

        if ( includeSpaces )
        {
            foreach ( var workspace in inventory.Workspaces )
            {
                var result = MatchClauses( SpaceResult( workspace, inventory.Tabs, 0 ), clauses, workspace.Focused );
                if ( result is not null )
                {
                    candidates.Add( CreateWorkspaceCandidate( workspace, result ) );
                }
            }
        }

        if ( includeTabs )
        {
            foreach ( var tab in inventory.Tabs )
            {
                if ( IsOutsideWorkspace( workspaceId, tab ) )
                {
                    continue;
                }

                var parentLabel = labels.GetValueOrDefault( tab.WorkspaceId );
                var parent = inventory.Workspaces.FirstOrDefault( workspace => workspace.Id == tab.WorkspaceId );
                var result = MatchClauses( TabResult( tab, parentLabel, 0 ), clauses, tab.Focused );
                if ( MatchesTabAttention( query, result ) )
                {
                    candidates.Add( CreateTabCandidate( tab, parent, result ) );
                }
            }
        }

        if ( !browse )
        {
            return candidates
                .Select( static candidate => candidate.Result )
                .ToList( );
        }

        // Browse order: focused first, then spaces before tabs, space number, tab
        // number, label, ID. One ordinal encodes it in the score so the order
        // survives the coordinator's re-sort.
        var ordered = OrderBrowseCandidates( candidates );

        return ScoreBrowseCandidates( ordered );
    }

    private static SearchResult? MatchClauses( SearchResult result, List<QueryClause> clauses, bool focused )
    {
        var total = 0.0;
        var positiveCount = 0;

        foreach ( var clause in clauses )
        {
            var score = ClauseScore( result, clause );

            if ( clause.Excluded )
            {
                if ( score > 0 )
                {
                    return null;
                }

                continue;
            }

            if ( score <= 0 )
            {
                return null;
            }

            total += score;
            positiveCount++;
        }

        return new SearchResult( result ) { Score = (positiveCount == 0 ? BrowseBaseline : total / positiveCount) + (focused ? FocusedBonus : 0) };
    }

    private static double ClauseScore( SearchResult result, QueryClause clause )
    {
        if ( IsUnqualifiedIncludedClause( clause ) )
        {
            var title = LabelScore( clause.Text, result.Title );
            var parent = GetParentSpaceScore( result, clause );

            return Math.Max( title, parent );
        }

        // Field constraints and exclusions use the same literal values as the
        // coordinator, before the provider's candidate limit can discard rows.
        IEnumerable<string?> values = GetClauseValues( result, clause );
        var score = ScoreClauseValues( clause, values );

        return clause.Field == QueryFieldIds.Metadata ? score * ParentSpaceWeight : score;
    }

    private const double BrowseBaseline = 0.36;
    private const double BrowseStep = 0.0001;

    private sealed record Candidate( SearchResult Result, bool Focused, int Kind, long SpaceNumber, long TabNumber, string Label, bool Orphan );

    private static bool IsBrowseRequest( ParsedQuery query, SearchRequest request )
    {
        return request.HerdrWorkspaceId is not null ||
               query.ProviderFilter == ProviderIds.Herdr ||
               query.IncludedFilters.Any( static filter => filter.SourceId == SourceIds.Herdr );
    }

    private static bool IsEmptySearch( string text, bool browse )
    {
        return text.Length == 0 && !browse;
    }

    private static SearchResult[] OrderSearchResults( SearchRequest request, List<SearchResult> results )
    {
        return results
            .OrderByDescending( static result => result.Score )
            .ThenBy( static result => result.Title, StringComparer.OrdinalIgnoreCase )
            .ThenBy( static result => result.Id, StringComparer.Ordinal )
            .Take( Math.Clamp( request.Limit, 1, MaximumResults ) )
            .ToArray( );
    }

    private static bool HasOnlyOtherFilters( ParsedQuery query, FilterDescriptor[] herdrIncluded )
    {
        return query.IncludedFilters.Count > 0 && herdrIncluded.Length == 0;
    }

    private static bool IncludesWorkspaces( FilterDescriptor[] herdrIncluded )
    {
        return herdrIncluded.Length == 0 || herdrIncluded.Any( static filter => filter.Kind is null or ResultKinds.Workspace );
    }

    private static bool IncludesTabs( FilterDescriptor[] herdrIncluded )
    {
        return herdrIncluded.Length == 0 || herdrIncluded.Any( static filter => filter.Kind is null or ResultKinds.Tab );
    }

    private static bool IsEmptyBrowseQuery( bool browse, List<QueryClause> clauses )
    {
        return clauses.Count == 0 && !browse;
    }

    private static Dictionary<string, string> IndexWorkspaces( HerdrInventory inventory )
    {
        return inventory
            .Workspaces.GroupBy( static workspace => workspace.Id, StringComparer.Ordinal )
            .ToDictionary( static group => group.Key, static group => group.First( )
                .Label, StringComparer.Ordinal );
    }

    private static Candidate CreateWorkspaceCandidate( HerdrWorkspace workspace, SearchResult result )
    {
        return new Candidate( result, workspace.Focused, Kind: 0, workspace.Number, TabNumber: 0, workspace.Label,
            Orphan: false );
    }

    private static bool IsOutsideWorkspace( string? workspaceId, HerdrTab tab )
    {
        return workspaceId is not null && tab.WorkspaceId != workspaceId;
    }

    private static bool MatchesTabAttention( ParsedQuery query, [NotNullWhen( true )] SearchResult? result )
    {
        return result is not null && HerdrSearch.MatchesAttention( query, result );
    }

    private static Candidate CreateTabCandidate( HerdrTab tab, HerdrWorkspace? parent, SearchResult result )
    {
        return new Candidate( result, tab.Focused, Kind: 1, parent?.Number ?? long.MaxValue, tab.Number, tab.Label,
            Orphan: parent is null );
    }

    private static Candidate[] OrderBrowseCandidates( List<Candidate> candidates )
    {
        return candidates
            .OrderByDescending( static candidate => candidate.Focused )
            .ThenBy( static candidate => candidate.Orphan )
            .ThenBy( static candidate => candidate.Kind )
            .ThenBy( static candidate => candidate.SpaceNumber )
            .ThenBy( static candidate => candidate.TabNumber )
            .ThenBy( static candidate => candidate.Label, StringComparer.OrdinalIgnoreCase )
            .ThenBy( static candidate => candidate.Result.Id, StringComparer.Ordinal )
            .ToArray( );
    }

    private static List<SearchResult> ScoreBrowseCandidates( Candidate[] ordered )
    {
        return ordered
            .Select( ( candidate, ordinal ) => new SearchResult( candidate.Result ) { Score = BrowseBaseline - (ordinal * BrowseStep) } )
            .ToList( );
    }

    private static bool IsUnqualifiedIncludedClause( QueryClause clause )
    {
        return clause is { Field: null, Excluded: false };
    }

    private static double GetParentSpaceScore( SearchResult result, QueryClause clause )
    {
        if ( result.Kind == ResultKinds.Tab )
        {
            return LabelScore( clause.Text, result.Metadata[WorkspaceLabelMetadata] ) * ParentSpaceWeight;
        }

        return 0;
    }

    private static IEnumerable<string?> GetClauseValues( SearchResult result, QueryClause clause )
    {
        return clause.Field switch
        {
            QueryFieldIds.Title => [result.Title],
            QueryFieldIds.Metadata => result.Metadata.Values.Prepend( result.Subtitle ),
            null => result
                .Metadata.Values.Prepend( result.Subtitle )
                .Prepend( result.Title ),
            _ => []
        };
    }

    private static double ScoreClauseValues( QueryClause clause, IEnumerable<string?> values )
    {
        return values
            .Where( value => value?.Contains( clause.Text, StringComparison.OrdinalIgnoreCase ) == true )
            .Select( value => FuzzyMatcher.Score( clause.Text, value! ) )
            .DefaultIfEmpty( 0 )
            .Max( );
    }

    // Phrase matching shares Unicode folding, without skipping words or crossing labels.
    private static double LabelScore( string text, string label )
    {
        if ( text.Any( char.IsWhiteSpace ) )
        {
            return FuzzyMatcher.ScorePhrase( text, label );
        }

        return FuzzyMatcher.Score( text, label );
    }
}
