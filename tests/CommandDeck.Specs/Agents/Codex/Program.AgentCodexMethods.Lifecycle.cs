using System.Text.Json.Nodes;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task CodexSessionMapsFailuresInterruptsAndDeclinesAsync( )
    {
        await using FakeAppServer server = new( );
        server.Authenticated = true;
        var turn = 0;

        server.TurnScript = async ( fake, threadId, turnId ) =>
        {
            var current = Interlocked.Increment( ref turn );
            if ( current == 1 )
            {
                fake.Notify( "error", new JsonObject
                {
                    ["threadId"] = threadId,
                    ["turnId"] = turnId,
                    ["willRetry"] = false,
                    ["error"] = new JsonObject { ["message"] = "quota", [CodexRpcTokens.CodexErrorInfo] = "usageLimitExceeded" }
                } );

                fake.Notify( CodexRpcTokens.TurnCompleted,
                    new JsonObject
                    {
                        ["threadId"] = threadId,
                        ["turn"] = new JsonObject
                        {
                            ["id"] = turnId,
                            ["status"] = "failed",
                            ["items"] = new JsonArray( ),
                            ["error"] = new JsonObject { ["message"] = "quota exceeded", [CodexRpcTokens.CodexErrorInfo] = "usageLimitExceeded" }
                        }
                    } );

                return;
            }

            if ( current == 2 )
            {
                fake.Notify( "item/agentMessage/delta", new JsonObject
                {
                    ["threadId"] = threadId,
                    ["turnId"] = turnId,
                    ["itemId"] = "m",
                    ["delta"] = "partial"
                } );

                await Task.Delay( 30_000 );
            }
        };

        await using var client = await CodexAppServerClient.ConnectAsync( server, "spec", default );

        var session = await client.StartThreadAsync( new AgentSessionOptions { DeveloperInstructions = "spec" }, "/tmp", default );

        RecordingTurnHost host = new( );
        AiOperationException failure = null!;

        try
        {
            await session.RunTurnAsync( new AgentTurnInput { Text = "one" }, host, default );
        }
        catch ( AiOperationException exception )
        {
            failure = exception;
        }

        True( failure is not null );
        Equal( "rate-limited", failure!.Category );
        using CancellationTokenSource cancellation = new( );
        RecordingTurnHost second = new( );

        var run = session
            .RunTurnAsync( new AgentTurnInput { Text = "two" }, second, cancellation.Token )
            .AsTask( );

        await Task.Delay( 200 );
        cancellation.Cancel( );

        await ThrowsAsync<OperationCanceledException>( async ( ) => await run.WaitAsync( TimeSpan.FromSeconds( 10 ) ) );

        True( server.Received.Any( frame => frame["method"]
                                                ?.GetValue<string>( ) ==
                                            "turn/interrupt" ) );

        Equal( "partial", second.Text.ToString( ) );

        var declined = await server.RequestAsync( "item/fileChange/requestApproval", new JsonObject { ["threadId"] = "thread-1" } );

        Equal( -32601, declined["error"]?["code"]
            ?.GetValue<int>( ) );

        var noTurn = await server.RequestAsync( CodexRpcTokens.ItemToolCall, new JsonObject
        {
            ["threadId"] = "thread-1",
            ["callId"] = "c",
            ["tool"] = "x",
            ["arguments"] = new JsonObject( )
        } );

        True( noTurn["error"] is not null );
    }

    private static async Task CodexBackendOwnsLoginAndRuntimeLifecycleAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-agent-backend-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            var binary = CreateFakeCodexBinary( root );
            var options = CreateAgentOptions( root, binary );
            CodexCredentialStore credentials = new( options.StateDirectory );
            FakeCodexLauncher launcher = new( );
            await using CodexAgentBackend backend = new( options, credentials, _ => launcher, clientVersion: "spec" );

            Equal( "unauthenticated", backend.Describe( ).AuthenticationState );
            Equal( "configured", backend.Describe( ).HealthState );
            True( !backend.IsRunning );
            var models = await backend.DiscoverModelsAsync( includeHidden: false, default );
            True( models is { Error: null, Models.Count: 1 } );
            True( backend.IsRunning );
            Equal( 1, launcher.Requests.Count );

            True( launcher
                .Requests[0]
                .RuntimeHome.StartsWith( Path.Combine( root, "runtime" ), StringComparison.Ordinal ) );

            True( File.Exists( Path.Combine( launcher.Requests[0].RuntimeHome, "config.toml" ) ) );
            Equal( binary, launcher.Requests[0].BinaryPath );
            Equal( "0.152.1", backend.BinaryVersion );
            var pending = await backend.StartLoginAsync( AgentLoginMethods.DeviceCode, null, default );
            Equal( "pending", pending.State );
            Equal( "ABCD-EFGH", pending.UserCode );
            launcher.Current!.CompleteLogin( success: true );
            await Task.Delay( 100 );
            Equal( "authenticated", (await backend.GetLoginStateAsync( default )).State );
            True( credentials.Exists );
            await backend.StopAsync( default );
            True( !backend.IsRunning );
            True( !Directory.Exists( launcher.Requests[0].RuntimeHome ) );
            True( credentials.Exists );
            True( launcher.Current.StopRequested );
            var again = await backend.DiscoverModelsAsync( includeHidden: false, default );
            True( again.Error is null );
            Equal( 2, launcher.Requests.Count );
            True( File.Exists( Path.Combine( launcher.Requests[1].RuntimeHome, "auth.json" ) ) );
            var loggedOut = await backend.LogoutAsync( default );
            Equal( "unauthenticated", loggedOut.State );
            True( !credentials.Exists );

            // Codex announces the logout after replying to it. Handled after
            // LogoutAsync has returned, that announcement copies nothing back;
            // the account read after it orders the two on the connection.
            launcher.Current.Notify( "account/updated", new JsonObject { ["authMode"] = null } );
            await backend.GetLoginStateAsync( default );
            True( !credentials.Exists );
            var badOptions = CreateAgentOptions( root, Path.Combine( root, "missing-codex" ) );
            await using CodexAgentBackend broken = new( badOptions, credentials, _ => launcher );
            var failed = await broken.DiscoverModelsAsync( false, default );
            Equal( "unavailable", failed.Error?.Category );
            Equal( "unavailable", broken.Describe( ).HealthState );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
