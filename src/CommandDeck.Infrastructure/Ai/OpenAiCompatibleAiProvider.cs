using System.Net.Http.Headers;
using System.Text.Json;

namespace CommandDeck;

public sealed class OpenAiCompatibleAiProvider : IAiProvider
{
    private const string ModelsRoute = "models";
    private const string ChatRoute = "chat/completions";
    private const string AdapterContractVersion = "openai-chat-completions-v1";

    private readonly AiProviderOptions _options;
    private readonly HttpClient _client;
    private readonly IAiCredentialResolver _credentials;

    public OpenAiCompatibleAiProvider( AiProviderOptions options, HttpClient client, IAiCredentialResolver credentials )
    {
        _options = options;
        _client = client;
        _credentials = credentials;
    }

    public string Id => _options.Id;

    public AiProviderDescriptor Describe( ) => ProviderDescriptor.Create( _options,
        new AiProviderCapabilities
        {
            Discovery = true,
            Streaming = true,
            Usage = true,
            RateMetadata = true,
            AdapterContractVersion = AdapterContractVersion
        } );

    public async ValueTask<IReadOnlyList<AiModelDescriptor>> DiscoverModelsAsync( CancellationToken cancellationToken )
    {
        var credential = await _credentials
            .ResolveAsync( _options.Credential, cancellationToken )
            .ConfigureAwait( false );

        using HttpRequestMessage request = new( HttpMethod.Get, ModelsRoute );

        if ( credential is not null )
        {
            using ( credential )
            {
                request.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", credential.ReadUtf8( ) );
            }
        }

        using var response = await _client
            .SendAsync( request, HttpCompletionOption.ResponseHeadersRead, cancellationToken )
            .ConfigureAwait( false );

        AiHttpContent.EnsureSuccess( response );

        var body = await AiHttpContent
            .ReadBoundedJsonAsync( response.Content, CommandDeckJsonContext.Default.OpenAiModelsResponse, AiLimits.MaximumDiscoveryBytes, cancellationToken )
            .ConfigureAwait( false );

        return ReadModels( body );
    }

    public AiPreparedProviderRequest PrepareRequest( AiPrompt prompt )
    {
        OpenAiChatRequest request = CreateChatRequest( prompt );

        return PrepareChatRequest( request );
    }

    public async IAsyncEnumerable<AiProviderStreamEvent> StreamAsync( AiPreparedProviderRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken )
    {
        AiHttpContent.EnsurePreparedRequest( request, Id );

        var credential = await _credentials
            .ResolveAsync( _options.Credential, cancellationToken )
            .ConfigureAwait( false );

        using ByteArrayContent content = new( request.Body );
        content.Headers.ContentType = new MediaTypeHeaderValue( System.Net.Mime.MediaTypeNames.Application.Json ) { CharSet = "utf-8" };
        using HttpRequestMessage message = new( HttpMethod.Post, ChatRoute ) { Content = content };

        if ( credential is not null )
        {
            using ( credential )
            {
                message.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", credential.ReadUtf8( ) );
            }
        }

        using var response = await _client
            .SendAsync( message, HttpCompletionOption.ResponseHeadersRead, cancellationToken )
            .ConfigureAwait( false );

        AiHttpContent.EnsureSuccess( response );
        var rateLimit = ReadHeaderLong( response, "x-ratelimit-limit-requests" );
        var rateRemaining = ReadHeaderLong( response, "x-ratelimit-remaining-requests" );

        await using AiIdleTimeoutStream stream = await AiHttpContent
            .OpenGenerationStreamAsync( response, cancellationToken )
            .ConfigureAwait( false );

        AiUsage? observedUsage = null;

        await foreach ( var value in OpenAiSseReader.ReadAsync( stream, cancellationToken ) )
        {
            if ( HasUsage( value ) )
            {
                observedUsage = MergeUsage( observedUsage, value.Usage );

                yield return WithObservedUsage( rateLimit, rateRemaining, observedUsage, value );
            }
            else
            {
                yield return value;
            }
        }
    }

    private static AiUsage? MergeUsage( AiUsage? prior, AiUsage? current )
    {
        if ( current is null )
        {
            return prior;
        }
        else
        {
            return new AiUsage
            {
                InputTokens = current.InputTokens ?? prior?.InputTokens,
                OutputTokens = current.OutputTokens ?? prior?.OutputTokens,
                TotalTokens = current.TotalTokens ?? prior?.TotalTokens
            };
        }
    }

    private static long? ReadHeaderLong( HttpResponseMessage response, string name )
    {
        if ( response.Headers.TryGetValues( name, out var values ) &&
             long.TryParse( values.FirstOrDefault( ), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                 out var value ) &&
             value >= 0 )
        {
            return value;
        }
        else
        {
            return null;
        }
    }

    private static AiModelDescriptor[] ReadModels( OpenAiModelsResponse? body )
    {
        return body
                   ?.Data.Where( static model => !string.IsNullOrWhiteSpace( model.Id ) )
                   .Where( static model => System.Text.Encoding.UTF8.GetByteCount( model.Id! ) <= AiLimits.MaximumModelIdBytes &&
                                           model.Id!.All( static character => !char.IsControl( character ) ) )
                   .GroupBy( static model => model.Id!, StringComparer.Ordinal )
                   .Select( static group => group.First( ) )
                   .OrderBy( static model => model.Id, StringComparer.OrdinalIgnoreCase )
                   .Take( AiLimits.MaximumModels )
                   .Select( static model => new AiModelDescriptor { Id = model.Id!, DisplayName = model.Id!, Owner = model.OwnedBy } )
                   .ToArray( ) ??
               [];
    }

    private OpenAiChatRequest CreateChatRequest( AiPrompt prompt )
    {
        return new( )
        {
            Model = prompt.ModelOverride ?? _options.Model,
            Messages = AiProviderMessages.Build( prompt ),
            Stream = true,
            StreamOptions = new OpenAiStreamOptions { IncludeUsage = true },
            ToolChoice = "none"
        };
    }

    private AiPreparedProviderRequest PrepareChatRequest( OpenAiChatRequest request )
    {
        return new AiPreparedProviderRequest
        {
            ProviderId = Id,
            ModelId = request.Model,
            Destination = new Uri( _client.BaseAddress!, ChatRoute ).AbsoluteUri,
            Body = JsonSerializer.SerializeToUtf8Bytes( request, CommandDeckJsonContext.Default.OpenAiChatRequest )
        };
    }

    private static bool HasUsage( AiProviderStreamEvent value )
    {
        return value.Terminal || value.Usage is not null;
    }

    private AiProviderStreamEvent WithObservedUsage( long? rateLimit, long? rateRemaining, AiUsage? observedUsage, AiProviderStreamEvent value )
    {
        return new AiProviderStreamEvent
        {
            Text = value.Text,
            Terminal = value.Terminal,
            FinishCategory = value.FinishCategory,
            Usage = new AiUsage
            {
                InputTokens = observedUsage?.InputTokens,
                OutputTokens = observedUsage?.OutputTokens,
                TotalTokens = observedUsage?.TotalTokens,
                ContextLimit = _options.ContextLimit,
                RateLimit = rateLimit,
                RateRemaining = rateRemaining
            }
        };
    }
}
