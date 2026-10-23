namespace CommandDeck;

public sealed partial class HerdrProvider
{
    private const string HerdrWorkspacePrefix = ResultIdPrefixes.Herdr + "workspace:";
    private const string HerdrTabPrefix = ResultIdPrefixes.Herdr + "tab:";

    private static SearchResult SpaceResult( HerdrWorkspace workspace, IReadOnlyList<HerdrTab> tabs, double score )
    {
        var number = workspace.Number.ToString( System.Globalization.CultureInfo.InvariantCulture );
        var title = workspace.Label.Length > 0 ? workspace.Label : $"Space {number}";
        var subtitle = GetWorkspaceSubtitle( workspace, number );
        List<DetailField> details = CreateWorkspaceDetails( workspace, number );

        if ( workspace.AgentStatus.Length > 0 )
        {
            details.Add( new DetailField( "Agent status", workspace.AgentStatus ) );
        }

        details.Add( new DetailField( "Herdr ID", workspace.Id, Monospace: true ) );
        AddTabPreview( details, workspace.Id, tabs );

        return new SearchResult
        {
            Id = $"{HerdrWorkspacePrefix}{workspace.Id}",
            Kind = ResultKinds.Workspace,
            Title = title,
            Subtitle = subtitle,
            Icon = Icon,
            ProviderId = ProviderIds.Herdr,
            SourceId = SourceIds.Herdr,
            Score = score,
            Details = details,
            Actions =
            [
                FocusAction( SwitchSpaceActionId, "Switch Herdr Space", HerdrTargetKinds.Workspace, workspace.Id ),
                SpaceAction( BrowseTabsActionId, "Browse tabs", ActionKinds.BrowseHerdrTabs, workspace.Id ),
                SpaceAction( NewTabActionId, "New tab in this space", ActionKinds.HerdrCreateTab, workspace.Id )
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [TargetKindMetadata] = HerdrTargetKinds.Workspace,
                [TargetIdMetadata] = workspace.Id,
                [WorkspaceIdMetadata] = workspace.Id,
                [WorkspaceLabelMetadata] = workspace.Label,
                [FocusedMetadata] = workspace.Focused ? "true" : "false",
                [AgentStatusMetadata] = workspace.AgentStatus
            }
        };
    }

    private static SearchResult TabResult( HerdrTab tab, string? parentLabel, double score )
    {
        var number = tab.Number.ToString( System.Globalization.CultureInfo.InvariantCulture );
        var title = tab.Label.Length > 0 ? tab.Label : $"Tab {number}";
        var space = GetSpaceLabel( parentLabel );
        var subtitle = GetTabSubtitle( tab, number, space );
        List<DetailField> details = CreateTabDetails( tab, number, space );

        if ( tab.AgentStatus.Length > 0 )
        {
            details.Add( new DetailField( "Agent status", tab.AgentStatus ) );
        }

        details.Add( new DetailField( "Herdr ID", tab.Id, Monospace: true ) );

        return new SearchResult
        {
            Id = $"{HerdrTabPrefix}{tab.Id}",
            Kind = ResultKinds.Tab,
            Title = title,
            Subtitle = subtitle,
            Icon = Icon,
            ProviderId = ProviderIds.Herdr,
            SourceId = SourceIds.Herdr,
            Score = score,
            Details = details,
            Actions =
            [
                FocusAction( SwitchTabActionId, "Switch Herdr Tab", HerdrTargetKinds.Tab, tab.Id ),
                new CommandAction
                {
                    Id = RenameTabActionId,
                    Title = "Rename tab",
                    Kind = ActionKinds.HerdrRenameTab,
                    Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.TargetId] = tab.Id },
                    Inputs = [new ActionInput { Name = NameInput, DefaultValue = tab.Label }]
                }
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [TargetKindMetadata] = HerdrTargetKinds.Tab,
                [TargetIdMetadata] = tab.Id,
                [WorkspaceIdMetadata] = tab.WorkspaceId,
                [WorkspaceLabelMetadata] = parentLabel ?? string.Empty,
                [FocusedMetadata] = tab.Focused ? "true" : "false",
                [AgentStatusMetadata] = tab.AgentStatus
            }
        };
    }

    private static CommandAction FocusAction( string id, string title, string targetKind, string targetId ) => new( )
    {
        Id = id,
        Title = title,
        Kind = ActionKinds.HerdrFocus,
        Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
        {
            [ActionArgumentNames.TargetKind] = targetKind,
            [ActionArgumentNames.TargetId] = targetId
        }
    };

    private static string Plural( long count, string noun ) =>
        $"{count.ToString( System.Globalization.CultureInfo.InvariantCulture )} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string GetWorkspaceSubtitle( HerdrWorkspace workspace, string number )
    {
        return $"Space {number} · {Plural( workspace.TabCount, "tab" )} · {Plural( workspace.PaneCount, "pane" )}" +
               (workspace.Focused ? " · focused" : string.Empty);
    }

    private static List<DetailField> CreateWorkspaceDetails( HerdrWorkspace workspace, string number )
    {
        return
        [
            new( "Number", number ),
            new( "Tabs", workspace.TabCount.ToString( System.Globalization.CultureInfo.InvariantCulture ) ),
            new( "Panes", workspace.PaneCount.ToString( System.Globalization.CultureInfo.InvariantCulture ) )
        ];
    }

    private static string GetSpaceLabel( string? parentLabel )
    {
        if ( parentLabel is null )
        {
            return UnknownSpaceLabel;
        }

        if ( parentLabel.Length > 0 )
        {
            return parentLabel;
        }

        return "Unnamed space";
    }

    private static string GetTabSubtitle( HerdrTab tab, string number, string space )
    {
        return $"In {space} · Tab {number} · {Plural( tab.PaneCount, "pane" )}" + (tab.Focused ? " · focused" : string.Empty);
    }

    private static List<DetailField> CreateTabDetails( HerdrTab tab, string number, string space )
    {
        return
        [
            new( "Space", space ),
            new( "Number", number ),
            new( "Panes", tab.PaneCount.ToString( System.Globalization.CultureInfo.InvariantCulture ) )
        ];
    }
}
