namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ShortcutDaemonHotkeyAndDisconnectAsync( )
    {
        using HotkeyFixture fixture = new( );
        PrepareShortcutSources( fixture );
        List<string> commands = [];
        var current = "reset";
        var service = ShortcutService( fixture, ( _, arguments, _, _ ) =>
        {
            var command = string.Join( ' ', arguments );
            lock ( commands )
            {
                commands.Add( command );
            }

            if ( command == "submap" )
            {
                return ValueTask.FromResult( ShortcutProcessResult( current ) );
            }

            if ( command == "configerrors" )
            {
                return ValueTask.FromResult( ShortcutProcessResult( "" ) );
            }

            if ( command == "-j binds" )
            {
                return ValueTask.FromResult( ShortcutProcessResult( "[]" ) );
            }

            if ( arguments[0] == "dispatch" )
            {
                current = arguments[^1];
            }

            return ValueTask.FromResult( ShortcutProcessResult( "ok" ) );
        } );
        using CancellationTokenSource stop = new( );
        var (runtime, _, root, _) = await StartAgentRuntimeAsync( new ScriptedAgentBackend( ), stop.Token,
            new RuntimeConstructionOptions { ShortcutEditorFactory = ( ) => service } );
        try
        {
            await using ( runtime )
            {
                await using ( var client = await ConnectWithRetryAsync( SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" ) ) )
                {
                    await client.SendAsync( new( )
                    {
                        RequestId = 1,
                        Operation = ProtocolOperations.ShortcutHotkey,
                        Shortcut = new( ) { Id = "hotkey", Chord = "Ctrl+Alt+F10" }
                    }, default );
                    var response = await CollectUntilDoneAsync( client, 1 );
                    True( response.All( static message => message.Type != ProtocolMessageTypes.Error ) );
                    Equal( "CTRL + ALT + F10", response[0].Preferences!.ShortcutEditor!.Hotkey );
                    Equal( "CTRL + ALT + F10", HyprlandHotkeyConfig.ReadChord( File.ReadAllText( fixture.Paths.HotkeyFile ) ) );
                    await client.SendAsync( new( ) { RequestId = 2, Operation = ProtocolOperations.ShortcutRecordStart, Shortcut = new( ) }, default );
                    var recording = await CollectUntilDoneAsync( client, 2 );
                    Equal( "done", recording.Single( ).Type );
                    Equal( ShortcutLua.RecordingSubmap, current );
                }

                await ClipboardEventuallyAsync( ( ) => current == "reset" );
                lock ( commands )
                {
                    True( commands.Contains( "reload", StringComparer.Ordinal ) );
                }
            }
        }
        finally
        {
            stop.Cancel( );
            Directory.Delete( root, recursive: true );
        }
    }
}
