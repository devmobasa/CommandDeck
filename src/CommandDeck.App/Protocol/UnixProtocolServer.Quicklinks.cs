namespace CommandDeck;

public sealed partial class UnixProtocolServer
{
    private const string QuicklinkOperationPrefix = "quicklinks.";

    private async ValueTask<ProtocolMessage> HandleQuicklinksAsync( ProtocolRequest request, CancellationToken cancellationToken )
    {
        if ( request is { Operation: ProtocolOperations.QuicklinksRemove, Confirmed: false } )
        {
            throw new InvalidOperationException( "quicklinks.remove requires confirmed: true." );
        }

        QuicklinksResult? result;
        if ( request.Operation == ProtocolOperations.QuicklinksGet )
        {
            result = await _runtime
                .GetQuicklinksAsync( cancellationToken )
                .ConfigureAwait( false );
        }
        else
        {
            result = await _runtime
                .ChangeQuicklinkAsync( request.Operation[QuicklinkOperationPrefix.Length..], request.Quicklink, request.QuicklinkId, cancellationToken )
                .ConfigureAwait( false );
        }

        return new ProtocolMessage { RequestId = request.RequestId, Type = ProtocolMessageTypes.QuicklinksResult, Quicklinks = result };
    }
}
