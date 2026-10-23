namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task HeyClientListsBoxesAsync( )
    {
        HeyFake fake = new( );

        var listing = await fake
            .Client( )
            .ListBoxAsync( "imbox", 500, CancellationToken.None );

        Equal( "box|view|imbox|--json|--limit=50", string.Join( '|', fake.Calls[0].Args ) );
        True( listing.HasMore );
        Equal( "9101,9103", string.Join( ',', listing.Threads.Select( static thread => thread.TopicId ) ) );
        Equal( ( bool? ) false, listing.Threads[0].Seen );
        Equal( ( bool? ) true, listing.Threads[1].Seen );
        Equal( ( bool? ) false, listing.Threads[0].BubbledUp );
        Equal( ( bool? ) true, listing.Threads[1].BubbledUp );
        Equal( ( long? ) 11, listing.Threads[0].BoxId );
        Equal( ( long? ) 601, listing.Threads[0].PostingId );
        Equal( "Lunch?", listing.Threads[0].Subject );
        Equal( "Alice", listing.Threads[0].Sender );

        Equal( new DateTimeOffset( 2026, 9, 19, 9, 0, 0,
            TimeSpan.Zero ), listing.Threads[1].At );

        Equal( "https://app.hey.com/topics/9103", listing.Threads[1].AppUrl );

        await ThrowsAsync<ArgumentOutOfRangeException>( async ( ) => await fake
            .Client( )
            .ListBoxAsync( "imbox; rm", 10, CancellationToken.None ) );
    }

    private static async Task HeyClientReportsTypedFailuresAsync( )
    {
        async Task<HeyFailureKind?> KindForAsync( ProcessResult result )
        {
            HeyFake fake = new( ) { Respond = _ => result };

            return (await fake
                .Client( )
                .SearchAsync( new HeySearchQuery { Text = "x" }, CancellationToken.None )).Failure?.Kind;
        }

        Equal( HeyFailureKind.SignedOut, await KindForAsync( HeyResult( HeySignedOutJson, 1 ) ) );
        Equal( HeyFailureKind.Network, await KindForAsync( HeyResult( """{"ok":false,"error":"dial tcp: offline","code":"network"}""", 1 ) ) );
        Equal( HeyFailureKind.RateLimited, await KindForAsync( HeyResult( """{"ok":false,"error":"slow down","code":"rate_limit"}""", 1 ) ) );
        Equal( HeyFailureKind.Rejected, await KindForAsync( HeyResult( """{"ok":false,"error":"bad --date","code":"usage"}""", 1 ) ) );
        Equal( HeyFailureKind.Failed, await KindForAsync( HeyResult( "", 2 ) ) );
        Equal( HeyFailureKind.Failed, await KindForAsync( HeyResult( """{"ok":true,"data":{"not":"a list"}}""" ) ) );

        Equal( HeyFailureKind.TimedOut, await KindForAsync( new ProcessResult
        {
            ExitCode = -1,
            StandardOutput = "",
            StandardError = "",
            TimedOut = true
        } ) );

        Equal( HeyFailureKind.Failed, await KindForAsync( new ProcessResult
        {
            ExitCode = -1,
            StandardOutput = "",
            StandardError = "",
            OutputLimitExceeded = true
        } ) );

        // A successful envelope from a failing process is not trusted.
        Equal( HeyFailureKind.Failed, await KindForAsync( HeyResult( HeySearchJson, 1 ) ) );

        HeyClient broken = new( static ( _, _, _, _, _ ) => throw new IOException( "exec failed" ), static ( ) => HeyBinary, HeyEnvironment );

        Equal( HeyFailureKind.Failed, (await broken.SearchAsync( new HeySearchQuery { Text = "x" }, CancellationToken.None )).Failure?.Kind );

        // Only the caller's cancellation escapes as an exception.
        HeyClient cancelled = new( static ( _, _, _, _, token ) => throw new OperationCanceledException( token ), static ( ) => HeyBinary, HeyEnvironment );

        await ThrowsAsync<OperationCanceledException>( async ( ) =>
            await cancelled.SearchAsync( new HeySearchQuery { Text = "x" }, new CancellationToken( true ) ) );
    }

    private static void HeyClientCleansMailText( )
    {
        Equal( "a b c", HeyClient.Clean( "  a\tb​\r\nc  ", 50 ) );
        Equal( "", HeyClient.Clean( "\u0000\u0007", 50 ) );
        Equal( "ab", HeyClient.Clean( "ab😀", 3 ) );
        Equal( "https://app.hey.com/topics/5", HeyClient.ThreadUrl( 5 ) );

        // The space kept between words counts toward the bound, and the bound
        // never ends on a space.
        Equal( "abcd", HeyClient.Clean( "abcd e", 5 ) );
        Equal( "ab cd", HeyClient.Clean( "ab cd ef", 5 ) );
        Equal( "abc", HeyClient.Clean( "abc 😀", 5 ) );

        foreach ( var length in Enumerable.Range( 1, 12 ) )
        {
            True( HeyClient.Clean( "a b  c\td e😀 f", length )
                      .Length <=
                  length, "the bound holds at every length" );
        }
    }

    // The real runner: a stand-in `hey` proves the root working directory and
    // that only the forwarded environment reaches the process.
    private static async Task HeyClientRunsRealProcessFromRootAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-hey-client-" ).FullName;

        try
        {
            var script = Path.Combine( root, "hey" );

            await File.WriteAllTextAsync( script, """
                                                  #!/bin/sh
                                                  if [ "$PWD" = "/" ] && [ "$HEY_NONINTERACTIVE" = "1" ] && [ -z "$COMMANDDECK_HEY_SPEC_SECRET" ]; then
                                                    printf '{"ok":true,"data":{"authenticated":true}}'
                                                  else
                                                    printf '{"ok":true,"data":{"authenticated":false}}'
                                                  fi
                                                  """ );

            File.SetUnixFileMode( script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

            Environment.SetEnvironmentVariable( "COMMANDDECK_HEY_SPEC_SECRET", "leaked" );
            HeyClient client = new( resolve: ( ) => script );
            var status = await client.GetStatusAsync( CancellationToken.None );
            Equal( HeyAvailability.SignedIn, status.Availability );
        }
        finally
        {
            Environment.SetEnvironmentVariable( "COMMANDDECK_HEY_SPEC_SECRET", null );
            Directory.Delete( root, recursive: true );
        }
    }

    // Keeps the environment assertion readable: fixed order, no set semantics.
    private sealed class HeyEnvironmentKeyOrder : IComparer<string>
    {
        private static readonly string[] Order =
        [
            "PATH", "HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "MISE_DATA_DIR", "LANG", "LC_ALL", "NO_COLOR", "HEY_NONINTERACTIVE"
        ];

        public int Compare( string? left, string? right ) =>
            Rank( left )
                .CompareTo( Rank( right ) );

        private static int Rank( string? key )
        {
            var index = Array.IndexOf( Order, key );

            return index < 0 ? int.MaxValue : index;
        }
    }
}
