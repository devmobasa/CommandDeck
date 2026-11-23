using System.Text.Json;

namespace CommandDeck;

public sealed record HerdrAgent( string PaneId, string TabId, string WorkspaceId, string Name, string AgentStatus, bool Focused )
{
    public bool App { get; init; }

    // A desktop agent's terminal position in its tab, from 1, when the tab has
    // several terminals; 0 otherwise.
    public int Position { get; init; }
}

public sealed partial class HerdrClient
{
    private const string AgentCommand = "agent";
    private const string PaneIdField = "pane_id";
    private const int MaximumAgentNameCharacters = 64;

    public const int MaximumAgents = 512;
    public const int AgentListStandardOutputBytes = 512 * ByteUnits.BytesPerKibibyte;
    public const int AgentListStandardErrorBytes = 32 * ByteUnits.BytesPerKibibyte;

    // Every pane Herdr knows runs an agent, with its tab and reported status.
    public async ValueTask<IReadOnlyList<HerdrAgent>?> ListAgentsAsync( CancellationToken cancellationToken )
    {
        if ( !_available( ) )
        {
            return null;
        }

        ProcessResult result;

        try
        {
            result = await RunAsync( [AgentCommand, ListCommand], ListTimeout, AgentListStandardOutputBytes, AgentListStandardErrorBytes,
                    cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return null;
        }

        return ParseAgents( result );
    }

    private static List<HerdrAgent>? ParseAgents( ProcessResult result )
    {
        var items = ReadResultArray( result, "agent_list", "agents" );
        if ( items is null )
        {
            return null;
        }

        using ( items.Value.Document )
        {
            List<HerdrAgent> agents = [];

            foreach ( var element in items
                         .Value.Items.EnumerateArray( )
                         .Take( MaximumAgents ) )
            {
                var paneId = ReadString( element, PaneIdField );
                var tabId = ReadString( element, TabIdField );
                var workspaceId = ReadString( element, WorkspaceIdField );
                if ( IsInvalidRecord( element, paneId ) || !IsValidIdentifier( tabId ) || !IsValidIdentifier( workspaceId ) )
                {
                    continue;
                }

                agents.Add( new HerdrAgent( paneId!, tabId!, workspaceId!, ReadAgentName( element ), ReadAgentStatus( element ),
                    ReadBoolean( element, "focused" ) == true ) );
            }

            return agents;
        }
    }

    // The name Herdr shows for the pane: its display name, the detected agent,
    // or the name it was given.
    private static string ReadAgentName( JsonElement element )
    {
        var name = ReadString( element, "display_agent" ) ?? ReadString( element, "agent" ) ?? ReadString( element, "name" ) ?? string.Empty;

        var clean = new string( name
            .Where( static character => !char.IsControl( character ) )
            .ToArray( ) ).Trim( );

        return Truncate( clean, MaximumAgentNameCharacters );
    }
}
