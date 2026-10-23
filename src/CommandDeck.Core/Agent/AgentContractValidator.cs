using System.Text;

namespace CommandDeck;

public static class AgentContractValidator
{
    internal const int MaximumBackendIdCharacters = 64;
    internal const int MaximumReasoningEffortCharacters = 32;
    private const int MaximumPackageNameCharacters = 255;

    public static void ValidatePromptText( string? text )
    {
        if ( string.IsNullOrWhiteSpace( text ) )
        {
            throw new InvalidOperationException( "The agent prompt must not be empty." );
        }

        AiContractValidator.ValidateUtf8( text, AgentLimits.MaximumPromptBytes, "agent prompt", false );
    }

    public static void ValidateTrustLevel( int level )
    {
        if ( !AgentTrustLevels.IsValid( level ) )
        {
            throw new InvalidOperationException( "The trust level is outside the supported range." );
        }
    }

    public static void ValidateDuration( string? duration )
    {
        if ( duration is not (AgentTrustDurations.OneHour or AgentTrustDurations.UntilDowngrade) )
        {
            throw new InvalidOperationException( "The trust duration must be one-hour or until-downgrade." );
        }
    }

    public static void ValidatePreferences( AgentPreferences preferences )
    {
        ArgumentNullException.ThrowIfNull( preferences );

        ValidateTrustLevel( preferences.TrustLevel );

        if ( preferences.ContextPolicy is not (AgentContextPolicies.ApprovedFolders or AgentContextPolicies.AskEveryAccess) )
        {
            throw new InvalidOperationException( "The context policy is not supported." );
        }

        if ( preferences.ConversationTtlMinutes is < 0 or > AgentLimits.MaximumConversationTtlMinutes )
        {
            throw new InvalidOperationException( "The conversation retention is outside the supported range." );
        }

        if ( preferences.TurnTimeoutMinutes is < AgentLimits.MinimumTurnTimeoutMinutes or > AgentLimits.MaximumTurnTimeoutMinutes )
        {
            throw new InvalidOperationException( "The turn timeout is outside the supported range." );
        }

        if ( preferences.BackendId is not null )
        {
            AiContractValidator.ValidateIdentifier( preferences.BackendId, MaximumBackendIdCharacters, "backendId" );
        }

        if ( preferences.ModelId is not null )
        {
            AiContractValidator.ValidateUtf8( preferences.ModelId, AiLimits.MaximumModelIdBytes, "modelId", false, allowLineBreaks: false );
        }

        if ( preferences.ReasoningEffort is not null )
        {
            AiContractValidator.ValidateIdentifier( preferences.ReasoningEffort, MaximumReasoningEffortCharacters, "reasoningEffort" );
        }

        if ( preferences.TrustDuration is not null )
        {
            ValidateDuration( preferences.TrustDuration );
        }

        if ( preferences.LoopbackPreflightProviderId is not null )
        {
            AiContractValidator.ValidateIdentifier( preferences.LoopbackPreflightProviderId, AiLimits.MaximumProviderIdCharacters,
                "loopbackPreflightProviderId" );
        }
    }

    public static void ValidateArgv( IReadOnlyList<string>? argv )
    {
        if ( HasNoArguments( argv ) )
        {
            throw new InvalidOperationException( "The command requires at least one argument." );
        }

        if ( argv.Count > AgentLimits.MaximumCommandArguments )
        {
            throw new InvalidOperationException( "The command has too many arguments." );
        }

        foreach ( var argument in argv )
        {
            if ( argument is null )
            {
                throw new InvalidOperationException( "Command arguments must not be null." );
            }

            if ( argument.Contains( '\0', StringComparison.Ordinal ) )
            {
                throw new InvalidOperationException( "Command arguments must not contain NUL." );
            }

            if ( Encoding.UTF8.GetByteCount( argument ) > AgentLimits.MaximumCommandArgumentBytes )
            {
                throw new InvalidOperationException( "A command argument is too long." );
            }
        }
    }

    public static bool IsValidPackageName( string? name )
    {
        if ( IsInvalidPackageName( name ) )
        {
            return false;
        }

        foreach ( var character in name )
        {
            var allowed = IsPackageNameCharacter( character );
            if ( !allowed )
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> NormalizePackageNames( IReadOnlyList<string>? names )
    {
        if ( HasNoPackageNames( names ) )
        {
            throw new InvalidOperationException( "At least one package name is required." );
        }

        List<string> normalized = [];
        foreach ( var name in names )
        {
            if ( !IsValidPackageName( name ) )
            {
                throw new InvalidOperationException( "A package name is invalid." );
            }

            if ( !normalized.Contains( name, StringComparer.Ordinal ) )
            {
                normalized.Add( name );
            }
        }

        if ( normalized.Count > AgentLimits.MaximumPackageNames )
        {
            throw new InvalidOperationException( $"At most {AgentLimits.MaximumPackageNames} package names are allowed." );
        }

        normalized.Sort( StringComparer.Ordinal );

        return normalized;
    }

    private static bool HasNoArguments( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] IReadOnlyList<string>? argv )
    {
        return argv is null || argv.Count == 0;
    }

    private static bool IsInvalidPackageName( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? name )
    {
        return string.IsNullOrEmpty( name ) || name.Length > MaximumPackageNameCharacters || name[0] is '-' or '.';
    }

    private static bool IsPackageNameCharacter( char character )
    {
        return char.IsAsciiLetterOrDigit( character ) || character is '@' or '.' or '_' or '+' or '-';
    }

    private static bool HasNoPackageNames( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] IReadOnlyList<string>? names )
    {
        return names is null || names.Count == 0;
    }
}
