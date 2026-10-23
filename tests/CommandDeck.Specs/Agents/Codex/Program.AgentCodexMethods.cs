using System.Text.Json.Nodes;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static ValueTask<JsonNode?> HandleEchoRequest( string method )
    {
        if ( method == "echo" )
        {
            return ValueTask.FromResult<JsonNode?>( new JsonObject { ["echoed"] = true } );
        }

        throw new JsonRpcException( -32601, "declined" );
    }

    private sealed class RecordingTurnHost : IAgentTurnHost
    {
        public List<string> Statuses { get; } = [];
        public System.Text.StringBuilder Text { get; } = new( );
        public System.Text.StringBuilder Reasoning { get; } = new( );
        public List<AgentToolCall> ToolCalls { get; } = [];
        public AgentUserInputRequest? UserInput { get; private set; }
        public AiUsage? Usage { get; private set; }
        public Func<AgentToolCall, AgentToolResult> ToolHandler { get; set; } = static _ => AgentToolResult.Ok( "{\"ok\":true}" );
        public Func<AgentUserInputRequest, AgentUserInputResult> InputHandler { get; set; } = static _ => new AgentUserInputResult { Declined = true };

        public ValueTask OnStatusAsync( string status )
        {
            Statuses.Add( status );

            return ValueTask.CompletedTask;
        }

        public ValueTask OnTextAsync( string delta )
        {
            Text.Append( delta );

            return ValueTask.CompletedTask;
        }

        public ValueTask OnReasoningAsync( string delta )
        {
            Reasoning.Append( delta );

            return ValueTask.CompletedTask;
        }

        public ValueTask OnUsageAsync( AiUsage usage )
        {
            Usage = usage;

            return ValueTask.CompletedTask;
        }

        public ValueTask<AgentToolResult> OnToolCallAsync( AgentToolCall toolCall, CancellationToken cancellationToken )
        {
            ToolCalls.Add( toolCall );

            return ValueTask.FromResult( ToolHandler( toolCall ) );
        }

        public ValueTask<AgentUserInputResult> OnUserInputAsync( AgentUserInputRequest request, CancellationToken cancellationToken )
        {
            UserInput = request;

            return ValueTask.FromResult( InputHandler( request ) );
        }
    }

    private static async Task JsonRpcConnectionCorrelatesRequestsAndFailsClosedAsync( )
    {
        await using FakeAppServer server = new( );
        await using JsonRpcConnection connection = new( server.Transport );
        List<string> notifications = [];

        connection.NotificationHandler = ( method, _, _ ) =>
        {
            notifications.Add( method );

            return ValueTask.CompletedTask;
        };

        connection.RequestHandler = static ( method, _, _ ) => HandleEchoRequest( method );
        connection.Start( );
        var result = await connection.CallAsync( "model/list", new JsonObject( ), default );
        True( result?["data"] is JsonArray );

        await ThrowsAsync<JsonRpcException>( async ( ) => await connection.CallAsync( "unknown/method", null, default ) );

        server.Notify( "thread/status/changed", new JsonObject { ["threadId"] = "x" } );
        var echoed = await server.RequestAsync( "echo", new JsonObject( ) );

        True( echoed["result"]?["echoed"]
                  ?.GetValue<bool>( ) ==
              true );

        var declined = await server.RequestAsync( "item/commandExecution/requestApproval", new JsonObject( ) );

        Equal( -32601, declined["error"]?["code"]
            ?.GetValue<int>( ) );

        await Task.Delay( 50 );
        True( notifications.Contains( "thread/status/changed" ) );

        var pending = connection
            .CallAsync( "spec/hang", new JsonObject( ), default )
            .AsTask( );

        await Task.Delay( 50 );
        await server.StopAsync( default );
        await ThrowsAsync<Exception>( async ( ) => await pending.WaitAsync( TimeSpan.FromSeconds( 5 ) ) );
    }

    private static async Task CodexClientHandshakeThreadAndTurnFollowTheClosedContractAsync( )
    {
        await using FakeAppServer server = new( );
        server.Authenticated = true;

        server.TurnScript = async ( fake, threadId, turnId ) =>
        {
            fake.Notify( "item/started", new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
                ["startedAtMs"] = 1,
                ["item"] = new JsonObject { ["type"] = "reasoning", ["id"] = "r1" }
            } );

            fake.Notify( "item/reasoning/summaryTextDelta", new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
                ["itemId"] = "r1",
                ["summaryIndex"] = 0,
                ["delta"] = "thinking"
            } );

            var toolReply = await fake.RequestAsync( CodexRpcTokens.ItemToolCall,
                new JsonObject
                {
                    ["threadId"] = threadId,
                    ["turnId"] = turnId,
                    ["callId"] = "call-1",
                    ["tool"] = AgentToolNames.Search,
                    ["arguments"] = new JsonObject { ["query"] = "firefox" }
                } );

            fake.Notify( "item/agentMessage/delta", new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
                ["itemId"] = "m1",
                ["delta"] = "Hello "
            } );

            var inputReply = await fake.RequestAsync( "item/tool/requestUserInput",
                new JsonObject
                {
                    ["threadId"] = threadId,
                    ["turnId"] = turnId,
                    ["itemId"] = "q1",
                    ["isBlocking"] = true,
                    ["questions"] = new JsonArray( new JsonObject
                    {
                        ["id"] = "which",
                        ["header"] = "Choice",
                        ["question"] = "Which one?",
                        ["options"] = new JsonArray( new JsonObject { ["label"] = "A", ["description"] = "a" },
                            new JsonObject { ["label"] = "B", ["description"] = "b" } )
                    } )
                } );

            fake.Notify( "item/agentMessage/delta", new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
                ["itemId"] = "m1",
                ["delta"] = "world"
            } );

            fake.Notify( "thread/tokenUsage/updated",
                new JsonObject
                {
                    ["threadId"] = threadId,
                    ["turnId"] = turnId,
                    ["tokenUsage"] = new JsonObject
                    {
                        ["total"] = new JsonObject
                        {
                            ["inputTokens"] = 10,
                            ["outputTokens"] = 5,
                            ["totalTokens"] = 15,
                            ["cachedInputTokens"] = 0,
                            ["reasoningOutputTokens"] = 0
                        },
                        ["last"] = new JsonObject( ),
                        ["modelContextWindow"] = 1000
                    }
                } );

            fake.Notify( CodexRpcTokens.TurnCompleted,
                new JsonObject
                {
                    ["threadId"] = threadId,
                    ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = "completed", ["items"] = new JsonArray( ) }
                } );

            _ = toolReply;
            _ = inputReply;
        };

        await using var client = await CodexAppServerClient.ConnectAsync( server, "spec", default );
        await AssertHandshakeFramesAsync( server );
        var models = await client.ListModelsAsync( includeHidden: true, default );
        AssertModelInventory( models );
        var account = await client.ReadAccountAsync( default );
        True( account.Authenticated );

        var tools = AgentToolCatalog.ForTrustLevel( AgentTrustLevels.ApprovedContext, packagesAvailable: false );

        var session = await client.StartThreadAsync(
            new AgentSessionOptions
            {
                ModelId = "fake-sol",
                ReasoningEffort = "low",
                TrustLevel = AgentTrustLevels.ApprovedContext,
                DeveloperInstructions = "spec",
                Tools = [.. tools]
            }, "/tmp", default );

        AssertThreadStartContract( server.LastThreadStart!, tools );
        RecordingTurnHost host = new( );

        host.InputHandler = request => new AgentUserInputResult { Answers = new Dictionary<string, string>( StringComparer.Ordinal ) { ["which"] = "B" } };

        await session.RunTurnAsync( new AgentTurnInput { Text = "hi", ReasoningEffort = "ultra" }, host, default );

        AssertTurnOutcome( server, host );
        await session.DisposeAsync( );
    }

    private static async Task AssertHandshakeFramesAsync( FakeAppServer server )
    {
        True( await WaitUntilAsync( ( ) => Task.FromResult( server.Received.Any( frame => frame["method"]
                                                                                              ?.GetValue<string>( ) ==
                                                                                          "initialized" ) ), TimeSpan.FromSeconds( 2 ) ) );

        var initialize = server.Received.First( frame => frame["method"]
                                                             ?.GetValue<string>( ) ==
                                                         "initialize" );

        var capabilities = initialize["params"]!["capabilities"]!;
        True( capabilities["experimentalApi"]!.GetValue<bool>( ) );
        True( !capabilities["requestAttestation"]!.GetValue<bool>( ) );

        True( server.Received.Any( frame => frame["method"]
                                                ?.GetValue<string>( ) ==
                                            "initialized" ) );
    }

    private static void AssertModelInventory( AgentModelInventory models )
    {
        Equal( 2, models.Models.Count );
        Equal( "fake-sol", models.DefaultModelId );

        Equal( "ultra", models
            .Models[0]
            .ReasoningEfforts[1].Id );

        True( models.Models[1].Hidden );
    }

    private static void AssertThreadStartContract( JsonObject threadStart, IReadOnlyList<AgentToolDefinition> tools )
    {
        Equal( ":read-only", threadStart["permissions"]!.GetValue<string>( ) );
        Equal( "never", threadStart["approvalPolicy"]!.GetValue<string>( ) );
        Equal( "user", threadStart["approvalsReviewer"]!.GetValue<string>( ) );
        True( threadStart["ephemeral"]!.GetValue<bool>( ) );
        True( !threadStart["allowProviderModelFallback"]!.GetValue<bool>( ) );
        True( threadStart["sandbox"] is null );
        var dynamicTools = ( JsonArray ) threadStart["dynamicTools"]!;
        Equal( tools.Count, dynamicTools.Count );
        True( dynamicTools.Any( tool => tool!["name"]!.GetValue<string>( ) == "commanddeck_read_file" ) );
    }

    private static void AssertTurnOutcome( FakeAppServer server, RecordingTurnHost host )
    {
        Equal( "Hello world", host.Text.ToString( ) );
        Equal( "thinking", host.Reasoning.ToString( ) );
        Equal( 1, host.ToolCalls.Count );
        Equal( "commanddeck_search", host.ToolCalls[0].Name );

        True( host
            .ToolCalls[0]
            .ArgumentsJson.Contains( "firefox", StringComparison.Ordinal ) );

        True( host.UserInput is { Questions: [{ Options.Count: 2 }] } input );
        Equal( 15L, host.Usage!.TotalTokens );
        True( host.Statuses.Contains( "Thinking" ) );
        Equal( "ultra", server.LastTurnStart!["effort"]!.GetValue<string>( ) );
        var toolResponse = server.Received.First( frame => frame["result"]?["contentItems"] is JsonArray );
        True( toolResponse["result"]!["success"]!.GetValue<bool>( ) );
        var answerResponse = server.Received.First( frame => frame["result"]?["answers"] is JsonObject );
        Equal( "B", answerResponse["result"]!["answers"]!["which"]!["answers"]![0]!.GetValue<string>( ) );
    }
}
