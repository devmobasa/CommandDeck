namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task CliAgentBackendOwnsSignInModelsAndSandboxedTurnsAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-cli-agent-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            var binary = CreateFakeCliBinary( root, "claude" );
            var options = CreateCliAgentOptions( root, claude: binary );
            ScriptedCliLauncher launcher = CreateTurnClaudeLauncher( );
            await using CliAgentBackend backend = new( new ClaudeCodeProfile( ), options, launcher );
            Equal( "claude.local", backend.Id );
            var descriptor = backend.Describe( );

            True( descriptor is { Available: true, Capabilities.Login: true, Capabilities.Tools: false, Capabilities.Conversation: true } );

            Equal( "unauthenticated", descriptor.AuthenticationState );
            Equal( 0, launcher.Requests.Count );
            var probed = await backend.GetLoginStateAsync( default );
            Equal( "unauthenticated", probed.State );
            Equal( "auth status --json", string.Join( ' ', launcher.Requests[0].Arguments ) );
            True( launcher.Requests[0].HomePath == Path.Combine( root, "state", "claude", "home" ) );

            True( launcher
                .Requests[0]
                .WorkingDirectory.StartsWith( Path.Combine( root, "runtime", "claude" ), StringComparison.Ordinal ) );

            Equal( binary, launcher.Requests[0].BinaryPath );
            Equal( UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, UnixModeOf( launcher.Requests[0].HomePath ) );

            var models = await backend.DiscoverModelsAsync( false, default );
            True( models is { Error: null, Models.Count: 4, DefaultModelId: "sonnet" } );
            Equal( 1, launcher.Requests.Count );

            var apiKey = await ThrowsAiAsync( async ( ) => await backend.StartLoginAsync( AgentLoginMethods.ApiKey, null, default ) );

            Equal( "unsupported", apiKey.Category );
            var pending = await backend.StartLoginAsync( AgentLoginMethods.Browser, null, default );
            Equal( "pending", pending.State );

            True( pending is { RequiresCode: true, VerificationUrl: "https://claude.com/oauth?code=true&state=abc" } );
            True( launcher.Requests[1].StandardInput );
            Equal( "pending", (await backend.GetLoginStateAsync( default )).State );
            var completed = await backend.CompleteLoginAsync( "pasted-code", default );
            Equal( "authenticated", completed.State );
            Equal( "pasted-code\n", launcher.Processes[1].Stdin.Text );

            True( File.Exists( Path.Combine( root, "state", "claude", "home", ".claude", ".credentials.json" ) ) );

            True( !Directory.Exists( Path.Combine( root, "runtime", "claude" ) ) ||
                  Directory.GetDirectories( Path.Combine( root, "runtime", "claude" ) )
                      .Length ==
                  0 );

            RecordingTurnHost host = new( );

            var session = await backend.StartSessionAsync( new AgentSessionOptions { ModelId = "sonnet", DeveloperInstructions = "answer only", Tools = [] },
                default );

            await session.RunTurnAsync( new AgentTurnInput { Text = "hello" }, host, default );
            Equal( "Hello", host.Text.ToString( ) );
            Equal( 5L, host.Usage?.TotalTokens );
            var turn = launcher.Requests[^1];

            True( turn.Arguments.Contains( "--print" ) && !turn.Arguments.Contains( "--resume" ) && turn.StandardInput );

            Equal( "hello", launcher.Processes[^1].Stdin.Text );
            Equal( "sess-1", (( CliAgentSession ) session).ContinuationId );
            await session.RunTurnAsync( new AgentTurnInput { Text = "again" }, host, default );
            True( HasArgumentPair( launcher.Requests[^1].Arguments, "--resume", "sess-1" ) );

            True( backend.Describe( )
                .Error is null );

            var failure = await ThrowsAiAsync( async ( ) =>
            {
                await session.RunTurnAsync( new AgentTurnInput { Text = "fail me" }, host, default );

                return true;
            } );

            Equal( "authentication", failure.Category );

            Equal( "authentication", backend.Describe( )
                .Error?.Category );

            var loggedOut = await backend.LogoutAsync( default );
            Equal( "unauthenticated", loggedOut.State );
            Equal( "auth logout", string.Join( ' ', launcher.Requests[^1].Arguments ) );

            True( !File.Exists( Path.Combine( root, "state", "claude", "home", ".claude", ".credentials.json" ) ) );

            await backend.StopAsync( default );
            True( !Directory.Exists( Path.Combine( root, "runtime", "claude" ) ) );

            var missing = new CliAgentBackend( new CursorProfile( ), CreateCliAgentOptions( root, cursor: Path.Combine( root, "no-cursor" ) ), launcher );

            var unavailable = missing.Describe( );
            True( unavailable is { Available: false, HealthState: "unavailable" } );
            var missingModels = await missing.DiscoverModelsAsync( false, default );
            Equal( "unavailable", missingModels.Error?.Category );
            await missing.DisposeAsync( );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task CliAgentGrokDeviceCodeLoginHarvestsUrlAndCodeAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-cli-grok-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            var binary = CreateFakeCliBinary( root, "grok" );
            TaskCompletionSource authorize = new( TaskCreationOptions.RunContinuationsAsynchronously );
            ScriptedCliLauncher launcher = CreateGrokLoginLauncher( authorize );

            await using CliAgentBackend backend = new( new GrokProfile( ), CreateCliAgentOptions( root, grok: binary ), launcher );

            Equal( "unauthenticated", (await backend.GetLoginStateAsync( default )).State );
            True( launcher.Requests.Count == 0, "the credential-file probe spawns nothing" );
            var pending = await backend.StartLoginAsync( AgentLoginMethods.DeviceCode, null, default );
            Equal( "pending", pending.State );
            Equal( "https://accounts.x.ai/oauth2/device?user_code=ABCD-EFGH", pending.VerificationUrl );
            True( !pending.RequiresCode && !launcher.Requests[0].StandardInput );
            await Task.Delay( 50 );
            Equal( "ABCD-EFGH", (await backend.GetLoginStateAsync( default )).UserCode );

            var rejected = await ThrowsAiAsync( async ( ) => await backend.CompleteLoginAsync( "nope", default ) );

            Equal( "unsupported", rejected.Category );
            authorize.SetResult( );

            for ( var attempt = 0; attempt < 50 && (await backend.GetLoginStateAsync( default )).State == AgentAuthenticationStates.Pending; attempt++ )
            {
                await Task.Delay( 20 );
            }

            Equal( "authenticated", (await backend.GetLoginStateAsync( default )).State );
            True( backend.Home.CredentialExists );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static ScriptedCliLauncher CreateTurnClaudeLauncher( )
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
                    process.Stdout.Write(
                        "Opening browser to sign in…\nIf the browser didn't open, visit: https://claude.com/oauth?code=true&state=abc\nPaste code here if prompted > " );

                    await process.Stdin.Closed.Task;
                    authenticated = process.Stdin.Text == "pasted-code\n";
                    Directory.CreateDirectory( Path.Combine( request.HomePath, ".claude" ) );

                    await File.WriteAllTextAsync( Path.Combine( request.HomePath, ".claude", ".credentials.json" ), "{}" );

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

                    if ( process.Stdin.Text == "fail me" )
                    {
                        process.Stdout.WriteLine(
                            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"result\":\"Not logged in\"}" );

                        process.Exit( 1 );

                        return;
                    }

                    process.Stdout.WriteLine( "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-1\"}" );

                    process.Stdout.WriteLine(
                        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}}" );

                    process.Stdout.WriteLine(
                        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"lo\"}}}" );

                    process.Stdout.WriteLine(
                        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Hello\",\"session_id\":\"sess-1\",\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}" );

                    process.Exit( 0 );
                }
                else
                {
                    process.Exit( 2 );
                }
            }
        };
    }

    private static ScriptedCliLauncher CreateGrokLoginLauncher( TaskCompletionSource authorize )
    {
        return new( )
        {
            Script = async ( request, process ) =>
            {
                if ( string.Join( ' ', request.Arguments ) == "login --device-auth" )
                {
                    process.Stdout.Write(
                        "\nTo sign in, open this URL in your browser:\n\n  https://accounts.x.ai/oauth2/device?user_code=ABCD-EFGH\n\nConfirm this code in your browser:\n\n  ABCD-EFGH\n\nWaiting for authorization...\n" );

                    await authorize.Task;
                    Directory.CreateDirectory( Path.Combine( request.HomePath, ".grok" ) );
                    await File.WriteAllTextAsync( Path.Combine( request.HomePath, ".grok", "auth.json" ), "{}" );
                    process.Exit( 0 );

                    return;
                }

                process.Exit( 3 );
            }
        };
    }
}
