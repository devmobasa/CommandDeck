using System.Net.Sockets;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task SocketAiClearIsACompletionBarrierAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-clear-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );
        await using var provider = await ScriptedOllamaServer.StartAsync( );
        var socket = SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" );
        var config = Path.Combine( root, "config.json" );
        CommandDeckOptions options = CreateScriptedAiSocketOptions( root, provider );

        await File.WriteAllTextAsync( config, System.Text.Json.JsonSerializer.Serialize( options, CommandDeckJsonContext.Default.CommandDeckOptions ) );

        using CancellationTokenSource serverCancellation = new( );

        try
        {
            await using var runtime = await CommandDeckRuntime.CreateAsync( config, CancellationToken.None );
            UnixProtocolServer server = new( runtime, socket );
            var serverTask = server.RunAsync( serverCancellation.Token );
            await using var client = await ConnectWithRetryAsync( socket );
            var first = await RequestPreflightAsync( client, 1, "generation to clear" );
            var queued = await RequestPreflightAsync( client, 2, "stale queued preflight" );
            await client.SendAsync( CreateAiStartRequest( first ), CancellationToken.None );
            await provider.GenerationStarted.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );
            await SendQueuedPreflightsAsync( client );

            await client.SendAsync( new ProtocolRequest { RequestId = 7, Operation = ProtocolOperations.AiClear }, CancellationToken.None );

            (var generationDone, var clearDone, var queuedRejected, var ordered, var terminal) = await ReadClearCompletionAsync( client );

            AssertClearCompletion( generationDone, clearDone, queuedRejected, ordered );
            Equal( "cancelled", terminal?.Outcome );
            await client.SendAsync( CreateQueuedStartRequest( queued ), CancellationToken.None );
            var stale = await ReadMessageAsync( client, 8, "ai-done", CancellationToken.None );
            AssertStaleTerminal( stale );
            await client.SendAsync( CreateAiOutputRequest( first ), CancellationToken.None );
            var output = await ReadMessageAsync( client, 9, "action-result", CancellationToken.None );
            AssertFailedAction( output, "Cleared generation unexpectedly retained executable output." );
            await ProveCompletedOutputAndChallengeArePurgedAsync( client, root );
            serverCancellation.Cancel( );
            await serverTask;
        }
        finally
        {
            serverCancellation.Cancel( );
            Directory.Delete( root, recursive: true );
        }

        static ProtocolRequest CreateAiStartRequest( AiPreflight first )
        {
            return new ProtocolRequest
            {
                RequestId = 3,
                Operation = ProtocolOperations.AiStart,
                PreflightId = first.Id,
                PayloadDigest = first.PayloadDigest,
                Generation = first.Generation,
                SessionId = "socket-clear-session-000000000000000"
            };
        }

        static ProtocolRequest CreateQueuedStartRequest( AiPreflight queued )
        {
            return new ProtocolRequest
            {
                RequestId = 8,
                Operation = ProtocolOperations.AiStart,
                PreflightId = queued.Id,
                PayloadDigest = queued.PayloadDigest,
                Generation = queued.Generation,
                SessionId = "socket-stale-session-00000000000000"
            };
        }

        static ProtocolRequest CreateAiOutputRequest( AiPreflight first )
        {
            return new ProtocolRequest
            {
                RequestId = 9,
                Operation = ProtocolOperations.AiOutput,
                Generation = first.Generation,
                OutputAction = "copy"
            };
        }
    }

    private static async ValueTask SendQueuedPreflightsAsync( UnixProtocolClient client )
    {
        for ( long requestId = 4; requestId <= 6; requestId++ )
        {
            await client.SendAsync( CreateAiPreflightRequest( requestId ), CancellationToken.None );
        }

        static ProtocolRequest CreateAiPreflightRequest( long requestId )
        {
            return new ProtocolRequest
            {
                RequestId = requestId,
                Operation = ProtocolOperations.AiPreflight,
                AiDraft = new AiDraft
                {
                    ProviderId = "clear-test",
                    ModelId = "clear-model",
                    UserText = $"queued operation {requestId}",
                    Attachments = []
                }
            };
        }
    }

    private static async ValueTask ProveCompletedOutputAndChallengeArePurgedAsync( UnixProtocolClient client, string root )
    {
        var completed = await RequestPreflightAsync( client, 10, "completed output" );
        await client.SendAsync( CreateAiStartRequest( completed ), CancellationToken.None );
        var completedTerminal = await ReadMessageAsync( client, 11, "ai-done", CancellationToken.None );
        Equal( "completed", completedTerminal?.AiDone?.Outcome );
        var existingOutput = Path.Combine( root, "existing-output.txt" );
        await File.WriteAllTextAsync( existingOutput, "original" );

        await client.SendAsync( CreateAiOutputRequest( completed, existingOutput ), CancellationToken.None );

        var challenge = await ReadMessageAsync( client, 12, "action-result", CancellationToken.None );
        var confirmationToken = RequireOverwriteChallenge( challenge );

        await client.SendAsync( new ProtocolRequest { RequestId = 13, Operation = ProtocolOperations.AiClear }, CancellationToken.None );

        _ = await ReadMessageAsync( client, 13, "done", CancellationToken.None );

        await client.SendAsync( CreateConfirmedOutputRequest( completed, existingOutput, confirmationToken ), CancellationToken.None );

        var purged = await ReadMessageAsync( client, 14, "action-result", CancellationToken.None );
        AssertFailedAction( purged, "ai.clear retained completed output or its overwrite authority." );
        Equal( "original", await File.ReadAllTextAsync( existingOutput ) );

        static ProtocolRequest CreateAiStartRequest( AiPreflight completed )
        {
            return new ProtocolRequest
            {
                RequestId = 11,
                Operation = ProtocolOperations.AiStart,
                PreflightId = completed.Id,
                PayloadDigest = completed.PayloadDigest,
                Generation = completed.Generation,
                SessionId = "socket-complete-session-000000000000"
            };
        }

        static ProtocolRequest CreateAiOutputRequest( AiPreflight completed, string existingOutput )
        {
            return new ProtocolRequest
            {
                RequestId = 12,
                Operation = ProtocolOperations.AiOutput,
                Generation = completed.Generation,
                OutputAction = "save",
                Path = existingOutput
            };
        }

        static ProtocolRequest CreateConfirmedOutputRequest( AiPreflight completed, string existingOutput, string confirmationToken )
        {
            return new ProtocolRequest
            {
                RequestId = 14,
                Operation = ProtocolOperations.AiOutput,
                Generation = completed.Generation,
                OutputAction = "save",
                Path = existingOutput,
                ConfirmationToken = confirmationToken
            };
        }
    }

    private static void AssertClearCompletion( bool generationDone, bool clearDone, bool queuedRejected, bool ordered )
    {
        if ( !generationDone || !clearDone || !queuedRejected || !ordered )
        {
            throw CreateException( generationDone, clearDone, queuedRejected, ordered );
        }

        static InvalidOperationException CreateException( bool generationDone, bool clearDone, bool queuedRejected, bool ordered )
        {
            return new InvalidOperationException( $"Completion state was generation={generationDone}, clear={clearDone}, " +
                                                  $"queuedRejected={queuedRejected}, ordered={ordered}." );
        }
    }

    private static void AssertStaleTerminal( ProtocolMessage? stale )
    {
        if ( stale?.AiDone?.Outcome != "failed" || stale.AiDone.Error?.Category != "stale-preflight" )
        {
            throw new InvalidOperationException( "Cleared preflight was not rejected as a stale AI terminal." );
        }
    }

    private static string RequireOverwriteChallenge( ProtocolMessage? challenge )
    {
        if ( challenge?.ActionResult?.Succeeded != false || string.IsNullOrWhiteSpace( challenge.ActionResult.ConfirmationToken ) )
        {
            throw new InvalidOperationException( "Socket save did not issue an overwrite confirmation challenge." );
        }

        return challenge!.ActionResult!.ConfirmationToken!;
    }

    private static void AssertFailedAction( ProtocolMessage? message, string failure )
    {
        if ( message?.ActionResult?.Succeeded != false )
        {
            throw new InvalidOperationException( failure );
        }
    }

    private static async ValueTask<(bool GenerationDone, bool ClearDone, bool QueuedRejected, bool Ordered, AiDone? Terminal)> ReadClearCompletionAsync(
        UnixProtocolClient client )
    {
        var generationDone = false;
        var clearDone = false;
        HashSet<long> queuedErrors = [];
        HashSet<long> queuedDone = [];
        var messageIndex = 0;
        var generationDoneIndex = -1;
        var clearDoneIndex = -1;
        AiDone? terminal = null;
        using CancellationTokenSource readTimeout = new( TimeSpan.FromSeconds( 5 ) );
        await foreach ( var line in client.ReadRawMessagesAsync( readTimeout.Token ) )
        {
            var message = DeserializeMessage( line );
            messageIndex++;

            switch (message?.RequestId, message?.Type)
            {
                case (3, "ai-done" ):
                    terminal = message.AiDone;

                    break;
                case (3, "done" ):
                    generationDone = true;
                    generationDoneIndex = messageIndex;

                    break;
                case ( >= 4 and <= 6, "error" ):
                    queuedErrors.Add( message.RequestId );

                    break;
                case ( >= 4 and <= 6, "done" ):
                    queuedDone.Add( message.RequestId );

                    break;
                case (7, "error" ):
                    throw new InvalidOperationException( $"ai.clear failed: {message.Error}" );
                case (7, "done" ):
                    clearDone = true;
                    clearDoneIndex = messageIndex;

                    break;
            }

            if ( generationDone && clearDone && queuedDone.Count == 3 )
            {
                break;
            }
        }

        return (generationDone, clearDone, queuedErrors.SetEquals( [4L, 5L, 6L] ) && queuedDone.SetEquals( [4L, 5L, 6L] ),
            generationDoneIndex > 0 && clearDoneIndex > generationDoneIndex, terminal);
    }

    private static async ValueTask<AiPreflight> RequestPreflightAsync( UnixProtocolClient client, long requestId, string prompt )
    {
        await client.SendAsync( CreateAiPreflightRequest( requestId, prompt ), CancellationToken.None );

        var message = await ReadMessageAsync( client, requestId, "ai-preflight-result", CancellationToken.None );

        var result = message?.AiPreflight;
        if ( result is null )
        {
            throw new InvalidOperationException( "Socket preflight did not return authority." );
        }

        return result;

        static ProtocolRequest CreateAiPreflightRequest( long requestId, string prompt )
        {
            return new ProtocolRequest
            {
                RequestId = requestId,
                Operation = ProtocolOperations.AiPreflight,
                AiDraft = new AiDraft
                {
                    ProviderId = "clear-test",
                    ModelId = "clear-model",
                    UserText = prompt,
                    Attachments = []
                }
            };
        }
    }
}
