using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CommandDeck;

public static class Doctor
{
    private const int ReportWidthCharacters = 76;
    private const int CheckNameColumnWidthCharacters = 22;
    private const int RemediationIndentCharacters = 28;
    private const int MaximumProcessesToScan = 4096;

    public static async ValueTask<int> RunAsync( CommandDeckRuntime runtime, string? configPath, CancellationToken cancellationToken )
    {
        List<Check> checks =
        [
            Check.Value( "Operating system", OperatingSystem.IsLinux( ), RuntimeInformation.OSDescription ),
            Check.Value( "Architecture", RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64,
                RuntimeInformation.ProcessArchitecture.ToString( ) ),
            Check.Value( "Native AOT runtime", !RuntimeFeature.IsDynamicCodeSupported, GetRuntimeDescription( ) ),
            Check.Value( "Hyprland session",
                !string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( EnvironmentVariableNames.HyprlandInstanceSignature ) ),
                Environment.GetEnvironmentVariable( EnvironmentVariableNames.HyprlandInstanceSignature ) ?? "not detected" ),
            Check.Command( ToolExecutables.Hyprctl, required: true ),
            Check.Command( ToolExecutables.DesktopOpener, required: true ),
            Check.Command( ToolExecutables.OmarchyShell, required: false ),
            Check.Command( ToolExecutables.ClipboardCopy, required: false )
        ];

        if ( runtime.Options.Search.Calculator.UseQalc is not null )
        {
            // Not required: the warning never changes Doctor's exit code.
            checks.Add( Check.Value( "Calculator configuration", false, "search.calculator.useQalc is deprecated and ignored; remove it", required: false ) );
        }

        if ( runtime.Options.Indexing.EnablePdfExtraction )
        {
            checks.Add( Check.Command( ToolExecutables.PdfToText, required: true ) );
        }

        if ( runtime.Options.Indexing.EnablePandocExtraction )
        {
            checks.Add( Check.Command( ToolExecutables.Pandoc, required: true ) );
        }

        var fts = await runtime
            .Catalog.VerifyFts5Async( cancellationToken )
            .ConfigureAwait( false );

        checks.Add( Check.Value( "SQLite FTS5", fts, fts ? "available" : "unavailable" ) );

        // Optional: never fails Doctor. The PATH-selected client's own status
        // response is authoritative, not package state or /usr/bin/herdr.
        var herdr = await runtime
            .Herdr.GetStatusAsync( cancellationToken )
            .ConfigureAwait( false );

        checks.Add( Check.Value( "Herdr default session", herdr.Availability == HerdrAvailability.Available, $"{herdr.Detail} (PATH-selected herdr client)",
            required: false ) );

        string? resolvedConfig;
        if ( string.IsNullOrWhiteSpace( configPath ) )
        {
            resolvedConfig = PathExpansion.GetDefaultConfigPath( );
        }
        else
        {
            resolvedConfig = PathExpansion.Expand( configPath );
        }

        checks.Add( Check.Value( "Configuration", true, DescribeConfigurationPath( resolvedConfig ) ) );

        var stats = await runtime
            .Catalog.GetStatsAsync( cancellationToken )
            .ConfigureAwait( false );

        checks.Add( Check.Value( "Catalog", true, $"{stats.ItemCount} items; {stats.DatabaseBytes} bytes" ) );

        var indexing = await runtime
            .Indexer.GetIncrementalStatusAsync( cancellationToken )
            .ConfigureAwait( false );

        foreach ( var root in indexing.Roots )
        {
            (var passed, var detail) = DescribeIndexRoot( root );
            checks.Add( Check.Value( $"Index root {root.Id}", passed, detail, required: false ) );
        }

        await AddAssistantChecksAsync( runtime, checks, cancellationToken )
            .ConfigureAwait( false );

        var desktop = await DesktopWiringDoctor
            .CreateDefault( )
            .InspectAsync( cancellationToken )
            .ConfigureAwait( false );

        checks.AddRange( desktop.Select( Check.FromDesktop ) );

        return WriteReport( checks );
    }

    private static int WriteReport( List<Check> checks )
    {
        Console.WriteLine( "CommandDeck doctor" );
        Console.WriteLine( new string( '─', ReportWidthCharacters ) );

        foreach ( var check in checks )
        {
            string? marker;
            if ( check.Passed )
            {
                marker = "OK";
            }
            else
            {
                if ( check.Skipped )
                {
                    marker = "SKIP";
                }
                else
                {
                    if ( check.Required )
                    {
                        marker = "FAIL";
                    }
                    else
                    {
                        marker = "WARN";
                    }
                }
            }

            Console.WriteLine( $"{marker,-5} {check.Name,-CheckNameColumnWidthCharacters} {check.Detail}" );

            if ( NeedsRemediation( check ) )
            {
                Console.WriteLine( $"{string.Empty,-RemediationIndentCharacters} Fix: {check.Remediation}" );
            }
        }

        Console.WriteLine( new string( '─', ReportWidthCharacters ) );
        var failed = checks.Any( static check => (check is { Required: true, Passed: false }) );
        Console.WriteLine( GetChecksSummary( failed ) );

        return failed ? 1 : 0;
    }

    private static async ValueTask AddAssistantChecksAsync( CommandDeckRuntime runtime, List<Check> checks, CancellationToken cancellationToken )
    {
        var agent = runtime.Options.Agent;

        try
        {
            var codex = await CodexBinaryLocator
                .LocateAsync( agent, cancellationToken )
                .ConfigureAwait( false );

            checks.Add( Check.Value( "Codex CLI", true,
                $"codex-cli {codex.Version} at {codex.CanonicalPath}" +
                (codex.Reviewed ? string.Empty : "; unreviewed App Server version admitted by agent.maximumCodex*Version"), required: false ) );
        }
        catch ( AiOperationException exception )
        {
            checks.Add( Check.Value( "Codex CLI", false, exception.SafeSummary ?? "unavailable", required: false ) );
        }

        foreach ( var profile in CliAgentProfile.All )
        {
            try
            {
                var binary = CliAgentBinaryLocator.Locate( profile, profile.BackendId switch
                {
                    AgentBackendIds.ClaudeCode => agent.ClaudeBinaryPath,
                    AgentBackendIds.Cursor => agent.CursorBinaryPath,
                    AgentBackendIds.Grok => agent.GrokBinaryPath,
                    _ => null
                } );

                checks.Add( Check.Value( profile.DisplayName + " CLI", true, binary.CanonicalPath, required: false ) );
            }
            catch ( AiOperationException exception )
            {
                checks.Add( Check.Value( profile.DisplayName + " CLI", false, exception.SafeSummary ?? "unavailable", required: false ) );
            }
        }

        checks.Add( Check.Value( "Bubblewrap", File.Exists( agent.BubblewrapPath ), agent.BubblewrapPath, required: false ) );
        checks.Add( Check.Value( "systemd-run", File.Exists( agent.SystemdRunPath ), agent.SystemdRunPath, required: false ) );

        var credential = new CodexCredentialStore( agent.StateDirectory ).Exists;

        checks.Add( Check.Value( "Assistant credential", credential, credential ? "CommandDeck-owned Codex login present" : "not signed in",
            required: false ) );

        try
        {
            var status = await runtime
                .GetAgentStatusAsync( cancellationToken )
                .ConfigureAwait( false );

            checks.Add( Check.Value( "Package helper", status.Helper.Installed, DescribePackageHelper( status ), required: false ) );
        }
        catch ( Exception exception ) when ( exception is InvalidOperationException or IOException )
        {
            checks.Add( Check.Value( "Package helper", false, "status unavailable", required: false ) );
        }

        var agentProcess = FindPolkitAgentProcess( );

        checks.Add( Check.Value( "Polkit agent", agentProcess is not null, agentProcess ?? "no known authentication agent process found", required: false ) );
    }

    private static string? FindPolkitAgentProcess( )
    {
        string[] known = ["quickshell", "hyprpolkitagent", "polkit-gnome-au", "lxqt-policykit-", "polkit-kde-authe"];

        try
        {
            var scanned = 0;
            foreach ( var directory in Directory.EnumerateDirectories( "/proc" ) )
            {
                if ( ++scanned > MaximumProcessesToScan )
                {
                    break;
                }

                var commPath = Path.Combine( directory, "comm" );
                if ( !File.Exists( commPath ) )
                {
                    continue;
                }

                var name = File
                    .ReadAllText( commPath )
                    .Trim( );

                if ( known.Any( candidate => name.StartsWith( candidate, StringComparison.Ordinal ) ) )
                {
                    return name;
                }
            }
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
        }

        return null;
    }

    // A root the watch limit covers only partly is a warning, like an
    // unwatched one, with the reason and the remedy.
    internal static (bool Passed, string Detail) DescribeIndexRoot( IndexRootStatus root )
    {
        var fullyWatched = IsFullyWatchedRoot( root );
        string? state;
        if ( root.Paused )
        {
            state = "paused";
        }
        else
        {
            if ( fullyWatched )
            {
                state = (root.WatchedDirectoryCount > 0 ? $"watched; {root.WatchedDirectoryCount} directories; " : "watched; ") +
                        $"{root.IndexedItemCount} indexed item(s)";
            }
            else
            {
                state = string.Join( " ", new[] { root.LastError, root.Remediation }.Where( static value => !string.IsNullOrWhiteSpace( value ) ) );
            }
        }

        return (fullyWatched || root.Paused, $"{root.Path}: {state}");
    }

    private sealed class Check
    {
        public required string Name { get; init; }
        public required bool Passed { get; init; }
        public required bool Required { get; init; }
        public required string Detail { get; init; }
        public bool Skipped { get; init; }
        public string? Remediation { get; init; }

        // Desktop wiring is never required: a skip or warning keeps the exit code.
        public static Check FromDesktop( DesktopCheck check ) => new( )
        {
            Name = check.Name,
            Passed = check.State == DesktopCheckState.Passed,
            Skipped = check.State == DesktopCheckState.Skipped,
            Required = false,
            Detail = check.Detail,
            Remediation = check.Remediation
        };

        public static Check Value( string name, bool passed, string detail, bool required = true ) => new( )
        {
            Name = name,
            Passed = passed,
            Detail = detail,
            Required = required
        };

        public static Check Command( string command, bool required )
        {
            var found = ProcessRunner.ExistsOnPath( command );

            return new Check( )
            {
                Name = command,
                Passed = found,
                Required = required,
                Detail = OptionalToolCatalog.DoctorDetail( command, found )
            };
        }
    }

    private static string GetRuntimeDescription( )
    {
        if ( RuntimeFeature.IsDynamicCodeSupported )
        {
            return "JIT build (allowed for development)";
        }
        else
        {
            return "Native AOT";
        }
    }

    private static string DescribeConfigurationPath( string resolvedConfig )
    {
        if ( File.Exists( resolvedConfig ) )
        {
            return resolvedConfig;
        }
        else
        {
            return $"{resolvedConfig} (defaults in use)";
        }
    }

    private static bool NeedsRemediation( Check check )
    {
        return check is { Passed: false, Remediation: not null };
    }

    private static string GetChecksSummary( bool failed )
    {
        if ( failed )
        {
            return "Required checks failed.";
        }
        else
        {
            return "All required checks passed.";
        }
    }

    private static string DescribePackageHelper( AgentStatus status )
    {
        if ( status.Helper.Installed )
        {
            return $"installed, protocol {status.Helper.Version ?? "?"}";
        }
        else
        {
            return status.Helper.Message ?? "not installed";
        }
    }

    private static bool IsFullyWatchedRoot( IndexRootStatus root )
    {
        return root is { Watched: true, UnwatchedDirectoryCount: 0 } && string.IsNullOrEmpty( root.Remediation );
    }
}
