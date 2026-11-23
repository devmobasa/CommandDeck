namespace CommandDeck.Specs;

public static partial class Program
{
    private const string HerdrAppTwoProcessWindowsJson = """
                                                         [{"address":"0x1","class":"herdr-app","pid":100,"focusHistoryID":4,"mapped":true},
                                                          {"address":"0x2","class":"herdr-app","pid":100,"focusHistoryID":2,"mapped":true},
                                                          {"address":"0x3","class":"herdr-app","pid":200,"focusHistoryID":1,"mapped":true},
                                                          {"address":"0x4","class":"herdr-app","pid":200,"focusHistoryID":0,"mapped":false},
                                                          {"address":"0x5","class":"kitty","pid":300,"focusHistoryID":0}]
                                                         """;

    private static async Task HerdrAppWindowBelongsToTheSocketOwnerAsync( )
    {
        const string windows = HerdrAppTwoProcessWindowsJson;

        // Only a shown window of the process listening on the socket counts.
        Equal( "0x2", HerdrAppWindows.Choose( windows, 100 ).Address );
        Equal( "0x3", HerdrAppWindows.Choose( windows, 200 ).Address );

        // The owner's only window is hidden: nothing is raised, though another app shows one.
        const string hiddenOwner = """
                                   [{"address":"0x2","class":"herdr-app","pid":100,"focusHistoryID":2,"mapped":true},
                                    {"address":"0x4","class":"herdr-app","pid":200,"focusHistoryID":0,"mapped":false}]
                                   """;
        var hidden = HerdrAppWindows.Choose( hiddenOwner, 200 );
        True( hidden.Address is null && !hidden.Unverified );

        // Without a verified listener nothing is raised, even with one app process.
        var single = windows.Replace( "\"pid\":200", "\"pid\":100", StringComparison.Ordinal );
        True( HerdrAppWindows.Choose( single, null ) is { Address: null, Unverified: true } );
        Equal( "0x3", HerdrAppWindows.Choose( single, 100 ).Address );
        True( HerdrAppWindows.Choose( windows, 999 ) is { Address: null, Unverified: false } );
        True( HerdrAppWindows.Choose( """[{"address":"0x9","class":"herdr-app"}]""", 1 ) is { Address: null, Unverified: false } );

        // The listener's window counts whatever its class, as with a custom HERDR_APP_ID.
        Equal( "0x5", HerdrAppWindows.Choose( """[{"address":"0x5","class":"herdr-dev","pid":3}]""", 3 ).Address );
        True( HerdrAppWindows.Choose( """[{"address":"0x5","class":"kitty","pid":3}]""", 4 ).Address is null );

        // The listener is the process the kernel names for the socket, however
        // the path is spelled. The socket root keeps the path short enough.
        var directory = SpecTemporaryFiles.CreateSocketDirectory( "herdr-listener" ).FullName;

        try
        {
            var path = Path.Combine( directory, "h.sock" );
            using System.Net.Sockets.Socket socket = new( System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified );
            socket.Bind( new System.Net.Sockets.UnixDomainSocketEndPoint( path ) );
            socket.Listen( 4 );

            Equal( Environment.ProcessId, HerdrAppWindows.ReadListener( path ) );
            Equal( Environment.ProcessId, HerdrAppWindows.ReadListener( Path.Combine( directory, ".", "h.sock" ) ) );
            True( HerdrAppWindows.ReadListener( Path.Combine( directory, "none.sock" ) ) is null );
        }
        finally
        {
            Directory.Delete( directory, recursive: true );
        }

        // The CLI's default home: XDG_CONFIG_HOME when it is absolute, else ~/.config.
        Equal( "/x/herdr-app/herdr-app.sock",
            HerdrAppWindows.SocketPath( new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "/x", ["HOME"] = "/home/u" }.GetValueOrDefault ) );
        Equal( "/home/u/.config/herdr-app/herdr-app.sock",
            HerdrAppWindows.SocketPath( new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "rel", ["HOME"] = "/home/u" }.GetValueOrDefault ) );

        // Without HOME the CLI uses the account's home directory, and so does the lookup.
        Equal( "/home/acct/.config/herdr-app/herdr-app.sock", HerdrAppWindows.SocketPath( static _ => null, static ( ) => "/home/acct" ) );
        True( HerdrAppWindows.SocketPath( static _ => null, static ( ) => null ) is null );

        // The CLI gets no relative XDG_CONFIG_HOME, so it resolves the same home.
        var environment = new HerdrAppClient( new HerdrAppFake( ).Runner, static ( ) => true,
            new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "rel", ["HOME"] = "/home/u" }.GetValueOrDefault ).BuildEnvironment( );
        True( !environment.ContainsKey( "XDG_CONFIG_HOME" ) && environment["HOME"] == "/home/u" );

        // An unverified listener, or a different one after the switch, raises nothing.
        foreach ( (int? before, int? after) in new (int?, int?)[] { (null, null), (100, 200), (200, null) } )
        {
            var outcome = await AppActivator( after ).ActivateAppAsync( HerdrTargetKinds.AppTab, before, CancellationToken.None );
            True( !outcome.Succeeded );
            Equal( "Switched Herdr tab, but could not focus its window.", outcome.Message );
            True( outcome.DetailMarkdown?.Contains( "could not verify which Herdr app window", StringComparison.Ordinal ) == true, outcome.DetailMarkdown );
        }

        True( (await AppActivator( 200 ).ActivateAppAsync( HerdrTargetKinds.AppTab, 200, CancellationToken.None )).Succeeded );

        // Through the executor: without a verified listener the switch still runs, but no window is raised...
        HerdrAppFake unverified = new( );
        var tab = (await HerdrAppRowsAsync( )).Single( static row => row.Id == "herdr:app-tab:tb4" );
        var unread = await new LinuxActionExecutor( herdrWindowActivator: AppActivator( 100 ),
            herdrApp: new HerdrAppClient( unverified.Runner, static ( ) => true, listener: static ( ) => null ) ).ExecuteAsync( tab, tab.Actions[0], "",
            CancellationToken.None );

        True( !unread.Succeeded );
        Equal( "Switched Herdr tab, but could not focus its window.", unread.Message );
        Equal( """do|tab.select|{"id":"tb4"}""", Commands( unverified ) );

        // ...or between the switch and the raise.
        var raised = await new LinuxActionExecutor( herdrWindowActivator: AppActivator( 200 ),
            herdrApp: new HerdrAppClient( new HerdrAppFake( ).Runner, static ( ) => true, listener: static ( ) => 100 ) ).ExecuteAsync( tab,
            tab.Actions[0], "", CancellationToken.None );

        True( !raised.Succeeded );
        Equal( "Switched Herdr tab, but could not focus its window.", raised.Message );

        static HerdrWindowActivator AppActivator( int? listener ) => new( new HerdrClient( new HerdrFake( ).Runner, static ( ) => false ),
            static ( _, _, _ ) => ValueTask.FromResult( HerdrWindowTitleResult.Success( ) ),
            static ( _, args, _, _ ) => ValueTask.FromResult( HerdrResult( args[0] == "-j" ? HerdrAppTwoProcessWindowsJson : "ok\n" ) ), static ( ) => true,
            static ( ) => "unused", ( ) => listener );
    }
}
