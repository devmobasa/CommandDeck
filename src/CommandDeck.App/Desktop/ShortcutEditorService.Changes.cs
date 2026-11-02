namespace CommandDeck;

public sealed partial class ShortcutEditorService
{
    private ShortcutOverride Prepare( List<ShortcutOverride> changes, ShortcutEditRequest edit, ShortcutEditorSnapshot snapshot, bool confirmed )
    {
        var chord = edit.Chord.Length == 0 ? "" : HyprlandHotkeyConfig.NormalizeChord( edit.Chord );
        if ( chord.Length == 0 && !confirmed )
        {
            throw new InvalidOperationException( "Confirm removing the shortcut first." );
        }

        var existing = changes.FirstOrDefault( change => change.Id == edit.Id );
        if ( existing is not null )
        {
            if ( existing.Dispatcher.Length == 0 && chord.Length > 0 )
            {
                throw new InvalidOperationException( "Restore this shortcut before changing it." );
            }

            return existing with { Chord = chord };
        }

        if ( edit.Command.Length > 0 )
        {
            if ( edit.Description.Length == 0 || edit.Command.Length > ShortcutLimits.MaximumCommandCharacters || edit.Description.Length >
                 ShortcutLimits.MaximumDescriptionCharacters )
            {
                throw new InvalidOperationException( "Choose a command with a short description." );
            }

            return new( )
            {
                Id = edit.Id.Length == 0 ? Guid.NewGuid( ).ToString( "N" ) : edit.Id,
                Chord = chord,
                Description = edit.Description,
                Dispatcher = "hl.dsp.exec_cmd(" + ShortcutLua.Quote( edit.Command ) + ")",
                Options = "{ description = " + ShortcutLua.Quote( edit.Description ) + " }",
                Added = true
            };
        }

        var row = snapshot.Bindings.FirstOrDefault( binding => binding.Id == edit.Id );
        if ( row is not { Changeable: true } )
        {
            throw new InvalidOperationException( "Change this binding in your Hyprland configuration." );
        }

        var source = ReadSources( ).Single( value => value.Id == edit.Id );

        return new( )
        {
            Id = edit.Id,
            OriginalChord = source.Call.Chord,
            Chord = chord,
            Description = source.Call.Description,
            Callee = source.Call.Callee,
            Dispatcher = source.Call.Dispatcher,
            Options = source.Call.Options,
            SourceFile = source.File
        };
    }

    private async ValueTask<ShortcutEditorSnapshot> SetHotkeyAsync( ShortcutEditRequest edit, bool confirmed, IReadOnlyList<Keybinding> inventory,
        List<ShortcutOverride> changes, CancellationToken cancellationToken )
    {
        if ( !ModuleInstalled( ) || File.ReadAllText( _paths.HotkeyFile ).Contains( ShortcutLua.SupportOnlyMarker, StringComparison.Ordinal ) )
        {
            throw new InvalidOperationException( "You bound this key yourself. Change it in your Hyprland bindings, or run commanddeck hotkey install." );
        }

        var previous = changes.ToList( );
        var originalChord = changes.FirstOrDefault( static change => change.HotkeyEdit )?.OriginalChord ??
            HyprlandHotkeyConfig.ReadChord( File.ReadAllText( _paths.HotkeyFile ) ) ?? changes.FirstOrDefault( static change =>
                 change.LauncherHotkey )?.OriginalChord ?? "";
        var installer = new HyprlandHotkeyInstaller( _paths );
        var chord = edit.Chord.Length == 0 ? "" : installer.Check( edit.Chord );
        if ( chord.Length == 0 && !confirmed )
        {
            throw new InvalidOperationException( "Confirm removing the shortcut first." );
        }

        var snapshot = Snapshot( inventory, changes );
        var conflicts = snapshot.Bindings.Where( binding => !binding.LauncherHotkey && binding.Submap.Length == 0 && binding.Chord.Length > 0 &&
            ShortcutLua.CanonicalChord( binding.Chord ) == ShortcutLua.CanonicalChord( chord ) ).ToList( );
        if ( conflicts.Count > 0 && !edit.Replace )
        {
            return Snapshot( inventory, changes, conflicts[0].Id, conflicts[0].Description );
        }

        ApplyHotkeyChange( changes, conflicts, snapshot, originalChord, chord );
        await SaveAsync( changes, previous, inventory, cancellationToken, chord ).ConfigureAwait( false );

        return await SnapshotAfterReloadAsync( inventory, previous, changes, cancellationToken ).ConfigureAwait( false );
    }

    private void ApplyHotkeyChange( List<ShortcutOverride> changes, IReadOnlyList<Keybinding> conflicts,
        ShortcutEditorSnapshot snapshot, string originalChord, string chord )
    {
        changes.RemoveAll( static change => change.LauncherHotkey );
        foreach ( var conflict in conflicts.Take( 1 ) )
        {
            Put( changes, ConflictRemoval( changes, conflict, snapshot ) with { Chord = "", ReplacedBy = ShortcutIds.LauncherHotkey } );
        }

        Put( changes, new( )
        {
            Id = ShortcutIds.LauncherHotkey,
            OriginalChord = originalChord,
            Chord = chord,
            Description = "Open CommandDeck",
            LauncherHotkey = true,
            HotkeyEdit = true,
            Dispatcher = "hl.dsp.global(\"commanddeck:toggle\")",
            Options = "{ description = \"CommandDeck\" }",
            SourceFile = _paths.HotkeyFile
        } );
    }

}
