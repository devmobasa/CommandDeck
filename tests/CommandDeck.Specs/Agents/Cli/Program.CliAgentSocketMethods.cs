namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task CliAgentBackendSignsInAndAnswersThroughTheDaemonAsync( )
    {
        var binaryRoot = SpecTemporaryFiles.PathFor( $"commanddeck-cli-socket-bin-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( binaryRoot );
        var binary = CreateFakeCliBinary( binaryRoot, "claude" );
        ScriptedCliLauncher launcher = CreateSocketClaudeLauncher( );
        ScriptedAgentBackend codex = new( );

        RuntimeConstructionOptions constructionOptions = new( )
        {
            CliAgentBackendFactory = ( profile, options ) => CreateClaudeBackend( profile, options, binary, launcher )
        };

        using CancellationTokenSource serverCancellation = new( );
        CommandDeckRuntime runtime;
        string root;

        (runtime, _, root, _) = await StartAgentRuntimeAsync( codex, serverCancellation.Token, constructionOptions );

        try
        {
            await using ( runtime )
            {
                var socket = SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" );
                await using var client = await ConnectWithRetryAsync( socket );

                await client.SendAsync( new ProtocolRequest { RequestId = 1, Operation = ProtocolOperations.AgentBackendsGet }, default );

                var inventory = (await CollectUntilDoneAsync( client, 1 )).Single( static message => message.AgentBackends is not null ).AgentBackends!;

                Equal( 2, inventory.Backends.Count );
                var claude = inventory.Backends.Single( static backend => backend.Id == "claude.local" );

                True( claude is { Available: true, Capabilities.Login: true, Capabilities.Tools: false, Kind: "claude-code-cli" } );

                Equal( "unauthenticated", claude.AuthenticationState );

                await client.SendAsync(
                    new ProtocolRequest
                    {
                        RequestId = 2,
                        Operation = ProtocolOperations.AgentPreferencesUpdate,
                        AgentPreferences = new AgentPreferences { Enabled = true, BackendId = AgentBackendIds.ClaudeCode }
                    }, default );

                _ = await CollectUntilDoneAsync( client, 2 );

                await client.SendAsync( new ProtocolRequest { RequestId = 3, Operation = ProtocolOperations.AgentStatusGet }, default );

                var status = (await CollectUntilDoneAsync( client, 3 )).Single( static message => message.AgentStatus is not null ).AgentStatus!;

                Equal( "claude.local", status.Backend?.Id );
                Equal( "unauthenticated", status.Login.State );
                Equal( "claude.local", status.Login.BackendId );

                await client.SendAsync( new ProtocolRequest { RequestId = 4, Operation = ProtocolOperations.AgentTurnStart, Query = "hi" }, default );

                var unauthenticated = await CollectUntilDoneAsync( client, 4 );

                True( unauthenticated.Any( static message => message.Type == "error" && message.Error!.Contains( "Claude Code", StringComparison.Ordinal ) ) );

                await client.SendAsync(
                    new ProtocolRequest
                    {
                        RequestId = 5,
                        Operation = ProtocolOperations.AgentLoginStart,
                        LoginMethod = AgentLoginMethods.Browser,
                        BackendId = AgentBackendIds.ClaudeCode
                    }, default );

                var pending = (await CollectUntilDoneAsync( client, 5 )).Single( static message => message.AgentLogin is not null ).AgentLogin!;

                Equal( "pending", pending.State );
                True( pending is { RequiresCode: true, VerificationUrl: "https://claude.com/oauth?code=true" } );

                await client.SendAsync( new ProtocolRequest
                {
                    RequestId = 6,
                    Operation = ProtocolOperations.AgentLoginComplete,
                    LoginCode = "socket-code",
                    BackendId = AgentBackendIds.ClaudeCode
                }, default );

                var completed = (await CollectUntilDoneAsync( client, 6 )).Single( static message => message.AgentLogin is not null ).AgentLogin!;

                Equal( "authenticated", completed.State );

                var rows = await runtime.SearchAsync( new SearchRequest { Query = "claude: hi", Limit = 8 }, default );

                var askRow = rows.Single( static row => row.Id == "agent:claude" );
                Equal( "Ask AI: hi", askRow.Title );

                Equal( "claude.local", askRow
                    .Actions[0]
                    .Arguments["backendId"] );

                True( (await runtime.SearchAsync( new SearchRequest { Query = "ai: hi", Limit = 8 }, default )).Any( static row => row.Id == "agent:claude" ) );

                await client.SendAsync( new ProtocolRequest { RequestId = 7, Operation = ProtocolOperations.AgentTurnStart, Query = "hello" }, default );

                var turn = await CollectUntilDoneAsync( client, 7 );

                var text = string.Concat( turn.Select( static message => message.AgentEvent?.Text ?? string.Empty ) );

                Equal( "Hello from Claude", text );
                var done = turn.Single( static message => message.AgentEvent?.Done is not null ).AgentEvent!.Done!;
                Equal( "completed", done.Outcome );

                Equal( "claude.local", runtime.GetAgentConversation( )
                    .Summary?.BackendId );

                Equal( 0, codex.Sessions );

                await client.SendAsync(
                    new ProtocolRequest { RequestId = 8, Operation = ProtocolOperations.AgentLogout, BackendId = AgentBackendIds.ClaudeCode }, default );

                var loggedOut = (await CollectUntilDoneAsync( client, 8 )).Single( static message => message.AgentLogin is not null ).AgentLogin!;

                Equal( "unauthenticated", loggedOut.State );
                True( codex.Authenticated, "signing out of one backend leaves the others alone" );

                True( launcher.Requests.All( static request => !request.HomePath.Contains( "/.claude", StringComparison.Ordinal ) &&
                                                               request.HomePath.Contains( Path.Combine( "state", "claude", "home" ),
                                                                   StringComparison.Ordinal ) ) );

                serverCancellation.Cancel( );
            }
        }
        finally
        {
            serverCancellation.Cancel( );
            Directory.Delete( root, recursive: true );
            Directory.Delete( binaryRoot, recursive: true );
        }
    }

    private static CliAgentBackend? CreateClaudeBackend( CliAgentProfile profile, AgentOptions options, string binary, ScriptedCliLauncher launcher )
    {
        if ( profile.BackendId != AgentBackendIds.ClaudeCode )
        {
            return null;
        }

        var claudeOptions = new AgentOptions
        {
            RuntimeDirectory = options.RuntimeDirectory,
            StateDirectory = options.StateDirectory,
            BubblewrapPath = options.BubblewrapPath,
            SystemdRunPath = options.SystemdRunPath,
            ClaudeBinaryPath = binary
        };

        return new CliAgentBackend( profile, claudeOptions, launcher );
    }

    private static ScriptedCliLauncher CreateSocketClaudeLauncher( )
    {
        var authenticated = false;

        return new( )
        {
            Script = async ( request, process ) =>
            {
                var argv = string.Join( ' ', request.Arguments );
                if ( argv == "auth status --json" )
                {
                    process.Stdout.WriteLine( authenticated ? "{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}" : "{\"loggedIn\":false}" );

                    process.Exit( 0 );
                }
                else if ( argv == "auth login" )
                {
                    process.Stdout.Write( "If the browser didn't open, visit: https://claude.com/oauth?code=true\nPaste code here if prompted > " );

                    await process.Stdin.Closed.Task;
                    authenticated = process.Stdin.Text.Trim( ) == "socket-code";
                    process.Exit( 0 );
                }
                else if ( argv == "auth logout" )
                {
                    authenticated = false;
                    process.Exit( 0 );
                }
                else if ( request.Arguments.Contains( "--print" ) )
                {
                    await process.Stdin.Closed.Task;
                    process.Stdout.WriteLine( "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sock-1\"}" );

                    process.Stdout.WriteLine(
                        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello from Claude\"}}}" );

                    process.Stdout.WriteLine(
                        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Hello from Claude\",\"session_id\":\"sock-1\"}" );

                    process.Exit( 0 );
                }
                else
                {
                    process.Exit( 2 );
                }
            }
        };
    }
}
