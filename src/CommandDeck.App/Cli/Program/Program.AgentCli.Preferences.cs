using System.Globalization;
using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const string TrueValue = "true";
    private const string FalseValue = "false";
    private const string DefaultValue = "default";

    private static async Task<int> RunAgentPreferencesAsync( CommandDeckRuntime runtime, CliArguments arguments, IReadOnlyList<string> positionals,
        CancellationToken cancellationToken )
    {
        var action = ReadCliAction( positionals, 1, AgentCliActions.Show );
        if ( action is not (AgentCliActions.Show or AgentCliActions.Set) )
        {
            Console.Error.WriteLine( "Use 'agent preferences show' or 'agent preferences set'." );

            return 2;
        }

        var current = await runtime
            .GetAgentPreferencesAsync( cancellationToken )
            .ConfigureAwait( false );

        if ( action == AgentCliActions.Set )
        {
            current = await runtime
                .SetAgentPreferencesAsync( BuildAgentPreferences( current, arguments ), cancellationToken )
                .ConfigureAwait( false );
        }

        return WriteAgentPreferences( current, arguments.HasFlag( CliOptionNames.Json ) );
    }

    private static AgentPreferences BuildAgentPreferences( AgentPreferences current, CliArguments arguments ) => new( )
    {
        Enabled = ReadAgentBoolean( arguments, CliOptionNames.Enabled, current.Enabled ),
        BackendId = arguments.GetOption( CliOptionNames.Backend ) ?? current.BackendId,
        ModelId = arguments.GetOption( CliOptionNames.Model ) ?? current.ModelId,
        ReasoningEffort = arguments.GetOption( CliOptionNames.Effort ) ?? current.ReasoningEffort,
        TrustLevel = current.TrustLevel,
        ContextPolicy = arguments.GetOption( CliOptionNames.Context ) ?? current.ContextPolicy,
        ShowHiddenModels = ReadAgentBoolean( arguments, CliOptionNames.HiddenModels, current.ShowHiddenModels ),
        WebSearchEnabled = ReadAgentBoolean( arguments, CliOptionNames.WebSearch, current.WebSearchEnabled ),
        UseCodexConfiguration = current.UseCodexConfiguration,
        AurEnabled = current.AurEnabled,
        ConversationTtlMinutes = ReadAgentInteger( arguments, CliOptionNames.Ttl, current.ConversationTtlMinutes ),
        TurnTimeoutMinutes = ReadAgentInteger( arguments, CliOptionNames.Timeout, current.TurnTimeoutMinutes ),
        TrustDuration = current.TrustDuration,
        TrustLeaseExpiresUnixMilliseconds = current.TrustLeaseExpiresUnixMilliseconds,
        LoopbackPreflightProviderId = current.LoopbackPreflightProviderId
    };

    private static int WriteAgentPreferences( AgentPreferences preferences, bool json )
    {
        if ( json )
        {
            Console.WriteLine( JsonSerializer.Serialize( preferences, CommandDeckJsonContext.Default.AgentPreferences ) );

            return 0;
        }

        WriteAgentRow( "Enabled", preferences.Enabled ? TrueValue : FalseValue );
        WriteAgentRow( "Backend", preferences.BackendId ?? DefaultValue );
        WriteAgentRow( "Model", preferences.ModelId ?? "backend default" );
        WriteAgentRow( "Effort", preferences.ReasoningEffort ?? "backend default" );
        WriteAgentRow( "Trust level", $"{preferences.TrustLevel}" );
        WriteAgentRow( "Context", preferences.ContextPolicy );
        WriteAgentRow( "Hidden models", preferences.ShowHiddenModels ? TrueValue : FalseValue );
        WriteAgentRow( "Web search", preferences.WebSearchEnabled ? TrueValue : FalseValue );
        WriteAgentRow( "Retention", $"{preferences.ConversationTtlMinutes} minute(s)" );
        WriteAgentRow( "Turn timeout", $"{preferences.TurnTimeoutMinutes} minute(s)" );

        return 0;
    }

    private static bool ReadAgentBoolean( CliArguments arguments, string name, bool current )
    {
        var value = arguments.GetOption( name );
        if ( value is null )
        {
            return current;
        }
        else
        {
            if ( bool.TryParse( value, out var parsed ) )
            {
                return parsed;
            }
            else
            {
                throw new InvalidOperationException( $"{name} must be true or false." );
            }
        }
    }

    private static int ReadAgentInteger( CliArguments arguments, string name, int current )
    {
        var value = arguments.GetOption( name );
        if ( value is null )
        {
            return current;
        }
        else
        {
            if ( int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed ) )
            {
                return parsed;
            }
            else
            {
                throw new InvalidOperationException( $"{name} must be a whole number." );
            }
        }
    }
}
