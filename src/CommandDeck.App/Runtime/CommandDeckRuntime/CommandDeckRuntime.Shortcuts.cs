namespace CommandDeck;

public sealed partial class CommandDeckRuntime
{
    private ShortcutEditorService? _shortcuts;
    private readonly SemaphoreSlim _shortcutEdits = new( 1, 1 );
    private readonly Lock _shortcutCreation = new( );

    public ShortcutEditorService Shortcuts
    {
        get
        {
            lock ( _shortcutCreation )
            {
                return _shortcuts ??= (_constructionOptions.ShortcutEditorFactory ?? ShortcutEditorService.FromEnvironment)( );
            }
        }
    }

    private async ValueTask DisposeShortcutsAsync( )
    {
        if ( _shortcuts is not null )
        {
            await _shortcuts.DisposeAsync( ).ConfigureAwait( false );
        }

        _shortcutEdits.Dispose( );
    }

    public async ValueTask<ShortcutEditorSnapshot> ChangeShortcutAsync( ProtocolRequest request, CancellationToken cancellationToken )
    {
        // Reject empty routed probes before accessing any files or processes.
        var edit = request.Shortcut;
        if ( edit is null )
        {
            throw new InvalidOperationException( "A shortcut edit is required." );
        }

        await _shortcutEdits.WaitAsync( cancellationToken ).ConfigureAwait( false );
        try
        {
            var inventory = await Shortcuts.ReadInventoryAsync( cancellationToken ).ConfigureAwait( false );
            var editor = await Shortcuts.ChangeAsync( request.Operation, edit, request.Confirmed, inventory, cancellationToken ).ConfigureAwait( false );
            if ( editor.ConflictDescription is null )
            {
                List<Keybinding> hints = [.. editor.Bindings.Where( static row => !row.LauncherHotkey )];
                var launcherChord = editor.HotkeyManaged ? editor.Hotkey : inventory.FirstOrDefault( static row => row.Description ==
                     HyprlandHotkeyConfig.BindingDescription )?.Chord;
                if ( editor.Bindings.Any( row => row.LauncherHotkey && row.Chord.Length == 0 &&
                    ShortcutLua.CanonicalChord( row.OriginalChord ) == ShortcutLua.CanonicalChord( launcherChord ?? "" ) ) )
                {
                    launcherChord = null;
                }

                if ( !string.IsNullOrEmpty( launcherChord ) )
                {
                    hints.Add( new( ) { Chord = launcherChord, Description = HyprlandHotkeyConfig.BindingDescription } );
                }

                _keybindings?.ApplyInventory( hints );
            }

            return editor;
        }
        finally
        {
            _shortcutEdits.Release( );
        }
    }
}
