namespace CommandDeck;

public sealed partial class UnixProtocolServer
{
    private async ValueTask HandleShortcutsAsync( ClientWriter writer, ProtocolRequest request, ClientRequestContext context )
    {
        if ( request.Shortcut is null )
        {
            throw new InvalidOperationException( "A shortcut request is required." );
        }

        var token = context.CancellationToken;
        ShortcutEditorSnapshot? editor = null;
        if ( request.Operation == ProtocolOperations.ShortcutRecordStart )
        {
            await _runtime.Shortcuts.StartRecordingAsync( context.OwnerId, token ).ConfigureAwait( false );
        }
        else if ( request.Operation == ProtocolOperations.ShortcutRecordStop )
        {
            await _runtime.Shortcuts.Recording.StopAsync( context.OwnerId ).ConfigureAwait( false );
        }
        else if ( request.Operation != ProtocolOperations.ShortcutExport )
        {
            editor = await _runtime.ChangeShortcutAsync( request, token ).ConfigureAwait( false );
        }

        if ( request.Operation is ProtocolOperations.ShortcutRecordStart or ProtocolOperations.ShortcutRecordStop )
        {
            await writer.DoneAsync( request.RequestId ).ConfigureAwait( false );

            return;
        }

        var snapshot = await _runtime.GetPreferencesAsync( true, token ).ConfigureAwait( false );
        await writer.WriteAsync( new ProtocolMessage
        {
            RequestId = request.RequestId,
            Type = ProtocolMessageTypes.PreferencesResult,
            Preferences = LimitCustomizationSummaries( new( )
            {
                Preferences = snapshot.Preferences,
                Defaults = snapshot.Defaults,
                Customizations = snapshot.Customizations,
                TotalCustomizationCount = snapshot.TotalCustomizationCount,
                CustomizationsTruncated = snapshot.CustomizationsTruncated,
                Shortcuts = snapshot.Shortcuts,
                Keybindings = editor?.Bindings ?? snapshot.Keybindings,
                ShortcutEditor = editor ?? snapshot.ShortcutEditor
            } )
        } ).ConfigureAwait( false );
        await writer.DoneAsync( request.RequestId ).ConfigureAwait( false );
    }
}
