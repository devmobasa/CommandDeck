using System.Security.Cryptography;

namespace CommandDeck;

public sealed partial class AgentConversationCoordinator
{
    private static string NewId( ) => Convert
        .ToHexString( RandomNumberGenerator.GetBytes( RandomIdBytes ) )
        .ToLowerInvariant( );

    private static string Sanitize( string message )
    {
        var trimmed = message.Length > MaximumErrorMessageCharacters ? message[..MaximumErrorMessageCharacters] : message;

        return new string( trimmed
            .Where( static character => !char.IsControl( character ) )
            .ToArray( ) );
    }

    private AdmittedAgentTools CreateTurnToolRouter( Conversation conversation, long turnId )
    {
        return new( )
        {
            TryAdmit = minimum =>
            {
                lock ( _sync )
                {
                    return IsCurrentTurnUnderLock( conversation, turnId ) &&
                           conversation.TrustLevel >= minimum &&
                           (minimum != AgentTrustLevels.Packages || _packages is not null);
                }
            },
            Search = async ( arguments, token ) => AgentToolResult.Ok( await AgentLauncherTools
                .SearchAsync( _launcher, ReadString( arguments, AgentToolFields.Query ), ReadInt( arguments, "limit", 8 ), token )
                .ConfigureAwait( false ) ),
            Invoke = ( arguments, token ) => InvokeToolAsync( conversation, arguments, token ),
            Context = ( name, arguments, token ) => ContextToolAsync( conversation, name, arguments, token ),
            Command = ( arguments, token ) => CommandToolAsync( conversation, arguments, token ),
            PackageSearch = PackageSearchToolAsync,
            PackageChange = ( arguments, token ) => PackageChangeToolAsync( conversation, arguments, token )
        };
    }

    private AgentOperationSummary? GetOldestOperation( )
    {
        return _operations
            .Values.OrderBy( static value => value.UpdatedUnixMilliseconds )
            .FirstOrDefault( );
    }
}
