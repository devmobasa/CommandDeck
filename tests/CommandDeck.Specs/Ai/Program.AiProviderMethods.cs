using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AiProvidersDiscoverAndStreamThroughExplicitContractsAsync( )
    {
        RecordingAiHandler ollamaHandler = CreateProviderFixtureOllamaHandler( );
        AiProviderOptions ollamaOptions = CreateProviderFixtureOllamaOptions( );
        using var ollamaClient = CreateClient( ollamaHandler, ollamaOptions.BaseUrl );
        OllamaAiProvider ollama = new( ollamaOptions, ollamaClient );
        var localModels = await ollama.DiscoverModelsAsync( default );
        Equal( 1, localModels.Count );
        Equal( "qwen3:8b", localModels[0].Id );

        var local = await ReadProviderTextAsync( ollama, new AiPrompt { Text = "sentinel-local", SystemPrompt = "inert text only" } );

        Equal( "local", local );
        Equal( 2, ollamaHandler.Requests.Count );

        True( ollamaHandler
            .Requests[1]
            .Body.Contains( "\"stream\":true", StringComparison.Ordinal ) );

        True( ollamaHandler
            .Requests[1]
            .Body.Contains( "sentinel-local", StringComparison.Ordinal ) );

        TrackingCredentialResolver credentialResolver = new( "provider-secret" );
        RecordingAiHandler cloudHandler = CreateProviderFixtureCloudHandler( credentialResolver );
        AiProviderOptions cloudOptions = CreateProviderFixtureCloudOptions( );
        using var cloudClient = CreateClient( cloudHandler, cloudOptions.BaseUrl );
        OpenAiCompatibleAiProvider cloud = new( cloudOptions, cloudClient, credentialResolver );
        Equal( "environment-reference", cloud.Describe( ).CredentialState );
        var cloudModels = await cloud.DiscoverModelsAsync( default );
        Equal( "gpt-safe", cloudModels.Single( ).Id );

        var remote = await ReadProviderTextAsync( cloud, new AiPrompt { Text = "sentinel-cloud", SystemPrompt = "inert text only" } );

        Equal( "cloud", remote );
        Equal( "Bearer provider-secret", cloudHandler.Requests[0].Authorization );
        Equal( "Bearer provider-secret", cloudHandler.Requests[1].Authorization );

        True( cloudHandler
            .Requests[1]
            .Body.Contains( "\"tool_choice\":\"none\"", StringComparison.Ordinal ) );

        True( !cloudHandler
            .Requests[1]
            .Body.Contains( "provider-secret", StringComparison.Ordinal ) );
    }

    private static async Task AiProviderErrorsAreBoundedAndNormalizedAsync( )
    {
        Equal( "unavailable", AiErrorClassifier.Classify( new InvalidOperationException( "busy credential digest stale redirect byte limit too large" ) ) );

        AiProviderOptions options = CreateOptions( );
        RecordingAiHandler redirect = CreateRedirect( );
        await using ( AiProviderRegistry registry = new( new AiOptions { Enabled = true, Providers = [options] }, new FixedTransportFactory( redirect ) ) )
        {
            var inventory = await registry.DiscoverModelsAsync( "cloud", default );
            Equal( "redirect-rejected", inventory.Error!.Category );
            Equal( 1, redirect.Requests.Count );
        }

        RecordingAiHandler rateLimited = CreateRateLimited( );
        await using ( AiProviderRegistry registry = new( new AiOptions { Enabled = true, Providers = [options] }, new FixedTransportFactory( rateLimited ) ) )
        {
            var inventory = await registry.DiscoverModelsAsync( "cloud", default );
            Equal( "rate-limited", inventory.Error!.Category );
            Equal( 7, inventory.Error.RetryAfterSeconds );
        }

        var oversized = "{\"data\":[{\"id\":\"" + new string( 'x', AiLimits.MaximumDiscoveryBytes ) + "\"}]}";

        RecordingAiHandler tooLarge = new( _ => Json( HttpStatusCode.OK, oversized ) );

        await using AiProviderRegistry oversizedRegistry = new( new AiOptions { Enabled = true, Providers = [options] },
            new FixedTransportFactory( tooLarge ) );

        var oversizedInventory = await oversizedRegistry.DiscoverModelsAsync( "cloud", default );
        Equal( "response-too-large", oversizedInventory.Error!.Category );

        await AssertDiscoveryCategoryAsync( options, _ => Json( HttpStatusCode.Unauthorized, "secret body" ), "authentication" );
        await AssertDiscoveryCategoryAsync( options, _ => Json( HttpStatusCode.Forbidden, "secret body" ), "permission" );
        await AssertDiscoveryCategoryAsync( options, _ => Json( HttpStatusCode.OK, "{malformed" ), "malformed-response" );
        await AssertDiscoveryCategoryAsync( options, _ => throw new HttpRequestException( "offline" ), "network" );
        await AssertDiscoveryCategoryAsync( options, _ => throw new TaskCanceledException( "provider timeout" ), "timeout" );

        RecordingAiHandler noCredentialConnection = new( _ => throw new InvalidOperationException( "transport must not be reached" ) );

        await using AiProviderRegistry missingCredentialRegistry = new( new AiOptions { Enabled = true, Providers = [withCredential( )] },
            new FixedTransportFactory( noCredentialConnection ), new MissingCredentialResolver( ) );

        var missingCredential = await missingCredentialRegistry.DiscoverModelsAsync( "cloud", default );
        Equal( "credential-missing", missingCredential.Error!.Category );
        Equal( 0, noCredentialConnection.Requests.Count );

        AiProviderOptions withCredential( ) => new( )
        {
            Id = options.Id,
            DisplayName = options.DisplayName,
            Kind = options.Kind,
            BaseUrl = options.BaseUrl,
            Model = options.Model,
            AllowRemoteEndpoint = options.AllowRemoteEndpoint,
            Credential = new AiCredentialOptions { Kind = AiCredentialKinds.Environment, Name = "MISSING_TEST_KEY" }
        };

        static AiProviderOptions CreateOptions( )
        {
            return new( )
            {
                Id = "cloud",
                DisplayName = "Cloud",
                Kind = AiAdapterKinds.OpenAiChatCompletions,
                BaseUrl = "https://api.example.test/v1",
                Model = "model",
                AllowRemoteEndpoint = true
            };
        }

        static RecordingAiHandler CreateRedirect( )
        {
            return new( _ =>
            {
                HttpResponseMessage response = new( HttpStatusCode.TemporaryRedirect );
                response.Headers.Location = new Uri( "https://redirect.example.test/models" );

                return response;
            } );
        }

        static RecordingAiHandler CreateRateLimited( )
        {
            return new( _ =>
            {
                HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue( TimeSpan.FromSeconds( 7 ) );

                return response;
            } );
        }
    }

    private static async Task AssertDiscoveryCategoryAsync( AiProviderOptions options, Func<HttpRequestMessage, HttpResponseMessage> response,
        string expectedCategory )
    {
        RecordingAiHandler handler = new( response );
        await using AiProviderRegistry registry = new( new AiOptions { Enabled = true, Providers = [options] }, new FixedTransportFactory( handler ) );

        var inventory = await registry.DiscoverModelsAsync( "cloud", default );
        Equal( expectedCategory, inventory.Error!.Category );
    }

    private static async Task AiGenerationBindsPreflightStreamAndOutputToClientAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-generation-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            RecordingAiHandler handler = CreateHandler( );
            AiOptions options = OneLocalAiProvider( );

            await using var catalog = new SqliteCatalog( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( default );
            await using AiProviderRegistry providers = new( options, new FixedTransportFactory( handler ) );

            AiPreflightService preflights = new( providers,
                new FilePreviewService( new IndexingOptions { Roots = [] }, new PrivacyOptions { NeverIndex = [] } ), catalog.AiPreferences, ( _, _ ) => null );

            using AiGenerationCoordinator generations = new( preflights );
            const string owner = "test-generation-client";

            var preflight = await preflights.CreateAsync( owner, new AiDraft
            {
                ProviderId = "ollama",
                ModelId = "qwen",
                UserText = "Generate inert text",
                Attachments = []
            }, default );

            List<AiChunk> chunks = [];
            List<AiDone> terminals = [];

            await generations.RunAsync( owner, preflight.Id, preflight.PayloadDigest, preflight.Generation, chunk =>
            {
                chunks.Add( chunk );

                return ValueTask.CompletedTask;
            }, done =>
            {
                terminals.Add( done );

                return ValueTask.CompletedTask;
            }, default );

            Equal( 2, chunks.Count );
            Equal( "one two", string.Concat( chunks.Select( static chunk => chunk.Text ) ) );
            Equal( 1, terminals.Count );
            Equal( "completed", terminals[0].Outcome );
            Equal( 4L, terminals[0].Usage!.TotalTokens );

            Equal( Convert
                .ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( handler.Requests.Single( ).Body ) ) )
                .ToLowerInvariant( ), preflight.PayloadDigest );

            True( generations.TryGetOutput( owner, preflight.Generation, out var output ) );
            Equal( "one two", output );
            True( !generations.TryGetOutput( "other-client", preflight.Generation, out _ ) );
            await AssertAiOutputIdentityAsync( generations, owner, preflight.Generation, root );
            await AssertTerminalSinkAttemptedOnceAsync( generations, preflights, owner );
            generations.ClearClient( owner );
            True( !generations.TryGetOutput( owner, preflight.Generation, out _ ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static RecordingAiHandler CreateHandler( )
        {
            return new( _ => Json( HttpStatusCode.OK,
                "{\"model\":\"qwen\",\"message\":{\"content\":\"one \"},\"done\":false}\n" +
                "{\"model\":\"qwen\",\"message\":{\"content\":\"two\"},\"done\":false}\n" +
                "{\"model\":\"qwen\",\"message\":{\"content\":\"\"},\"done\":true," +
                "\"done_reason\":\"stop\",\"prompt_eval_count\":2,\"eval_count\":2}\n" ) );
        }
    }

    private static async Task AiGenerationCancellationHasOneTerminalAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-cancel-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            var firstLine = Encoding.UTF8.GetBytes( "{\"model\":\"qwen\",\"message\":{\"content\":\"first\"},\"done\":false}\n" );

            RecordingAiHandler handler = new( _ => new HttpResponseMessage( HttpStatusCode.OK )
            {
                Content = new StreamContent( new CancellableAiStream( firstLine ) )
            } );

            AiOptions options = OneLocalAiProvider( );

            await using var catalog = new SqliteCatalog( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( default );
            await using AiProviderRegistry providers = new( options, new FixedTransportFactory( handler ) );

            AiPreflightService preflights = new( providers,
                new FilePreviewService( new IndexingOptions { Roots = [] }, new PrivacyOptions { NeverIndex = [] } ), catalog.AiPreferences, ( _, _ ) => null );

            using AiGenerationCoordinator generations = new( preflights );
            const string owner = "test-cancel-client";

            var preflight = await preflights.CreateAsync( owner, new AiDraft
            {
                ProviderId = "ollama",
                ModelId = "qwen",
                UserText = "Cancel after one chunk",
                Attachments = []
            }, default );

            using CancellationTokenSource cancellation = new( TimeSpan.FromSeconds( 2 ) );
            List<AiDone> terminals = [];
            var elapsed = System.Diagnostics.Stopwatch.StartNew( );

            await generations.RunAsync( owner, preflight.Id, preflight.PayloadDigest, preflight.Generation, _ =>
            {
                generations.ClearClient( owner );

                return ValueTask.CompletedTask;
            }, done =>
            {
                terminals.Add( done );

                return ValueTask.CompletedTask;
            }, cancellation.Token );

            elapsed.Stop( );
            Equal( 1, terminals.Count );
            Equal( "cancelled", terminals[0].Outcome );
            Equal( "cancelled", terminals[0].Error!.Category );
            True( elapsed.Elapsed < TimeSpan.FromMilliseconds( 500 ) );
            True( !generations.TryGetOutput( owner, preflight.Generation, out _ ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
