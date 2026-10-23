using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

/// <summary>
/// Configurable named web-search engines (U3). Each engine renders a result
/// that opens its <c>urlTemplate</c> with the query percent-encoded, reusing the
/// same <see cref="ActionKinds.OpenUrl"/> plumbing (xdg-open) as quicklinks and
/// the web fallback - the URL is substituted here so no open machinery is
/// duplicated. Four shapes are understood:
/// <list type="bullet">
///   <item><c>&lt;alias&gt;: &lt;text&gt;</c> - routes to that one engine as an
///   exact-alias top match (e.g. <c>g: rust traits</c> opens Google).</item>
///   <item>a bare <c>&lt;text&gt;</c> query - one low-scored result per engine
///   whose <c>includeInGlobal</c> is set, so a plain search still offers the
///   web engines alongside catalog matches.</item>
///   <item><c>web: &lt;text&gt;</c> - one result per configured engine, including
///   engines kept out of global searches.</item>
///   <item>a bare or <c>web:</c> query that is one typed URL (see
///   <see cref="TypedUrl"/>) - an "Open &lt;url&gt;" row placed in the explicit Top
///   Match tier, ahead of the engines, with Copy URL as its second action. Its
///   id is fixed, so accepted use never stores the address.</item>
/// </list>
/// The engine alias is recovered from the raw query, so routing is identical
/// whether or not the alias was registered as a dynamic filter for the parser.
/// Engine aliases are config-driven; nothing is hardcoded here. AOT-safe: no
/// reflection, and results carry only the (already public) destination URL.
/// </summary>
public sealed class WebSearchProvider : ICommandProvider
{
    private const string WebSearchPrefix = "web-search:";

    private const string CopyUrlActionId = "copy-url";
    private const string EngineIdMetadata = "engineId";
    private const string AliasMetadata = "alias";
    private const string OpenActionId = "open";
    private const string UrlMetadata = "url";
    private const double AmbientScore = 0.1;

    public const string TypedUrlResultId = "web-search:url";

    private readonly IReadOnlyList<WebEngineOptions> _engines;
    private readonly Dictionary<string, WebEngineOptions> _byAlias;

    public WebSearchProvider( IReadOnlyList<WebEngineOptions> engines )
    {
        ArgumentNullException.ThrowIfNull( engines );

        _engines = [.. engines];
        _byAlias = new Dictionary<string, WebEngineOptions>( StringComparer.OrdinalIgnoreCase );

        foreach ( var engine in _engines )
        {
            if ( !string.IsNullOrWhiteSpace( engine.Alias ) )
            {
                _byAlias[engine
                    .Alias.Trim( )
                    .TrimEnd( ':' )] = engine;
            }
        }
    }

    public string Id => ProviderIds.WebSearch;

    public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( query );
        ArgumentNullException.ThrowIfNull( request );

        cancellationToken.ThrowIfCancellationRequested( );

        // 1. Explicit engine prefix: "<alias>: <text>" targets exactly one engine.
        if ( TryFindEngine( query, out var remainder, out var engine ) )
        {
            var prefixed = remainder.Trim( );

            return BuildPrefixedResults( engine, prefixed );
        }

        // 2. Explicit web searches include every engine; bare queries use global engines.
        //    A different explicit filter
        //    (file:, app:, tz:, ...) routes elsewhere and must not draw web rows.
        if ( ExcludesWebSearch( query ) )
        {
            return Empty;
        }

        var text = query.Text.Trim( );
        if ( text.Length == 0 )
        {
            return Empty;
        }

        // 3. A typed URL opens directly, whether or not any engine is configured.
        List<SearchResult> results = [];

        if ( TypedUrl.TryParse( text, out var url ) )
        {
            results.Add( BuildTypedUrlResult( url ) );
        }

        foreach ( var candidate in _engines )
        {
            if ( IncludesEngine( query, candidate ) )
            {
                results.Add( BuildResult( candidate, text, exact: false ) );
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResult>>( results );
    }

    private static SearchResult BuildResult( WebEngineOptions engine, string queryText, bool exact )
    {
        var url = UrlTemplateTokens.Expand( engine.UrlTemplate, queryText );

        // Stable, query-independent id so repeated use of an engine accrues usage
        // history under one key, like other results.
        var id = $"{WebSearchPrefix}{engine.Id}";
        Dictionary<string, string> metadata = CreateMetadata( engine, url, id );

        if ( exact )
        {
            // Reuse the exact-alias convention: an explicit engine query is the
            // top match, scored above ambient results by the coordinator.
            metadata[ResultMetadataKeys.ExactAlias] = engine.Alias;
        }

        return new SearchResult
        {
            Id = id,
            Kind = ResultKinds.WebSearch,
            Title = $"Search {engine.Name} for “{queryText}”",
            Subtitle = $"{engine.Name} · {engine.Alias}:",
            Icon = "internet-web-browser",
            ProviderId = ProviderIds.WebSearch,
            SourceId = SourceIds.WebSearch,
            Score = exact ? 1 : AmbientScore,
            CanCustomize = false,
            Section = ResultSections.WebSearch,
            Details =
            [
                new DetailField( "Engine", engine.Name ),
                new DetailField( "URL", url, Monospace: true )
            ],
            Metadata = metadata,
            Actions =
            [
                new CommandAction
                {
                    Id = OpenActionId,
                    Title = $"Search {engine.Name}",
                    Kind = ActionKinds.OpenUrl,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal ) { [ActionArgumentNames.UrlTemplate] = url }
                }
            ]
        };
    }

    // The explicit placement ranks the row with exact aliases rather than through
    // a pinned score, so a real exact alias stays ahead of it. One fixed id keeps
    // the address out of usage history; query history follows its own setting.
    private static SearchResult BuildTypedUrlResult( string url ) => new( )
    {
        Id = TypedUrlResultId,
        Kind = ResultKinds.WebSearch,
        Title = $"Open {url}",
        Subtitle = "Typed URL · default browser",
        Icon = "internet-web-browser",
        ProviderId = ProviderIds.WebSearch,
        SourceId = SourceIds.WebSearch,
        Score = 1,
        CanCustomize = false,
        Section = ResultSections.WebSearch,
        Details = [new DetailField( "URL", url, Monospace: true )],
        Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
        {
            [UrlMetadata] = url,
            [ResultMetadataKeys.CatalogResultId] = TypedUrlResultId,
            [ResultPlacements.MetadataKey] = ResultPlacements.Explicit
        },
        Actions =
        [
            new CommandAction
            {
                Id = OpenActionId,
                Title = "Open URL",
                Kind = ActionKinds.OpenUrl,
                Arguments = new UrlActionPayload( url ).ToArguments( )
            },
            new CommandAction
            {
                Id = CopyUrlActionId,
                Title = "Copy URL",
                Kind = ActionKinds.CopyText,
                Arguments = new TextActionPayload( url ).ToArguments( )
            }
        ]
    };

    // Recover a leading "<token>:" prefix from the raw query. The token must be a
    // single whitespace-free run before the first colon; the remainder is the
    // engine's query text. Returns false for a bare query or a mid-word colon.
    private static bool TrySplitAlias( string original, out string alias, out string remainder )
    {
        alias = string.Empty;
        remainder = string.Empty;
        var trimmed = original.Trim( );
        var colon = trimmed.IndexOf( ':', StringComparison.Ordinal );
        if ( colon <= 0 )
        {
            return false;
        }

        var candidate = trimmed[..colon];
        if ( candidate.Any( char.IsWhiteSpace ) )
        {
            return false;
        }

        alias = candidate;
        remainder = trimmed[(colon + 1)..];

        return true;
    }

    private static ValueTask<IReadOnlyList<SearchResult>> Single( SearchResult result ) =>
        ValueTask.FromResult<IReadOnlyList<SearchResult>>( [result] );

    private static ValueTask<IReadOnlyList<SearchResult>> Empty =>
        ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );

    private static ValueTask<IReadOnlyList<SearchResult>> BuildPrefixedResults( WebEngineOptions engine, string prefixed )
    {
        if ( prefixed.Length == 0 )
        {
            return Empty;
        }

        return Single( BuildResult( engine, prefixed, exact: true ) );
    }

    private static bool ExcludesWebSearch( ParsedQuery query )
    {
        return query.HasExplicitFilter && !query.IncludesFilter( SearchFilterIds.WebSearch );
    }

    private static bool IncludesEngine( ParsedQuery query, WebEngineOptions candidate )
    {
        return candidate.IncludeInGlobal || query.IncludesFilter( SearchFilterIds.WebSearch );
    }

    private static Dictionary<string, string> CreateMetadata( WebEngineOptions engine, string url, string id )
    {
        return new( StringComparer.Ordinal )
        {
            [EngineIdMetadata] = engine.Id,
            [AliasMetadata] = engine.Alias,
            [UrlMetadata] = url,
            [ResultMetadataKeys.CatalogResultId] = id
        };
    }

    private bool TryFindEngine( ParsedQuery query, [NotNullWhen( true )] out string? remainder, [NotNullWhen( true )] out WebEngineOptions? engine )
    {
        remainder = default;
        engine = default;

        return TrySplitAlias( query.Original, out var alias, out remainder ) && _byAlias.TryGetValue( alias, out engine );
    }
}
