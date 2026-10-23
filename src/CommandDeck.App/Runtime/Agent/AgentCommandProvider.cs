namespace CommandDeck;

/// <summary>
/// What the launcher row needs to know: whether the assistant is on, which
/// assistant backend is selected (null when a direct provider is), whether that
/// backend is signed in, and a descriptor for every registered backend so an
/// explicit prefix can name one that is not selected.
/// </summary>
public sealed record AgentRowState(
    bool Enabled,
    string? SelectedBackendId,
    bool Authenticated,
    AgentConversationSummary? Conversation,
    string? ModelId,
    IReadOnlyDictionary<string, AgentBackendDescriptor>? Backends = null );

public sealed class AgentCommandProvider( Func<AgentRowState?> state ) : ICommandProvider
{
    private const double ContinueRowScore = 1.2;
    private const string AgentConversationResultId = "agent:conversation";
    private const string AgentIconName = "dialog-question";

    private const string CodexPromptPrefix = "codex ";
    private const int MaximumPromptTitleCharacters = 80;
    private const string AgentResultPrefix = "agent:";
    private const double AskRowScore = 1.5;

    public string Id => ProviderIds.Assistant;

    public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );
        var current = state( );
        var prompt = ExtractPrompt( query );
        if ( current is null || prompt is null )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        List<SearchResult> results = [];
        var targetId = query.ExplicitBackendId ?? current.SelectedBackendId;
        if ( targetId is not null )
        {
            results.Add( BuildAskRow( current, targetId, query.ExplicitBackendId, prompt ) );
        }

        if ( ShouldShowConversationForEmptyPrompt( current, prompt, targetId ) )
        {
            results.Add( ContinueRow( current.Conversation!, ContinueRowScore ) );
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResult>>( results );
    }

    private SearchResult BuildAskRow( AgentRowState current, string targetId, string? explicitBackend, string prompt )
    {
        var descriptor = current.Backends is not null && current.Backends.TryGetValue( targetId, out var known ) ? known : null;
        var displayName = descriptor?.DisplayName ?? AgentBackendIds.DisplayName( targetId );
        var isSelected = string.Equals( targetId, current.SelectedBackendId, StringComparison.OrdinalIgnoreCase );
        bool authenticated;
        if ( explicitBackend is null || isSelected )
        {
            authenticated = current.Authenticated;
        }
        else
        {
            authenticated = descriptor?.AuthenticationState == AgentAuthenticationStates.Authenticated;
        }

        return new SearchResult
        {
            Id = AgentResultPrefix + ShortName( targetId ),
            Kind = ResultKinds.Assistant,
            Title = prompt.Length == 0 ? "Ask AI" : $"Ask AI: {Truncate( prompt )}",
            Subtitle = Subtitle( current, descriptor, displayName, authenticated, isSelected ? current.ModelId : null, targetId ),
            Icon = AgentIconName,
            ProviderId = Id,
            SourceId = SourceIds.Ai,
            Score = AskRowScore,
            DetailMarkdown = targetId switch
            {
                AgentBackendIds.Codex => "The conversation stays inside CommandDeck. Tools run only after your confirmation.",
                _ => "The conversation stays inside CommandDeck. This backend answers only; it has no CommandDeck tools."
            },
            Actions =
            [
                new CommandAction
                {
                    Id = "ask",
                    Title = "Ask",
                    Kind = ActionKinds.AgentPrompt,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                    {
                        [ActionArgumentNames.Prompt] = prompt, [ActionArgumentNames.BackendId] = targetId
                    }
                }
            ]
        };
    }

    private static string Subtitle( AgentRowState current, AgentBackendDescriptor? descriptor, string displayName, bool authenticated, string? model,
        string targetId )
    {
        if ( !current.Enabled )
        {
            return "Assistant is off; press Enter to review the enable card";
        }

        if ( descriptor is { Available: false } )
        {
            return $"Install the {displayName} CLI to use this backend";
        }

        if ( !authenticated )
        {
            return $"Sign in to {displayName} inside CommandDeck to continue";
        }

        var account = AccountDescription( targetId );
        if ( model is null )
        {
            return $"{displayName} runs locally; inference uses {account}";
        }
        else
        {
            return $"{displayName} · {model} · inference uses {account}";
        }
    }

    public static SearchResult ContinueRow( AgentConversationSummary conversation, double score )
    {
        string title;
        if ( conversation.PendingRequestId is null )
        {
            title = "Continue conversation";
        }
        else
        {
            title = AgentBackendIds.DisplayName( conversation.BackendId ) + " is waiting for your answer";
        }

        return new( )
        {
            Id = AgentConversationResultId,
            Kind = ResultKinds.Assistant,
            Title = title,
            Subtitle = conversation.PendingRequestId is null ? $"{conversation.TurnCount} turn(s) · {conversation.State}" : "1 question waiting",
            Icon = AgentIconName,
            ProviderId = ProviderIds.Assistant,
            SourceId = SourceIds.Ai,
            Score = score,
            Section = ResultSections.Assistant,
            Actions =
            [
                new CommandAction { Id = AppActionIds.Open, Title = AppActionTitles.Open, Kind = ActionKinds.AgentOpen }
            ]
        };
    }

    private static string? ExtractPrompt( ParsedQuery query )
    {
        if ( query.IsAiMode )
        {
            return query.Text.Trim( );
        }

        var original = query.Original.TrimStart( );
        if ( original.StartsWith( CodexPromptPrefix, StringComparison.OrdinalIgnoreCase ) )
        {
            return original[CodexPromptPrefix.Length..]
                .Trim( );
        }

        return null;
    }

    private static string ShortName( string backendId ) => backendId switch
    {
        AgentBackendIds.Codex => "codex",
        AgentBackendIds.ClaudeCode => "claude",
        AgentBackendIds.Cursor => "cursor",
        AgentBackendIds.Grok => "grok",
        _ => backendId
    };

    private static string AccountDescription( string backendId ) => backendId switch
    {
        AgentBackendIds.ClaudeCode => "your Anthropic account",
        AgentBackendIds.Cursor => "your Cursor account",
        AgentBackendIds.Grok => "your xAI account",
        _ => "your OpenAI account"
    };

    private static string Truncate( string value ) => value.Length > MaximumPromptTitleCharacters ? value[..MaximumPromptTitleCharacters] + "…" : value;

    private static bool ShouldShowConversationForEmptyPrompt( AgentRowState current, string prompt, string? targetId )
    {
        return targetId is not null && current is { Enabled: true, Conversation: not null } && prompt.Length == 0;
    }
}
