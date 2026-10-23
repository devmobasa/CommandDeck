namespace CommandDeck;

public static partial class ProtocolRequestValidator
{
    private static void ValidateAiOperation( ProtocolRequest request )
    {
        switch ( request.Operation )
        {
            case ProtocolOperations.AiModelsGet:
                AiContractValidator.ValidateIdentifier( request.ProviderId, AiLimits.MaximumProviderIdCharacters, "providerId" );

                if ( !request.Refresh )
                {
                    throw new InvalidOperationException( "ai.models.get requires refresh=true." );
                }

                break;
            case ProtocolOperations.AiSelectionUpdate:
                if ( request.ProviderId is null != (request.ModelId is null) )
                {
                    throw new InvalidOperationException( "AI selection requires both providerId and modelId, or both null." );
                }

                if ( request.ProviderId is not null )
                {
                    AiContractValidator.ValidateIdentifier( request.ProviderId, AiLimits.MaximumProviderIdCharacters, "providerId" );

                    AiContractValidator.ValidateUtf8( request.ModelId, AiLimits.MaximumModelIdBytes, "modelId", false, allowLineBreaks: false );
                }

                break;
            case ProtocolOperations.AiPreflight:
                if ( request.AiDraft is null )
                {
                    throw new InvalidOperationException( "ai.preflight requires aiDraft." );
                }

                break;
            case ProtocolOperations.AiStart:
                ValidateAiStart( request );

                break;
            case ProtocolOperations.AiTemplatesDelete when string.IsNullOrWhiteSpace( request.AiTemplateId ) || !request.Confirmed:
                throw new InvalidOperationException( "ai.templates.delete requires templateId and confirmed=true." );
            case ProtocolOperations.AiTemplatesSave when request.AiTemplate is null:
                throw new InvalidOperationException( "ai.templates.save requires aiTemplate." );
            case ProtocolOperations.AiOutput when request.Generation <= 0 ||
                                                  request.OutputAction is not AiOutputActions.Copy and not AiOutputActions.Save and not AiOutputActions.Open:
                throw new InvalidOperationException( "ai.output requires generation and action copy, save, or open." );
        }
    }

    private static void ValidateAiStart( ProtocolRequest request )
    {
        var payloadDigest = request.PayloadDigest;
        if ( HasInvalidPreflightIdentity( request, request.SessionId, payloadDigest ) )
        {
            throw new InvalidOperationException( "ai.start requires preflightId, SHA-256 payloadDigest, sessionId, and generation." );
        }

        AiContractValidator.ValidateIdentifier( request.PreflightId, MaximumPreflightIdCharacters, "preflightId" );

        if ( payloadDigest.Any( static character => !char.IsAsciiHexDigit( character ) ) )
        {
            throw new InvalidOperationException( "payloadDigest must be a SHA-256 hex digest." );
        }

        if ( HasInvalidSessionId( request.SessionId ) )
        {
            throw new InvalidOperationException( "sessionId must use 32-64 safe ASCII characters." );
        }
    }

    private static bool HasNegativeRevision( ProtocolRequest request )
    {
        return request.ExpectedRevision is < 0 || request.DraftRevision < 0;
    }

    private static bool HasInvalidPreflightIdentity( ProtocolRequest request, [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? sessionId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? payloadDigest )
    {
        return string.IsNullOrWhiteSpace( request.PreflightId ) ||
               payloadDigest?.Length != DigestLengths.Sha256HexCharacters ||
               request.Generation <= 0 ||
               string.IsNullOrWhiteSpace( sessionId );
    }

    private static bool HasInvalidSessionId( string sessionId )
    {
        return sessionId.Length is < MinimumSessionIdCharacters or > MaximumSessionIdCharacters ||
               sessionId.Any( static character => !char.IsAscii( character ) || !(char.IsAsciiLetterOrDigit( character ) || character is '.' or '_' or '-') );
    }
}
