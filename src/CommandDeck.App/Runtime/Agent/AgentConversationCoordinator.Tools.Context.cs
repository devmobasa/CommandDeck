using System.Text;
using System.Text.Json.Nodes;

namespace CommandDeck;

public sealed partial class AgentConversationCoordinator
{
    private async ValueTask<AgentToolResult> ContextToolAsync( Conversation conversation, string tool, JsonObject arguments,
        CancellationToken cancellationToken )
    {
        if ( IsContextBudgetExhausted( conversation, tool ) )
        {
            return AgentToolResult.Denied( "The context budget for this turn is exhausted." );
        }

        var operation = tool switch
        {
            AgentToolNames.ListDirectory => AgentContextOperations.ListDirectory,
            AgentToolNames.ReadFile => AgentContextOperations.ReadFile,
            _ => AgentContextOperations.SearchText
        };

        var requested = ReadString( arguments, AgentToolFields.Path );

        // This tool runs inside the turn, so it authorizes against the enforced
        // lease without revoking; maintenance or the next turn ends the turn.
        var preferences = await EnforceLeaseAsync( cancellationToken )
            .ConfigureAwait( false );

        var authorization = await _context
            .AuthorizeAsync( requested, operation, preferences, cancellationToken )
            .ConfigureAwait( false );

        if ( authorization is { Allowed: false, RequiresDecision: false } )
        {
            return AgentToolResult.Denied( authorization.Reason );
        }

        if ( !authorization.Allowed )
        {
            var granted = await RequestContextDecisionAsync( conversation, operation, authorization, arguments, cancellationToken )
                .ConfigureAwait( false );

            if ( !granted )
            {
                return AgentToolResult.Denied( "The user declined access to " + authorization.CanonicalPath + "." );
            }
        }

        return ExecuteContextOperation( conversation, operation, authorization.CanonicalPath, arguments, cancellationToken );
    }

    private async ValueTask<bool> RequestContextDecisionAsync( Conversation conversation, string operation, AgentContextAuthorization authorization,
        JsonObject arguments, CancellationToken cancellationToken )
    {
        List<string> decisions = [AgentRequestDecisions.Allow, AgentRequestDecisions.Deny, AgentRequestDecisions.CancelTurn];
        if ( authorization is { Protected: false, ProposedFolder: not null } )
        {
            decisions.Insert( 1, AgentRequestDecisions.AllowFolder );
        }

        AgentPendingRequest request = new( )
        {
            Id = NewId( ),
            Kind = AgentPendingRequestKinds.Context,
            TurnId = conversation.ActiveTurnId,
            Prompt = GetContextDecisionPrompt( operation, authorization ),
            Detail = authorization.Reason,
            Context = new AgentContextRequest
            {
                Operation = operation,
                Path = authorization.CanonicalPath,
                Query = ReadOptional( arguments, AgentToolFields.Query ),
                MaximumBytes = AgentLimits.MaximumFileBytes,
                ProposedFolder = authorization.Protected ? null : authorization.ProposedFolder!,
                ProtectedTarget = authorization.Protected
            },
            Decisions = decisions
        };

        var decision = await ParkAsync( conversation, request, cancellationToken )
            .ConfigureAwait( false );

        if ( ShouldRememberFolderGrant( authorization, decision ) )
        {
            await _context
                .GrantFolderAsync( authorization.ProposedFolder!, cancellationToken )
                .ConfigureAwait( false );

            return true;
        }

        return decision.Decision == AgentRequestDecisions.Allow;
    }

    private AgentToolResult ExecuteContextOperation( Conversation conversation, string operation, string canonicalPath, JsonObject arguments,
        CancellationToken cancellationToken )
    {
        switch ( operation )
        {
            case AgentContextOperations.ListDirectory:
                JsonArray entries = new( );

                foreach ( var entry in _context.ListDirectory( canonicalPath, AgentLimits.MaximumDirectoryEntries ) )
                {
                    JsonNode entryNode = new JsonObject { [AgentToolFields.Name] = entry.Name, [AgentToolFields.Kind] = entry.Kind, ["size"] = entry.Size };

                    entries.Add( entryNode );
                }

                return AgentToolResult.Ok( new JsonObject { [AgentToolFields.Path] = canonicalPath, ["entries"] = entries }.ToJsonString( ) );
            case AgentContextOperations.ReadFile:
                var text = _context.ReadTextFile( canonicalPath, AgentLimits.MaximumFileBytes, out var truncated );

                if ( !ChargeContextBudget( conversation, Encoding.UTF8.GetByteCount( text ) ) )
                {
                    return AgentToolResult.Denied( "The context budget for this turn is exhausted." );
                }

                return AgentToolResult.Ok( new JsonObject
                {
                    [AgentToolFields.Path] = canonicalPath,
                    [AgentToolFields.Truncated] = truncated,
                    [AgentToolFields.Text] = text
                }.ToJsonString( ) );
            default:
                var remaining = RemainingContextBudget( conversation );

                if ( remaining <= 0 )
                {
                    return AgentToolResult.Denied( "The context budget for this turn is exhausted." );
                }

                var query = ReadString( arguments, AgentToolFields.Query );

                var matches = _context.SearchText( canonicalPath, query, AgentLimits.MaximumSearchMatches,
                    Math.Min( remaining, AgentLimits.MaximumContextBytesPerTurn / 4 ), cancellationToken );

                JsonArray array = new( );

                foreach ( var match in matches )
                {
                    JsonNode matchNode = new JsonObject
                    {
                        [AgentToolFields.Path] = match.Path,
                        [AgentToolFields.Line] = match.Line,
                        [AgentToolFields.Text] = match.Text
                    };

                    array.Add( matchNode );
                }

                var content = new JsonObject { ["matches"] = array }.ToJsonString( );
                if ( !ChargeContextBudget( conversation, Encoding.UTF8.GetByteCount( content ) ) )
                {
                    return AgentToolResult.Denied( "The context budget for this turn is exhausted." );
                }

                return AgentToolResult.Ok( content );
        }
    }

    private int RemainingContextBudget( Conversation conversation )
    {
        lock ( _sync )
        {
            return ( int ) Math.Max( 0, AgentLimits.MaximumContextBytesPerTurn - conversation.ContextBytesUsed );
        }
    }

    private bool ChargeContextBudget( Conversation conversation, int bytes )
    {
        lock ( _sync )
        {
            if ( conversation.ContextBytesUsed >= AgentLimits.MaximumContextBytesPerTurn ||
                 conversation.ContextBytesUsed + bytes > AgentLimits.MaximumContextBytesPerTurn )
            {
                return false;
            }

            conversation.ContextBytesUsed += bytes;

            return true;
        }
    }

    private bool IsContextBudgetExhausted( Conversation conversation, string tool )
    {
        return tool != AgentToolNames.ListDirectory && RemainingContextBudget( conversation ) <= 0;
    }

    private static bool ShouldRememberFolderGrant( AgentContextAuthorization authorization, PendingDecision decision )
    {
        return decision.Decision == AgentRequestDecisions.AllowFolder && authorization is { ProposedFolder: not null, Protected: false };
    }

    private static string GetContextDecisionPrompt( string operation, AgentContextAuthorization authorization )
    {
        if ( authorization.Protected )
        {
            return "Allow a single read of a protected path?";
        }
        else
        {
            return $"Allow the assistant to {DescribeOperation( operation )}?";
        }
    }
}
