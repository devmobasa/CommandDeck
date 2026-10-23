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

    private static void AddTabPreview( List<DetailField> details, string workspaceId, IReadOnlyList<HerdrTab> tabs )
    {
        const int previewLimit = 8;
        var children = OrderWorkspaceTabs( workspaceId, tabs );
        foreach ( var tab in children.Take( previewLimit ) )
        {
            var label = tab.Label.Length > 0 ? tab.Label : $"Tab {tab.Number}";
            var status = tab.AgentStatus.Length > 0 ? $" · {tab.AgentStatus}" : string.Empty;
            details.Add( new DetailField( label, Plural( tab.PaneCount, "pane" ) + status ) );
        }

        if ( children.Length > previewLimit )
        {
            details.Add( new DetailField( "More tabs", $"{children.Length - previewLimit} more — choose Browse tabs" ) );
        }
    }

    private static List<ActionInput> CreateWorkflowInputs( string kind )
    {
        if ( kind == ActionKinds.HerdrCreateTab )
        {
            return [new ActionInput { Name = NameInput, DefaultValue = "New tab" }];
        }

        return [];
    }

    private static HerdrTab[] OrderWorkspaceTabs( string workspaceId, IReadOnlyList<HerdrTab> tabs )
    {
        return tabs
            .Where( tab => tab.WorkspaceId == workspaceId )
            .OrderBy( static tab => tab.Number )
            .ThenBy( static tab => tab.Id, StringComparer.Ordinal )
            .ToArray( );
    }
}
