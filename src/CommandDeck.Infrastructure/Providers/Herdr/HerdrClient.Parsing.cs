using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace CommandDeck;

public sealed partial class HerdrClient
{
    private static HerdrStatus ParseStatus( string json )
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse( json );
        }
        catch ( JsonException )
        {
            return Status( HerdrAvailability.Failed, "Herdr status returned malformed JSON." );
        }

        using ( document )
        {
            var root = document.RootElement;
            if ( root.ValueKind != JsonValueKind.Object )
            {
                return Status( HerdrAvailability.Failed, "Herdr status returned malformed JSON." );
            }

            var running = ReadBoolean( root, "running" );
            if ( running is null )
            {
                return Status( HerdrAvailability.Failed, "Herdr status omitted the running field." );
            }

            var version = ReadString( root, "version" );
            var protocol = ReadInteger( root, "protocol" );
            var socketPath = ReadSocketPath( root );

            if ( running == false )
            {
                return Status( HerdrAvailability.Stopped, "Herdr is installed; its default server is not running.", version, protocol, socketPath );
            }

            // compatible is the CLI admission field; endpoint_compatible describes
            // the client-rendering endpoint and cannot admit a mismatched CLI.
            var server = $"server {version ?? "unknown"} protocol {protocol?.ToString( CultureInfo.InvariantCulture ) ?? "unknown"}";

            return DescribeCompatibility( root, version, protocol, socketPath, server );
        }
    }

    private static string? ReadSocketPath( JsonElement root )
    {
        var socketPath = ReadString( root, "socket" );

        return GetValidSocketPath( socketPath );
    }

    private static List<HerdrWorkspace>? ParseWorkspaces( ProcessResult result )
    {
        var items = ReadResultArray( result, "workspace_list", "workspaces" );
        if ( items is null )
        {
            return null;
        }

        using ( items.Value.Document )
        {
            List<HerdrWorkspace> workspaces = [];

            foreach ( var element in items
                         .Value.Items.EnumerateArray( )
                         .Take( MaximumWorkspaces ) )
            {
                var id = ReadString( element, WorkspaceIdField );
                if ( IsInvalidRecord( element, id ) )
                {
                    continue;
                }

                workspaces.Add( CreateWorkspace( element, id ) );
            }

            return workspaces;
        }
    }

    private static List<HerdrTab>? ParseTabs( ProcessResult result )
    {
        var items = ReadResultArray( result, "tab_list", "tabs" );
        if ( items is null )
        {
            return null;
        }

        using ( items.Value.Document )
        {
            List<HerdrTab> tabs = [];

            foreach ( var element in items
                         .Value.Items.EnumerateArray( )
                         .Take( MaximumTabs ) )
            {
                var id = ReadString( element, TabIdField );
                var workspaceId = ReadString( element, WorkspaceIdField );

                if ( IsInvalidRecord( element, id ) )
                {
                    continue;
                }

                tabs.Add( CreateTab( element, id, workspaceId ) );
            }

            return tabs;
        }
    }

    // Returns the array under result.<property> when the envelope is a successful
    // response of the expected type; the caller disposes the document.
    private static (JsonDocument Document, JsonElement Items)? ReadResultArray( ProcessResult result, string expectedType, string property )
    {
        if ( HasInvalidListOutput( result ) )
        {
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse( result.StandardOutput );
        }
        catch ( JsonException )
        {
            return null;
        }

        var envelope = document.RootElement;
        if ( CannotReadResultItems( expectedType, property, envelope, out var items ) )
        {
            document.Dispose( );

            return null;
        }

        return (document, items);
    }

    private static HerdrFocusOutcome VerifyFocusAcknowledgement( string json, string targetKind, string targetId )
    {
        var expectedType = targetKind == HerdrTargetKinds.Workspace ? WorkspaceInfoType : TabInfoType;
        var idProperty = targetKind == HerdrTargetKinds.Workspace ? WorkspaceIdField : TabIdField;
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse( json );
        }
        catch ( JsonException )
        {
            return Failure( "Herdr could not switch to this result.", "Herdr returned a malformed acknowledgement." );
        }

        using ( document )
        {
            var envelope = document.RootElement;
            if ( CannotReadFocusRecord( targetKind, expectedType, envelope, out var record ) )
            {
                return Failure( "Herdr could not switch to this result.", "Herdr returned an unexpected acknowledgement." );
            }

            if ( HasUnconfirmedFocus( targetId, idProperty, record ) )
            {
                return Failure( "Herdr could not switch to this result.", "Herdr did not confirm the requested target as focused." );
            }
        }

        return new HerdrFocusOutcome { Succeeded = true, Message = targetKind == HerdrTargetKinds.Workspace ? "Switched Herdr space." : "Switched Herdr tab." };
    }

    private static string DescribeFocusError( string standardError, string targetKind )
    {
        var code = ReadErrorCode( standardError );

        return code switch
        {
            "server_not_running" => "Herdr is no longer running.",
            "workspace_not_found" => "That Herdr space no longer exists. Search again.",
            "tab_not_found" => "That Herdr tab no longer exists. Search again.",
            "protocol_mismatch" => "The Herdr client and server are incompatible.",
            _ => GetFocusFailureMessage( targetKind )
        };
    }

    private static string? ReadErrorCode( string standardError )
    {
        if ( string.IsNullOrWhiteSpace( standardError ) )
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse( standardError.Trim( ) );
            var root = document.RootElement;
            if ( CannotReadError( root, out var error ) )
            {
                return null;
            }

            return ReadString( error, "code" );
        }
        catch ( JsonException )
        {
            return null;
        }
    }

    private static string ReadLabel( JsonElement element )
    {
        var label = ReadString( element, "label" ) ?? string.Empty;

        var clean = new string( label
            .Where( static character => !char.IsControl( character ) )
            .ToArray( ) ).Trim( );

        return clean.Length > MaximumLabelCharacters ? clean[..MaximumLabelCharacters] : clean;
    }

    private static string ReadAgentStatus( JsonElement element )
    {
        var status = ReadString( element, "agent_status" ) ?? string.Empty;

        return GetValidAgentStatus( status );
    }

    private static long ReadCount( JsonElement element, string property ) =>
        Math.Max( 0, ReadInteger( element, property ) ?? 0 );

    private static string? ReadString( JsonElement element, string property )
    {
        if ( element.ValueKind == JsonValueKind.Object && element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.String )
        {
            return value.GetString( );
        }

        return null;
    }

    private static bool? ReadBoolean( JsonElement element, string property )
    {
        if ( TryReadProperty( element, property, out var value ) )
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        else
        {
            return null;
        }
    }

    private static long? ReadInteger( JsonElement element, string property )
    {
        if ( TryReadIntegerProperty( element, property, out var number ) )
        {
            return number;
        }
        else
        {
            return null;
        }
    }

    private static HerdrStatus DescribeCompatibility( JsonElement root, string? version, long? protocol, string? socketPath, string server )
    {
        if ( ReadBoolean( root, "compatible" ) == true )
        {
            return Status( HerdrAvailability.Available, $"{server}; the selected client is compatible", version, protocol, socketPath );
        }

        return Status( HerdrAvailability.Incompatible, $"{server} is not compatible with the selected client", version, protocol, socketPath );
    }

    private static string? GetValidSocketPath( string? socketPath )
    {
        if ( IsValidSocketPath( socketPath ) )
        {
            return socketPath;
        }

        return null;
    }

    private static bool IsInvalidRecord( JsonElement element, string? id )
    {
        return element.ValueKind != JsonValueKind.Object || !IsValidIdentifier( id );
    }

    private static HerdrWorkspace CreateWorkspace( JsonElement element, string? id )
    {
        return new HerdrWorkspace( id!, ReadCount( element, "number" ), ReadLabel( element ), ReadBoolean( element, "focused" ) == true,
            ReadCount( element, "pane_count" ), ReadCount( element, "tab_count" ), ReadAgentStatus( element ) );
    }

    private static HerdrTab CreateTab( JsonElement element, string? id, string? workspaceId )
    {
        return new HerdrTab( id!, IsValidIdentifier( workspaceId ) ? workspaceId! : string.Empty, ReadCount( element, "number" ), ReadLabel( element ),
            ReadBoolean( element, "focused" ) == true, ReadCount( element, "pane_count" ), ReadAgentStatus( element ) );
    }

    private static bool HasInvalidListOutput( ProcessResult result )
    {
        return result.TimedOut ||
               result.AnyOutputLimitExceeded ||
               result.MemoryLimitExceeded ||
               result.ExitCode != 0 ||
               string.IsNullOrWhiteSpace( result.StandardOutput );
    }

    private static bool HasUnconfirmedFocus( string targetId, string idProperty, JsonElement record )
    {
        return ReadString( record, idProperty ) != targetId || ReadBoolean( record, "focused" ) != true;
    }

    private static string GetFocusFailureMessage( string targetKind )
    {
        if ( targetKind == HerdrTargetKinds.Workspace )
        {
            return "Herdr could not switch to this space.";
        }

        return "Herdr could not switch to this tab.";
    }

    private static string GetValidAgentStatus( string status )
    {
        if ( IsValidAgentStatus( status ) )
        {
            return status;
        }

        return string.Empty;
    }

    private static bool CannotReadResultItems( string expectedType, string property, JsonElement envelope, out JsonElement items )
    {
        items = default;

        return envelope.ValueKind != JsonValueKind.Object ||
               !envelope.TryGetProperty( "result", out var payload ) ||
               payload.ValueKind != JsonValueKind.Object ||
               ReadString( payload, "type" ) != expectedType ||
               !payload.TryGetProperty( property, out items ) ||
               items.ValueKind != JsonValueKind.Array;
    }

    private static bool CannotReadFocusRecord( string targetKind, string expectedType, JsonElement envelope, out JsonElement record )
    {
        record = default;

        return envelope.ValueKind != JsonValueKind.Object ||
               !envelope.TryGetProperty( "result", out var payload ) ||
               payload.ValueKind != JsonValueKind.Object ||
               ReadString( payload, "type" ) != expectedType ||
               !payload.TryGetProperty( targetKind, out record ) ||
               record.ValueKind != JsonValueKind.Object;
    }

    private static bool CannotReadError( JsonElement root, out JsonElement error )
    {
        error = default;

        return root.ValueKind != JsonValueKind.Object || !root.TryGetProperty( "error", out error ) || error.ValueKind != JsonValueKind.Object;
    }

    private static bool TryReadProperty( JsonElement element, string property, out JsonElement value )
    {
        value = default;

        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty( property, out value );
    }

    private static bool TryReadIntegerProperty( JsonElement element, string property, out long number )
    {
        number = default;

        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty( property, out var value ) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64( out number );
    }

    private static bool IsValidSocketPath( [NotNullWhen( true )] string? socketPath )
    {
        return socketPath is not null &&
               socketPath.Length <= MaximumPathCharacters &&
               Path.IsPathFullyQualified( socketPath ) &&
               !socketPath.Any( char.IsControl );
    }

    private static bool IsValidAgentStatus( string status )
    {
        return status.Length <= MaximumAgentStatusCharacters && status.All( static character => char.IsLetterOrDigit( character ) || character is '-' or '_' );
    }
}
