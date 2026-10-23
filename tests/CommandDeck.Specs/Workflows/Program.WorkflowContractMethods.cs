using System.Text.Json.Nodes;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ToolRoutingUsesLiveAdmissionAsync( )
    {
        var active = true;
        var level = AgentTrustLevels.Packages;
        var calls = 0;

        ValueTask<AgentToolResult> Effect( JsonObject arguments, CancellationToken token )
        {
            calls++;

            return ValueTask.FromResult( AgentToolResult.Ok( "fixture" ) );
        }

        AdmittedAgentTools tools = CreateTools( minimum => active && level >= minimum, Effect );

        foreach ( var (name, minimum) in CreateToolAdmissionCases( ) )
        {
            level = minimum;
            True( (await AgentToolRouter.DispatchAsync( name, [], tools, default )).Success );
            var admitted = calls;
            level = minimum - 1;
            True( !(await AgentToolRouter.DispatchAsync( name, [], tools, default )).Success );
            level = AgentTrustLevels.Packages;
            active = false;
            True( !(await AgentToolRouter.DispatchAsync( name, [], tools, default )).Success );
            Equal( admitted, calls );
            active = true;
        }

        True( !(await AgentToolRouter.DispatchAsync( "invented", [], tools, default )).Success );

        static AdmittedAgentTools CreateTools( Func<int, bool> tryAdmit, Func<JsonObject, CancellationToken, ValueTask<AgentToolResult>> effect )
        {
            return new( )
            {
                TryAdmit = tryAdmit,
                Search = effect,
                Invoke = effect,
                Context = ( _, arguments, token ) => effect( arguments, token ),
                Command = effect,
                PackageSearch = effect,
                PackageChange = effect
            };
        }

        static (string, int)[] CreateToolAdmissionCases( )
        {
            return new[]
            {
                (AgentToolNames.Search, AgentTrustLevels.Launcher), (AgentToolNames.Invoke, AgentTrustLevels.Launcher),
                (AgentToolNames.ReadFile, AgentTrustLevels.ApprovedContext), (AgentToolNames.RunCommand, AgentTrustLevels.ConfirmedCommands),
                (AgentToolNames.SearchPackages, AgentTrustLevels.Packages), (AgentToolNames.ProposePackageChange, AgentTrustLevels.Packages)
            };
        }
    }

    private static async Task EditorPayloadRejectsInvalidArgumentsAsync( )
    {
        var original = new EditorActionPayload( "/tmp/literal {line}-{column}-{key}-{path} ; $(touch nope)", 12, 3, "key" );
        var decoded = EditorActionPayload.Read( original.ToArguments( ) );
        Equal( original.Path, decoded.Path );
        Equal( 12, decoded.Line );
        Equal( 3, decoded.Column );
        Equal( "key", decoded.Key );
        Throws<InvalidOperationException>( ( ) => _ = new EditorActionPayload( "", 1, 1 ) );
        Throws<InvalidOperationException>( ( ) => _ = new EditorActionPayload( "/tmp/test", 0, 1 ) );
        LinuxActionExecutor executor = new( );
        foreach ( var invalid in new[] { "-1", "0", "1;touch nope", "999999999999" } )
        {
            var arguments = original.ToArguments( );
            arguments["line"] = invalid;

            var result = await executor.ExecuteAsync( CacheResult( "editor" ), new CommandAction
            {
                Id = "edit",
                Title = "Edit",
                Kind = ActionKinds.EditAtLine,
                Arguments = arguments
            }, "", default );

            True( !result.Succeeded );
            True( result.Message!.Contains( "positive integer", StringComparison.Ordinal ) );
        }

        await VerifyEnvironmentEditorsAsync( original );
    }

    private static async Task VerifyEnvironmentEditorsAsync( EditorActionPayload location )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-environment-editors-" ).FullName;
        var capture = Path.Combine( root, "argv" );
        var visual = Environment.GetEnvironmentVariable( "VISUAL" );
        var editor = Environment.GetEnvironmentVariable( "EDITOR" );
        try
        {
            foreach ( var name in new[] { "code", "codium", "nvim", "vim", "vi", "other" } )
            {
                var executable = Path.Combine( root, name );
                await File.WriteAllTextAsync( executable, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{capture}'\n" );

                File.SetUnixFileMode( executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
                Environment.SetEnvironmentVariable( "VISUAL", executable + " --existing" );
                Environment.SetEnvironmentVariable( "EDITOR", "/does-not-exist" );

                var action = new CommandAction
                {
                    Id = "edit",
                    Title = "Edit",
                    Kind = ActionKinds.EditAtLine,
                    Arguments = location.ToArguments( )
                };

                LinuxActionExecutor executor = new( );
                var result = await executor.ExecuteAsync( CacheResult( "editor" ), action, "", default );
                True( result.Succeeded, result.Message );

                var expected = name switch
                {
                    "code" or "codium" => new[] { "--existing", "--goto", location.Path + ":12:3" },
                    "nvim" or "vim" or "vi" => new[] { "--existing", "+call cursor(12,3)", location.Path },
                    _ => new[] { "--existing", "+12", location.Path }
                };

                var actual = await File.ReadAllLinesAsync( capture );
                True( expected.SequenceEqual( actual, StringComparer.Ordinal ) );
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable( "VISUAL", visual );
            Environment.SetEnvironmentVariable( "EDITOR", editor );
            Directory.Delete( root, recursive: true );
        }
    }
}
