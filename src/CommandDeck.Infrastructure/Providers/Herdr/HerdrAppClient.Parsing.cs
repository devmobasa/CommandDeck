using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CommandDeck;

// Reads `herdr-app ls`: spaces in order, each with its tabs in order, each
// with its terminals. Positions become numbers from 1; a tab is focused when it
// is the shown space's selected tab. A terminal whose program reports a status
// or an agent is an agent. A space or tab carries its most urgent agent status,
// in the app's order: blocked, done, working, idle.
public sealed partial class HerdrAppClient
{
    private const int RefusedExitCode = 1;
    private const int NoAnswerExitCode = 2;
    private const string NoWindowError = "no Herdr window at";
    private const string NoReplyError = "the window did not answer";
    private const int MaximumAgentNameCharacters = 64;
    private const int MaximumReasonCharacters = 200;
    private const string ErrorProperty = "error";

    // Leaves room for the longest row ID built from an app ID,
    // herdr:new-tab:app-space:<id>, within the protocol's identifier limit.
    private static readonly int MaximumAppIdentifierCharacters =
        ProtocolLimits.MaximumIdentifierCharacters - HerdrSearch.NewTabResultPrefix.Length - HerdrScopes.AppSpacePrefix.Length;

    // A change counts only when Herdr acknowledges it with exit 0: {"ok":true},
    // or for tab.new the IDs it created and no "ok". A refusal is exit 1 with an
    // error string. A process that never started (the runner's exit -1) changed
    // nothing. Any other ending may hide a change that happened, so it is
    // unconfirmed rather than failed.
    private static AppAnswer ReadReply( ProcessResult result, bool created )
    {
        if ( result.ExitCode < 0 )
        {
            return new( AppReply.Failed, Reason( result.StandardError ) );
        }

        // The CLI exits 2 when it cannot connect (nothing was sent), when the
        // window hangs up after the command was sent, and on a usage error;
        // only the first is a closed window, and a hangup may hide a change.
        if ( result.ExitCode == NoAnswerExitCode )
        {
            return result.StandardError.Contains( NoWindowError, StringComparison.Ordinal )
                ? new( AppReply.NoAnswer, Reason( result.StandardError ) )
                : new( AppReply.Unconfirmed, null );
        }

        using var document = ParseObject( result.StandardOutput );
        var error = document is null ? null : HerdrClient.ReadString( document.RootElement, ErrorProperty );

        // The window queued the command but did not answer within its five
        // seconds, so it may still happen.
        if ( result.ExitCode == RefusedExitCode && error == NoReplyError )
        {
            return new( AppReply.Unconfirmed, null );
        }

        if ( result.ExitCode == RefusedExitCode )
        {
            return error is null ? new( AppReply.Failed, Reason( result.StandardError ) ) : new( AppReply.Refused, Reason( error ) );
        }

        return result.ExitCode == 0 && document is not null && !document.RootElement.TryGetProperty( ErrorProperty, out _ ) &&
               IsAcknowledged( document.RootElement, created )
            ? new( AppReply.Ok, null )
            : new( AppReply.Unconfirmed, null );
    }

    private static bool IsAcknowledged( JsonElement reply, bool created ) =>
        reply.TryGetProperty( "ok", out var ok )
            ? ok.ValueKind == JsonValueKind.True
            : created && (HerdrClient.IsValidIdentifier( HerdrClient.ReadString( reply, "tab" ) ) ||
                          HerdrClient.IsValidIdentifier( HerdrClient.ReadString( reply, "pane" ) ) ||
                          HerdrClient.IsValidIdentifier( HerdrClient.ReadString( reply, "terminal" ) ));

    private static bool IsValidAppIdentifier( [NotNullWhen( true )] string? id ) =>
        HerdrClient.IsValidIdentifier( id ) && id is not null && id.Length <= MaximumAppIdentifierCharacters;

    private static JsonDocument? ParseObject( string json )
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse( json );
        }
        catch ( JsonException )
        {
            return null;
        }

        if ( document.RootElement.ValueKind == JsonValueKind.Object )
        {
            return document;
        }

        document.Dispose( );

        return null;
    }

    // The CLI reports refusals on stdout, so a failure without standard error
    // logs Herdr's own error text.
    private static string LoggedError( ProcessResult result )
    {
        if ( result.ExitCode == 0 || !string.IsNullOrWhiteSpace( result.StandardError ) )
        {
            return result.StandardError;
        }

        using var document = ParseObject( result.StandardOutput );

        return document is null ? string.Empty : HerdrClient.ReadString( document.RootElement, ErrorProperty ) ?? string.Empty;
    }

    private static string? Reason( string? text )
    {
        var line = text?.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).FirstOrDefault( );
        var clean = Clean( line, MaximumReasonCharacters );

        return clean.Length > 0 ? clean : null;
    }

    private static HerdrAppInventory? ParseInventory( string json )
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse( json );
        }
        catch ( JsonException )
        {
            return null;
        }

        using ( document )
        {
            var root = document.RootElement;
            if ( root.ValueKind != JsonValueKind.Object || !root.TryGetProperty( "spaces", out var spaces ) || spaces.ValueKind != JsonValueKind.Array )
            {
                return null;
            }

            List<HerdrWorkspace> workspaces = [];
            List<HerdrTab> tabs = [];
            List<HerdrAgent> agents = [];
            var position = 0;

            foreach ( var space in spaces
                         .EnumerateArray( )
                         .Take( HerdrClient.MaximumWorkspaces ) )
            {
                position++;
                if ( ReadSpace( space, position, tabs, agents ) is { } workspace )
                {
                    workspaces.Add( workspace );
                }
            }

            return new HerdrAppInventory { Inventory = new HerdrInventory { Workspaces = workspaces, Tabs = tabs }, Agents = agents };
        }
    }

    private static HerdrWorkspace? ReadSpace( JsonElement space, int number, List<HerdrTab> tabs, List<HerdrAgent> agents )
    {
        var id = HerdrClient.ReadString( space, "id" );
        if ( space.ValueKind != JsonValueKind.Object || !IsValidAppIdentifier( id ) )
        {
            return null;
        }

        var shown = HerdrClient.ReadBoolean( space, "active" ) == true;
        var status = string.Empty;
        long tabCount = 0;
        long paneCount = 0;

        if ( space.TryGetProperty( "tabs", out var children ) && children.ValueKind == JsonValueKind.Array )
        {
            var position = 0;

            foreach ( var child in children.EnumerateArray( ) )
            {
                position++;
                if ( tabs.Count >= HerdrClient.MaximumTabs || ReadTab( child, id!, position, shown, agents ) is not { } tab )
                {
                    continue;
                }

                tabs.Add( tab );
                tabCount++;
                paneCount += tab.PaneCount;
                status = MostUrgent( status, tab.AgentStatus );
            }
        }

        var machine = Clean( HerdrClient.ReadString( space, "machine" ), HerdrClient.MaximumLabelCharacters );

        return new HerdrWorkspace( id!, number, ReadName( space, HerdrClient.MaximumLabelCharacters ), shown, paneCount, tabCount, status )
        {
            App = true,
            Machine = machine.Length > 0 ? machine : null
        };
    }

    private static HerdrTab? ReadTab( JsonElement tab, string spaceId, int number, bool spaceShown, List<HerdrAgent> agents )
    {
        var id = HerdrClient.ReadString( tab, "id" );
        if ( tab.ValueKind != JsonValueKind.Object || !IsValidAppIdentifier( id ) )
        {
            return null;
        }

        var focused = spaceShown && HerdrClient.ReadBoolean( tab, "active" ) == true;
        var status = string.Empty;
        long panes = 0;

        if ( tab.TryGetProperty( "terminals", out var terminals ) && terminals.ValueKind == JsonValueKind.Array )
        {
            var several = terminals
                .EnumerateArray( )
                .Count( static terminal => terminal.ValueKind == JsonValueKind.Object ) > 1;

            foreach ( var terminal in terminals.EnumerateArray( ) )
            {
                if ( terminal.ValueKind != JsonValueKind.Object )
                {
                    continue;
                }

                panes++;
                var agentStatus = HerdrClient.GetValidAgentStatus( HerdrClient.ReadString( terminal, "status" ) ?? string.Empty );
                status = MostUrgent( status, agentStatus );
                AddAgent( terminal, agentStatus, id!, spaceId, several ? ( int ) panes : 0, agents );
            }
        }

        return new HerdrTab( id!, spaceId, number, ReadName( tab, HerdrClient.MaximumLabelCharacters ), focused, panes, status ) { App = true };
    }

    // The listing does not say which pane has focus, so no desktop agent claims it.
    // The name is the agent and, when it differs, the terminal's own name, so
    // two sessions of one agent in a tab stay apart.
    private static void AddAgent( JsonElement terminal, string status, string tabId, string spaceId, int position, List<HerdrAgent> agents )
    {
        var pane = HerdrClient.ReadString( terminal, "pane" );
        var app = HerdrClient.ReadString( terminal, "app" );
        if ( agents.Count >= HerdrClient.MaximumAgents || !IsValidAppIdentifier( pane ) || status.Length == 0 && app is null )
        {
            return;
        }

        var label = Clean( HerdrClient.ReadString( terminal, NameArgument ), MaximumAgentNameCharacters );
        var agent = Clean( app, MaximumAgentNameCharacters );
        var name = agent.Length == 0 ? label : label.Length == 0 || label == agent ? agent : $"{agent} ({label})";

        agents.Add( new HerdrAgent( pane!, tabId, spaceId, HerdrClient.Truncate( name, 2 * MaximumAgentNameCharacters ), status, Focused: false )
        {
            App = true,
            Position = position
        } );
    }

    private static string ReadName( JsonElement element, int maximum ) => Clean( HerdrClient.ReadString( element, NameArgument ), maximum );

    private static string Clean( string? value, int maximum )
    {
        var clean = new string( (value ?? string.Empty)
            .Where( static character => !char.IsControl( character ) )
            .ToArray( ) ).Trim( );

        return HerdrClient.Truncate( clean, maximum );
    }

    private static string MostUrgent( string current, string candidate ) => Urgency( candidate ) < Urgency( current ) ? candidate : current;

    private static int Urgency( string status ) => status switch
    {
        "blocked" => 0,
        "done" => 1,
        "working" => 2,
        "idle" => 3,
        "" => 5,
        _ => 4
    };
}
