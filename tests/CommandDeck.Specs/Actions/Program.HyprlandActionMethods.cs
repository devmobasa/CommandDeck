namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task HyprlandActionDispatchAsync( )
    {
        var workspace = CreateWorkspace( );
        var window = CreateWindow( );
        List<string[]> calls = [];
        var response = new ProcessResult { ExitCode = 0, StandardOutput = "ok", StandardError = "" };
        LinuxActionExecutor executor = CreateExecutor( calls, ( ) => response );

        foreach ( var (result, action, expected) in CreateDispatchCases( workspace, window ) )
        {
            var before = calls.Count;
            True( (await executor.ExecuteAsync( result, action, "", default )).Succeeded );
            Equal( before + 1, calls.Count );
            Equal( "hyprctl|dispatch|" + expected, string.Join( '|', calls[^1] ) );
        }

        workspace
            .Actions[0]
            .Arguments[HyprlandActionOperations.ArgumentKey] = "named\"\\\n";

        True( (await executor.ExecuteAsync( workspace, workspace.Actions[0], "", default )).Succeeded );
        Equal( """hl.dsp.focus({ workspace = "named\"\\\010" })""", calls[^1][2] );

        workspace
            .Actions[0]
            .Arguments[HyprlandActionOperations.ArgumentKey] = "8";

        foreach ( var failedResponse in CreateFailureResponses( ) )
        {
            response = failedResponse;
            var before = calls.Count;
            var outcome = await executor.ExecuteAsync( window, window.Actions[0], "", default );
            True( !outcome.Succeeded );
            Equal( before + 1, calls.Count );
        }

        var unsupported = HyprlandAction( "unknown", "not-an-operation", "8" );
        var callCount = calls.Count;
        True( !(await executor.ExecuteAsync( workspace, unsupported, "", default )).Succeeded );
        Equal( callCount, calls.Count );

        static LinuxActionExecutor CreateExecutor( List<string[]> calls, Func<ProcessResult> readResponse )
        {
            return new( hyprlandRunner: ( file, arguments, timeout, token ) =>
            {
                token.ThrowIfCancellationRequested( );
                Equal( TimeSpan.FromSeconds( 3 ), timeout );
                calls.Add( [file, .. arguments] );

                return ValueTask.FromResult( readResponse( ) );
            } );
        }

        static SearchResult CreateWorkspace( )
        {
            return new SearchResult
            {
                Id = "workspace:8",
                Kind = ResultKinds.Workspace,
                Title = "Workspace 8",
                ProviderId = ProviderIds.Hyprland,
                SourceId = SourceIds.WindowsWorkspaces,
                Actions = [HyprlandAction( "switch", HyprlandActionOperations.SwitchWorkspace, "8" )]
            };
        }

        static SearchResult CreateWindow( )
        {
            return new SearchResult
            {
                Id = "window:0xabc",
                Kind = ResultKinds.Window,
                Title = "Window",
                ProviderId = ProviderIds.Hyprland,
                SourceId = SourceIds.WindowsWorkspaces,
                Actions =
                [
                    HyprlandAction( "focus", HyprlandActionOperations.FocusWindow, "address:0xabc" ),
                    HyprlandAction( "close", HyprlandActionOperations.CloseWindow, "address:0xabc" )
                ]
            };
        }

        static (SearchResult, CommandAction, string)[] CreateDispatchCases( SearchResult workspace, SearchResult window )
        {
            return new[]
            {
                (workspace, workspace.Actions[0], """hl.dsp.focus({ workspace = "8" })"""),
                (window, window.Actions[0], """hl.dsp.focus({ window = "address:0xabc" })"""),
                (window, window.Actions[1], """hl.dsp.window.close({ window = "address:0xabc" })""")
            };
        }

        static ProcessResult[] CreateFailureResponses( )
        {
            return new[]
            {
                new ProcessResult { ExitCode = 7, StandardOutput = "bad Lua", StandardError = "" },
                new ProcessResult { ExitCode = 0, StandardOutput = "warning: no window", StandardError = "" },
                new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "ok",
                    StandardError = "",
                    TimedOut = true
                },
                new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "ok",
                    StandardError = "",
                    OutputLimitExceeded = true
                },
                new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "ok",
                    StandardError = "truncated",
                    StandardErrorLimitExceeded = true
                }
            };
        }
    }

    private static CommandAction HyprlandAction( string id, string operation, string argument ) => new( )
    {
        Id = id,
        Title = id,
        Kind = ActionKinds.HyprlandDispatch,
        Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
        {
            [HyprlandActionOperations.OperationKey] = operation,
            [HyprlandActionOperations.ArgumentKey] = argument
        }
    };
}
