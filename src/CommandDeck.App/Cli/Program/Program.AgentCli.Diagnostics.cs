namespace CommandDeck;

public static partial class Program
{
    private const string UnavailableValue = "unavailable";

    private static async Task<int> RunAgentProbeAsync( CommandDeckRuntime runtime, IReadOnlyList<string> positionals, CancellationToken cancellationToken )
    {
        var action = ReadCliAction( positionals, 1, AgentCliActions.Tools );
        if ( action != AgentCliActions.Tools )
        {
            Console.Error.WriteLine( "Use 'agent probe tools'." );

            return 2;
        }

        var preferences = await runtime
            .GetAgentPreferencesAsync( cancellationToken )
            .ConfigureAwait( false );

        var level = preferences.TrustLevel;
        WriteAgentRow( "Trust level", $"{level} ({AgentTrustLevels.Name( level )})" );
        Console.WriteLine( "Tools:" );

        foreach ( var tool in AgentToolCatalog.ForTrustLevel( level, packagesAvailable: true ) )
        {
            Console.WriteLine( $"  {Safe( tool.Name )}" );
        }

        (var binary, var failure) = await LocateCodexAsync( runtime.Options.Agent, cancellationToken )
            .ConfigureAwait( false );

        WriteAgentSandboxProbe( runtime.Options.Agent, level, binary );
        Console.WriteLine( "config.toml:" );
        var configuration = CodexRuntimeHome.BuildConfiguration( level, preferences.WebSearchEnabled );
        foreach ( var line in configuration.Split( '\n' ) )
        {
            if ( line.Length > 0 )
            {
                Console.WriteLine( $"  {Safe( line )}" );
            }
        }

        WriteAgentRow( "Codex binary", DescribeAgentBinary( binary, failure ) );

        return 0;
    }

    private static async ValueTask<(CodexBinaryInfo? Binary, string? Failure)> LocateCodexAsync( AgentOptions options, CancellationToken cancellationToken )
    {
        try
        {
            return (await CodexBinaryLocator
                .LocateAsync( options, cancellationToken )
                .ConfigureAwait( false ), null);
        }
        catch ( AiOperationException exception )
        {
            return (null, exception.SafeSummary);
        }
    }

    private static void WriteAgentSandboxProbe( AgentOptions options, int trustLevel, CodexBinaryInfo? binary )
    {
        var generation = Guid
            .NewGuid( )
            .ToString( AppFormats.CompactGuid );

        var home = Path.Combine( PathExpansion.Expand( options.RuntimeDirectory ), "codex", generation );

        var argv = BubblewrapCodexLauncher.BuildArguments( new CodexLaunchRequest
        {
            BinaryPath = binary?.CanonicalPath ?? "/usr/bin/codex",
            RuntimeHome = home,
            Generation = generation,
            TrustLevel = trustLevel,
            WorkingDirectory = home
        } );

        WriteAgentRow( "Runtime home", home );
        WriteAgentRow( "Sandbox", options.BubblewrapPath );
        Console.WriteLine( "Bubblewrap argv:" );

        foreach ( var argument in argv )
        {
            Console.WriteLine( $"  {Safe( argument )}" );
        }
    }

    private static string DescribeAgentBinary( CodexBinaryInfo? binary, string? failure )
    {
        if ( binary is null )
        {
            return failure ?? UnavailableValue;
        }
        else
        {
            return $"{binary.CanonicalPath} version {binary.Version}";
        }
    }
}
