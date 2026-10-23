namespace CommandDeck.Specs;

public static partial class Program
{
    private static void AgentContractsEnforceTrustAndPreferenceBounds( )
    {
        Equal( "line", AgentToolFields.Line );
        Equal( "subtitle", AgentToolFields.Subtitle );
        Equal( "actions", AgentToolFields.Actions );
        Equal( "copy", AiOutputActions.Copy );
        Equal( "save", AiOutputActions.Save );
        Equal( "open", AiOutputActions.Open );

        True( AgentTrustLevels.IsValid( 0 ) && AgentTrustLevels.IsValid( 6 ) && !AgentTrustLevels.IsValid( 7 ) );

        Equal( "Unrestricted", AgentTrustLevels.Name( 6 ) );
        Equal( "Approved context", AgentTrustLevels.Name( 2 ) );

        Throws<InvalidOperationException>( ( ) => AgentContractValidator.ValidatePreferences( new AgentPreferences { TrustLevel = 9 } ) );
        Throws<InvalidOperationException>( ( ) => AgentContractValidator.ValidatePreferences( new AgentPreferences { ConversationTtlMinutes = 999_999 } ) );
        Throws<InvalidOperationException>( ( ) => AgentContractValidator.ValidatePreferences( new AgentPreferences { TurnTimeoutMinutes = 1 } ) );
        Throws<InvalidOperationException>( ( ) => AgentContractValidator.ValidatePreferences( new AgentPreferences { ContextPolicy = "everything" } ) );

        AgentContractValidator.ValidatePreferences( AgentPreferences.CreateDefault( ) );
        Throws<InvalidOperationException>( ( ) => AgentContractValidator.ValidatePromptText( "   " ) );
        Throws<InvalidOperationException>( ( ) => AgentContractValidator.ValidatePromptText( new string( 'x', AgentLimits.MaximumPromptBytes + 1 ) ) );
        Throws<InvalidOperationException>( ( ) => AgentContractValidator.ValidateArgv( [] ) );
        Throws<InvalidOperationException>( ( ) => AgentContractValidator.ValidateArgv( ["ls", "a" + ( char ) 0 + "b"] ) );

        var names = AgentContractValidator.NormalizePackageNames( ["ripgrep", "fd", "ripgrep"] );
        Equal( "fd|ripgrep", string.Join( '|', names ) );

        Throws<InvalidOperationException>( ( ) => AgentContractValidator.NormalizePackageNames( ["--flag"] ) );
        Throws<InvalidOperationException>( ( ) => AgentContractValidator.NormalizePackageNames( ["a/b"] ) );

        Throws<InvalidOperationException>( ( ) => AgentContractValidator.NormalizePackageNames( Enumerable
            .Range( 0, 17 )
            .Select( index => "pkg" + index )
            .ToArray( ) ) );

        var unrestricted = new AgentPreferences { Enabled = true, WebSearchEnabled = true, AurEnabled = true }.WithTrust( AgentTrustLevels.Unrestricted,
            AgentTrustDurations.UntilDowngrade, null );

        Equal( 6, unrestricted.TrustLevel );
        var cleared = unrestricted.WithAuthorityCleared( );
        Equal( 2, cleared.TrustLevel );

        True( cleared is { WebSearchEnabled: false, AurEnabled: false, TrustDuration: null, Enabled: true } );
    }

    private static void AgentRuntimeHomeAndSandboxArgumentsStayClosed( )
    {
        var configuration = CodexRuntimeHome.BuildConfiguration( AgentTrustLevels.ApprovedContext, webSearch: false );

        True( configuration.Contains( "persistence = \"none\"", StringComparison.Ordinal ) );
        True( configuration.Contains( "web_search = \"disabled\"", StringComparison.Ordinal ) );
        True( configuration.Contains( "[analytics]\nenabled = false", StringComparison.Ordinal ) );
        True( configuration.Contains( "shell_tool = false", StringComparison.Ordinal ) );
        True( configuration.Contains( "browser_use = false", StringComparison.Ordinal ) );

        var unrestricted = CodexRuntimeHome.BuildConfiguration( AgentTrustLevels.Unrestricted, webSearch: true );

        True( !unrestricted.Contains( "[features]", StringComparison.Ordinal ) );
        True( unrestricted.Contains( "web_search = \"live\"", StringComparison.Ordinal ) );

        var commandTrust = CodexRuntimeHome.BuildConfiguration( AgentTrustLevels.Packages, webSearch: true );

        True( commandTrust.Contains( "web_search = \"disabled\"", StringComparison.Ordinal ) );
        CodexLaunchRequest request = CreateRequest( );
        var arguments = BubblewrapCodexLauncher.BuildArguments( request );
        var joined = string.Join( ' ', arguments );

        True( joined.Contains( "--unshare-all --share-net --die-with-parent --new-session", StringComparison.Ordinal ) );
        True( joined.Contains( "--tmpfs /home", StringComparison.Ordinal ) );
        True( joined.Contains( "--tmpfs /run", StringComparison.Ordinal ) );
        True( joined.Contains( "--bind /run/user/1000/commanddeck/codex/gen /run/user/1000/commanddeck/codex/gen", StringComparison.Ordinal ) );
        True( joined.Contains( "--ro-bind /opt/codex/bin/codex /opt/commanddeck/codex", StringComparison.Ordinal ) );
        True( joined.Contains( "--clearenv", StringComparison.Ordinal ) );
        True( joined.Contains(
            "--setenv XDG_RUNTIME_DIR /run/user/1000/commanddeck/codex/gen/xdg " +
            "--setenv XDG_CONFIG_HOME /run/user/1000/commanddeck/codex/gen/xdg/config " +
            "--setenv XDG_CACHE_HOME /run/user/1000/commanddeck/codex/gen/xdg/cache " +
            "--setenv XDG_DATA_HOME /run/user/1000/commanddeck/codex/gen/xdg/data " +
            "--setenv XDG_STATE_HOME /run/user/1000/commanddeck/codex/gen/xdg/state", StringComparison.Ordinal ) );

        // Code Mode is left enabled (the gpt-5.6-* models require it); its tool
        // calls stay mediated through item/tool/call. The launch is unchanged.
        True( joined.EndsWith( "-- /opt/commanddeck/codex app-server --stdio --strict-config", StringComparison.Ordinal ) );

        foreach ( var prohibited in CodexArgv.ProhibitedFlags )
        {
            True( !joined.Contains( prohibited, StringComparison.OrdinalIgnoreCase ) );
        }

        Throws<InvalidOperationException>( ( ) => CodexArgv.AssertSafe( ["app-server", "--yolo"] ) );
        Throws<InvalidOperationException>( ( ) => CodexArgv.AssertSafe( ["--dangerously-bypass-approvals-and-sandbox"] ) );

        Equal( "item/tool/call", CodexRpcTokens.ItemToolCall );
        Equal( "account/login/start", CodexRpcTokens.AccountLoginStart );
        Equal( "turn/completed", CodexRpcTokens.TurnCompleted );
        Equal( "codexErrorInfo", CodexRpcTokens.CodexErrorInfo );
        CodexAppServerContract.Validate( );
        Equal( "0.152.1", CodexAppServerContract.ReviewedVersion );
        Equal( 64, CodexAppServerContract.ReviewedSchemaDigest.Length );

        var transient = TransientUnitCodexLauncher.BuildArguments( new CodexLaunchRequest
        {
            BinaryPath = "/opt/codex/bin/codex",
            RuntimeHome = "/run/user/1000/commanddeck/codex/l6",
            Generation = "l6",
            TrustLevel = AgentTrustLevels.Unrestricted,
            WorkingDirectory = "/home/user",
            SocketPath = "/run/user/1000/commanddeck/codex/l6/app-server.sock"
        } );

        var transientJoined = string.Join( ' ', transient );

        True( transientJoined.StartsWith( "--user --unit=commanddeck-codex-l6 --collect --quiet", StringComparison.Ordinal ) );
        True( transientJoined.Contains( "--listen unix:///run/user/1000/commanddeck/codex/l6/app-server.sock", StringComparison.Ordinal ) );
        True( !transientJoined.Contains( "yolo", StringComparison.OrdinalIgnoreCase ) );

        static CodexLaunchRequest CreateRequest( )
        {
            return new( )
            {
                BinaryPath = "/opt/codex/bin/codex",
                RuntimeHome = "/run/user/1000/commanddeck/codex/gen",
                Generation = "gen",
                TrustLevel = AgentTrustLevels.ApprovedContext,
                WorkingDirectory = "/run/user/1000/commanddeck/codex/gen"
            };
        }
    }

    private static async Task AgentUnifiedRoutingUsesOnlyTheSelectedBackendAsync( )
    {
        AiOptions options = CreateOptions( );
        AiCommandProvider answerOnly = new( options, ( ) => "cloud-two" );

        AgentCommandProvider codexNotSelected = new( ( ) =>
            new AgentRowState( Enabled: true, SelectedBackendId: null, Authenticated: true, Conversation: null, ModelId: null ) );

        SearchRequest request = new( ) { Query = "ai: hello", Limit = 8 };
        var ai = QueryParser.Parse( request.Query );
        Equal( "assistant", ai.ProviderFilter );
        var answerRows = await answerOnly.SearchAsync( ai, request, default );
        Equal( 1, answerRows.Count );
        Equal( "ai:cloud-two", answerRows[0].Id );
        Equal( 0, (await codexNotSelected.SearchAsync( ai, request, default )).Count );
        var explicitCodex = QueryParser.Parse( "codex: hello" );
        Equal( 0, (await answerOnly.SearchAsync( explicitCodex, request, default )).Count );
        Equal( "agent:codex", (await codexNotSelected.SearchAsync( explicitCodex, request, default )).Single( ).Id );

        var spaceCodex = QueryParser.Parse( "codex hello" );
        Equal( "assistant", spaceCodex.ProviderFilter );
        Equal( "hello", spaceCodex.Text );
        Equal( "agent:codex", (await codexNotSelected.SearchAsync( spaceCodex, request, default )).Single( ).Id );

        AgentCommandProvider codexSelected = new( ( ) => new AgentRowState(
            Enabled: true, SelectedBackendId: AgentBackendIds.Codex, Authenticated: true, Conversation: null, ModelId: "selected" ) );

        var selectedRow = (await codexSelected.SearchAsync( ai, request, default )).Single( );
        Equal( "agent:codex", selectedRow.Id );
        Equal( "Ask AI: hello", selectedRow.Title );

        True( selectedRow.Subtitle?.Contains( "Codex", StringComparison.Ordinal ) == true, "the subtitle still identifies the selected backend" );

        Equal( "codex.local", selectedRow
            .Actions.Single( )
            .Arguments["backendId"] );

        Equal( "Ask AI", (await codexSelected.SearchAsync( QueryParser.Parse( "ai:" ), request, default )).Single( ).Title );
        Equal( 0, (await new AiCommandProvider( options, ( ) => null ).SearchAsync( ai, request, default )).Count );
        Equal( 0, (await codexSelected.SearchAsync( QueryParser.Parse( "ordinary search" ), request, default )).Count );

        static AiOptions CreateOptions( )
        {
            return new( )
            {
                Enabled = true,
                Providers =
                [
                    new AiProviderOptions
                    {
                        Id = "local-one",
                        Kind = AiAdapterKinds.Ollama,
                        Model = "one",
                        BaseUrl = "http://127.0.0.1:11434",
                        Enabled = true
                    },
                    new AiProviderOptions
                    {
                        Id = "cloud-two",
                        Kind = "openai",
                        Model = "two",
                        BaseUrl = "https://example.invalid/v1",
                        Enabled = true
                    }
                ]
            };
        }
    }

    private static void UnknownPrefixesAndPromptsAreNotSearchErrors( )
    {
        var typing = QueryParser.Parse( "a:" );

        True( typing is { Expression.IncludeFilterIds.Count: 0, Expression.Clauses.Count: 1 } && typing.Expression.Clauses[0].Text == "a",
            "an unknown prefix typed with a trailing colon is plain text" );

        var note = QueryParser.Parse( "note: buy milk" );

        True( note
            .Expression.Clauses.Select( static clause => clause.Text )
            .SequenceEqual( ["note", "buy", "milk"] ), "unknown prefixes never raise an error" );

        var field = QueryParser.Parse( "title:" );
        True( field.Expression.Clauses.Count == 0, "an incomplete field clause is ignored while typing" );
        var app = QueryParser.Parse( "app:" );
        True( app.HasExplicitFilter, "known filters still resolve" );

        ProtocolRequestValidator.Validate( new ProtocolRequest
        {
            RequestId = 1,
            Operation = ProtocolOperations.AgentTurnStart,
            Query = "Reply with exactly the single word: pong"
        } );

        ProtocolRequestValidator.Validate( new ProtocolRequest
        {
            RequestId = 2,
            Operation = ProtocolOperations.PackageSearch,
            Query = "editor: vim",
            Limit = 8
        } );
    }

    private static async Task<string> WriteFakeCodexAsync( string root, string version )
    {
        var path = Path.Combine( root, $"codex-{version}" );
        await File.WriteAllTextAsync( path, $"#!/bin/sh\necho 'codex-cli {version}'\n" );

        if ( OperatingSystem.IsLinux( ) )
        {
            File.SetUnixFileMode( path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
        }

        return path;
    }

    private static async Task CodexVersionRangeAdmitsTheZeroLineButRejectsAMajorBumpAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.PathFor( $"commanddeck-codex-version-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            // The default range spans the 0.x line above the reviewed version:
            // a routine Codex update runs, but is marked unreviewed.
            var admitted = await CodexBinaryLocator.LocateAsync( new AgentOptions { CodexBinaryPath = await WriteFakeCodexAsync( root, "0.153.0" ) }, default );

            True( admitted is { Version: "0.153.0", Reviewed: false }, "the default range admits a newer 0.x version but marks it unreviewed" );

            // The reviewed version is admitted and marked reviewed.
            var reviewed = await CodexBinaryLocator.LocateAsync(
                new AgentOptions { CodexBinaryPath = await WriteFakeCodexAsync( root, CodexAppServerContract.ReviewedVersion ) }, default );

            True( reviewed.Reviewed, "the reviewed version is marked reviewed" );

            // A 1.0 major bump falls outside the default range and is rejected
            // so its App Server schema can be reviewed first.
            await ThrowsAsync<AiOperationException>( async ( ) =>
                await CodexBinaryLocator.LocateAsync( new AgentOptions { CodexBinaryPath = await WriteFakeCodexAsync( root, "1.0.0" ) }, default ) );

            // A version below the floor is rejected: it predates the protocol.
            await ThrowsAsync<AiOperationException>( async ( ) =>
                await CodexBinaryLocator.LocateAsync( new AgentOptions { CodexBinaryPath = await WriteFakeCodexAsync( root, "0.151.9" ) }, default ) );

            // An operator can still tighten the ceiling by configuration.
            await ThrowsAsync<AiOperationException>( async ( ) => await CodexBinaryLocator.LocateAsync(
                new AgentOptions
                {
                    CodexBinaryPath = await WriteFakeCodexAsync( root, "0.153.0" ),
                    MaximumCodexMinorVersion = 152,
                    MaximumCodexPatchVersion = 1
                }, default ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
