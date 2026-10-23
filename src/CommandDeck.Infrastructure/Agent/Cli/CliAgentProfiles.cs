using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandDeck;

public static class CliAgentStreamFormats
{
    // Anthropic Messages wire framing as NDJSON: system/init, stream_event,
    // assistant, user, result. Emitted by Claude Code (`--output-format
    // stream-json`) and Grok (`--output-format streaming-messages-json`).
    public const string Messages = "messages";

    // Cursor's `--output-format stream-json`: system/init, user, assistant,
    // tool_call, result.
    public const string Cursor = "cursor";
}

/// <summary>One turn as the profile needs it to build argv.</summary>
public sealed class CliAgentTurnRequest
{
    public required string Prompt { get; init; }
    public string? ModelId { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ContinuationId { get; init; }
    public string? SystemPrompt { get; init; }
    public string? PromptFilePath { get; init; }
}

/// <summary>
/// What CommandDeck knows about one CLI agent: where to find it, how to sign
/// in, how to list models, and how to run a single answer-only turn inside the
/// sandbox. Every argv is fixed here so no user text can add a flag.
/// </summary>
public abstract class CliAgentProfile
{
    private const int MaximumAsciiCharacter = 0x7f;
    protected const string DefaultModelSuffix = "(default)";

    public abstract string BackendId { get; }
    public abstract string DisplayName { get; }
    public abstract string Kind { get; }
    public abstract string ShortName { get; }
    public abstract string ContractVersion { get; }
    public abstract string AccountDescription { get; }
    public abstract IReadOnlyList<string> ExecutableNames { get; }
    public virtual IReadOnlyList<string> FallbackPaths => [];
    public abstract string LoginMethod { get; }
    public virtual bool RequiresPastedCode => false;
    public abstract IReadOnlyList<string> LoginArguments { get; }
    public virtual IReadOnlyDictionary<string, string> LoginEnvironment { get; } = new Dictionary<string, string>( StringComparer.Ordinal );
    public abstract IReadOnlyList<string> LogoutArguments { get; }

    /// <summary>Command whose output reports the sign-in state; null probes the credential files.</summary>
    public virtual IReadOnlyList<string>? StatusArguments => null;

    /// <summary>Owned-home relative files that hold the sign-in; deleted on sign-out.</summary>
    public abstract IReadOnlyList<string> CredentialFiles { get; }

    public virtual IReadOnlyList<string>? ModelsArguments => null;
    public virtual IReadOnlyList<AgentModelDescriptor> StaticModels => [];
    public abstract string StreamFormat { get; }
    public virtual bool PromptOnStandardInput => false;
    public virtual bool PromptFromFile => false;

    public static IReadOnlyList<string> ProhibitedFlags { get; } =
    [
        "--dangerously-skip-permissions",
        "--allow-dangerously-skip-permissions",
        "bypassPermissions",
        "--yolo",
        "--force",
        "--always-approve",
        "--api-key"
    ];

    public abstract IReadOnlyList<string> BuildTurnArguments( CliAgentTurnRequest request );

    /// <summary>Parses a status probe; null means the output was unusable.</summary>
    public virtual (bool LoggedIn, string? Method)? ParseStatus( string standardOutput ) => null;

    public virtual IReadOnlyList<AgentModelDescriptor> ParseModels( string standardOutput ) => StaticModels;

    public static void AssertSafe( IReadOnlyList<string> argv )
    {
        foreach ( var argument in argv )
        {
            foreach ( var prohibited in ProhibitedFlags )
            {
                if ( argument.Contains( prohibited, StringComparison.OrdinalIgnoreCase ) )
                {
                    throw new InvalidOperationException( "A prohibited CLI agent flag was rejected." );
                }
            }
        }
    }

    public static IReadOnlyList<CliAgentProfile> All { get; } =
    [
        new ClaudeCodeProfile( ),
        new CursorProfile( ),
        new GrokProfile( )
    ];

    protected static AgentModelDescriptor Model( string id, string displayName, bool isDefault = false, string? description = null,
        IReadOnlyList<string>? reasoningEfforts = null ) => new( )
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            IsDefault = isDefault,
            ReasoningEfforts = reasoningEfforts
                               ?.Select( static effort => new AgentReasoningEffort { Id = effort } )
                               .ToList( ) ??
                           []
        };

    protected static IReadOnlyList<AgentModelDescriptor> ParseModelLines( string output, Func<string, AgentModelDescriptor?> parse )
    {
        List<AgentModelDescriptor> models = [];

        foreach ( var raw in output.Split( '\n' ) )
        {
            var line = raw.Trim( );
            if ( ShouldSkipModelLine( models, line ) )
            {
                continue;
            }

            var model = parse( line );
            if ( IsNewModel( models, model ) )
            {
                models.Add( model );
            }
        }

        return models;
    }

    protected static bool IsSafeModelId( string value ) =>
        value.Length is > 0 and <= AgentModelLimits.MaximumIdCharacters &&
        value.All( static character => character <= MaximumAsciiCharacter && !char.IsControl( character ) && !char.IsWhiteSpace( character ) );

    private static bool ShouldSkipModelLine( List<AgentModelDescriptor> models, string line )
    {
        return line.Length == 0 || models.Count >= AgentLimits.MaximumModels;
    }

    private static bool IsNewModel( List<AgentModelDescriptor> models, [NotNullWhen( true )] AgentModelDescriptor? model )
    {
        return model is not null && models.All( value => value.Id != model.Id );
    }
}

public sealed class ClaudeCodeProfile : CliAgentProfile
{
    // Claude CLI --effort choices; leave the default to the CLI/account.
    private static readonly string[] SupportedEfforts = ["low", "medium", "high", "xhigh", "max"];

    public override string BackendId => AgentBackendIds.ClaudeCode;
    public override string DisplayName => "Claude Code";
    public override string Kind => AgentBackendKinds.ClaudeCodeCli;
    public override string ShortName => "claude";
    public override string ContractVersion => "claude-code-stream-json-v1";
    public override string AccountDescription => "your Anthropic account";
    public override IReadOnlyList<string> ExecutableNames => ["claude"];
    public override IReadOnlyList<string> FallbackPaths => ["$HOME/.local/bin/claude"];
    public override string LoginMethod => AgentLoginMethods.Browser;
    public override bool RequiresPastedCode => true;
    public override IReadOnlyList<string> LoginArguments => ["auth", "login"];
    public override IReadOnlyList<string> LogoutArguments => ["auth", "logout"];
    public override IReadOnlyList<string>? StatusArguments => ["auth", "status", "--json"];
    public override IReadOnlyList<string> CredentialFiles => [".claude/.credentials.json"];
    public override string StreamFormat => CliAgentStreamFormats.Messages;
    public override bool PromptOnStandardInput => true;

    // Claude Code has no model-list command; these aliases resolve to the
    // account's current model of each tier at request time.
    public override IReadOnlyList<AgentModelDescriptor> StaticModels { get; } =
    [
        Model( "sonnet", "Sonnet (latest)", isDefault: true, description: "The account's current Sonnet model.", reasoningEfforts: SupportedEfforts ),
        Model( "opus", "Opus (latest)", description: "The account's current Opus model.", reasoningEfforts: SupportedEfforts ),
        Model( "haiku", "Haiku (latest)", description: "The account's current Haiku model.", reasoningEfforts: SupportedEfforts ),
        Model( "fable", "Fable (latest)", description: "The account's current Fable model, when available.", reasoningEfforts: SupportedEfforts )
    ];

    public override IReadOnlyList<string> BuildTurnArguments( CliAgentTurnRequest request )
    {
        ArgumentNullException.ThrowIfNull( request );

        List<string> argv = CreateTurnArguments( );

        if ( request.ModelId is { Length: > 0 } model )
        {
            argv.AddRange( ["--model", model] );
        }

        if ( request.ReasoningEffort is { Length: > 0 } effort )
        {
            argv.AddRange( ["--effort", effort] );
        }

        if ( request.SystemPrompt is { Length: > 0 } system )
        {
            argv.AddRange( ["--append-system-prompt", system] );
        }

        if ( request.ContinuationId is { Length: > 0 } continuation )
        {
            argv.AddRange( ["--resume", continuation] );
        }

        AssertSafe( argv );

        return argv;
    }

    public override (bool LoggedIn, string? Method)? ParseStatus( string standardOutput )
    {
        try
        {
            var node = JsonNode.Parse( standardOutput.Trim( ) );
            if ( node is not JsonObject status )
            {
                return null;
            }

            var loggedIn = ( bool? ) status["loggedIn"] ?? false;
            var method = ( string? ) status["authMethod"];

            return (loggedIn, method);
        }
        catch ( JsonException )
        {
            return null;
        }
    }

    private static List<string> CreateTurnArguments( )
    {
        return
        [
            "--print",
            "--output-format", "stream-json",
            "--input-format", "text",
            "--verbose",
            "--include-partial-messages",

            // Answer-only: no built-in tools, no MCP servers, no settings from a
            // project, and nothing can prompt for permission.
            "--tools", "",
            "--restricted",
            "--strict-mcp-config",
            "--permission-mode", "dontAsk",
            "--permission-prompts", "none",
            "--max-turns", "1"
        ];
    }
}

public sealed class CursorProfile : CliAgentProfile
{
    private const string ModelSeparator = " - ";

    public override string BackendId => AgentBackendIds.Cursor;
    public override string DisplayName => "Cursor";
    public override string Kind => AgentBackendKinds.CursorCli;
    public override string ShortName => "cursor";
    public override string ContractVersion => "cursor-cli-stream-json-v1";
    public override string AccountDescription => "your Cursor account";
    public override IReadOnlyList<string> ExecutableNames => ["cursor-agent"];
    public override IReadOnlyList<string> FallbackPaths => ["$HOME/.local/bin/cursor-agent"];
    public override string LoginMethod => AgentLoginMethods.Browser;
    public override IReadOnlyList<string> LoginArguments => ["login"];

    public override IReadOnlyDictionary<string, string> LoginEnvironment { get; } =
        new Dictionary<string, string>( StringComparer.Ordinal ) { ["NO_OPEN_BROWSER"] = "1" };

    public override IReadOnlyList<string> LogoutArguments => ["logout"];
    public override IReadOnlyList<string>? StatusArguments => ["status", "--format", "json"];
    public override IReadOnlyList<string> CredentialFiles => [".config/cursor-agent", ".cursor/auth.json", ".config/cursor/auth.json"];
    public override IReadOnlyList<string>? ModelsArguments => ["models"];
    public override string StreamFormat => CliAgentStreamFormats.Cursor;

    public override IReadOnlyList<string> BuildTurnArguments( CliAgentTurnRequest request )
    {
        ArgumentNullException.ThrowIfNull( request );

        List<string> argv = CreateTurnArguments( );

        if ( request.ModelId is { Length: > 0 } model )
        {
            argv.AddRange( ["--model", model] );
        }

        if ( request.ContinuationId is { Length: > 0 } continuation )
        {
            argv.AddRange( ["--resume", continuation] );
        }

        // Cursor takes the prompt as a positional argument; a system prompt is
        // folded in front of it because the CLI has no flag for one.
        var prompt = CombinePrompts( request );
        argv.AddRange( ["--", prompt] );
        AssertSafe( argv );

        return argv;
    }

    public override (bool LoggedIn, string? Method)? ParseStatus( string standardOutput )
    {
        // The CLI prints a certificate-store warning before the JSON on some hosts.
        var start = standardOutput.IndexOf( '{' );
        if ( start < 0 )
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse( standardOutput[start..]
                .Trim( ) );

            if ( node is not JsonObject status )
            {
                return null;
            }

            var authenticated = IsAuthenticated( status );

            return (authenticated, null);
        }
        catch ( JsonException )
        {
            return null;
        }
    }

    public override IReadOnlyList<AgentModelDescriptor> ParseModels( string standardOutput ) =>
        ParseModelLines( standardOutput, ParseModelLine );

    private static List<string> CreateTurnArguments( )
    {
        return
        [
            "--print",
            "--output-format", "stream-json",
            "--stream-partial-output",

            // Ask mode is read-only Q&A; the sandbox hides the real home anyway.
            "--mode", "ask",
            "--sandbox", "enabled"
        ];
    }

    private static string CombinePrompts( CliAgentTurnRequest request )
    {
        if ( request.SystemPrompt is { Length: > 0 } system )
        {
            return system + "\n\n" + request.Prompt;
        }

        return request.Prompt;
    }

    private static bool IsAuthenticated( JsonObject status )
    {
        return ( bool? ) status["isAuthenticated"] ?? string.Equals( ( string? ) status["status"], "authenticated", StringComparison.Ordinal );
    }

    private static AgentModelDescriptor? ParseModelLine( string line )
    {
        // "gpt-5.2 - GPT-5.2" or "auto - Auto (default)".
        var separator = line.IndexOf( ModelSeparator, StringComparison.Ordinal );
        if ( separator <= 0 )
        {
            return null;
        }

        var id = line[..separator]
            .Trim( );

        var display = line[(separator + ModelSeparator.Length)..]
            .Trim( );

        if ( !IsSafeModelId( id ) )
        {
            return null;
        }

        var isDefault = display.EndsWith( DefaultModelSuffix, StringComparison.Ordinal );
        if ( isDefault )
        {
            display = display[..^DefaultModelSuffix.Length]
                .Trim( );
        }

        return Model( id, display, isDefault );
    }
}

public sealed class GrokProfile : CliAgentProfile
{
    private const string AsteriskModelPrefix = "* ";
    private const string DashModelPrefix = "- ";

    public override string BackendId => AgentBackendIds.Grok;
    public override string DisplayName => "Grok";
    public override string Kind => AgentBackendKinds.GrokCli;
    public override string ShortName => "grok";
    public override string ContractVersion => "grok-cli-streaming-messages-json-v1";
    public override string AccountDescription => "your xAI account";
    public override IReadOnlyList<string> ExecutableNames => ["grok"];
    public override IReadOnlyList<string> FallbackPaths => ["$HOME/.grok/bin/grok"];
    public override string LoginMethod => AgentLoginMethods.DeviceCode;
    public override IReadOnlyList<string> LoginArguments => ["login", "--device-auth"];
    public override IReadOnlyList<string> LogoutArguments => ["logout"];
    public override IReadOnlyList<string> CredentialFiles => [".grok/auth.json"];
    public override IReadOnlyList<string>? ModelsArguments => ["models"];
    public override string StreamFormat => CliAgentStreamFormats.Messages;
    public override bool PromptFromFile => true;

    public override IReadOnlyList<string> BuildTurnArguments( CliAgentTurnRequest request )
    {
        ArgumentNullException.ThrowIfNull( request );

        if ( string.IsNullOrEmpty( request.PromptFilePath ) )
        {
            throw new InvalidOperationException( "Grok turns read the prompt from a private file." );
        }

        List<string> argv = CreateTurnArguments( request.PromptFilePath );

        if ( request.ModelId is { Length: > 0 } model )
        {
            argv.AddRange( ["--model", model] );
        }

        if ( request.ReasoningEffort is { Length: > 0 } effort )
        {
            argv.AddRange( ["--reasoning-effort", effort] );
        }

        if ( request.SystemPrompt is { Length: > 0 } system )
        {
            argv.AddRange( ["--rules", system] );
        }

        if ( request.ContinuationId is { Length: > 0 } continuation )
        {
            argv.AddRange( ["--resume", continuation] );
        }

        AssertSafe( argv );

        return argv;
    }

    public override IReadOnlyList<AgentModelDescriptor> ParseModels( string standardOutput ) =>
        ParseModelLines( standardOutput, ParseModelLine );

    private static List<string> CreateTurnArguments( string promptFilePath )
    {
        return
        [
            "--prompt-file", promptFilePath,
            "--output-format", "streaming-messages-json",
            "--include-partial-messages",
            "--tools", "",
            "--disallowed-tools", "Agent",
            "--permission-mode", "dontAsk",
            "--no-subagents",
            "--disable-web-search",
            "--no-plan",
            "--max-turns", "1",
            "--verbatim"
        ];
    }

    private static bool HasNoModelBullet( string line )
    {
        return !line.StartsWith( AsteriskModelPrefix, StringComparison.Ordinal ) && !line.StartsWith( DashModelPrefix, StringComparison.Ordinal );
    }

    private static AgentModelDescriptor? ParseModelLine( string line )
    {
        // "  * grok-4.6 (default)" or "  - grok-4.5".
        if ( HasNoModelBullet( line ) )
        {
            return null;
        }

        var prefix = line.StartsWith( AsteriskModelPrefix, StringComparison.Ordinal ) ? AsteriskModelPrefix : DashModelPrefix;
        var body = line[prefix.Length..]
            .Trim( );

        var isDefault = body.EndsWith( DefaultModelSuffix, StringComparison.Ordinal );
        var id = (isDefault ? body[..^DefaultModelSuffix.Length] : body).Trim( );

        return IsSafeModelId( id ) ? Model( id, id, isDefault ) : null;
    }
}
