namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditStructuredTitleAsync( )
    {
        var title = AlgebraResult( "app:title", ResultKinds.Application, "Needle editor", SourceIds.Applications );
        SearchResult subtitle = new( )
        {
            Id = "app:subtitle",
            Kind = ResultKinds.Application,
            Title = "Other editor",
            Subtitle = "Needle",
            SourceId = SourceIds.Applications,
            Score = 0.8
        };
        SearchCoordinator coordinator = new( [new AlgebraSearchProvider( "applications", [title, subtitle] )], new SearchOptions( ) );

        foreach ( var field in new[] { "title", "TITLE", "TiTlE" } )
        {
            var results = await coordinator.SearchAsync( new SearchRequest
            {
                Expression = new QueryExpression { Clauses = [new QueryClause { Field = field, Text = "needle" }] }
            }, default );
            Equal( "app:title", results.Single( ).Id );
        }

        var textual = await coordinator.SearchAsync( new SearchRequest { Query = "title:needle" }, default );
        Equal( "app:title", textual.Single( ).Id );
    }
}
