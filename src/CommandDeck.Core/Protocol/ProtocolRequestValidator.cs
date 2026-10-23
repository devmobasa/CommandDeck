namespace CommandDeck;

public static partial class ProtocolRequestValidator
{
    private const int MaximumResultLimit = 256;

    public static void Validate( ProtocolRequest request, ContributionRegistrySnapshot? registry = null, bool deferFilterResolution = false )
    {
        ArgumentNullException.ThrowIfNull( request );

        if ( request.Version != ProtocolLimits.CurrentVersion )
        {
            throw new InvalidOperationException( $"Unsupported protocol version {request.Version}. Expected version " + $"{ProtocolLimits.CurrentVersion}." );
        }

        if ( request.RequestId <= 0 )
        {
            throw new InvalidOperationException( "requestId must be a positive integer." );
        }

        if ( request.Offset < 0 )
        {
            throw new InvalidOperationException( "offset must not be negative." );
        }

        if ( request.Limit is < 1 or > MaximumResultLimit )
        {
            throw new InvalidOperationException( "limit must be between 1 and 256." );
        }

        if ( HasInvalidCancelTarget( request ) )
        {
            throw new InvalidOperationException( "cancel requires a positive targetRequestId." );
        }

        ValidateEnabledFlag( request );
        CheckLength( request.Operation, nameof( request.Operation ), MaximumOperationCharacters );
        CheckLength( request.Query, nameof( request.Query ), ProtocolLimits.MaximumQueryCharacters );
        CheckLength( request.ResultId, nameof( request.ResultId ), ProtocolLimits.MaximumIdentifierCharacters );
        CheckLength( request.TargetResultId, nameof( request.TargetResultId ), ProtocolLimits.MaximumIdentifierCharacters );
        CheckLength( request.InvocationReference, nameof( request.InvocationReference ), ProtocolLimits.MaximumIdentifierCharacters );
        CheckLength( request.ActionId, nameof( request.ActionId ), ProtocolLimits.MaximumIdentifierCharacters );
        CheckLength( request.ProviderId, nameof( request.ProviderId ), ProtocolLimits.MaximumIdentifierCharacters );

        ActionInputValues.Validate( request.InputValues );
        ValidateBrowserFields( request );
        ValidateAiFields( request );
        ValidateAgentFields( request );
        ValidateQueryExpression( request, registry, deferFilterResolution );
        ValidateClipboardFields( request );
        ValidateExtensionFields( request );
        ValidateExtensionSession( request );
        CheckLength( request.Scope, nameof( request.Scope ), MaximumScopeCharacters );
        CheckLength( request.Path, nameof( request.Path ), MaximumPathCharacters );
        CheckLength( request.RootId, nameof( request.RootId ), ProtocolLimits.MaximumIdentifierCharacters );
        CheckLength( request.Alias, nameof( request.Alias ), MaximumAliasCharacters );
        CheckLength( request.TargetFilterId, nameof( request.TargetFilterId ), ProtocolLimits.MaximumIdentifierCharacters );
        CheckLength( request.CustomizationId, nameof( request.CustomizationId ), ProtocolLimits.MaximumIdentifierCharacters );
        CheckLength( request.ActiveApplication, nameof( request.ActiveApplication ), ProtocolLimits.MaximumIdentifierCharacters );

        if ( HasMissingSourcePreferences( request ) )
        {
            throw new InvalidOperationException( "preferences.sources must be an array." );
        }

        if ( request.Preferences?.Sources.Count > MaximumPreferenceSources )
        {
            throw new InvalidOperationException( "preferences contains more than 160 sources." );
        }

        if ( HasInvalidSourcePreferences( request ) )
        {
            throw new InvalidOperationException( "preferences.sources must contain valid source objects." );
        }

        if ( request.StableIds is null )
        {
            throw new InvalidOperationException( "stableIds must be an array." );
        }

        if ( HasInvalidStableIds( request ) )
        {
            throw new InvalidOperationException( "favorite reorder exceeds protocol limits." );
        }

        CheckLength( request.ActiveWorkspace, nameof( request.ActiveWorkspace ), ProtocolLimits.MaximumIdentifierCharacters );
        CheckLength( request.CurrentDirectory, nameof( request.CurrentDirectory ), MaximumPathCharacters );
        CheckLength( request.SelectedProject, nameof( request.SelectedProject ), MaximumPathCharacters );
        CheckLength( request.QuicklinkId, "quicklinkId", QuicklinkValidator.MaximumIdCharacters );

        if ( request.Quicklink is not null )
        {
            QuicklinkValidator.Validate( request.Quicklink );
        }

        ValidateSavedSearch( request, registry, deferFilterResolution );
    }

    private static void ValidateAiFields( ProtocolRequest request )
    {
        CheckLength( request.ModelId, nameof( request.ModelId ), MaximumModelIdCharacters );
        CheckLength( request.PreflightId, nameof( request.PreflightId ), MaximumPreflightIdCharacters );
        CheckLength( request.PayloadDigest, nameof( request.PayloadDigest ), MaximumPayloadDigestCharacters );
        CheckLength( request.SessionId, nameof( request.SessionId ), MaximumSessionIdCharacters );
        CheckLength( request.OutputAction, nameof( request.OutputAction ), MaximumOutputActionCharacters );
        CheckLength( request.ConfirmationToken, nameof( request.ConfirmationToken ), MaximumConfirmationTokenCharacters );
        CheckLength( request.AiTemplateId, nameof( request.AiTemplateId ), MaximumAiTemplateIdCharacters );

        if ( request.AiDraft is not null )
        {
            AiContractValidator.ValidateDraft( request.AiDraft );
        }

        if ( request.AiProvider is not null )
        {
            ConfigurationValidator.ValidateAiProvider( request.AiProvider );
        }

        if ( HasNegativeRevision( request ) )
        {
            throw new InvalidOperationException( "expectedRevision and draftRevision must not be negative." );
        }

        ValidateAiOperation( request );
        ValidateAiManagementOperation( request );
    }

    private static void ValidateAiManagementOperation( ProtocolRequest request )
    {
        switch ( request.Operation )
        {
            case ProtocolOperations.AiSettingsUpdate when request.Enabled is null:
                throw new InvalidOperationException( "ai.settings.update requires enabled." );
            case ProtocolOperations.AiProvidersSave when request.AiProvider is null:
                throw new InvalidOperationException( "ai.providers.save requires aiProvider." );
            case ProtocolOperations.AiProvidersDelete when string.IsNullOrWhiteSpace( request.ProviderId ) || !request.Confirmed:
                throw new InvalidOperationException( "ai.providers.delete requires providerId and confirmed=true." );
            case ProtocolOperations.AiProvidersDelete:
                AiContractValidator.ValidateIdentifier( request.ProviderId, AiLimits.MaximumProviderIdCharacters, "providerId" );

                break;
            case ProtocolOperations.AiProvidersTest when request.AiProvider is null:
                throw new InvalidOperationException( "ai.providers.test requires aiProvider." );
        }
    }

    private static void ValidateAgentFields( ProtocolRequest request )
    {
        CheckLength( request.BackendId, nameof( request.BackendId ), AgentContractValidator.MaximumBackendIdCharacters );
        CheckLength( request.ReasoningEffort, nameof( request.ReasoningEffort ), AgentContractValidator.MaximumReasoningEffortCharacters );
        CheckLength( request.LoginMethod, nameof( request.LoginMethod ), MaximumLoginMethodCharacters );
        CheckLength( request.LoginCode, nameof( request.LoginCode ), AiProviderSettingsLimits.MaximumLoginCodeCharacters );
        CheckLength( request.ConversationId, nameof( request.ConversationId ), MaximumConversationIdCharacters );
        CheckLength( request.PendingRequestId, nameof( request.PendingRequestId ), MaximumPendingRequestIdCharacters );
        CheckLength( request.Decision, nameof( request.Decision ), MaximumDecisionCharacters );
        CheckLength( request.ChallengeId, nameof( request.ChallengeId ), MaximumChallengeIdCharacters );
        CheckLength( request.Phrase, nameof( request.Phrase ), MaximumPhraseCharacters );
        CheckLength( request.TrustDuration, nameof( request.TrustDuration ), MaximumTrustDurationCharacters );
        CheckLength( request.GrantId, nameof( request.GrantId ), MaximumGrantIdCharacters );
        CheckLength( request.GrantKind, nameof( request.GrantKind ), MaximumGrantKindCharacters );
        CheckLength( request.GrantScope, nameof( request.GrantScope ), MaximumPathCharacters );
        CheckLength( request.PackageOperation, nameof( request.PackageOperation ), MaximumPackageOperationCharacters );
        CheckLength( request.PreviewId, nameof( request.PreviewId ), MaximumPreviewIdCharacters );
        CheckLength( request.OperationId, nameof( request.OperationId ), MaximumOperationIdCharacters );

        if ( request.PackageNames is null )
        {
            throw new InvalidOperationException( "packageNames must be an array." );
        }

        if ( request.PackageNames.Count > AgentLimits.MaximumPackageNames )
        {
            throw new InvalidOperationException( "packageNames exceeds the supported count." );
        }

        if ( request.Answers is not null )
        {
            if ( request.Answers.Count > MaximumAnswers )
            {
                throw new InvalidOperationException( "answers exceeds the supported count." );
            }

            foreach ( (var key, var value) in request.Answers )
            {
                CheckLength( key, "answers key", MaximumAnswerKeyCharacters );
                CheckLength( value, "answers value", AgentLimits.MaximumAnswerCharacters );
            }
        }

        if ( request.AgentPreferences is not null )
        {
            AgentContractValidator.ValidatePreferences( request.AgentPreferences );
        }

        ValidateAgentOperation( request );
    }

    private static void ValidateAgentOperation( ProtocolRequest request )
    {
        switch ( request.Operation )
        {
            case ProtocolOperations.AgentTurnStart:
                AgentContractValidator.ValidatePromptText( request.Query );

                break;
            case ProtocolOperations.AgentTurnInput when string.IsNullOrWhiteSpace( request.PendingRequestId ):
                throw new InvalidOperationException( "agent.turn.input requires pendingRequestId." );
            case ProtocolOperations.AgentContextResolve
                when string.IsNullOrWhiteSpace( request.PendingRequestId ) || string.IsNullOrWhiteSpace( request.Decision ):
                throw new InvalidOperationException( "agent.context.resolve requires pendingRequestId and decision." );
            case ProtocolOperations.AgentPreferencesUpdate when request.AgentPreferences is null:
                throw new InvalidOperationException( "agent.preferences.update requires agentPreferences." );
            case ProtocolOperations.AgentLoginStart
                when request.LoginMethod is not (AgentLoginMethods.DeviceCode or AgentLoginMethods.ApiKey or AgentLoginMethods.Browser):
                throw new InvalidOperationException( "agent.login.start requires loginMethod device-code, api-key, or browser." );
            case ProtocolOperations.AgentLoginComplete when string.IsNullOrWhiteSpace( request.LoginCode ) ||
                                                            request.LoginCode.Length > AiProviderSettingsLimits.MaximumLoginCodeCharacters ||
                                                            request.LoginCode.Any( char.IsControl ):
                throw new InvalidOperationException( "agent.login.complete requires a loginCode." );
            case ProtocolOperations.AgentOutput when request.Generation <= 0 ||
                                                     request.OutputAction is not AiOutputActions.Copy and not AiOutputActions.Save and not AiOutputActions.Open:
                throw new InvalidOperationException( "agent.output requires generation and action copy, save, or open." );
        }

        ValidateAgentAuthorityOperation( request );
        ValidatePackageOperation( request );
    }

    private static void ValidateAgentAuthorityOperation( ProtocolRequest request )
    {
        switch ( request.Operation )
        {
            case ProtocolOperations.AgentTrustChallenge when request.TrustLevel is null || !AgentTrustLevels.IsValid( request.TrustLevel.Value ):
                throw new InvalidOperationException( "agent.trust.challenge requires a valid trustLevel." );
            case ProtocolOperations.AgentTrustUpdate when string.IsNullOrWhiteSpace( request.ChallengeId ) || request.TrustLevel is null:
                throw new InvalidOperationException( "agent.trust.update requires challengeId and trustLevel." );
            case ProtocolOperations.AgentGrantsAdd when string.IsNullOrWhiteSpace( request.GrantKind ) || string.IsNullOrWhiteSpace( request.GrantScope ):
                throw new InvalidOperationException( "agent.grants.add requires grantKind and grantScope." );
            case ProtocolOperations.AgentGrantsRemove when string.IsNullOrWhiteSpace( request.GrantId ):
                throw new InvalidOperationException( "agent.grants.remove requires grantId." );
            case ProtocolOperations.AgentGrantsClear when !request.Confirmed:
                throw new InvalidOperationException( "agent.grants.clear requires confirmed: true." );
        }
    }

    private static void ValidatePackageOperation( ProtocolRequest request )
    {
        switch ( request.Operation )
        {
            case ProtocolOperations.PackageSearch when string.IsNullOrWhiteSpace( request.Query ):
                throw new InvalidOperationException( "package.search requires query." );
            case ProtocolOperations.PackagePreview when request.PackageOperation is not (PackageOperations.Install or PackageOperations.Remove) ||
                                                        request.PackageNames.Count == 0:
                throw new InvalidOperationException( "package.preview requires packageOperation and packageNames." );
            case ProtocolOperations.PackageCommit when string.IsNullOrWhiteSpace( request.PreviewId ) || !request.Confirmed:
                throw new InvalidOperationException( "package.commit requires previewId and confirmed: true." );
            case ProtocolOperations.PackageCancel when string.IsNullOrWhiteSpace( request.OperationId ):
                throw new InvalidOperationException( "package.cancel requires operationId." );
        }
    }

    private static void ValidateExtensionSession( ProtocolRequest request )
    {
        if ( ProtocolRoutes.IsIn( request.Operation, ProtocolFamily.ExtensionSession ) )
        {
            var input = request.ExtensionInput;
            if ( input is null )
            {
                throw new InvalidOperationException( "Missing extension session input." );
            }

            if ( HasInvalidExtensionInput( input ) )
            {
                throw new InvalidOperationException( "Invalid extension session input." );
            }

            ValidateExtensionFormInput( input );

            if ( HasMissingExtensionResult( request ) )
            {
                throw new InvalidOperationException( "Select an extension command." );
            }

            if ( HasMissingExtensionSession( request ) )
            {
                throw new InvalidOperationException( "Missing extension session identity." );
            }
        }
    }

    private static void ValidateExtensionFormInput( ExtensionSessionInput input )
    {
        if ( HasInvalidExtensionForm( input ) )
        {
            throw new InvalidOperationException( "Invalid extension form input." );
        }
    }

    private static void ValidateExtensionFields( ProtocolRequest request )
    {
        request.Extension?.Validate( );

        if ( request.SourcePatch is { } patch )
        {
            if ( HasInvalidSourcePatch( request, patch ) )
            {
                throw new InvalidOperationException( "Source patches require 1-32 bounded changes and no full preferences." );
            }
        }
    }

    // favorite.set and hidden.set take an explicit Boolean; an omitted flag is
    // rejected rather than read as removal.
    private static void ValidateEnabledFlag( ProtocolRequest request )
    {
        var requiresEnabled = RequiresEnabledFlag( request );
        if ( requiresEnabled && request.Enabled is null )
        {
            throw new InvalidOperationException( $"{request.Operation} requires enabled." );
        }
    }

    private static void ValidateBrowserFields( ProtocolRequest request )
    {
        CheckLength( request.HerdrWorkspaceId, nameof( request.HerdrWorkspaceId ), MaximumHerdrWorkspaceIdCharacters );

        if ( HasInvalidHerdrWorkspace( request ) )
        {
            throw new InvalidOperationException( "HerdrWorkspaceId must be a valid Herdr identifier." );
        }

        CheckLength( request.OmarchyMenuId, nameof( request.OmarchyMenuId ), MaximumOmarchyMenuIdCharacters );

        if ( HasInvalidOmarchyMenu( request ) )
        {
            throw new InvalidOperationException( "OmarchyMenuId must be a valid Omarchy menu identifier." );
        }

        if ( HasConflictingBrowseModes( request ) )
        {
            throw new InvalidOperationException( "A search can browse only one Omarchy menu, Herdr space or folder." );
        }

        CheckLength( request.BrowseDirectory, nameof( request.BrowseDirectory ), MaximumPathCharacters );

        if ( request.BrowseOffset is < 0 or > MaximumBrowseOffset )
        {
            throw new InvalidOperationException( "browseOffset must be between 0 and 50000." );
        }
    }

    private static void CheckLength( string? value, string name, int maximum )
    {
        if ( value?.Length > maximum )
        {
            throw new InvalidOperationException( $"{name} exceeds the {maximum}-character protocol limit." );
        }
    }

    private static void ValidateSavedSearch( ProtocolRequest request, ContributionRegistrySnapshot? registry, bool deferFilterResolution )
    {
        CheckLength( request.SavedSearchId, nameof( request.SavedSearchId ), ProtocolLimits.MaximumIdentifierCharacters );

        if ( request.SavedSearch is null )
        {
            return;
        }

        CheckLength( request.SavedSearch.Id, "savedSearch.id", MaximumSavedSearchIdCharacters );
        CheckLength( request.SavedSearch.Name, "savedSearch.name", MaximumSavedSearchNameCharacters );
        _ = SavedSearchValidator.ValidateName( request.SavedSearch.Name );

        if ( deferFilterResolution )
        {
            _ = QueryParser.ValidateShape( request.SavedSearch.Expression );
        }
        else
        {
            _ = SavedSearchValidator.ValidateExpression( request.SavedSearch.Expression, registry );
        }

        _ = SavedSearchValidator.NormalizeShortcut( request.SavedSearch.Shortcut );
    }

    private static void ValidateQueryExpression( ProtocolRequest request, ContributionRegistrySnapshot? registry, bool deferFilterResolution )
    {
        if ( request.Expression is null )
        {
            throw new InvalidOperationException( "expression must be an object." );
        }

        if ( request.Operation is ProtocolOperations.AgentTurnStart or ProtocolOperations.PackageSearch )
        {
            return;
        }

        if ( deferFilterResolution )
        {
            _ = QueryParser.ValidateShape( request.Expression );
            _ = QueryParser.Parse( request.Query );
        }
        else
        {
            _ = QueryParser.Parse( request.Query, request.Expression, registry: registry );
        }
    }

    private static bool HasInvalidCancelTarget( ProtocolRequest request )
    {
        return string.Equals( request.Operation, ProtocolOperations.Cancel, StringComparison.Ordinal ) && request.TargetRequestId <= 0;
    }

    private static bool HasMissingSourcePreferences( ProtocolRequest request )
    {
        return request.Preferences is not null && request.Preferences.Sources is null;
    }

    private static bool HasInvalidSourcePreferences( ProtocolRequest request )
    {
        return request.Preferences?.Sources.Any( static source => source is null || string.IsNullOrWhiteSpace( source.SourceId ) ) == true;
    }

    private static bool HasInvalidStableIds( ProtocolRequest request )
    {
        return request.StableIds.Count > CustomizationLimits.MaximumReorderItems ||
               request.StableIds.Any( static value => value is null || value.Length > ProtocolLimits.MaximumIdentifierCharacters );
    }

    private static bool HasInvalidExtensionInput( ExtensionSessionInput input )
    {
        return input.Features is null ||
               input.Features.Length > MaximumExtensionFeatures ||
               input.Features.Any( feature => feature is null || feature.Length > MaximumExtensionFeatureCharacters ) ||
               input.Sequence < 0 ||
               input.Revision < 0 ||
               input.Handle?.Length > MaximumExtensionHandleCharacters ||
               input.ItemId?.Length > MaximumExtensionItemIdCharacters ||
               (input.ConfirmationId?.Length > MaximumExtensionConfirmationIdCharacters || input.SearchLease?.Length > MaximumExtensionSearchLeaseCharacters);
    }

    private static bool HasMissingExtensionResult( ProtocolRequest request )
    {
        return request.Operation == ProtocolOperations.ExtensionOpen && string.IsNullOrWhiteSpace( request.ResultId );
    }

    private static bool HasMissingExtensionSession( ProtocolRequest request )
    {
        return request.Operation != ProtocolOperations.ExtensionOpen && string.IsNullOrWhiteSpace( request.SessionId );
    }

    private static bool HasInvalidExtensionForm( ExtensionSessionInput input )
    {
        return input.FormRevision < 0 ||
               input.Values is { Count: > MaximumExtensionFormValues } ||
               input.Values?.Keys.Any( key => key.Length > MaximumExtensionFormKeyCharacters ) == true;
    }

    private static bool HasInvalidSourcePatch( ProtocolRequest request, SourcePreferencePatch patch )
    {
        return request.Preferences is not null ||
               patch.RegistryRevision < 0 ||
               patch.PreferenceRevision < 0 ||
               patch.Changes is not { Count: > 0 and <= MaximumSourcePatchChanges } ||
               patch.Changes.Any( change =>
                   change is null || string.IsNullOrWhiteSpace( change.SourceId ) || change.SourceId.Length > MaximumPatchedSourceIdCharacters );
    }

    private static bool RequiresEnabledFlag( ProtocolRequest request )
    {
        return string.Equals( request.Operation, ProtocolOperations.FavoriteSet, StringComparison.Ordinal ) ||
               string.Equals( request.Operation, ProtocolOperations.HiddenSet, StringComparison.Ordinal );
    }

    private static bool HasInvalidHerdrWorkspace( ProtocolRequest request )
    {
        return request.HerdrWorkspaceId is not null && (request.HerdrWorkspaceId.Length == 0 || request.HerdrWorkspaceId.Any( char.IsControl ));
    }

    private static bool HasInvalidOmarchyMenu( ProtocolRequest request )
    {
        return request.OmarchyMenuId is not null &&
               (request.OmarchyMenuId.Trim( )
                    .Length ==
                0 ||
                request.OmarchyMenuId.Any( char.IsControl ));
    }

    private static bool HasConflictingBrowseModes( ProtocolRequest request )
    {
        return request.OmarchyMenuId is not null && (request.HerdrWorkspaceId is not null || request.BrowseDirectory is not null);
    }
}
