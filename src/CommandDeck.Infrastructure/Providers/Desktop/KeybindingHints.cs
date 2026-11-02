using System.Text;
using System.Text.RegularExpressions;

namespace CommandDeck;

/// <summary>
/// Puts Hyprland on launcher rows. A row whose title is a binding's description
/// shows that binding's chord, which teaches the key; a row that launches
/// something offers a binding to copy into the Hyprland configuration. Lua
/// binds report no command, so the description is the only link to a row. The
/// binding list is read in the background and kept for a minute, so a search
/// never waits for it. Outside a Hyprland session nothing changes.
/// </summary>
public sealed partial class KeybindingHints
{
    public const string ChangeShortcutActionId = "change-shortcut";
    public const string CopyBindingActionId = "copy-hyprland-binding";
    public const string PlaceholderChord = "SUPER + ALT + KEY";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes( 1 );
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds( 600 );

    private static readonly HashSet<string> HintedKinds = new( StringComparer.Ordinal )
    {
        ResultKinds.Application, ResultKinds.Omarchy, ResultKinds.CustomCommand
    };

    private readonly Func<CancellationToken, ValueTask<string?>> _read;
    private readonly Func<bool> _inHyprland;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new( );
    private IReadOnlyDictionary<string, string> _chords = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private long _generation;
    private Task _refresh = Task.CompletedTask;

    public KeybindingHints( Func<CancellationToken, ValueTask<string?>>? read = null, Func<bool>? inHyprland = null, TimeProvider? clock = null )
    {
        _read = read ?? ReadBindsAsync;
        _inHyprland = inHyprland ?? (static ( ) =>
            !string.IsNullOrEmpty( Environment.GetEnvironmentVariable( EnvironmentVariableNames.HyprlandInstanceSignature ) ));
        _clock = clock ?? TimeProvider.System;
    }

    // The latest background read, so specs can wait for it.
    public Task Refresh
    {
        get
        {
            lock ( _gate )
            {
                return _refresh;
            }
        }
    }

    public IReadOnlyList<SearchResult> Decorate( IReadOnlyList<SearchResult> results )
    {
        ArgumentNullException.ThrowIfNull( results );

        if ( !_inHyprland( ) )
        {
            return results;
        }

        var chords = StartRefreshIfStale( );
        SearchResult[]? decorated = null;
        for ( var index = 0; index < results.Count; index++ )
        {
            var next = Decorate( results[index], chords );
            if ( !ReferenceEquals( next, results[index] ) )
            {
                decorated ??= [.. results];
                decorated[index] = next;
            }
        }

        return decorated ?? results;
    }

    private IReadOnlyDictionary<string, string> StartRefreshIfStale( )
    {
        lock ( _gate )
        {
            var now = _clock.GetUtcNow( );
            if ( _refresh.IsCompleted && now - _readAt >= Lifetime )
            {
                _readAt = now;
                var generation = _generation;
                _refresh = Task.Run( ( ) => RefreshAsync( generation ) );
            }

            return _chords;
        }
    }

    private async Task RefreshAsync( long generation )
    {
        string? json;
        try
        {
            json = await _read( CancellationToken.None )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return;
        }

        if ( json is null )
        {
            return;
        }

        var bindings = HyprlandShortcutInspector.ParseKeybindings( json );
        lock ( _gate )
        {
            if ( generation == _generation )
            {
                _chords = Chords( bindings );
            }
        }
    }

    public void ApplyInventory( IReadOnlyList<Keybinding> bindings )
    {
        lock ( _gate )
        {
            _generation++;
            _chords = Chords( bindings );
            _readAt = _clock.GetUtcNow( );
        }
    }

    private static Dictionary<string, string> Chords( IReadOnlyList<Keybinding> bindings ) => bindings
        .Where( static binding => binding.Description.Length > 0 && binding.Submap.Length == 0 && binding.Chord.Length > 0 )
        .GroupBy( static binding => binding.Description, StringComparer.OrdinalIgnoreCase )
        .ToDictionary( static group => group.Key, static group => group.First( ).Chord, StringComparer.OrdinalIgnoreCase );

    private static SearchResult Decorate( SearchResult result, IReadOnlyDictionary<string, string> chords )
    {
        var chord = HintedKinds.Contains( result.Kind ) && !result.Metadata.ContainsKey( ResultMetadataKeys.Keybinding ) &&
                    chords.TryGetValue( result.Title.Trim( ), out var found )
            ? found
            : null;

        var binding = CreateBindingAction( result );
        if ( chord is null && binding is null && !result.Metadata.ContainsKey( ResultMetadataKeys.Keybinding ) )
        {
            return result;
        }

        var metadata = chord is null
            ? result.Metadata
            : new Dictionary<string, string>( result.Metadata, StringComparer.Ordinal ) { [ResultMetadataKeys.Keybinding] = chord };

        var shortcut = CreateShortcutAction( result, chord ?? result.Metadata.GetValueOrDefault( ResultMetadataKeys.Keybinding ) );
        List<CommandAction> actions = [.. result.Actions];
        if ( binding is not null )
        {
            actions.Add( binding );
        }

        if ( shortcut is not null )
        {
            actions.Add( shortcut );
        }

        return new SearchResult( result ) { Metadata = metadata, Actions = actions };
    }

    private static CommandAction? CreateShortcutAction( SearchResult result, string? chord )
    {
        if ( result.Actions.Any( static action => action.Id == ChangeShortcutActionId ) )
        {
            return null;
        }

        var command = result.Actions.Select( BindingCommand ).FirstOrDefault( static value => !string.IsNullOrWhiteSpace( value ) );
        if ( chord is null && command is null )
        {
            return null;
        }

        return new( )
        {
            Id = ChangeShortcutActionId,
            Title = chord is null ? "Add Shortcut…" : "Change Shortcut…",
            Kind = ActionKinds.OpenSettings,
            Arguments = new( StringComparer.Ordinal )
            {
                [ActionArgumentNames.SettingId] = SettingIds.DesktopShortcuts,
                [ActionArgumentNames.ShortcutId] = "added:" + result.Id,
                [ActionArgumentNames.ShortcutChord] = chord ?? "",
                [ActionArgumentNames.ShortcutDescription] = result.Title,
                [ActionArgumentNames.ShortcutCommand] = command ?? ""
            }
        };
    }

    // The command a binding would run, from the row's own launch or run action.
    private static CommandAction? CreateBindingAction( SearchResult result )
    {
        if ( result.Actions.Any( static action => action.Id == CopyBindingActionId ) )
        {
            return null;
        }

        var command = result.Actions
            .Select( BindingCommand )
            .FirstOrDefault( static value => !string.IsNullOrWhiteSpace( value ) );

        if ( command is null )
        {
            return null;
        }

        return new CommandAction
        {
            Id = CopyBindingActionId,
            Title = "Copy Hyprland Binding",
            Kind = ActionKinds.CopyText,
            Arguments = new TextActionPayload( FormatBinding( command, result.Title ) ).ToArguments( )
        };
    }

    private static string? BindingCommand( CommandAction action )
    {
        return action.Kind switch
        {
            ActionKinds.LaunchDesktop when action.Arguments.GetValueOrDefault( ResultMetadataKeys.Application.DesktopId ) is { Length: > 0 } desktopId =>
                "uwsm-app -- " + ShellWord( desktopId.EndsWith( FileExtensions.Desktop, StringComparison.Ordinal ) ? desktopId : desktopId +
                     FileExtensions.Desktop ),
            ActionKinds.LaunchArgv when action.Arguments.GetValueOrDefault( ActionArgumentNames.Argv ) is { Length: > 0 } launchArgv =>
                string.Join( ' ', LaunchArgumentCodec.Decode( launchArgv ).Select( ShellWord ) ),
            ActionKinds.RunArgv when action.Arguments.GetValueOrDefault( ActionArgumentNames.Argv ) is { Length: > 0 } argv =>
                string.Join( ' ', argv.Split( '\u001f' ).Select( ShellWord ) ),
            ActionKinds.RunTrustedShell => action.Arguments.GetValueOrDefault( ActionArgumentNames.Command ),
            _ => null
        };
    }

    // One line in Omarchy's Lua configuration; the chord is a placeholder to choose.
    public static string FormatBinding( string command, string description ) =>
        ($"hl.bind(\"{PlaceholderChord}\", hl.dsp.exec_cmd({LuaString( command )}" +
            $"), {{ description = {LuaString( description )} }}) -- choose KEY");

    private static string LuaString( string value )
    {
        StringBuilder text = new( "\"" );
        foreach ( var character in value )
        {
            text.Append( character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                _ => character.ToString( )
            } );
        }

        return text
            .Append( '"' )
            .ToString( );
    }

    private static string ShellWord( string value ) =>
        value.Length > 0 && SafeShellWord( ).IsMatch( value ) ? value : "'" + value.Replace( "'", "'\\''", StringComparison.Ordinal ) + "'";

    private static async ValueTask<string?> ReadBindsAsync( CancellationToken cancellationToken )
    {
        if ( !ProcessRunner.ExistsOnPath( ToolExecutables.Hyprctl ) )
        {
            return null;
        }

        var result = await ProcessRunner
            .RunAsync( ToolExecutables.Hyprctl, ["-j", "binds"], standardInput: null, workingDirectory: null, ReadTimeout, cancellationToken )
            .ConfigureAwait( false );

        return result is { ExitCode: 0, TimedOut: false } ? result.StandardOutput : null;
    }

    [GeneratedRegex( "^[A-Za-z0-9_@%+=:,./-]+$" )]
    private static partial Regex SafeShellWord( );
}
