using System.Globalization;
using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const int AgentLabelWidthCharacters = 16;
    private const string AvailableValue = "available";
    private const string MissingValue = "missing";
    private const string TimestampFormat = "O";

    private static void WriteAgentRow( string label, string value ) => Console.WriteLine( $"{label + ":",-AgentLabelWidthCharacters}{Safe( value )}" );

    private static async Task<int> WriteAgentStatusAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var status = await runtime
            .GetAgentStatusAsync( cancellationToken )
            .ConfigureAwait( false );

        // This process never starts Codex, so it resolves the binary the way Doctor
        // does: from this CLI's PATH, which can differ from the daemon's
        // user-manager PATH.
        (var binary, var failure) = await LocateCodexAsync( runtime.Options.Agent, cancellationToken )
            .ConfigureAwait( false );

        status.BinaryPath = binary?.CanonicalPath;
        status.BinaryVersion = binary?.Version;
        status.BinaryReviewed = binary?.Reviewed;

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( status, CommandDeckJsonContext.Default.AgentStatus ) );

            return 0;
        }

        var backend = status.Backend;
        var options = runtime.Options.Agent;
        WriteAgentRow( "Enabled", status.Enabled ? TrueValue : FalseValue );
        WriteAgentRow( "Backend", $"{backend?.Id ?? NoneValue} ({backend?.DisplayName ?? NoneValue})" );
        WriteAgentRow( "Health", backend?.HealthState ?? AgentHealthStates.Disabled );
        WriteAgentRow( "Authentication", status.Login.State );
        WriteAgentRow( "Trust", $"{status.Trust.Level} ({status.Trust.LevelName})" );
        WriteAgentRow( "Trust lease", FormatAgentLease( status.Trust ) );
        WriteAgentRow( "Model", status.Preferences.ModelId ?? "backend default" );
        WriteAgentRow( "Effort", status.Preferences.ReasoningEffort ?? "backend default" );
        WriteAgentRow( "Sandbox", FormatAgentTool( status.SandboxAvailable, options.BubblewrapPath ) );
        WriteAgentRow( "systemd-run", FormatAgentTool( status.TransientUnitAvailable, options.SystemdRunPath ) );
        WriteAgentRow( "Binary", DescribeAgentStatusBinary( binary, failure ) );
        WriteAgentRow( "Helper", FormatAgentHelper( status.Helper ) );
        WriteAgentRow( "Conversation", FormatAgentConversation( status.Conversation ) );
        WriteAgentRow( "Pending", $"{status.PendingRequestCount}" );

        foreach ( var warning in status.Warnings )
        {
            WriteAgentRow( "Warning", warning );
        }

        return 0;
    }

    private static string FormatAgentTool( bool available, string path ) => $"{(available ? AvailableValue : MissingValue)} ({path})";

    private static string FormatAgentLease( AgentTrustState trust )
    {
        if ( trust.LeaseExpiresUnixMilliseconds is { } expires )
        {
            return $"{trust.Duration ?? AgentTrustDurations.UntilDowngrade} until " +
                   DateTimeOffset
                       .FromUnixTimeMilliseconds( expires )
                       .ToString( TimestampFormat, CultureInfo.InvariantCulture );
        }
        else
        {
            return trust.Duration ?? "no lease";
        }
    }

    private static string FormatAgentHelper( AgentHelperStatus helper )
    {
        if ( helper.Installed )
        {
            return $"installed version={helper.Version ?? UnknownValue} " + $"matches={helper.VersionMatches} polkit={helper.PolkitAgentLikely}";
        }
        else
        {
            return helper.Message ?? "not installed";
        }
    }

    private static string FormatAgentConversation( AgentConversationSummary? summary )
    {
        if ( summary is null )
        {
            return NoneValue;
        }
        else
        {
            return $"{summary.ConversationId} state={summary.State} turns={summary.TurnCount} " + $"model={summary.ModelId ?? DefaultValue}";
        }
    }

    private static async Task<int> WriteAgentBackendsAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var inventory = await runtime
            .GetAgentBackendsAsync( cancellationToken )
            .ConfigureAwait( false );

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( inventory, CommandDeckJsonContext.Default.AgentBackendInventory ) );

            return 0;
        }

        WriteAgentRow( "Enabled", inventory.Enabled ? TrueValue : FalseValue );
        WriteAgentRow( "Selected", inventory.SelectedBackendId ?? NoneValue );

        foreach ( var backend in inventory.Backends )
        {
            Console.WriteLine( $"{Safe( backend.Id )}  {Safe( backend.DisplayName )}  {Safe( backend.Kind )}  " +
                               $"{Safe( backend.HealthState )}  {Safe( backend.AuthenticationState )}  " +
                               $"available={backend.Available}" );
        }

        return 0;
    }

    private static async Task<int> WriteAgentModelsAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var inventory = await runtime
            .DiscoverAgentModelsAsync( arguments.GetOption( CliOptionNames.Backend ), arguments.HasFlag( CliOptionNames.Hidden ), cancellationToken )
            .ConfigureAwait( false );

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( inventory, CommandDeckJsonContext.Default.AgentModelInventory ) );

            return inventory.Error is null ? 0 : 1;
        }

        if ( inventory.Error is not null )
        {
            Console.Error.WriteLine( $"{Safe( inventory.Error.Category )}: {Safe( inventory.Error.Summary )}" );

            return 1;
        }

        WriteAgentRow( "Backend", inventory.BackendId );
        WriteAgentRow( "Default model", inventory.DefaultModelId ?? NoneValue );

        foreach ( var model in inventory.Models )
        {
            var efforts = string.Join( ",", model.ReasoningEfforts.Select( static effort => effort.Id ) );

            Console.WriteLine( $"{Safe( model.Id )}  {Safe( model.DisplayName )}  hidden={model.Hidden}  " +
                               $"default={model.IsDefault}  efforts=[{Safe( efforts )}]" );
        }

        return 0;
    }

    private static string DescribeAgentStatusBinary( CodexBinaryInfo? binary, string? failure )
    {
        if ( binary is null )
        {
            return $"not resolved ({failure})";
        }
        else
        {
            return $"{binary.CanonicalPath} {binary.Version}{(binary.Reviewed ? string.Empty : " (unreviewed App Server version)")}";
        }
    }
}
