namespace CommandDeck;

public sealed partial class HerdrProvider
{
    private static CommandAction SpaceAction( string id, string title, string kind, string workspaceId ) => new( )
    {
        Id = id,
        Title = title,
        Kind = kind,
        Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.TargetId] = workspaceId },
        Inputs = CreateWorkflowInputs( kind )
    };

    private static void AddTabPreview( List<DetailField> details, HerdrWorkspace workspace, IReadOnlyList<HerdrTab> tabs )
    {
        const int previewLimit = 8;
        var children = OrderWorkspaceTabs( workspace, tabs );
        foreach ( var tab in children.Take( previewLimit ) )
        {
            var label = tab.Label.Length > 0 ? tab.Label : $"Tab {tab.Number}";
            var status = tab.AgentStatus.Length > 0 ? $" · {tab.AgentStatus}" : string.Empty;
            details.Add( new DetailField( label, Plural( tab.PaneCount, PaneNoun( tab.App ) ) + status ) );
        }

        if ( children.Length > previewLimit )
        {
            details.Add( new DetailField( "More tabs", $"{children.Length - previewLimit} more — choose Browse tabs" ) );
        }
    }

    private const string CreateTabActionId = "create-tab";
    private const double CreateTabScore = 0.01;

    // Filter or create: in a browsed space, text that names no tab offers to
    // create one with that name, after the matching tabs. The name dialog opens
    // with the text filled in. An exact tab name, a filter, a field or excluded
    // clause, or a name Herdr would refuse offers nothing.
    // The scope names the app, so the offer creates the tab where the browsed
    // space lives even when the other app has a space with the same ID. The
    // launcher browses through the Herdr tabs filter, which is the view itself.
    private static SearchResult? CreateTabOffer( HerdrInventory inventory, ParsedQuery query, string? scope, RowContext context )
    {
        var name = query.Text.Trim( );
        if ( !HerdrScopes.TryParse( scope, out var app, out var workspaceId ) || !IsOnlyATabName( query, name ) )
        {
            return null;
        }

        var workspace = inventory.Workspaces.FirstOrDefault( workspace => workspace.App == app && workspace.Id == workspaceId );
        if ( workspace is null || inventory.Tabs.Any( tab =>
                tab.App == app && tab.WorkspaceId == workspaceId && string.Equals( tab.Label, name, StringComparison.OrdinalIgnoreCase ) ) )
        {
            return null;
        }

        var space = workspace.Label.Length > 0 ? workspace.Label : $"Space {workspace.Number}";
        var key = HerdrScopes.Of( app, workspace.Id );
        var prefix = context.Prefix( app, workspace.Machine );

        // A desktop space leads with its number, so same-named spaces never share a heading.
        var lead = app ? $"Space {workspace.Number.ToString( System.Globalization.CultureInfo.InvariantCulture )} · {prefix}" : prefix;

        return new SearchResult
        {
            Id = HerdrSearch.NewTabResultPrefix + key,
            Kind = ResultKinds.Workspace,
            Title = $"Create tab \"{name}\"",
            Subtitle = $"{lead}New tab in {space}",
            Icon = Icon,
            ProviderId = ProviderIds.Herdr,
            SourceId = SourceIds.Herdr,
            Score = CreateTabScore,
            Details = [new DetailField( "Space", space ), new DetailField( "Name", name )],
            Actions =
            [
                new CommandAction
                {
                    Id = CreateTabActionId,
                    Title = lead.Length > 0 ? $"Create tab: {lead}{space}" : "Create Tab",
                    Kind = ActionKinds.HerdrCreateTab,
                    Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.TargetId] = workspace.Id },
                    Inputs = [new ActionInput { Name = NameInput, DefaultValue = name }]
                }
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [TargetKindMetadata] = SpaceKind( workspace.App ),
                [TargetIdMetadata] = workspace.Id,
                [WorkspaceIdMetadata] = key,
                [WorkspaceLabelMetadata] = workspace.Label
            }
        };
    }

    private static bool IsOnlyATabName( ParsedQuery query, string name ) =>
        !IsUnusableTabName( name ) && query.IncludedFilters.All( static filter => filter.Id == SearchFilterIds.HerdrTabs ) &&
        query.ExcludedFilters.Count == 0 &&
        !query.Expression.Clauses.Any( static clause => clause.Field is not null || clause.Excluded );

    private static bool IsUnusableTabName( string name )
    {
        return name.Length == 0 || name.Length > HerdrClient.MaximumLabelCharacters || name.Any( char.IsControl );
    }

    private static List<ActionInput> CreateWorkflowInputs( string kind )
    {
        if ( kind == ActionKinds.HerdrCreateTab )
        {
            return [new ActionInput { Name = NameInput, DefaultValue = "New tab" }];
        }

        return [];
    }

    private static HerdrTab[] OrderWorkspaceTabs( HerdrWorkspace workspace, IReadOnlyList<HerdrTab> tabs )
    {
        return tabs
            .Where( tab => tab.App == workspace.App && tab.WorkspaceId == workspace.Id )
            .OrderBy( static tab => tab.Number )
            .ThenBy( static tab => tab.Id, StringComparer.Ordinal )
            .ToArray( );
    }
}
