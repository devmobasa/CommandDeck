namespace CommandDeck;

public sealed class SnippetProvider( IReadOnlyList<SnippetOptions> snippets ) : ICommandProvider
{
    private const string ResultIdNamespace = "snippet";
    private const int MaximumResults = 100;
    private const int ResultScore = 2;

    // Configuration validation already compiled each template; keep the result per snippet.
    private readonly PreparedSnippet[] _snippets =
        [.. snippets.Select( static snippet => new PreparedSnippet( snippet, SnippetTemplate.Compile( snippet.Text ) ) )];

    public string Id => ProviderIds.Snippets;

    public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );

        if ( !query.IncludedFilters.Any( static filter => filter.SourceId == SourceIds.Snippets ) )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        return SearchSnippets( query, request );
    }

    private static SearchResult Build( PreparedSnippet snippet ) => new( )
    {
        Id = StableId.Create( ResultIdNamespace, snippet.Options.Id ),
        Title = snippet.Options.Title,
        Subtitle = "Snippet · copy or preview with inputs",
        Icon = "edit-paste",
        Kind = ResultKinds.CustomCommand,
        ProviderId = ProviderIds.Snippets,
        SourceId = SourceIds.Snippets,
        DetailMarkdown = snippet.Options.Text,
        Score = ResultScore,
        Section = ResultSections.Snippets,
        Actions = [Action( snippet, ActionKinds.SnippetCopy, "Copy snippet" ), Action( snippet, ActionKinds.SnippetPreview, "Preview expanded snippet" )]
    };

    private static CommandAction Action( PreparedSnippet snippet, string kind, string title ) => new( )
    {
        Id = kind,
        Kind = kind,
        Title = title,
        Inputs = [.. snippet.Template.Inputs],
        Arguments = new SnippetActionPayload( snippet.Options.Text, snippet.Template.ReadsClipboardHistory ).ToArguments( )
    };

    private sealed record PreparedSnippet( SnippetOptions Options, CompiledSnippet Template );

    private ValueTask<IReadOnlyList<SearchResult>> SearchSnippets( ParsedQuery query, SearchRequest request )
    {
        return ValueTask.FromResult<IReadOnlyList<SearchResult>>( _snippets
            .Where( prepared => prepared.Options.Title.Contains( query.Text, StringComparison.OrdinalIgnoreCase ) ||
                                prepared.Options.Text.Contains( query.Text, StringComparison.OrdinalIgnoreCase ) )
            .Take( Math.Clamp( request.Limit, 1, MaximumResults ) )
            .Select( Build )
            .ToArray( ) );
    }
}
