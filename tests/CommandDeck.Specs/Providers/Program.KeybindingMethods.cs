namespace CommandDeck.Specs;

public static partial class Program
{
    // Omarchy 4 shaped: Lua binds report "__lua" and an index, never a command.
    private const string HyprlandBindsJson = """
                                             [
                                               {"modmask":64,"key":"SPACE","keycode":0,"description":"CommandDeck","dispatcher":"__lua","arg":"1","mouse":false,"submap":""},
                                               {"modmask":65,"key":"O","keycode":0,"description":"Obsidian","dispatcher":"__lua","arg":"2","mouse":false,"submap":""},
                                               {"modmask":0,"key":"XF86AudioMute","keycode":0,"description":"Mute","dispatcher":"__lua","arg":"3","mouse":false,"submap":""},
                                               {"modmask":64,"key":"SPACE","keycode":0,"description":"Omarchy menu","dispatcher":"__lua","arg":"4","mouse":false,"submap":""},
                                               {"modmask":64,"key":"mouse:272","keycode":0,"description":"Move window","dispatcher":"__lua","arg":"5","mouse":true,"submap":""},
                                               {"modmask":0,"key":"RETURN","keycode":0,"description":"Capture highlighted window","dispatcher":"__lua","arg":"6","mouse":false,"submap":"capture"},
                                               {"modmask":4,"key":"","keycode":0,"description":"No key","dispatcher":"__lua","arg":"7"},
                                               "not-an-object"
                                             ]
                                             """;

    private sealed class KeybindingClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new( 2026, 10, 29, 12, 0, 0, TimeSpan.Zero );

        public override DateTimeOffset GetUtcNow( ) => Now;
    }

    private static void HyprlandKeybindingsListEveryKeyWithItsDescription( )
    {
        var bindings = HyprlandShortcutInspector.ParseKeybindings( HyprlandBindsJson );

        Equal( ("Super+Space=CommandDeck|Super+Shift+O=Obsidian|XF86AudioMute=Mute|Super+Space=Omarchy " +
            "menu|Enter=Capture highlighted window@capture"),
            string.Join( '|', bindings.Select( static binding =>
                $"{binding.Chord}={binding.Description}" + (binding.Submap.Length > 0 ? "@" + binding.Submap : string.Empty) ) ) );

        Equal( 0, HyprlandShortcutInspector.ParseKeybindings( "{not json" ).Count );
        Equal( 0, HyprlandShortcutInspector.ParseKeybindings( "{}" ).Count );
    }

    private static async Task KeybindingHintsTeachKeysAndOfferBindingsAsync( )
    {
        var reads = 0;
        var inHyprland = true;
        KeybindingClock clock = new( );

        KeybindingHints hints = new( _ =>
        {
            Interlocked.Increment( ref reads );

            return ValueTask.FromResult<string?>( HyprlandBindsJson );
        }, ( ) => inHyprland, clock );

        SearchResult app = new( )
        {
            Id = "app:obsidian",
            Kind = ResultKinds.Application,
            SourceId = SourceIds.Applications,
            Title = "Obsidian",
            Actions =
            [
                new CommandAction
                {
                    Id = "launch",
                    Title = "Launch",
                    Kind = ActionKinds.LaunchDesktop,
                    Arguments = new( StringComparer.Ordinal ) { [ResultMetadataKeys.Application.DesktopId] = "obsidian.desktop" }
                }
            ]
        };

        SearchResult control = new( )
        {
            Id = "desktop:volume-mute",
            Kind = ResultKinds.CustomCommand,
            SourceId = SourceIds.DesktopControls,
            Title = "Mute",
            Actions =
            [
                new CommandAction
                {
                    Id = "run",
                    Title = "Mute",
                    Kind = ActionKinds.RunArgv,
                    Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Argv] = "notify-send\u001fit's \"done\"" }
                }
            ]
        };

        SearchResult file = new( )
        {
            Id = "file:mute",
            Kind = ResultKinds.File,
            SourceId = SourceIds.Files,
            Title = "Mute",
            Actions = [new CommandAction { Id = "open", Title = "Open", Kind = ActionKinds.OpenPath }]
        };

        SearchResult[] rows = [app, control, file];

        // The first search starts the read and is not held up by it.
        var first = hints.Decorate( rows );
        True( first.All( static row => !row.Metadata.ContainsKey( ResultMetadataKeys.Keybinding ) ) );
        await hints.Refresh;

        var decorated = hints.Decorate( rows );
        Equal( "Super+Shift+O", decorated[0].Metadata[ResultMetadataKeys.Keybinding] );
        Equal( "XF86AudioMute", decorated[1].Metadata[ResultMetadataKeys.Keybinding] );
        True( !decorated[2].Metadata.ContainsKey( ResultMetadataKeys.Keybinding ), "a file named Mute is not the Mute action" );
        Equal( 1, reads );

        // Rows that launch something offer a binding to paste into the Lua configuration.
        var launch = decorated[0].Actions.Single( static action => action.Id == KeybindingHints.CopyBindingActionId );
        Equal( "copy-text", launch.Kind );
        Equal( "Copy Hyprland Binding", launch.Title );

        Equal( """hl.bind("SUPER + ALT + KEY", hl.dsp.exec_cmd("uwsm-app -- obsidian.desktop"), { description = "Obsidian" }) -- choose KEY""",
            TextActionPayload.Read( launch.Arguments ).Text );

        Equal( """hl.bind("SUPER + ALT + KEY", hl.dsp.exec_cmd("notify-send 'it'\\''s \"done\"'"), { description = "Mute" }) -- choose KEY""",
            TextActionPayload.Read( decorated[1].Actions.Single( static action => action.Id == KeybindingHints.CopyBindingActionId ).Arguments ).Text );

        True( decorated[2].Actions.All( static action => action.Id != KeybindingHints.CopyBindingActionId ) );
        Equal( 3, hints.Decorate( decorated )[0].Actions.Count );
        var change = decorated[0].Actions.Single( static action => action.Id == KeybindingHints.ChangeShortcutActionId );
        Equal( "Change Shortcut…", change.Title );
        Equal( "open-settings", change.Kind );
        Equal( "Super+Shift+O", change.Arguments[ActionArgumentNames.ShortcutChord] );
        Equal( "shortcuts.desktop", change.Arguments["settingId"] );
        var add = first[0].Actions.Single( static action => action.Id == KeybindingHints.ChangeShortcutActionId );
        Equal( "Add Shortcut…", add.Title );
        Equal( "uwsm-app -- obsidian.desktop", add.Arguments[ActionArgumentNames.ShortcutCommand] );

        // The list is reused for a minute, then read again.
        clock.Now += TimeSpan.FromSeconds( 59 );
        _ = hints.Decorate( rows );
        await hints.Refresh;
        Equal( 1, reads );
        clock.Now += TimeSpan.FromSeconds( 1 );
        _ = hints.Decorate( rows );
        await hints.Refresh;
        Equal( 2, reads );

        // Outside a Hyprland session the rows are returned untouched.
        inHyprland = false;
        True( ReferenceEquals( rows, hints.Decorate( rows ) ) );
    }
}
