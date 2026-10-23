using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

sealed partial class SearchClient
{
    public async Task<List<JsonNode>> Burst( string prefix )
    {
        HashSet<int> pending = [];

        for ( var index = 0; index < 12; index++ )
        {
            var id = ++_request;
            pending.Add( id );

            await _writer.WriteLineAsync( new JsonObject { ["version"] = 3, ["requestId"] = id, ["operation"] = "search", ["query"] = prefix + (index == 11 ? "Meeting" : "Meet" + index), ["limit"] = 50 }.ToJsonString( ) );

            await Task.Delay( 25 );
        }

        using var deadline = new CancellationTokenSource( TimeSpan.FromSeconds( 5 ) );
        List<JsonNode> latest = [];

        while ( pending.Count > 0 )
        {
            var messageText = await _reader.ReadLineAsync( deadline.Token );
            if ( messageText is null )
            {
                throw new EndOfStreamException( );
            }

            var message = JsonNode.Parse( messageText )!;
            var id = message["requestId"]!.GetValue<int>( );
            if ( IsRequestError( message, id ) )
            {
                throw new InvalidOperationException( "Final typing query was rejected: " + message );
            }

            if ( id == _request && message["result"] is { } result )
            {
                latest.Add( result );
            }

            if ( message["type"]?.GetValue<string>( ) == "done" )
            {
                pending.Remove( id );
            }
        }

        return latest;
    }
    public async Task<List<JsonNode>> Call( string operation, JsonObject fields, bool error = false )
    {
        var id = ++_request;
        fields["version"] = 3;
        fields["requestId"] = id;
        fields["operation"] = operation;

        if ( operation == "extension.open" )
        {
            _sequence = 0;
        }

        if ( operation.StartsWith( "extension.", StringComparison.Ordinal ) )
        {
            fields["extensionInput"] ??= new JsonObject( );
        }

        if ( operation is "extension.present" or "extension.action" )
        {
            fields["extensionInput"]!["sequence"] = ++_sequence;
        }

        if ( operation == "extensions.manage" )
        {
            fields["extension"]!["expectedRevision"] = _revision;
        }

        await _writer.WriteLineAsync( fields.ToJsonString( ) );
        using var deadline = new CancellationTokenSource( TimeSpan.FromSeconds( 15 ) );
        List<JsonNode> messages = [];

        while ( true )
        {
            var messageText = await _reader.ReadLineAsync( deadline.Token );
            if ( messageText is null )
            {
                throw new EndOfStreamException( );
            }

            var message = JsonNode.Parse( messageText )!;
            messages.Add( message );

            if ( message["extensions"]?["revision"] is { } revision )
            {
                _revision = revision.GetValue<long>( );
            }

            if ( IsRequestDone( id, message ) )
            {
                break;
            }
        }

        var failed = messages.Any( message => message["type"]?.GetValue<string>( ) == "error" || message["extensionSession"]?["status"]?.GetValue<string>( ) == "failed" );
        if ( failed != error )
        {
            throw new InvalidOperationException( operation + ": " + string.Join( "\n", messages ) );
        }

        return messages;
    }

    private bool IsRequestError( JsonNode message, int id )
    {
        return id == _request && message["type"]?.GetValue<string>( ) == "error";
    }

    private static bool IsRequestDone( int id, JsonNode message )
    {
        return message["requestId"]?.GetValue<int>( ) == id && message["type"]?.GetValue<string>( ) == "done";
    }
}
