namespace CommandDeck.Specs;

public static partial class Program
{
    private static void CliAgentProfilesBuildClosedArgvAndParseOutputs( )
    {
        Equal( 3, CliAgentProfile.All.Count );

        True( CliAgentProfile
            .All.Select( static profile => profile.BackendId )
            .SequenceEqual( ["claude.local", "cursor.local", "grok.local"] ) );

        True( CliAgentProfile.All.All( static profile => AgentBackendIds.IsReserved( profile.BackendId ) ) );

        ClaudeCodeProfile claude = new( );

        var claudeArgv = claude.BuildTurnArguments( new CliAgentTurnRequest
        {
            Prompt = "hello",
            ModelId = "sonnet",
            ReasoningEffort = "high",
            ContinuationId = "session-1",
            SystemPrompt = "answer only"
        } );

        True( HasArgumentPair( claudeArgv, "--tools", "" ) && claudeArgv.Contains( "--restricted" ) );
        True( HasArgumentPair( claudeArgv, "--permission-mode", "dontAsk" ) && HasArgumentPair( claudeArgv, "--permission-prompts", "none" ) );
        True( HasArgumentPair( claudeArgv, "--resume", "session-1" ) && HasArgumentPair( claudeArgv, "--model", "sonnet" ) );
        True( HasArgumentPair( claudeArgv, "--append-system-prompt", "answer only" ) && HasArgumentPair( claudeArgv, "--max-turns", "1" ) );
        True( !claudeArgv.Contains( "--bare" ), "bare mode would disable the subscription sign-in" );
        True( !claudeArgv.Contains( "hello" ), "the prompt travels on stdin, never in argv" );
        True( claude is { PromptOnStandardInput: true, RequiresPastedCode: true, LoginMethod: "browser" } );

        Equal( (true, "claude.ai"), claude.ParseStatus( "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"email\":\"x\"}" ) );
        Equal( (false, null), claude.ParseStatus( "{\"loggedIn\":false}" ) );
        Equal( null, claude.ParseStatus( "not json" ) );
        Equal( 4, claude.ParseModels( string.Empty ).Count );

        Equal( "sonnet", claude
            .ParseModels( string.Empty )
            .Single( static model => model.IsDefault ).Id );

        CursorProfile cursor = new( );
        var cursorArgv = cursor.BuildTurnArguments( new CliAgentTurnRequest { Prompt = "review", SystemPrompt = "rules", ContinuationId = "chat-9" } );

        True( HasArgumentPair( cursorArgv, "--mode", "ask" ) && HasArgumentPair( cursorArgv, "--sandbox", "enabled" ) );
        True( HasArgumentPair( cursorArgv, "--resume", "chat-9" ) && cursorArgv[^2] == "--" && cursorArgv[^1] == "rules\n\nreview" );

        Equal( (true, null),
            cursor.ParseStatus( "Cannot open directory /x to load OpenSSL certificates.\n{\"status\":\"authenticated\",\"isAuthenticated\":true}" ) );

        var cursorModels = cursor.ParseModels( "Available models\n\nauto - Auto (default)\ngpt-5.2 - GPT-5.2\nbad line\n" );

        Equal( 2, cursorModels.Count );

        True( cursorModels[0].Id == "auto" && cursorModels[0].IsDefault && cursorModels[0].DisplayName == "Auto" );
        True( cursorModels[1].Id == "gpt-5.2" && !cursorModels[1].IsDefault );
        GrokProfile grok = new( );

        Throws<InvalidOperationException>( ( ) => grok.BuildTurnArguments( new CliAgentTurnRequest { Prompt = "x" } ) );

        var grokArgv = grok.BuildTurnArguments( new CliAgentTurnRequest { Prompt = "x", PromptFilePath = "/run/scratch/prompt.txt", ModelId = "grok-4.6" } );

        True( HasArgumentPair( grokArgv, "--prompt-file", "/run/scratch/prompt.txt" ) && HasArgumentPair( grokArgv, "--tools", "" ) );

        True( grokArgv.Contains( "--disable-web-search" ) &&
              grokArgv.Contains( "--no-subagents" ) &&
              HasArgumentPair( grokArgv, "--permission-mode", "dontAsk" ) );

        True( HasArgumentPair( grokArgv, "--output-format", "streaming-messages-json" ) );

        var grokModels =
            grok.ParseModels( "You are logged in with grok.com.\n\nDefault model: grok-4.6\n\nAvailable models:\n  * grok-4.6 (default)\n  - grok-4.5\n" );

        Equal( 2, grokModels.Count );
        True( grokModels[0].Id == "grok-4.6" && grokModels[0].IsDefault && grokModels[1].Id == "grok-4.5" );
        True( grok is { LoginMethod: "device-code", RequiresPastedCode: false, StatusArguments: null } );
        Throws<InvalidOperationException>( ( ) => CliAgentProfile.AssertSafe( ["--print", "--yolo"] ) );
        Throws<InvalidOperationException>( ( ) => CliAgentProfile.AssertSafe( ["--permission-mode", "bypassPermissions"] ) );
    }

    private static void ClaudeModelsExposeEffortsAndForwardCliArguments( )
    {
        ClaudeCodeProfile claude = new( );
        string[] expectedEfforts = ["low", "medium", "high", "xhigh", "max"];
        var models = claude.ParseModels( string.Empty );
        Equal( 4, models.Count );

        foreach ( var model in models )
        {
            True( model
                    .ReasoningEfforts.Select( static effort => effort.Id )
                    .SequenceEqual( expectedEfforts ), $"Claude model {model.Id} must expose every CLI effort to the picker." );

            foreach ( var effort in expectedEfforts )
            {
                var argv = claude.BuildTurnArguments( new CliAgentTurnRequest { Prompt = "hello", ModelId = model.Id, ReasoningEffort = effort } );

                True( HasArgumentPair( argv, "--model", model.Id ) && HasArgumentPair( argv, "--effort", effort ) );
                Equal( 1, argv.Count( static argument => argument == "--effort" ) );
            }
        }

        foreach ( var effort in new string?[] { null, string.Empty } )
        {
            var argv = claude.BuildTurnArguments( new CliAgentTurnRequest { Prompt = "hello", ReasoningEffort = effort } );

            True( !argv.Contains( "--effort" ), "An unset effort leaves the CLI default unchanged." );
        }
    }

    private static void CliAgentSandboxBindsOnlyTheOwnedHomeAndInstallTree( )
    {
        CliAgentLaunchRequest request = CreateRequest( );
        var sandbox = string.Join( ' ', BubblewrapCliAgentLauncher.BuildArguments( request ) );
        True( sandbox.Contains( "--unshare-all --share-net --die-with-parent", StringComparison.Ordinal ) );

        True( sandbox.Contains( "--tmpfs /home", StringComparison.Ordinal ) &&
              sandbox.Contains( "--bind /home/user/.local/state/commanddeck/claude/home /home/user/.local/state/commanddeck/claude/home",
                  StringComparison.Ordinal ) );

        True( sandbox.Contains( "--ro-bind /home/user/.local/share/claude/versions /home/user/.local/share/claude/versions", StringComparison.Ordinal ) );
        True( sandbox.Contains( "--clearenv --setenv HOME /home/user/.local/state/commanddeck/claude/home", StringComparison.Ordinal ) );

        True( sandbox.Contains( "--setenv NO_OPEN_BROWSER 1", StringComparison.Ordinal ) &&
              sandbox.EndsWith( " -- /home/user/.local/share/claude/versions/2.1.263 auth login", StringComparison.Ordinal ) );

        True( !sandbox.Contains( "/home/user/.claude", StringComparison.Ordinal ) && !sandbox.Contains( "--bind /home/user ", StringComparison.Ordinal ) );

        Throws<InvalidOperationException>( ( ) => BubblewrapCliAgentLauncher.BuildArguments( new CliAgentLaunchRequest
        {
            BinaryPath = "/bin/x",
            InstallPath = "/bin",
            HomePath = "/h",
            WorkingDirectory = "/w",
            Environment = new Dictionary<string, string>( StringComparer.Ordinal ) { ["BAD=KEY"] = "1" }
        } ) );

        static CliAgentLaunchRequest CreateRequest( )
        {
            return new( )
            {
                BinaryPath = "/home/user/.local/share/claude/versions/2.1.263",
                InstallPath = "/home/user/.local/share/claude/versions",
                HomePath = "/home/user/.local/state/commanddeck/claude/home",
                WorkingDirectory = "/run/user/1000/commanddeck/claude/gen",
                Arguments = ["auth", "login"],
                Environment = new Dictionary<string, string>( StringComparer.Ordinal ) { ["NO_OPEN_BROWSER"] = "1" }
            };
        }
    }

    private static void CliAgentStreamReaderMapsMessagesAndCursorFrames( )
    {
        CliAgentStreamReader messages = new( CliAgentStreamFormats.Messages );
        var init = messages.Read( "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\",\"model\":\"m\"}" );

        Equal( "s1", init?.SessionId );

        var delta = messages.Read(
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}}" );

        Equal( "Hel", delta?.Text );

        var thinking = messages.Read(
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hmm\"}}}" );

        Equal( "hmm", thinking?.Reasoning );
        Equal( null, messages.Read( "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}}" ) );

        var result = messages.Read(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Hello\",\"session_id\":\"s1\",\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}" );

        True( result is { Terminal: true, IsError: false, FinalText: null, Usage.TotalTokens: 14 } && messages.SawResult );

        Equal( null, messages.Read( "not json" ) );
        CliAgentStreamReader whole = new( CliAgentStreamFormats.Messages );

        Equal( "Whole", whole.Read( "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Whole\"}]}}" )
            ?.Text );

        var failed = whole.Read(
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"result\":\"Not logged in. Please run /login\"}" );

        True( failed is { Terminal: true, IsError: true, ErrorCategory: "authentication" } );
        True( failed!.ErrorSummary is not null && !failed.ErrorSummary.Contains( "/login", StringComparison.Ordinal ) );

        CliAgentStreamReader maxTurns = new( CliAgentStreamFormats.Messages );
        var limited = maxTurns.Read( "{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":false,\"result\":\"\"}" );

        True( limited is { IsError: true, ErrorCategory: "unavailable" } );
        CliAgentStreamReader fallback = new( CliAgentStreamFormats.Messages );

        Equal( "Final", fallback.Read( "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Final\"}" )
            ?.FinalText );

        CliAgentStreamReader cursor = new( CliAgentStreamFormats.Cursor );

        Equal( "c1", cursor.Read( "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"c1\"}" )
            ?.SessionId );

        Equal( "Hi", cursor.Read( "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hi\"}]},\"timestamp_ms\":1}" )
            ?.Text );

        Equal( null, cursor.Read( "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hi there\"}]},\"model_call_id\":\"m1\"}" ) );

        Equal( "Tool call declined", cursor.Read( "{\"type\":\"tool_call\",\"subtype\":\"started\",\"call_id\":\"t\"}" )
            ?.Status );

        var cursorResult = cursor.Read( "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Hi there\",\"session_id\":\"c1\"}" );

        True( cursorResult is { Terminal: true, IsError: false, FinalText: null } );
    }
}
