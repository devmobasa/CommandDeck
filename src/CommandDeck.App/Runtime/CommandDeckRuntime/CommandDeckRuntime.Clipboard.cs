namespace CommandDeck;

public sealed partial class CommandDeckRuntime
{
    private ClipboardHistoryService? _clipboardHistory;

    internal ValueTask<ClipboardResponse> HandleClipboardAsync( string ownerId, ProtocolRequest request, CancellationToken cancellationToken )
    {
        var history = _clipboardHistory;
        if ( history is null )
        {
            throw new InvalidOperationException( "Clipboard history is unavailable." );
        }

        return history.HandleAsync( ownerId, request, cancellationToken );
    }
}
