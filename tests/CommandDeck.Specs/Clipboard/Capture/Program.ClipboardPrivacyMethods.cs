namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ClipboardNoninteractiveSearchIsPrivateAsync( )
    {
        CountingSearchProvider clipboard = new( ProviderIds.Clipboard );
        SearchResult privateRow = CreatePrivateRow( );

        SearchCoordinator search = new( [clipboard, new StaticSearchProvider( [privateRow] )], new SearchOptions( ) );

        foreach ( var purpose in new[] { SearchPurpose.AssistantTool, SearchPurpose.Suggestion } )
        {
            foreach ( var query in new[] { "clip: private", "@clipboard private", "clip: app: private", "-clip: private", "private" } )
            {
                var results = await search.SearchAsync( new SearchRequest { Query = query }, purpose, default );
                Equal( 0, results.Count );
                Equal( 0, clipboard.Calls );
            }
        }

        await search.SearchAsync( new SearchRequest { Query = "clip: private" }, default );
        Equal( 1, clipboard.Calls );
        Equal( "denied", AgentLauncherTools.ClassifyRisk( privateRow, privateRow.Actions[0] ) );

        var aliases = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["cb"] = SourceIds.Clipboard };

        foreach ( var query in new[] { "cb: private", "-cb: private", "@clipboard private", "app: clip: private" } )
        {
            True( ClipboardPrivacy.ReferencesHistory( QueryParser.Parse( query, aliases: aliases ) ), "canonical clipboard references are sensitive" );
        }

        static SearchResult CreatePrivateRow( )
        {
            return new( )
            {
                Id = "clipboard:synthetic",
                Title = "private sentinel",
                Kind = ResultKinds.CustomCommand,
                SourceId = SourceIds.Clipboard,
                ProviderId = ProviderIds.Clipboard,
                Actions = [new( ) { Id = "open", Title = "Open", Kind = ActionKinds.OpenUrl }]
            };
        }
    }

    private static async Task ClipboardRuntimeDeniesAssistantCacheAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-clipboard-private-" ).FullName;

        try
        {
            var config = await WriteManagedSpecConfigAsync( root, new AiOptions { Enabled = false } );
            await using var runtime = await CommandDeckRuntime.CreateAsync( config, default );
            var request = new SearchRequest { Query = "clip:" };
            var interactive = await runtime.SearchAsync( request, default );
            True( interactive.Any( ClipboardPrivacy.IsHistory ), "interactive controls remain accessible" );
            IAgentLauncherHost assistant = runtime;
            var rows = await assistant.SearchAsync( request, default );

            True( rows.All( static row => !ClipboardPrivacy.IsHistory( row ) ), "assistant cannot search history" );

            foreach ( var row in interactive.Where( ClipboardPrivacy.IsHistory ) )
            {
                True( assistant.ResolveCachedResult( row.Id, row.InvocationReference! ) is null, "interactive cache cannot bypass assistant isolation" );

                var outcome = await assistant.InvokeAsync( row.Id, "clear", row.InvocationReference!, true, default );

                True( !outcome.Succeeded, "confirmed assistant invocation cannot clear history" );
            }

            var suggestions = await runtime.SuggestAsync( new SearchRequest { Query = "clip: sentinel" }, default );

            Equal( 0, suggestions.Suggestions.Count );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task ClipboardAiAttachmentsAreRejectedAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-clipboard-ai-" ).FullName;

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( default );

            await using AiProviderRegistry providers = new( OneLocalAiProvider( ), new TestAiTransportFactory( ) );

            SearchResult result = CreateResult( );

            using AiPreflightService service = new( providers,
                new FilePreviewService( new IndexingOptions { Roots = [] }, new PrivacyOptions { NeverIndex = [] } ), catalog.AiPreferences,
                ( _, _ ) => result );

            foreach ( var kind in new[] { AiAttachmentKinds.Result, AiAttachmentKinds.File } )
            {
                await ThrowsAsync<InvalidOperationException>( async ( ) => await service.CreateAsync( "owner",
                    new AiDraft
                    {
                        ProviderId = "ollama",
                        ModelId = "qwen",
                        UserText = "Explain",
                        Attachments = [new( ) { Kind = kind, ResultId = result.Id }]
                    }, default ) );
            }

            var explicitText = await service.CreateAsync( "owner", new AiDraft
            {
                ProviderId = "ollama",
                ModelId = "qwen",
                UserText = "Explain",
                Attachments = [new( ) { Kind = AiAttachmentKinds.Text, Text = "explicit user input" }]
            }, default );

            True( explicitText.Id.Length > 0, "ordinary explicit text remains supported" );

            static SearchResult CreateResult( )
            {
                return new( )
                {
                    Id = "clipboard:synthetic",
                    Title = "private sentinel",
                    Kind = ResultKinds.File,
                    SourceId = SourceIds.Clipboard,
                    ProviderId = ProviderIds.Clipboard,
                    Metadata = new( StringComparer.Ordinal ) { [ResultMetadataKeys.Path] = "/does/not/exist" }
                };
            }
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
