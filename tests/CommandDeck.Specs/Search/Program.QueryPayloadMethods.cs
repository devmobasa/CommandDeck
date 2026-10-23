namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AssistantSearchPreservesLiteralPromptAsync( )
    {
        AgentCommandProvider provider = new( ( ) => new AgentRowState(
            Enabled: true, SelectedBackendId: AgentBackendIds.Codex, Authenticated: true, Conversation: null, ModelId: null ) );

        SearchCoordinator search = new( [provider], new SearchOptions( ) );
        foreach ( var prompt in CreateLiteralPrompts( ) )
        {
            foreach ( var prefix in new[] { "ai:", "claude:", "" } )
            {
                var rows = await search.SearchAsync(
                    new SearchRequest
                    {
                        Query = prefix.Length == 0 ? prompt : $"{prefix} {prompt}",
                        Expression = new QueryExpression { IncludeFilterIds = prefix.Length == 0 ? [SearchFilterIds.Ai] : [] },
                        Limit = 8
                    }, CancellationToken.None );

                Equal( 1, rows.Count );

                Equal( prompt, rows[0]
                    .Actions.Single( )
                    .Arguments["prompt"] );

                Equal( prefix == "claude:" ? "claude.local" : "codex.local", rows[0]
                    .Actions.Single( )
                    .Arguments["backendId"] );
            }
        }

        static string[] CreateLiteralPrompts( )
        {
            return new[]
            {
                "Is -1 negative?", "Explain title:Lincoln and file:notes as literal text.", "title:Lincoln is a literal prompt, not a field filter.",
                "Explain the phrase \"four score", "Keep  two spaces and\na line break."
            };
        }
    }
}
