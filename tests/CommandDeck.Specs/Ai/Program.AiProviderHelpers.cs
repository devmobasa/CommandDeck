using System.Net;
using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private sealed class TrackingCredentialResolver( string value ) : IAiCredentialResolver
    {
        public AiCredentialLease? LastLease { get; private set; }

        public ValueTask<AiCredentialLease?> ResolveAsync( AiCredentialOptions reference, CancellationToken cancellationToken )
        {
            LastLease = new AiCredentialLease( Encoding.UTF8.GetBytes( value ) );

            return ValueTask.FromResult<AiCredentialLease?>( LastLease );
        }
    }

    private static AiOptions OneLocalAiProvider( ) => new( )
    {
        Enabled = true,
        Providers =
        [
            new AiProviderOptions
            {
                Id = "ollama",
                DisplayName = "Ollama",
                Kind = AiAdapterKinds.Ollama,
                BaseUrl = "http://127.0.0.1:11434",
                Model = "qwen"
            }
        ]
    };

    private static AiDraft Draft( string text ) => new( )
    {
        ProviderId = "ollama",
        ModelId = "qwen",
        UserText = text,
        Attachments = []
    };

    private static RecordingAiHandler CreateProviderFixtureOllamaHandler( )
    {
        return new( request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json( HttpStatusCode.OK, "{\"models\":[{\"model\":\"qwen3:8b\"},{\"name\":\"qwen3:8b\"}]}" ),
            "/api/chat" => Json( HttpStatusCode.OK,
                "{\"model\":\"qwen3:8b\",\"message\":{\"content\":\"local\"},\"done\":false}\n" +
                "{\"model\":\"qwen3:8b\",\"message\":{\"content\":\"\"},\"done\":true," +
                "\"done_reason\":\"stop\",\"prompt_eval_count\":4,\"eval_count\":1}\n" ),
            _ => Json( HttpStatusCode.NotFound, "{}" )
        } );
    }

    private static AiProviderOptions CreateProviderFixtureOllamaOptions( )
    {
        return new( )
        {
            Id = "ollama",
            DisplayName = "Ollama",
            Kind = AiAdapterKinds.Ollama,
            BaseUrl = "http://127.0.0.1:11434",
            Model = "qwen3:8b"
        };
    }

    private static AiProviderOptions CreateProviderFixtureCloudOptions( )
    {
        return new( )
        {
            Id = "cloud",
            DisplayName = "Cloud",
            Kind = AiAdapterKinds.OpenAiChatCompletions,
            BaseUrl = "https://api.example.test/v1",
            Model = "gpt-safe",
            AllowRemoteEndpoint = true,
            Credential = new AiCredentialOptions { Kind = AiCredentialKinds.Environment, Name = "TEST_ONLY" }
        };
    }

    private static RecordingAiHandler CreateProviderFixtureCloudHandler( TrackingCredentialResolver credentialResolver )
    {
        return new( request =>
        {
            if ( credentialResolver.LastLease?.IsDisposed != true )
            {
                throw new InvalidOperationException( "Credential bytes remained leased through transport execution." );
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => Json( HttpStatusCode.OK, "{\"data\":[{\"id\":\"gpt-safe\",\"owned_by\":\"test\"}]}" ),
                "/v1/chat/completions" => EventStream( "data: {\"choices\":[{\"delta\":{\"content\":\"cloud\"},\"finish_reason\":null}]}\n\n" +
                                                       "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}\n\n" +
                                                       "data: [DONE]\n\n" ),
                _ => Json( HttpStatusCode.NotFound, "{}" )
            };
        } );
    }

    private static SearchResult CreateAttachmentSearchResult( string file )
    {
        return new( )
        {
            Id = "file:attachment",
            Kind = ResultKinds.File,
            Title = "attachment.txt",
            SourceId = SourceIds.Files,
            Metadata = new Dictionary<string, string> { ["path"] = file }
        };
    }

    private static CommandDeckOptions CreateScriptedAiSocketOptions( string root, ScriptedOllamaServer provider )
    {
        return new( )
        {
            Storage = new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) },
            Indexing = new IndexingOptions { Roots = [], IndexDesktopApplications = false, IndexOmarchyCommands = false },
            Privacy = new PrivacyOptions { StoreUsageHistory = false, NeverIndex = [] },
            Ai = new AiOptions
            {
                Enabled = true,
                Providers =
                [
                    new AiProviderOptions
                    {
                        Id = "clear-test",
                        DisplayName = "Clear test",
                        Kind = AiAdapterKinds.Ollama,
                        BaseUrl = provider.BaseUrl + "ollama",
                        Model = "clear-model"
                    }
                ]
            }
        };
    }
}
