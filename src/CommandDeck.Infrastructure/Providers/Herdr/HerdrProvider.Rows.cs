namespace CommandDeck;

public sealed partial class HerdrProvider
{
    // Result IDs name the target kind, so a row from the desktop app can never
    // be mistaken for one from the terminal app: herdr:app-space:<id>.
    private static string SpaceKind( bool app ) => app ? HerdrTargetKinds.AppSpace : HerdrTargetKinds.Workspace;

    private static string TabKind( bool app ) => app ? HerdrTargetKinds.AppTab : HerdrTargetKinds.Tab;

    // When both apps are listed, rows say which one they belong to, so equal
    // names never hide which app a switch or a rename reaches.
    private static bool ListsBothApps( HerdrInventory inventory ) =>
        inventory.Workspaces.Any( static workspace => workspace.App ) && inventory.Workspaces.Any( static workspace => !workspace.App );

    // Whether rows say which app they belong to, and whether desktop rows say
    // which machine; both come before any name, so no name can imitate them.
    private readonly record struct RowContext( bool Apps, bool Machines )
    {
        public static RowContext None => default;

        public string Prefix( bool app, string? machine )
        {
            var parts = new List<string>( 2 );
            if ( Apps )
            {
                parts.Add( SourceName( app ) );
            }

            // Fixed words, so no machine alias (even "local") reads as this computer.
            if ( Machines && app )
            {
                parts.Add( machine is null ? "This computer" : $"Remote {machine}" );
            }

            return parts.Count == 0 ? string.Empty : string.Join( " · ", parts ) + " · ";
        }
    }

    // Both apps are named when both are listed, or when a browsed space shows
    // one app while the other is installed too.
    private RowContext ContextFor( HerdrInventory inventory, string? scope ) => new(
        ListsBothApps( inventory ) || scope is not null && client.IsInstalled && app?.IsInstalled == true,
        inventory.Workspaces.Any( static workspace => workspace.App && workspace.Machine is not null ) );

    private static string SourceName( bool app ) => app ? "Herdr app" : "Herdr terminal";

    // The desktop listing reports terminals, not mod panes such as boards.
    private static string PaneNoun( bool app ) => app ? "terminal" : "pane";

    private static string PaneLabel( bool app ) => app ? "Terminals" : "Panes";

    private static SearchResult SpaceResult( HerdrWorkspace workspace, IReadOnlyList<HerdrTab> tabs, double score, RowContext context = default )
    {
        var number = workspace.Number.ToString( System.Globalization.CultureInfo.InvariantCulture );
        var title = workspace.Label.Length > 0 ? workspace.Label : $"Space {number}";
        var prefix = context.Prefix( workspace.App, workspace.Machine );
        var position = workspace.App ? $"Space {number}" : string.Empty;
        var subtitle = workspace.App ? $"{position} · {prefix}{GetWorkspaceCounts( workspace )}" : prefix + GetWorkspaceSubtitle( workspace, number );
        List<DetailField> details = CreateWorkspaceDetails( workspace, number );

        if ( workspace.AgentStatus.Length > 0 )
        {
            details.Add( new DetailField( "Agent status", workspace.AgentStatus ) );
        }

        if ( workspace.App )
        {
            details.Add( new DetailField( "App", SourceName( app: true ) ) );
        }

        details.Add( new DetailField( "Herdr ID", workspace.Id, Monospace: true ) );
        AddTabPreview( details, workspace, tabs );

        return new SearchResult
        {
            Id = $"{ResultIdPrefixes.Herdr}{SpaceKind( workspace.App )}:{workspace.Id}",
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
                FocusAction( SwitchSpaceActionId, "Switch Herdr Space", SpaceKind( workspace.App ), workspace.Id ),
                SpaceAction( BrowseTabsActionId, "Browse tabs", ActionKinds.BrowseHerdrTabs, workspace.Id ),
                SpaceAction( NewTabActionId, NewTabTitle( position, prefix, title ), ActionKinds.HerdrCreateTab, workspace.Id )
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [TargetKindMetadata] = SpaceKind( workspace.App ),
                [TargetIdMetadata] = workspace.Id,
                [WorkspaceIdMetadata] = HerdrScopes.Of( workspace.App, workspace.Id ),
                [WorkspaceLabelMetadata] = workspace.Label,
                [MachineMetadata] = workspace.Machine ?? string.Empty,
                [PositionMetadata] = position,
                [FocusedMetadata] = workspace.Focused ? "true" : "false",
                [AgentStatusMetadata] = workspace.AgentStatus
            }
        };
    }

    private static SearchResult TabResult( HerdrTab tab, string? parentLabel, double score, RowContext context = default, HerdrWorkspace? parent = null )
    {
        var number = tab.Number.ToString( System.Globalization.CultureInfo.InvariantCulture );
        var title = tab.Label.Length > 0 ? tab.Label : $"Tab {number}";
        var space = GetSpaceLabel( parentLabel );
        var machine = parent?.Machine;
        var prefix = context.Prefix( tab.App, machine );
        var position = TabPosition( tab, number, parent );
        var subtitle = tab.App
            ? $"{position} · {prefix}In {space} · {GetTabCounts( tab )}"
            : prefix + GetTabSubtitle( tab, number, space );
        List<DetailField> details = CreateTabDetails( tab, number, space );

        if ( tab.AgentStatus.Length > 0 )
        {
            details.Add( new DetailField( "Agent status", tab.AgentStatus ) );
        }

        if ( tab.App )
        {
            details.Add( new DetailField( "App", SourceName( app: true ) ) );
        }

        details.Add( new DetailField( "Herdr ID", tab.Id, Monospace: true ) );

        return new SearchResult
        {
            Id = $"{ResultIdPrefixes.Herdr}{TabKind( tab.App )}:{tab.Id}",
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
                FocusAction( SwitchTabActionId, "Switch Herdr Tab", TabKind( tab.App ), tab.Id ),
                new CommandAction
                {
                    Id = RenameTabActionId,

                    // The dialog's heading names the tab's app, machine, and space when rows do.
                    Title = RenameTitle( tab, position, space, prefix ),
                    Kind = ActionKinds.HerdrRenameTab,
                    Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.TargetId] = tab.Id },
                    Inputs = [new ActionInput { Name = NameInput, DefaultValue = tab.Label }]
                }
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [TargetKindMetadata] = TabKind( tab.App ),
                [TargetIdMetadata] = tab.Id,
                [WorkspaceIdMetadata] = HerdrScopes.Of( tab.App, tab.WorkspaceId ),
                [WorkspaceLabelMetadata] = parentLabel ?? string.Empty,
                [MachineMetadata] = machine ?? string.Empty,
                [PositionMetadata] = position,
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
        return $"Space {number} · {Plural( workspace.TabCount, "tab" )} · {Plural( workspace.PaneCount, PaneNoun( workspace.App ) )}" +
               (workspace.Focused ? " · focused" : string.Empty);
    }

    private static List<DetailField> CreateWorkspaceDetails( HerdrWorkspace workspace, string number )
    {
        return
        [
            new( "Number", number ),
            new( "Tabs", workspace.TabCount.ToString( System.Globalization.CultureInfo.InvariantCulture ) ),
            new( PaneLabel( workspace.App ), workspace.PaneCount.ToString( System.Globalization.CultureInfo.InvariantCulture ) )
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
        return $"In {space} · Tab {number} · {GetTabCounts( tab )}";
    }

    private static string GetTabCounts( HerdrTab tab ) =>
        Plural( tab.PaneCount, PaneNoun( tab.App ) ) + (tab.Focused ? " · focused" : string.Empty);

    private static string GetWorkspaceCounts( HerdrWorkspace workspace ) =>
        $"{Plural( workspace.TabCount, "tab" )} · {Plural( workspace.PaneCount, PaneNoun( workspace.App ) )}" +
        (workspace.Focused ? " · focused" : string.Empty);

    // A desktop row leads with its space and tab numbers: the one part of a
    // caption or a dialog heading that two same-named rows never share.
    private static string TabPosition( HerdrTab tab, string number, HerdrWorkspace? parent )
    {
        if ( !tab.App )
        {
            return string.Empty;
        }

        return parent is null ? $"Tab {number}" : $"Space {parent.Number.ToString( System.Globalization.CultureInfo.InvariantCulture )} · Tab {number}";
    }

    private static string RenameTitle( HerdrTab tab, string position, string space, string prefix )
    {
        if ( tab.App )
        {
            return $"Rename tab: {position} · {prefix}{space}";
        }

        return prefix.Length > 0 ? $"Rename tab: {prefix}{space}" : "Rename tab";
    }

    private static string NewTabTitle( string position, string prefix, string title )
    {
        if ( position.Length > 0 )
        {
            return $"New tab: {position} · {prefix}{title}";
        }

        return prefix.Length > 0 ? $"New tab: {prefix}{title}" : "New tab in this space";
    }

    private static List<DetailField> CreateTabDetails( HerdrTab tab, string number, string space )
    {
        return
        [
            new( "Space", space ),
            new( "Number", number ),
            new( PaneLabel( tab.App ), tab.PaneCount.ToString( System.Globalization.CultureInfo.InvariantCulture ) )
        ];
    }
}
