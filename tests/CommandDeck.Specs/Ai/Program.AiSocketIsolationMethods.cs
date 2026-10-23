namespace CommandDeck.Specs;

public static partial class Program
{
    private static readonly TimeSpan PromptAiReplyDeadline = TimeSpan.FromSeconds( 5 );

    private static async Task SocketAiReadsAnswerWhileAnAnswerStreamsAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-reads-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );
        await using var provider = await ScriptedOllamaServer.StartAsync( );
        var socket = SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" );
        var config = await WriteScriptedAiConfigAsync( root, provider );
        using CancellationTokenSource serverCancellation = new( );

        try
        {
            await using var runtime = await CommandDeckRuntime.CreateAsync( config, CancellationToken.None );
            UnixProtocolServer server = new( runtime, socket );
            var serverTask = server.RunAsync( serverCancellation.Token );
            await using var client = await ConnectWithRetryAsync( socket );
            var streaming = await RequestPreflightAsync( client, 1, "answer that keeps streaming" );
            await client.SendAsync( CreateAiStartRequest( streaming ), CancellationToken.None );
            await provider.GenerationStarted.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

            // Settings sends these reads over the same connection while an answer streams.
            await RequirePromptAiReplyAsync( client, 3, ProtocolOperations.AiProvidersGet, "ai-providers-result" );
            await RequirePromptAiReplyAsync( client, 4, ProtocolOperations.AiSelectionGet, "ai-selection-result" );
            await RequirePromptAiReplyAsync( client, 5, ProtocolOperations.AiTemplatesGet, "ai-templates-result" );
            await RequirePromptAiReplyAsync( client, 6, ProtocolOperations.AiSettingsGet, "ai-settings-result" );

            await client.SendAsync( new ProtocolRequest { RequestId = 7, Operation = ProtocolOperations.AiSelectionGet }, CancellationToken.None );
            await client.SendAsync( new ProtocolRequest { RequestId = 8, Operation = ProtocolOperations.AiClear }, CancellationToken.None );

            var (terminal, readResult, readError) = await ReadClearWithConcurrentReadAsync( client );
            Equal( "cancelled", terminal?.Outcome );
            True( readError is null, $"a read sent before ai.clear failed: {readError}" );
            True( readResult is not null, "a read sent before ai.clear returned no selection" );

            await RequirePromptAiReplyAsync( client, 9, ProtocolOperations.AiProvidersGet, "ai-providers-result" );

            serverCancellation.Cancel( );
            await serverTask;
        }
        finally
        {
            serverCancellation.Cancel( );
            Directory.Delete( root, recursive: true );
        }

        static ProtocolRequest CreateAiStartRequest( AiPreflight streaming )
        {
            return new ProtocolRequest
            {
                RequestId = 2,
                Operation = ProtocolOperations.AiStart,
                PreflightId = streaming.Id,
                PayloadDigest = streaming.PayloadDigest,
                Generation = streaming.Generation,
                SessionId = "socket-read-session-0000000000000000"
            };
        }
    }

    private static async Task SocketAiClearKeepsFileSelectionAndTrustChallengesAsync( )
    {
        ScriptedAgentBackend backend = new( );
        using CancellationTokenSource serverCancellation = new( );

        (var runtime, var _, var root, var _) = await StartAgentRuntimeAsync( backend, serverCancellation.Token );

        var folder = Directory.CreateDirectory( Path.Combine( root, "selection" ) ).FullName;
        await File.WriteAllTextAsync( Path.Combine( folder, "first.txt" ), "first" );
        await File.WriteAllTextAsync( Path.Combine( folder, "second.txt" ), "second" );

        try
        {
            await using ( runtime )
            {
                await using var client = await ConnectWithRetryAsync( SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" ) );

                var files = await BrowseFileResultsAsync( client, 1, folder );
                var first = files["first.txt"];
                var second = files["second.txt"];
                Equal( 1, (await SelectFileOverSocketAsync( client, 2, first.Id )).Count );
                await client.SendAsync( CreateAgentPreferencesUpdateRequest( ), CancellationToken.None );
                _ = await CollectUntilDoneAsync( client, 3 );

                await client.SendAsync(
                    new ProtocolRequest { RequestId = 4, Operation = ProtocolOperations.AgentTrustChallenge, TrustLevel = AgentTrustLevels.ReadEntireHome },
                    CancellationToken.None );

                var challenge = (await CollectUntilDoneAsync( client, 4 ))[0].AgentTrustChallenge!;

                await client.SendAsync( new ProtocolRequest { RequestId = 5, Operation = ProtocolOperations.AiClear }, CancellationToken.None );

                _ = await ReadMessageAsync( client, 5, "done", CancellationToken.None );
                var kept = await SelectFileOverSocketAsync( client, 6, second.Id );
                True( kept.Count == 2, $"ai.clear left {kept.Count} of 2 selected files" );
                await client.SendAsync( CreateAgentTrustUpdateRequest( challenge ), CancellationToken.None );
                var applied = await CollectUntilDoneAsync( client, 7 );

                True( applied[0].AgentTrust?.Level == 3, $"ai.clear revoked the trust challenge: {applied[0].Error}" );

                await ProveDisconnectReleasesSelectionAndChallengesAsync( runtime, folder );
            }
        }
        finally
        {
            serverCancellation.Cancel( );
            Directory.Delete( root, recursive: true );
        }

        static ProtocolRequest CreateAgentPreferencesUpdateRequest( )
        {
            return new ProtocolRequest
            {
                RequestId = 3,
                Operation = ProtocolOperations.AgentPreferencesUpdate,
                AgentPreferences = new AgentPreferences { Enabled = true }
            };
        }

        static ProtocolRequest CreateAgentTrustUpdateRequest( AgentTrustChallenge challenge )
        {
            return new ProtocolRequest
            {
                RequestId = 7,
                Operation = ProtocolOperations.AgentTrustUpdate,
                ChallengeId = challenge.Id,
                TrustLevel = AgentTrustLevels.ReadEntireHome
            };
        }
    }

    private static async ValueTask ProveDisconnectReleasesSelectionAndChallengesAsync( CommandDeckRuntime runtime, string folder )
    {
        const string owner = "socket-spec-disconnected-client";

        var rows = await runtime.SearchAsync( new SearchRequest { Query = "browse:", BrowseDirectory = folder, SearchOwner = owner }, default );

        var firstId = rows.Single( row => row.Title == "first.txt" ).Id;
        Equal( 1, runtime.SelectFile( owner, firstId, true ).Count );

        var orphan = await runtime.CreateAgentTrustChallengeAsync( owner, AgentTrustLevels.Launcher, CancellationToken.None );

        (( IClientLifecycle ) runtime).ReleaseClient( owner );

        rows = await runtime.SearchAsync( new SearchRequest { Query = "browse:", BrowseDirectory = folder, SearchOwner = owner }, CancellationToken.None );

        var secondId = rows.Single( row => row.Title == "second.txt" ).Id;
        Equal( 1, runtime.SelectFile( owner, secondId, true ).Count );

        await ThrowsAsync<InvalidOperationException>( async ( ) =>
            await runtime.ApplyAgentTrustAsync( owner, orphan.Id, AgentTrustLevels.Launcher, null, null, CancellationToken.None ) );
    }

    private static async ValueTask<Dictionary<string, SearchResult>> BrowseFileResultsAsync( UnixProtocolClient client, long requestId, string folder )
    {
        await client.SendAsync( CreateSearchRequest( requestId, folder ), CancellationToken.None );
        var messages = await CollectUntilDoneAsync( client, requestId );

        return messages
            .Where( static message => message.Result is not null )
            .Select( static message => message.Result! )
            .Where( static result => result.Title is "first.txt" or "second.txt" )
            .ToDictionary( static result => result.Title, StringComparer.Ordinal );

        static ProtocolRequest CreateSearchRequest( long requestId, string folder )
        {
            return new ProtocolRequest
            {
                RequestId = requestId,
                Operation = ProtocolOperations.Search,
                Query = "browse:",
                BrowseDirectory = folder
            };
        }
    }

    private static async ValueTask<List<FileSelectionItem>> SelectFileOverSocketAsync( UnixProtocolClient client, long requestId, string resultId )
    {
        await client.SendAsync( CreateFilesSelectRequest( requestId, resultId ), CancellationToken.None );
        var message = await ReadMessageAsync( client, requestId, "file-selection", CancellationToken.None );
        var selection = message?.FileSelection;
        if ( selection is null )
        {
            throw new InvalidOperationException( "files.select returned no selection." );
        }

        return selection;

        static ProtocolRequest CreateFilesSelectRequest( long requestId, string resultId )
        {
            return new ProtocolRequest
            {
                RequestId = requestId,
                Operation = ProtocolOperations.FilesSelect,
                ResultId = resultId,
                Enabled = true
            };
        }
    }

    private static async ValueTask<string> WriteScriptedAiConfigAsync( string root, ScriptedOllamaServer provider )
    {
        var config = Path.Combine( root, "config.json" );
        CommandDeckOptions options = CreateScriptedAiSocketOptions( root, provider );

        await File.WriteAllTextAsync( config, System.Text.Json.JsonSerializer.Serialize( options, CommandDeckJsonContext.Default.CommandDeckOptions ) );

        return config;
    }

    private static async ValueTask RequirePromptAiReplyAsync( UnixProtocolClient client, long requestId, string operation, string expectedType )
    {
        await client.SendAsync( new ProtocolRequest { RequestId = requestId, Operation = operation }, CancellationToken.None );

        using CancellationTokenSource deadline = new( PromptAiReplyDeadline );
        ProtocolMessage? reply;

        try
        {
            reply = await ReadMessageAsync( client, requestId, expectedType, deadline.Token );
        }
        catch ( OperationCanceledException ) when ( deadline.IsCancellationRequested )
        {
            throw new InvalidOperationException( $"{operation} got no reply within {PromptAiReplyDeadline.TotalSeconds:0} s while an answer streamed." );
        }

        if ( reply is null )
        {
            throw new InvalidOperationException( $"{operation} completed without {expectedType}." );
        }
    }

    private static async ValueTask<(AiDone? Terminal, AiSelection? ReadResult, string? ReadError)> ReadClearWithConcurrentReadAsync( UnixProtocolClient client )
    {
        AiDone? terminal = null;
        AiSelection? readResult = null;
        string? readError = null;
        var streamDone = false;
        var readDone = false;
        var clearDone = false;
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 5 ) );
        await foreach ( var line in client.ReadRawMessagesAsync( timeout.Token ) )
        {
            var message = DeserializeMessage( line );
            switch (message?.RequestId, message?.Type)
            {
                case (2, "ai-done" ):
                    terminal = message.AiDone;

                    break;
                case (2, "done" ):
                    streamDone = true;

                    break;
                case (7, "ai-selection-result" ):
                    readResult = message.AiSelection;

                    break;
                case (7, "error" ):
                    readError = message.Error;

                    break;
                case (7, "done" ):
                    readDone = true;

                    break;
                case (8, "error" ):
                    throw new InvalidOperationException( $"ai.clear failed: {message.Error}" );
                case (8, "done" ):
                    clearDone = true;

                    break;
            }

            if ( streamDone && readDone && clearDone )
            {
                break;
            }
        }

        return (terminal, readResult, readError);
    }
}
