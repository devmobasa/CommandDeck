using System.Net.Http.Headers;
using System.Text.Json;

namespace CommandDeck;

public sealed class OllamaAiProvider : IAiProvider
{
    private const string TagsRoute = "api/tags";
    private const string ChatRoute = "api/chat";
    private const string AdapterContractVersion = "ollama-v1";

    private readonly AiProviderOptions _options;
    private readonly HttpClient _client;

    public OllamaAiProvider( AiProviderOptions options, HttpClient client )
    {
        _options = options;
        _client = client;
    }

    public string Id => _options.Id;

    public AiProviderDescriptor Describe( ) => ProviderDescriptor.Create( _options,
        new AiProviderCapabilities
        {
            Discovery = true,
            Streaming = true,
            Usage = true,
            AdapterContractVersion = AdapterContractVersion
        } );

    public async ValueTask<IReadOnlyList<AiModelDescriptor>> DiscoverModelsAsync( CancellationToken cancellationToken )
    {
        using var response = await _client
            .GetAsync( TagsRoute, HttpCompletionOption.ResponseHeadersRead, cancellationToken )
            .ConfigureAwait( false );

        AiHttpContent.EnsureSuccess( response );

        var body = await AiHttpContent
            .ReadBoundedJsonAsync( response.Content, CommandDeckJsonContext.Default.OllamaTagsResponse, AiLimits.MaximumDiscoveryBytes, cancellationToken )
            .ConfigureAwait( false );

        return NormalizeModels( body?.Models.Select( static model => model.Model ?? model.Name ) );
    }

    public AiPreparedProviderRequest PrepareRequest( AiPrompt prompt )
    {
        OllamaChatRequest request = CreateChatRequest( prompt );

        return PrepareChatRequest( request );
    }

    public async IAsyncEnumerable<AiProviderStreamEvent> StreamAsync( AiPreparedProviderRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken )
    {
        AiHttpContent.EnsurePreparedRequest( request, Id );
        using ByteArrayContent content = new( request.Body );
        content.Headers.ContentType = new MediaTypeHeaderValue( System.Net.Mime.MediaTypeNames.Application.Json ) { CharSet = "utf-8" };
        using HttpRequestMessage message = new( HttpMethod.Post, ChatRoute ) { Content = content };

        using var response = await _client
            .SendAsync( message, HttpCompletionOption.ResponseHeadersRead, cancellationToken )
            .ConfigureAwait( false );

        AiHttpContent.EnsureSuccess( response );

        await using AiIdleTimeoutStream stream = await AiHttpContent
            .OpenGenerationStreamAsync( response, cancellationToken )
            .ConfigureAwait( false );

        await foreach ( var value in OllamaNdjsonReader.ReadAsync( stream, request.ModelId, cancellationToken ) )
        {
            if ( HasContextLimit( value ) )
            {
                yield return WithContextLimit( value );
            }
            else
            {
                yield return value;
            }
        }
    }

    private static AiModelDescriptor[] NormalizeModels( IEnumerable<string?>? ids ) =>
        ids
            ?.Where( static id => !string.IsNullOrWhiteSpace( id ) )
            .Select( static id => id!.Trim( ) )
            .Where( static id => System.Text.Encoding.UTF8.GetByteCount( id ) <= AiLimits.MaximumModelIdBytes &&
                                 id.All( static character => !char.IsControl( character ) ) )
            .Distinct( StringComparer.Ordinal )
            .Order( StringComparer.OrdinalIgnoreCase )
            .Take( AiLimits.MaximumModels )
            .Select( static id => new AiModelDescriptor { Id = id, DisplayName = id } )
            .ToArray( ) ??
        [];

    private OllamaChatRequest CreateChatRequest( AiPrompt prompt )
    {
        return new( ) { Model = prompt.ModelOverride ?? _options.Model, Stream = true, Messages = AiProviderMessages.Build( prompt ) };
    }

    private AiPreparedProviderRequest PrepareChatRequest( OllamaChatRequest request )
    {
        return new AiPreparedProviderRequest
        {
            ProviderId = Id,
            ModelId = request.Model,
            Destination = new Uri( _client.BaseAddress!, ChatRoute ).AbsoluteUri,
            Body = JsonSerializer.SerializeToUtf8Bytes( request, CommandDeckJsonContext.Default.OllamaChatRequest )
        };
    }

    private bool HasContextLimit( AiProviderStreamEvent value )
    {
        return value.Terminal && _options.ContextLimit is not null;
    }

    private AiProviderStreamEvent WithContextLimit( AiProviderStreamEvent value )
    {
        return new AiProviderStreamEvent
        {
            Text = value.Text,
            Terminal = true,
            FinishCategory = value.FinishCategory,
            Usage = new AiUsage
            {
                InputTokens = value.Usage?.InputTokens,
                OutputTokens = value.Usage?.OutputTokens,
                TotalTokens = value.Usage?.TotalTokens,
                ContextLimit = _options.ContextLimit
            }
        };
    }
}
