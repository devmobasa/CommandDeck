namespace CommandDeck;

public sealed class AiCommandProvider : ICommandProvider
{
    private const string AiPrefix = "ai:";
    private const string AskActionId = "ask";

    private readonly Func<AiOptions> _options;
    private readonly Func<string?> _selectedBackend;

    public AiCommandProvider( AiOptions options, Func<string?>? selectedBackend = null ) : this( ( ) => options, selectedBackend )
    {
        ArgumentNullException.ThrowIfNull( options );
    }

    /// <summary>
    /// Reads the effective provider set on every search so a live settings
    /// mutation is reflected without rebuilding the provider list.
    /// </summary>
    public AiCommandProvider( Func<AiOptions> options, Func<string?>? selectedBackend = null )
    {
        if ( options is null )
        {
            throw new ArgumentNullException( nameof( options ) );
        }

        _options = options;
        _selectedBackend = selectedBackend ?? (( ) => null);
    }

    public string Id => ProviderIds.Assistant;

    public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );
        var options = _options( );

        // An explicit backend prefix (codex:, claude:, cursor:, grok:) names an
        // assistant backend, never a direct provider.
        if ( ShouldSkipQuery( query, options ) )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        List<SearchResult> results = [];
        var selected = _selectedBackend( );
        foreach ( var provider in options.Providers.Where( provider =>
                     provider.Enabled && selected is not null && string.Equals( provider.Id, selected, StringComparison.OrdinalIgnoreCase ) ) )
        {
            var title = GetTitle( provider );
            var score = GetScore( query, provider, title );
            if ( IsUnmatchedQuery( query, score ) )
            {
                continue;
            }

            results.Add( CreateResult( query, provider, title, score ) );
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResult>>( results );
    }

    private static bool ShouldSkipQuery( ParsedQuery query, AiOptions options )
    {
        return !options.Enabled || !query.IsAiMode || query.ExplicitBackendId is not null;
    }

    private static string GetTitle( AiProviderOptions provider )
    {
        if ( provider.Kind == AiAdapterKinds.Ollama )
        {
            return $"Ask local AI ({provider.Model})";
        }

        return $"Ask cloud AI ({provider.Model})";
    }

    private static double GetScore( ParsedQuery query, AiProviderOptions provider, string title )
    {
        if ( query.IsAiMode )
        {
            return 1;
        }

        return Math.Max( FuzzyMatcher.Score( query.Text, title ), FuzzyMatcher.Score( query.Text, provider.Id ) );
    }

    private static bool IsUnmatchedQuery( ParsedQuery query, double score )
    {
        return !query.IsAiMode && !string.IsNullOrWhiteSpace( query.Text ) && score <= 0;
    }

    private SearchResult CreateResult( ParsedQuery query, AiProviderOptions provider, string title, double score )
    {
        return new SearchResult
        {
            Id = $"{AiPrefix}{provider.Id}",
            Kind = ResultKinds.AiCommand,
            Title = title,
            Subtitle = GetRequestSubtitle( provider ),
            Icon = provider.Kind == AiAdapterKinds.Ollama ? "computer" : "network-server",
            ProviderId = Id,
            SourceId = SourceIds.Ai,
            Score = score,
            DetailMarkdown = "The prompt is sent only after you explicitly invoke this action.",
            Details =
            [
                new DetailField( "Provider", provider.Id, Monospace: true ),
                new DetailField( "Model", provider.Model, Monospace: true )
            ],
            Actions =
            [
                new CommandAction
                {
                    Id = AskActionId,
                    Title = "Ask",
                    Kind = ActionKinds.AiPrompt,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                    {
                        [ActionArgumentNames.ProviderId] = provider.Id,
                        [ActionArgumentNames.ModelId] = provider.Model,
                        [ActionArgumentNames.Prompt] = query.Text
                    }
                }
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [ResultMetadataKeys.ProviderId] = provider.Id,
                [ResultMetadataKeys.Prompt] = query.Text
            }
        };
    }

    private static string GetRequestSubtitle( AiProviderOptions provider )
    {
        if ( provider.Kind == AiAdapterKinds.Ollama )
        {
            return "Explicit local prompt; no cloud transfer";
        }

        return $"Explicit request to {new Uri( provider.BaseUrl ).Host}";
    }
}
