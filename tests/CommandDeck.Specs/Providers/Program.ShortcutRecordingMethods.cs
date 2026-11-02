namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ShortcutRecordingLifecycleAsync( )
    {
        ClipboardManualClock clock = new( HotkeyClock );
        List<string> dispatches = [];
        var current = ShortcutLua.RecordingSubmap;
        var module = true;
        var enterRecording = true;
        var confirmationDelay = TimeSpan.Zero;
        var failReset = false;
        var resetAttempts = 0;
        ExternalProcessRunner runner = ( _, arguments, _, _ ) =>
        {
            if ( arguments[0] == "submap" )
            {
                if ( confirmationDelay > TimeSpan.Zero && current == ShortcutLua.RecordingSubmap )
                {
                    var delay = confirmationDelay;
                    confirmationDelay = TimeSpan.Zero;
                    clock.Advance( delay );
                }

                return ValueTask.FromResult( ShortcutProcessResult( current ) );
            }

            Equal( "dispatch", arguments[0] );
            Equal( "submap", arguments[1] );
            dispatches.Add( arguments[2] );
            if ( arguments[2] == "reset" )
            {
                resetAttempts++;
                if ( failReset )
                {
                    return ValueTask.FromResult( ShortcutProcessResult( "warning", 1 ) );
                }
            }

            if ( enterRecording || arguments[2] == "reset" )
            {
                current = arguments[2];
            }

            return ValueTask.FromResult( ShortcutProcessResult( "ok" ) );
        };
        await using ShortcutRecordingSession session = new( runner, ( ) => module, clock, static ( ) => true );
        await session.ResetOnStartupAsync( default );
        Equal( "reset", current );
        current = "other-submap";
        await session.ResetOnStartupAsync( default );
        Equal( "other-submap", current );
        True( await session.StartAsync( "client-a", default ) );
        Equal( TimeSpan.FromSeconds( 10 ), clock.NextDelay );
        await session.StopAsync( "client-b" );
        Equal( ShortcutLua.RecordingSubmap, current );
        await ThrowsAsync<InvalidOperationException>( async ( ) => await session.StartAsync( "client-b", default ) );
        await session.StopAsync( "client-a" );
        Equal( "reset", current );
        True( await session.StartAsync( "client-a", default ) );
        clock.Advance( TimeSpan.FromSeconds( 10 ) );
        await ClipboardEventuallyAsync( ( ) => current == "reset" );
        Equal( 0, clock.PendingTimers );
        True( await session.StartAsync( "client-a", default ) );
        failReset = true;
        await ThrowsAsync<InvalidOperationException>( async ( ) => await session.StopAsync( "client-a" ) );
        Equal( TimeSpan.FromSeconds( 1 ), clock.NextDelay );
        failReset = false;
        clock.Advance( TimeSpan.FromSeconds( 1 ) );
        await ClipboardEventuallyAsync( ( ) => current == "reset" );
        True( resetAttempts >= 5 );
        True( await session.StartAsync( "client-a", default ) );
        current = "other-submap";
        var beforeDisconnect = dispatches.Count;
        await session.StopAsync( "client-a" );
        Equal( "other-submap", current );
        Equal( beforeDisconnect, dispatches.Count );
        module = false;
        var before = dispatches.Count;
        True( !await session.StartAsync( "manual", default ) );
        Equal( before, dispatches.Count );
        current = ShortcutLua.RecordingSubmap;
        await session.ResetOnStartupAsync( default );
        Equal( "reset", current );
        module = true;
        enterRecording = false;
        await ThrowsAsync<InvalidOperationException>( async ( ) => await session.StartAsync( "unacknowledged", default ) );
        Equal( "reset", current );
        Equal( 0, clock.PendingTimers );
        enterRecording = true;
        confirmationDelay = TimeSpan.FromSeconds( 5 );
        True( await session.StartAsync( "delayed-confirmation", default ) );
        Equal( TimeSpan.FromSeconds( 10 ), clock.NextDelay );
        await session.StopAsync( "delayed-confirmation" );
    }

    private static async Task ShortcutHotkeyChangesUseOwnedFileAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        var calls = 0;
        await using var service = ShortcutService( fixture, ( _, arguments, _, _ ) =>
        {
            if ( arguments[0] == "-j" )
            {
                return ValueTask.FromResult( ShortcutProcessResult( ShortcutFixtureInventoryJson ) );
            }

            if ( arguments[0] == "configerrors" )
            {
                return ValueTask.FromResult( ShortcutProcessResult( "" ) );
            }

            Equal( "reload", arguments[0] );
            calls++;

            return ValueTask.FromResult( ShortcutProcessResult( "ok" ) );
        } );
        var originalMain = File.ReadAllText( fixture.Paths.MainConfig );
        var originalBindings = File.ReadAllText( fixture.Paths.Bindings );
        var originalHotkey = File.ReadAllText( fixture.Paths.HotkeyFile );
        var linkedModule = Path.Combine( fixture.Root, "linked-hotkey.lua" );
        File.Move( fixture.Paths.HotkeyFile, linkedModule );
        File.CreateSymbolicLink( fixture.Paths.HotkeyFile, linkedModule );
        var snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutHotkey, new( ) { Id = "hotkey", Chord = "Super+F11" }, false,
             ShortcutInventoryFixture, default );
        Equal( "SUPER + F11", snapshot.Hotkey );
        Equal( originalHotkey, File.ReadAllText( fixture.Paths.HotkeyFile + ".bak" ) );
        Equal( originalMain, File.ReadAllText( fixture.Paths.MainConfig ) );
        Equal( originalBindings, File.ReadAllText( fixture.Paths.Bindings ) );
        Equal( 1, calls );
        await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ChangeAsync( ProtocolOperations.ShortcutHotkey,
            new( ) { Id = "hotkey", Chord = "Super+bad\"key" }, false, ShortcutInventoryFixture, default ) );
        Equal( 1, calls );
        True( new FileInfo( fixture.Paths.HotkeyFile ).LinkTarget is not null );
        True( !File.ReadAllText( service.ModuleFile ).Contains( "commanddeck:toggle", StringComparison.Ordinal ),
             "the keys module must not duplicate the installed global binding" );
        snapshot = await service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = "hotkey" }, true, ShortcutInventoryFixture, default );
        Equal( "CTRL + SPACE", snapshot.Hotkey );
        Equal( originalHotkey, File.ReadAllText( linkedModule ) );
        Equal( 0, snapshot.ChangeCount );
        File.Delete( fixture.Paths.HotkeyFile );
        await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ChangeAsync( ProtocolOperations.ShortcutHotkey,
            new( ) { Id = "hotkey", Chord = "Super+F12" }, false, ShortcutInventoryFixture, default ) );
        True( !File.Exists( fixture.Paths.HotkeyFile ) );
    }
}
