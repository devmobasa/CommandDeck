namespace CommandDeck.Specs;

public static partial class Program
{
    private static void QueryAlgebraParsesDeterministically( )
    {
        var parsed = QueryParser.Parse( "file: app: \"release notes\" -workspaces: -path:\"/private area\" title:roadmap" );

        True( parsed
            .IncludedFilters.Select( static value => value.Id )
            .SequenceEqual( ["files", "applications"] ) );

        Equal( "workspaces", parsed.ExcludedFilters.Single( ).Id );
        Equal( 3, parsed.Expression.Clauses.Count );
        Equal( "release notes", parsed.Expression.Clauses[0].Text );
        Equal( "path", parsed.Expression.Clauses[1].Field );
        Equal( "/private area", parsed.Expression.Clauses[1].Text );
        True( parsed.Expression.Clauses[1].Excluded );
        Equal( "title", parsed.Expression.Clauses[2].Field );
        Equal( "roadmap", parsed.Expression.Clauses[2].Text );
        var negativeOnly = QueryParser.Parse( "-secret" );
        Equal( string.Empty, negativeOnly.Text );
        True( negativeOnly.HasExplicitFilter );
        Equal( "secret", negativeOnly.Expression.Clauses.Single( ).Text );
        True( negativeOnly.Expression.Clauses.Single( ).Excluded );
        var excludedTerm = QueryParser.Parse( "roadmap -secret" );
        Equal( "roadmap", excludedTerm.Text );
        True( excludedTerm.Expression.Clauses[1].Excluded );

        Equal( "workspaces", QueryParser
            .Parse( "roadmap -workspaces:" )
            .ExcludedFilters.Single( ).Id );

        var applicationSearch = QueryParser.Parse( "app: roadmap -path:/private" );
        Equal( "path", applicationSearch.Expression.Clauses[1].Field );
        True( applicationSearch.Expression.Clauses[1].Excluded );
        var merged = QueryParser.Parse( "metadata:alpha", CreateQueryExpression( ) );
        Equal( "commands", merged.IncludedFilters.Single( ).Id );
        Equal( 2, merged.Expression.Clauses.Count );

        static QueryExpression CreateQueryExpression( )
        {
            return new QueryExpression
            {
                IncludeFilterIds = [SearchFilterIds.Commands],
                Clauses = [new QueryClause { Field = QueryFieldIds.Title, Text = "deploy" }]
            };
        }
    }

    private static void QueryAlgebraRejectsMalformedExpressions( )
    {
        Throws<InvalidOperationException>( ( ) => QueryParser.Parse( "\"unterminated" ) );
        var unknownPrefix = QueryParser.Parse( "unknown:" );

        True( unknownPrefix.Expression.Clauses is [{ Text: "unknown" }], "an unknown prefix is ordinary text while typing" );

        True( QueryParser.Parse( "title:" )
                  .Expression.Clauses.Count ==
              0, "an incomplete field clause is ignored while typing" );

        Throws<InvalidOperationException>( ( ) => QueryParser.Parse( string.Empty,
            new QueryExpression { IncludeFilterIds = [SearchFilterIds.Files, "FILES"] } ) );

        Throws<InvalidOperationException>( ( ) => QueryParser.Parse( string.Empty,
            new QueryExpression { IncludeFilterIds = [SearchFilterIds.Files], ExcludeFilterIds = [SearchFilterIds.Files] } ) );

        Throws<InvalidOperationException>( ( ) => ProtocolRequestValidator.Validate( new ProtocolRequest
        {
            RequestId = 1,
            Operation = ProtocolOperations.Search,
            Expression = null!
        } ) );

        Throws<InvalidOperationException>( ( ) => QueryParser.Parse( string.Empty, new QueryExpression { Clauses = null! } ) );
        Throws<InvalidOperationException>( ( ) => ProtocolRequestValidator.Validate( CreateOversizedLiteralQueryRequest( ) ) );

        foreach ( var privateQuery in new[] { "clip: secret", "hey: secret" } )
        {
            Throws<InvalidOperationException>( ( ) => SavedSearchValidator.ValidateExpression( new QueryExpression { LiteralQuery = privateQuery } ) );
        }

        static ProtocolRequest CreateOversizedLiteralQueryRequest( )
        {
            return new ProtocolRequest
            {
                RequestId = 1,
                Operation = ProtocolOperations.Search,
                Expression = new QueryExpression { LiteralQuery = new string( 'x', ProtocolLimits.MaximumQueryCharacters + 1 ) }
            };
        }
    }

    private static async Task QueryAlgebraEvaluatesAcrossProvidersAsync( )
    {
        var application = AlgebraResult( "app:editor", ResultKinds.Application, "Roadmap Editor", SourceIds.Applications );

        var file = AlgebraResult( "file:roadmap", ResultKinds.File, "Roadmap Notes", SourceIds.Files,
            new Dictionary<string, string>( StringComparer.Ordinal ) { [ResultMetadataKeys.Path] = "/docs/roadmap.md" } );

        var privateFile = AlgebraResult( "file:private", ResultKinds.File, "Roadmap Draft", SourceIds.Files,
            new Dictionary<string, string>( StringComparer.Ordinal ) { [ResultMetadataKeys.Path] = "/private/draft.md" } );

        var window = AlgebraResult( "window:1", ResultKinds.Window, "Roadmap", SourceIds.WindowsWorkspaces );

        var workspace = AlgebraResult( "workspace:1", ResultKinds.Workspace, "Roadmap", SourceIds.WindowsWorkspaces );

        SearchCoordinator coordinator = new( CreateAlgebraProviders( application, file, privateFile, window, workspace ), new SearchOptions( ) );

        var union = await coordinator.SearchAsync(
            new SearchRequest
            {
                Query = "roadmap",
                Expression = new QueryExpression { IncludeFilterIds = [SearchFilterIds.Applications, SearchFilterIds.Files] },
                Limit = 20
            }, CancellationToken.None );

        True( union
            .Select( static value => value.Id )
            .ToHashSet( StringComparer.Ordinal )
            .SetEquals( [application.Id, file.Id, privateFile.Id] ) );

        var intersected = await coordinator.SearchAsync( new SearchRequest
        {
            Query = string.Empty,
            Expression = new QueryExpression
            {
                IncludeFilterIds = [SearchFilterIds.Files],
                Clauses =
                [
                    new QueryClause { Field = QueryFieldIds.Title, Text = "roadmap" },
                    new QueryClause { Field = QueryFieldIds.Path, Text = "/private", Excluded = true }
                ]
            },
            Limit = 20
        }, CancellationToken.None );

        Equal( file.Id, intersected.Single( ).Id );

        var sharedProviderExclusion = await coordinator.SearchAsync(
            new SearchRequest { Query = "roadmap", Expression = new QueryExpression { ExcludeFilterIds = [SearchFilterIds.Windows] }, Limit = 20 },
            CancellationToken.None );

        True( sharedProviderExclusion.Any( value => value.Id == workspace.Id ) );
        True( sharedProviderExclusion.All( value => value.Id != window.Id ) );

        static IEnumerable<ICommandProvider> CreateAlgebraProviders( SearchResult application, SearchResult file, SearchResult privateFile, SearchResult window,
            SearchResult workspace )
        {
            return
            [
                new AlgebraSearchProvider( "catalog", [application, file, privateFile] ),
                new AlgebraSearchProvider( "hyprland", [window, workspace] )
            ];
        }
    }

    private static SearchResult AlgebraResult( string id, string kind, string title, string source, Dictionary<string, string>? metadata = null ) => new( )
    {
        Id = id,
        Kind = kind,
        Title = title,
        SourceId = source,
        ProviderId = source,
        Score = 0.8,
        Metadata = metadata ?? new Dictionary<string, string>( StringComparer.Ordinal )
    };

    private sealed class AlgebraSearchProvider( string id, IReadOnlyList<SearchResult> results ) : ICommandProvider
    {
        public string Id { get; } = id;

        public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken ) =>
            ValueTask.FromResult( results );
    }
}
