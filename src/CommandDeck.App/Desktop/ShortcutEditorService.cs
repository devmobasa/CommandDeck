using System.Text.Json;

namespace CommandDeck;

public sealed partial class ShortcutEditorService : IAsyncDisposable
{
    private readonly HyprlandHotkeyPaths _paths;
    private readonly string _stateFile;
    private readonly ShortcutSourceResolver _sources;
    private readonly string _omarchyDirectory;
    private readonly ExternalProcessRunner _run;
    private readonly Func<bool> _inHyprland;
    private readonly SemaphoreSlim _gate = new( 1, 1 );

    public ShortcutRecordingSession Recording { get; }
    public string ModuleFile => Path.Combine( _paths.HyprDirectory, "commanddeck_keys.lua" );

    public ShortcutEditorService( HyprlandHotkeyPaths paths, string omarchyDirectory, ExternalProcessRunner? runner = null,
        Func<bool>? inHyprland = null, TimeProvider? clock = null )
    {
        _paths = paths;
        _stateFile = Path.Combine( Path.GetDirectoryName( paths.BackupDirectory )!, "shortcut-overrides.json" );
        _sources = new( paths.HyprDirectory, omarchyDirectory );
        _omarchyDirectory = Path.TrimEndingDirectorySeparator( omarchyDirectory ) + Path.DirectorySeparatorChar;
        _run = runner ?? RunAsync;
        _inHyprland = inHyprland ?? (static ( ) =>
             !string.IsNullOrEmpty( Environment.GetEnvironmentVariable( EnvironmentVariableNames.HyprlandInstanceSignature ) ));
        Recording = new( _run, ( ) => _inHyprland( ) && ModuleInstalled( ), clock, _inHyprland );
    }

    public static ShortcutEditorService FromEnvironment( )
    {
        var paths = HyprlandHotkeyPaths.FromEnvironment( Environment.GetEnvironmentVariable );

        return new( paths, paths.OmarchyDirectory );
    }

    private static ValueTask<ProcessResult> RunAsync( string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token ) =>
        ProcessRunner.RunAsync( executable, arguments, null, null, timeout, token );

    private bool ModuleInstalled( ) => File.Exists( _paths.HotkeyFile ) && HyprlandHotkeyConfig.IsGenerated( File.ReadAllText( _paths.HotkeyFile ) ) &&
        File.Exists( _paths.MainConfig ) && LuaBindingReader.Read( File.ReadAllText( _paths.MainConfig ) ).Requires.Any( static require =>
             require.Ordered && require.Module == HyprlandHotkeyConfig.ModuleName );

    public async ValueTask<ShortcutEditorSnapshot> SnapshotAsync( IReadOnlyList<Keybinding> bindings, CancellationToken cancellationToken )
    {
        await _gate.WaitAsync( cancellationToken ).ConfigureAwait( false );
        try
        {
            return Snapshot( bindings, ReadChanges( ) );
        }
        finally
        {
            _gate.Release( );
        }
    }

    private List<ShortcutOverride> ReadChanges( ) => File.Exists( _stateFile )
        ? JsonSerializer.Deserialize( File.ReadAllText( _stateFile ), CommandDeckJsonContext.Default.ListShortcutOverride ) ?? [] : [];

    private ShortcutEditorSnapshot Snapshot( IReadOnlyList<Keybinding> bindings, List<ShortcutOverride> changes,
        string? conflictId = null, string? conflictDescription = null )
    {
        var sources = ReadSources( );
        List<Keybinding> rows = [];
        foreach ( var binding in bindings )
        {
            // The launcher hotkey has its own row, and recording's Escape is
            // CommandDeck's own plumbing, not a shortcut to show or change.
            if ( binding.Description == HyprlandHotkeyConfig.BindingDescription || binding.Submap == ShortcutLua.RecordingSubmap )
            {
                continue;
            }

            if ( changes.Any( change => change.Description == binding.Description &&
                    ShortcutLua.CanonicalChord( change.Chord ) == ShortcutLua.CanonicalChord( binding.Chord ) ) )
            {
                continue;
            }

            var matches = sources.Where( source => source.Call.Description == binding.Description &&
                ShortcutLua.CanonicalChord( source.Call.Chord ) == ShortcutLua.CanonicalChord( binding.Chord ) ).ToList( );
            var source = matches.LastOrDefault( ) ?? sources.FirstOrDefault( source => source.Call.Description == binding.Description );
            var changeable = UniqueBinding( bindings, binding ) && matches.Count == 1 &&
                 source is { BeforeModule: true, Conditional: false, Call.Literal: true } &&
                 binding.Submap.Length == 0;
            var file = source?.File ?? _paths.MainConfig;
            var id = matches.Count == 1 && binding.Submap.Length == 0 ? source!.Id
                : "readonly:" + ShortcutSourceResolver.BindingId( file, binding.Chord, binding.Description, binding.Submap );
            if ( changes.Any( change => change.Id == id ) )
            {
                continue;
            }

            rows.Add( new( )
            {
                Id = id,
                Chord = binding.Chord,
                Description = binding.Description,
                Submap = binding.Submap,
                Changeable = changeable,
                SourceFile = file,
                OriginalChord = binding.Chord,
                Reason = changeable ? "" : $"This binding is built by your Hyprland configuration (for example in a loop). Change it in {file}."
            } );
        }

        rows.AddRange( changes.Select( change => new Keybinding
        {
            Id = change.Id,
            Chord = change.HotkeyEdit && ShortcutLua.IsHotkeySuppressed( changes, change.Chord ) ? "" : change.Chord,
            Description = change.Description,
            Changeable = change.Dispatcher.Length > 0,
            Changed = true,
            Added = change.Added,
            Reason = change.Dispatcher.Length > 0 ? "" : $"Restore this shortcut, or change it in {change.SourceFile}.",
            SourceFile = change.SourceFile,
            OriginalChord = change.OriginalChord,
            LauncherHotkey = change.LauncherHotkey
        } ) );

        return CreateSnapshot( rows, changes, conflictId, conflictDescription );
    }

    private static bool UniqueBinding( IReadOnlyList<Keybinding> bindings, Keybinding binding ) =>
        bindings.Count( row => row.Description == binding.Description && SameChord( row.Chord, binding.Chord ) && row.Submap == binding.Submap ) == 1;

    private ShortcutEditorSnapshot CreateSnapshot( List<Keybinding> rows, List<ShortcutOverride> changes, string? conflictId, string? conflictDescription )
    {
        var installed = ModuleInstalled( );

        return new( )
        {
            Bindings = rows,
            ModuleInstalled = installed,
            HotkeyManaged = installed && !File.ReadAllText( _paths.HotkeyFile ).Contains( ShortcutLua.SupportOnlyMarker, StringComparison.Ordinal ),
            Hotkey = installed && !ShortcutLua.IsHotkeySuppressed( changes,
                 HyprlandHotkeyConfig.ReadChord( File.ReadAllText( _paths.HotkeyFile ) ) ??
                 "" ) ? HyprlandHotkeyConfig.ReadChord( File.ReadAllText( _paths.HotkeyFile ) ) ?? "" : "",
            ConfigurationPath = ModuleFile,
            ChangeCount = changes.Count,
            Lua = ExportChanges( changes ),
            ConflictId = conflictId,
            ConflictDescription = conflictDescription
        };
    }

    public async ValueTask<ShortcutEditorSnapshot> ChangeAsync( string operation, ShortcutEditRequest edit, bool confirmed,
        IReadOnlyList<Keybinding> inventory, CancellationToken cancellationToken )
    {
        if ( !_inHyprland( ) )
        {
            throw new InvalidOperationException( "Shortcut changes need an active Hyprland session." );
        }

        await _gate.WaitAsync( cancellationToken ).ConfigureAwait( false );
        try
        {
            var changes = ReadChanges( );
            var previous = changes.ToList( );
            var originalHotkey = HotkeyToRestore( operation, edit.Id, changes );
            if ( operation == ProtocolOperations.ShortcutHotkey )
            {
                return await SetHotkeyAsync( edit, confirmed, inventory, changes, cancellationToken ).ConfigureAwait( false );
            }

            if ( operation == ProtocolOperations.ShortcutResetAll )
            {
                if ( !confirmed )
                {
                    throw new InvalidOperationException( "Confirm Reset all first." );
                }

                changes.Clear( );
            }
            else if ( operation == ProtocolOperations.ShortcutRestore )
            {
                Restore( changes, edit.Id );
                if ( originalHotkey is not null )
                {
                    changes.RemoveAll( static change => change.LauncherHotkey && !change.HotkeyEdit );
                }

                if ( originalHotkey is not null )
                {
                    changes.RemoveAll( static change => change.HotkeyEdit );
                }
            }
            else
            {
                var snapshot = Snapshot( inventory, changes );
                var change = Prepare( changes, edit, snapshot, confirmed );
                var conflicts = snapshot.Bindings.Where( row => row.Id != edit.Id && !row.LauncherHotkey && row.Submap.Length == 0 && row.Chord.Length > 0 &&
                    ShortcutLua.CanonicalChord( row.Chord ) == ShortcutLua.CanonicalChord( change.Chord ) ).ToList( );
                var launcherConflict = inventory.FirstOrDefault( binding => binding.Description == HyprlandHotkeyConfig.BindingDescription &&
                     binding.Submap.Length == 0 &&
                    ShortcutLua.CanonicalChord( binding.Chord ) == ShortcutLua.CanonicalChord( change.Chord ) );
                if ( launcherConflict is not null )
                {
                    conflicts.Add( new( )
                    {
                        Id = "suppressed-hotkey",
                        Description = "Open CommandDeck",
                        Chord = launcherConflict.Chord,
                        OriginalChord = OriginalLauncherChord( launcherConflict.Chord ),
                        SourceFile = _paths.HotkeyFile,
                        LauncherHotkey = true
                    } );
                }

                if ( conflicts.Count > 0 && !edit.Replace )
                {
                    return Snapshot( inventory, changes, conflicts[0].Id, conflicts[0].Description );
                }

                foreach ( var conflict in conflicts.Take( 1 ) )
                {
                    var removal = ConflictRemoval( changes, conflict, snapshot );
                    Put( changes, removal with { Chord = "", ReplacedBy = edit.Id } );
                }

                Put( changes, change );
            }

            await SaveAsync( changes, previous, inventory, cancellationToken, originalHotkey ).ConfigureAwait( false );

            return await SnapshotAfterReloadAsync( inventory, previous, changes, cancellationToken ).ConfigureAwait( false );
        }
        finally
        {
            _gate.Release( );
        }
    }

    private async ValueTask<ShortcutEditorSnapshot> SnapshotAfterReloadAsync( IReadOnlyList<Keybinding> inventory, List<ShortcutOverride> previous,
        List<ShortcutOverride> changes, CancellationToken cancellationToken )
    {
        try
        {
            var fresh = await ReadInventoryAsync( cancellationToken ).ConfigureAwait( false );

            return Snapshot( fresh, changes );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            // The write/reload already succeeded. If the inventory read fails,
            // reconstruct the affected originals rather than report a failed save.
            var unaffected = inventory.Where( row => !previous.Any( change => change.Description == row.Description &&
                ShortcutLua.CanonicalChord( change.Chord ) == ShortcutLua.CanonicalChord( row.Chord ) ) );
            var originals = previous.Where( static change => change.OriginalChord.Length > 0 ).Select( static change =>
                new Keybinding { Chord = change.OriginalChord, Description = change.Description } );

            return Snapshot( unaffected.Concat( originals ).ToList( ), changes );
        }
    }

    private ShortcutOverride ConflictRemoval( List<ShortcutOverride> changes, Keybinding conflict, ShortcutEditorSnapshot snapshot )
    {
        if ( conflict.Changeable )
        {
            return Prepare( changes, new( ) { Id = conflict.Id }, snapshot, confirmed: true );
        }

        var description = conflict.LauncherHotkey ? HyprlandHotkeyConfig.BindingDescription : conflict.Description;
        var sources = ReadSources( ).Where( source => source.Call.Description == description &&
            (source.Call.Chord.Length == 0 || ShortcutLua.CanonicalChord( source.Call.Chord ) == ShortcutLua.CanonicalChord( conflict.Chord )) ).ToList( );
        var ownedHotkey = conflict.LauncherHotkey && ModuleInstalled( ) &&
            !File.ReadAllText( _paths.HotkeyFile ).Contains( ShortcutLua.SupportOnlyMarker, StringComparison.Ordinal );
        // A binding that may not be loaded is removed only from Omarchy's own defaults.
        if ( sources.Any( source => !source.BeforeModule || source.Conditional && !IsConditionalOmarchyDefault( source ) ) ||
             sources.Count == 0 && !ownedHotkey )
        {
            throw new InvalidOperationException( "This key loads after CommandDeck, or its load order is unknown. Change it in your Hyprland configuration." );
        }

        return changes.FirstOrDefault( item => item.Id == conflict.Id ) ?? new ShortcutOverride
        {
            Id = conflict.Id,
            OriginalChord = conflict.OriginalChord.Length > 0 ? conflict.OriginalChord : conflict.Chord,
            Description = conflict.Description,
            SourceFile = sources.LastOrDefault( )?.File ?? conflict.SourceFile,
            LauncherHotkey = conflict.LauncherHotkey
        };
    }

    private static void Put( List<ShortcutOverride> changes, ShortcutOverride change )
    {
        changes.RemoveAll( value => value.Id == change.Id );
        changes.Add( change );
    }

    private static string? HotkeyToRestore( string operation, string id, List<ShortcutOverride> changes )
    {
        if ( operation == ProtocolOperations.ShortcutResetAll )
        {
            return changes.FirstOrDefault( static change => change.HotkeyEdit )?.OriginalChord;
        }

        if ( operation != ProtocolOperations.ShortcutRestore )
        {
            return null;
        }

        var restored = RestoreIds( changes, id );

        return changes.FirstOrDefault( change => change.HotkeyEdit && restored.Contains( change.Id ) )?.OriginalChord;
    }

    private static HashSet<string> RestoreIds( List<ShortcutOverride> changes, string id )
    {
        if ( string.IsNullOrEmpty( id ) )
        {
            throw new InvalidOperationException( "Choose a shortcut to restore." );
        }

        HashSet<string> restored = new( StringComparer.Ordinal ) { id };
        Queue<string> pending = new( );
        pending.Enqueue( id );
        while ( pending.TryDequeue( out var current ) )
        {
            var original = changes.FirstOrDefault( change => change.Id == current )?.OriginalChord;
            foreach ( var candidate in changes )
            {
                var removedByThis = candidate.Chord.Length == 0 && candidate.ReplacedBy == current;
                var occupiesOriginal = !string.IsNullOrEmpty( original ) && candidate.Chord.Length > 0 &&
                    !(candidate.HotkeyEdit && ShortcutLua.IsHotkeySuppressed( changes, candidate.Chord )) &&
                    ShortcutLua.CanonicalChord( candidate.Chord ) == ShortcutLua.CanonicalChord( original );
                if ( (removedByThis || occupiesOriginal) && restored.Add( candidate.Id ) )
                {
                    pending.Enqueue( candidate.Id );
                }
            }
        }

        return restored;
    }

    private static void Restore( List<ShortcutOverride> changes, string id )
    {
        var restored = RestoreIds( changes, id );
        changes.RemoveAll( change => restored.Contains( change.Id ) );
    }

    private async ValueTask ReloadAsync( CancellationToken cancellationToken )
    {
        var result = await _run( ToolExecutables.Hyprctl, ["reload"], TimeSpan.FromSeconds( 3 ), cancellationToken ).ConfigureAwait( false );
        if ( result is not { ExitCode: 0, TimedOut: false, AnyOutputLimitExceeded: false } || result.StandardOutput.Trim( ) != "ok" )
        {
            throw new InvalidOperationException( "Hyprland could not reload. The previous shortcut files were restored." );
        }

        var errors = await _run( ToolExecutables.Hyprctl, ["configerrors"], TimeSpan.FromSeconds( 3 ), cancellationToken ).ConfigureAwait( false );
        if ( errors is not { ExitCode: 0, TimedOut: false, AnyOutputLimitExceeded: false } || !string.IsNullOrWhiteSpace( errors.StandardOutput ) )
        {
            throw new InvalidOperationException( "Hyprland reported configuration errors. The previous shortcut files were restored." );
        }
    }

    private IReadOnlyList<ResolvedShortcut> ReadSources( )
    {
        // A manual-hotkey user has no loader yet. Resolve against the same load
        // point we will install transactionally with the first desktop change.
        var main = !File.Exists( _paths.HotkeyFile ) && File.Exists( _paths.MainConfig )
            ? HyprlandHotkeyConfig.EnsureRequire( File.ReadAllText( _paths.MainConfig ) ) : null;

        return _sources.Read( main );
    }

    public async ValueTask DisposeAsync( )
    {
        await Recording.DisposeAsync( ).ConfigureAwait( false );
        _gate.Dispose( );
    }

    public async ValueTask StartRecordingAsync( string owner, CancellationToken cancellationToken )
    {
        await _gate.WaitAsync( cancellationToken ).ConfigureAwait( false );
        try
        {
            if ( _inHyprland( ) && ModuleInstalled( ) )
            {
                var module = File.ReadAllText( _paths.HotkeyFile );
                if ( !module.Contains( ShortcutLua.ModuleDefinitions, StringComparison.Ordinal ) ||
                     module.IndexOf( ShortcutLua.ModuleDefinitions, StringComparison.Ordinal ) < module.IndexOf( ShortcutLua.KeysRequire,
                     StringComparison.Ordinal ) || module.IndexOf( "hl.unbind(", StringComparison.Ordinal ) is var unbind && unbind >= 0 &&
                     unbind < module.IndexOf( ShortcutLua.KeysRequire, StringComparison.Ordinal ) )
                {
                    var changes = ReadChanges( );
                    await SaveAsync( changes, changes, await ReadInventoryAsync( cancellationToken ).ConfigureAwait( false ), cancellationToken )
                        .ConfigureAwait( false );
                }
            }

            if ( !await Recording.StartAsync( owner, cancellationToken ).ConfigureAwait( false ) )
            {
                throw new InvalidOperationException( "The recording module is unavailable. Try recording again." );
            }
        }
        finally
        {
            _gate.Release( );
        }
    }

    public async ValueTask<IReadOnlyList<Keybinding>> ReadInventoryAsync( CancellationToken cancellationToken )
    {
        if ( !_inHyprland( ) )
        {
            return [];
        }

        var result = await _run( ToolExecutables.Hyprctl, ["-j", "binds"], TimeSpan.FromSeconds( 3 ), cancellationToken ).ConfigureAwait( false );
        if ( result.ExitCode != 0 || result.TimedOut || result.AnyOutputLimitExceeded )
        {
            throw new InvalidOperationException( "Could not read Hyprland shortcuts." );
        }

        try
        {
            using var document = JsonDocument.Parse( result.StandardOutput );
            if ( document.RootElement.ValueKind != JsonValueKind.Array )
            {
                throw new JsonException( );
            }
        }
        catch ( JsonException )
        {
            throw new InvalidOperationException( "Could not read Hyprland shortcuts." );
        }

        return HyprlandShortcutInspector.ParseKeybindings( result.StandardOutput );
    }

}
