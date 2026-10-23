namespace CommandDeck;

public sealed partial class CommandDeckRuntime
{
    private const string WebFallbackId = "web-fallback";
    private const string DefaultWebFallbackIconName = "internet-web-browser";
    private const double WebFallbackScore = 0.1;

    private const string FallbackMetadataKey = "fallback";
    private const string WebFallbackKind = "web";

    private const string AssistantSearchOwner = "assistant-tool";

    public void RevokeInvocationClient( string owner ) => _cache.RemoveOwner( owner );

    private SearchResult GetCustomizableCachedResult( string resultId )
    {
        if ( !_cache.TryGet( resultId, out var cached ) || cached is null )
        {
            throw new InvalidOperationException( "The result expired. Search again before customizing it." );
        }

        if ( !cached.Result.CanCustomize )
        {
            throw new InvalidOperationException( "This result cannot be customized." );
        }

        return cached.Result;
    }

    private SearchResult? ResolveOwnedCachedResult( string owner, string resultId ) =>
        _cache.TryGetOwned( resultId, owner, out var cached ) ? cached?.Result : null;

    SearchResult? IAgentLauncherHost.ResolveCachedResult( string resultId, string invocationReference ) =>
        _invocations.ResolveAssistantResult( resultId, invocationReference, AssistantSearchOwner );

    ValueTask<ActionExecutionResult> IAgentLauncherHost.InvokeAsync( string resultId, string actionId, string invocationReference, bool confirmed,
        CancellationToken cancellationToken ) =>
        _invocations.InvokeAssistantAsync( resultId, actionId, invocationReference, AssistantSearchOwner, confirmed, cancellationToken );

    private SearchResult CacheSyntheticResult( SearchResult result, string owner )
    {
        var cachedResults = _cache.Store( [result], string.Empty, owner: owner );

        return cachedResults[0];
    }

    private async ValueTask<SearchResult?> BuildWebFallbackAsync( SearchRequest request, CancellationToken cancellationToken )
    {
        var parsed = QueryParser.Parse( request.Query, request.Expression, registry: Contributions );
        if ( CannotBuildWebFallback( parsed ) )
        {
            return null;
        }

        var quicklinks = await GetQuicklinksAsync( cancellationToken )
            .ConfigureAwait( false );

        var quicklink = quicklinks.Quicklinks.FirstOrDefault( entry => entry.Enabled &&
                                                                       string.Equals( entry.Quicklink.Id, quicklinks.WebFallbackQuicklinkId,
                                                                           StringComparison.Ordinal ) )
            ?.Quicklink;

        if ( quicklink is null )
        {
            return null;
        }

        return new SearchResult
        {
            Id = StableId.Create( WebFallbackId, $"{quicklink.Id}\n{parsed.Text}" ),
            Kind = ResultKinds.Quicklink,
            Title = $"Search the web for “{parsed.Text}”",
            Subtitle = quicklink.Title,
            Icon = quicklink.Icon ?? DefaultWebFallbackIconName,
            ProviderId = ProviderIds.Fallback,
            SourceId = SourceIds.CommandsQuicklinks,
            Section = ResultSections.Fallback,
            Score = WebFallbackScore,
            CanCustomize = false,
            Actions =
            [
                new CommandAction
                {
                    Id = AppActionIds.Open,
                    Title = "Search the Web",
                    Kind = ActionKinds.OpenUrl,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                    {
                        [ActionArgumentNames.UrlTemplate] = UrlTemplateTokens.Expand( quicklink.UrlTemplate, parsed.Text )
                    }
                }
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [FallbackMetadataKey] = WebFallbackKind,
                [ResultMetadataKeys.QuicklinkId] = quicklink.Id
            }
        };
    }

    private static bool CannotBuildWebFallback( ParsedQuery parsed )
    {
        return parsed.Text.Length == 0 || parsed.HasExplicitFilter || parsed.IsCalculator || parsed.IsAiMode || parsed.IsCommandMode;
    }
}
