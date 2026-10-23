using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task<(CommandDeckRuntime Runtime, UnixProtocolServer Server, string Root, ScriptedAgentBackend Backend)> StartAgentRuntimeAsync(
        ScriptedAgentBackend backend, CancellationToken serverToken, RuntimeConstructionOptions? constructionOptions = null, AgentOptions? agentOptions = null )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-agent-socket-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );
        var config = Path.Combine( root, "config.json" );
        CommandDeckOptions options = CreateOptions( agentOptions, root );

        await File.WriteAllTextAsync( config, JsonSerializer.Serialize( options, CommandDeckJsonContext.Default.CommandDeckOptions ), serverToken );

        var runtime = await CommandDeckRuntime.CreateAsync( config,
            new RuntimeConstructionOptions
            {
                AgentBackendFactory = ( _, _ ) => backend,
                CliAgentBackendFactory = constructionOptions?.CliAgentBackendFactory,
                PackageCatalogFactory = constructionOptions?.PackageCatalogFactory,
                AgentClock = constructionOptions?.AgentClock,
                PackageHelperFactory = constructionOptions?.PackageHelperFactory
            }, CancellationToken.None );

        UnixProtocolServer server = new( runtime, SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" ) );
        _ = server.RunAsync( serverToken );

        return (runtime, server, root, backend);

        static CommandDeckOptions CreateOptions( AgentOptions? agentOptions, string root )
        {
            return new( )
            {
                Storage = new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) },
                Indexing = new IndexingOptions { Roots = [], IndexDesktopApplications = false, IndexOmarchyCommands = false },
                Privacy = new PrivacyOptions { StoreUsageHistory = false, NeverIndex = [] },
                Ai = new AiOptions { Enabled = false, Providers = [] },
                Agent = agentOptions ??
                        new AgentOptions { RuntimeDirectory = Path.Combine( root, "runtime" ), StateDirectory = Path.Combine( root, "state" ) }
            };
        }
    }

    private static async Task<List<ProtocolMessage>> CollectUntilDoneAsync( UnixProtocolClient client, long requestId )
    {
        List<ProtocolMessage> messages = [];
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 20 ) );
        await foreach ( var line in client.ReadRawMessagesAsync( timeout.Token ) )
        {
            var message = DeserializeMessage( line );
            if ( message is null )
            {
                continue;
            }

            if ( message.RequestId != requestId )
            {
                continue;
            }

            messages.Add( message );

            if ( message.Type == "done" )
            {
                break;
            }
        }

        return messages;
    }

    private static async Task<List<ProtocolMessage>> ReadUntilRequestEventAsync( UnixProtocolClient client, long requestId )
    {
        List<ProtocolMessage> events = [];
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 20 ) );
        await foreach ( var line in client.ReadRawMessagesAsync( timeout.Token ) )
        {
            var message = DeserializeMessage( line );
            if ( message?.RequestId != requestId )
            {
                continue;
            }

            events.Add( message );

            if ( message.AgentEvent?.Kind == "request" )
            {
                break;
            }
        }

        return events;
    }

    private static async Task<List<ProtocolMessage>> ReadUntilSnapshotAsync( UnixProtocolClient client, long requestId )
    {
        List<ProtocolMessage> messages = [];
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 20 ) );
        await foreach ( var line in client.ReadRawMessagesAsync( timeout.Token ) )
        {
            var message = DeserializeMessage( line );
            if ( message?.RequestId != requestId )
            {
                continue;
            }

            messages.Add( message );

            if ( message.Type == "agent-conversation-result" )
            {
                break;
            }
        }

        return messages;
    }

    private static async Task AgentSocketStreamsDaemonOwnedTurnsAndParksQuestionsAsync( )
    {
        ScriptedAgentBackend backend = new( );
        TaskCompletionSource questionAsked = new( TaskCreationOptions.RunContinuationsAsynchronously );

        backend.Script = async ( _, host, token ) =>
        {
            await host.OnStatusAsync( "Thinking" );
            await host.OnTextAsync( "Hello " );

            var answer = await host.OnUserInputAsync(
                new AgentUserInputRequest { Questions = [new AgentQuestion { Id = "q1", Prompt = "Which?", Options = ["A", "B"] }] }, token );

            questionAsked.TrySetResult( );
            await host.OnTextAsync( "you chose " + (answer.Declined ? "nothing" : answer.Answers["q1"]) );
        };

        using CancellationTokenSource serverCancellation = new( );

        (var runtime, var _, var root, var _) = await StartAgentRuntimeAsync( backend, serverCancellation.Token );

        try
        {
            await using ( runtime )
            {
                var socket = SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" );
                await using var client = await ConnectWithRetryAsync( socket );

                await client.SendAsync( new ProtocolRequest { RequestId = 1, Operation = ProtocolOperations.AgentTurnStart, Query = "hi" }, default );

                var disabled = await CollectUntilDoneAsync( client, 1 );

                True( disabled.Any( message => message.Type == "error" && message.Error!.Contains( "disabled", StringComparison.OrdinalIgnoreCase ) ) );

                await client.SendAsync(
                    new ProtocolRequest
                    {
                        RequestId = 2,
                        Operation = ProtocolOperations.AgentPreferencesUpdate,
                        AgentPreferences = new AgentPreferences { Enabled = true, BackendId = AgentBackendIds.Codex }
                    }, default );

                var enabled = await CollectUntilDoneAsync( client, 2 );
                var enabledPreferences = enabled[0].AgentPreferences;
                True( enabledPreferences is { Enabled: true, TrustLevel: 2 } );

                await client.SendAsync( new ProtocolRequest { RequestId = 3, Operation = ProtocolOperations.AgentTurnStart, Query = "hi" }, default );

                var firstEvents = await ReadUntilRequestEventAsync( client, 3 );
                var pending = firstEvents[^1].AgentEvent!.Request!;
                Equal( "question", pending.Kind );
                True( firstEvents.Any( message => message.AgentEvent is { Kind: "text", Text: "Hello " } ) );

                True( firstEvents
                    .Select( message => message.AgentEvent!.Sequence )
                    .SequenceEqual( firstEvents
                        .Select( message => message.AgentEvent!.Sequence )
                        .Order( ) ) );

                var options = backend.LastOptions!;

                True( options.Tools.Any( tool => tool.Name == "commanddeck_read_file" ) &&
                      !options.Tools.Any( tool => tool.Name == "commanddeck_run_command" ) );

                await using var second = await ConnectWithRetryAsync( socket );

                await second.SendAsync( new ProtocolRequest { RequestId = 10, Operation = ProtocolOperations.AgentConversationGet }, default );

                var reopened = await ReadUntilSnapshotAsync( second, 10 );
                var snapshot = reopened[0].AgentConversation!;
                Equal( "waiting", snapshot.Summary?.State );
                Equal( pending.Id, snapshot.Pending?.Id );
                Equal( "Hello ", snapshot.ActiveText );
                True( snapshot.ActiveTurnId > 0 );

                var home = await runtime.SearchAsync( new SearchRequest { Query = string.Empty, Limit = 20 }, default );

                True( home.Any( result => result.Id == "agent:conversation" && result.Title.Contains( "waiting", StringComparison.OrdinalIgnoreCase ) ) );

                var aiRows = await runtime.SearchAsync( new SearchRequest { Query = "ai: what is 5+2", Limit = 20 }, default );

                True( aiRows.Any( result => result.Id == "agent:codex" ), "the Ask Codex row survives source toggles that never list the assistant" );

                await second.SendAsync( new ProtocolRequest
                {
                    RequestId = 11,
                    Operation = ProtocolOperations.AgentTurnInput,
                    PendingRequestId = pending.Id,
                    Answers = new Dictionary<string, string>( StringComparer.Ordinal ) { ["q1"] = "B" }
                }, default );

                await questionAsked.Task.WaitAsync( TimeSpan.FromSeconds( 10 ) );
                var rest = await CollectUntilDoneAsync( second, 10 );
                True( rest.Any( message => message.AgentEvent is { Kind: "done", Done.Outcome: "completed" } ) );
                var final = runtime.GetAgentConversation( );
                Equal( 2, final.Entries.Count );
                Equal( "Hello you chose B", final.Entries[1].Text );
                True( final is { OutputGeneration: > 0, OutputActions.Count: 3 } );
                var savePath = Path.Combine( root, "answer.md" );

                var saved = await runtime.ExecuteAgentOutputAsync( final.OutputGeneration, "save", savePath, null, default );

                True( saved.Succeeded );
                Equal( "Hello you chose B", await File.ReadAllTextAsync( savePath ) );
            }
        }
        finally
        {
            serverCancellation.Cancel( );
            Directory.Delete( root, recursive: true );
        }
    }
}
