namespace CommandDeck;

public static partial class ConfigurationValidator
{
    public static void ValidateAi( AiOptions? ai )
    {
        if ( HasInvalidAiProviders( ai ) )
        {
            throw new InvalidOperationException( "ai.providers must contain at most eight objects." );
        }

        HashSet<string> ids = new( StringComparer.OrdinalIgnoreCase );
        foreach ( var provider in ai.Providers )
        {
            ValidateAiProvider( provider );

            if ( !ids.Add( provider.Id ) )
            {
                throw new InvalidOperationException( $"AI provider ID '{provider.Id}' is duplicated." );
            }
        }
    }

    /// <summary>
    /// Validates one provider definition: identifier, reserved backend IDs,
    /// display name, adapter kind, endpoint policy, model, context limit, and the
    /// credential reference. Duplicate detection belongs to the enclosing set.
    /// </summary>
    public static void ValidateAiProvider( AiProviderOptions? provider )
    {
        if ( provider is null )
        {
            throw new InvalidOperationException( "AI provider definition is required." );
        }

        ValidateIdentifier( provider.Id, "AI provider ID" );

        if ( AgentBackendIds.IsReserved( provider.Id ) )
        {
            throw new InvalidOperationException( $"AI provider ID '{provider.Id}' is reserved for an assistant backend." );
        }

        var displayName = provider.DisplayName ?? provider.Id;
        if ( IsInvalidProviderDisplayName( displayName ) )
        {
            throw new InvalidOperationException( "AI provider display name is invalid." );
        }

        if ( provider.Kind is not AiAdapterKinds.Ollama and not AiAdapterKinds.OpenAiChatCompletions )
        {
            throw new InvalidOperationException( $"AI provider '{provider.Id}' has an unsupported adapter kind." );
        }

        if ( provider.BaseUrl is null )
        {
            throw new InvalidOperationException( $"AI provider '{provider.Id}' baseUrl is invalid." );
        }

        ValidateAiEndpoint( provider );

        AiContractValidator.ValidateUtf8( provider.Model, OptionLimits.AiProvider.Model.MaximumBytes, "AI model", false, allowLineBreaks: false );

        if ( provider.ContextLimit is < OptionLimits.AiProvider.ContextLimit.Minimum or > OptionLimits.AiProvider.ContextLimit.Maximum )
        {
            throw new InvalidOperationException(
                $"AI provider contextLimit must be {OptionLimits.AiProvider.ContextLimit.Minimum}-{OptionLimits.AiProvider.ContextLimit.Maximum} when configured." );
        }

        ValidateCredentialReference( provider.Credential, "AI credential" );
    }

    private static void ValidateAiEndpoint( AiProviderOptions provider )
    {
        var configuredWithoutOptionalSlash = RemoveOptionalTrailingSlash( provider.BaseUrl );
        if ( HasInvalidEndpointUri( provider, configuredWithoutOptionalSlash, out var uri ) )
        {
            throw new InvalidOperationException( $"AI provider '{provider.Id}' baseUrl is invalid." );
        }

        var loopback = AiEndpointPolicy.IsLiteralLoopback( uri );
        if ( IsDisallowedRemoteEndpoint( provider, uri, loopback ) )
        {
            throw new InvalidOperationException( $"Remote AI provider '{provider.Id}' requires HTTPS and allowRemoteEndpoint=true." );
        }

        if ( IsDisallowedRemoteOllama( provider, loopback ) )
        {
            throw new InvalidOperationException( "Remote Ollama requires explicit permission." );
        }
    }

    private static string RemoveOptionalTrailingSlash( string value ) =>
        value.EndsWith( '/', StringComparison.Ordinal ) ? value[..^1] : value;

    private static void ValidateCredentialReference( AiCredentialOptions? credential, string label )
    {
        if ( IsInvalidCredential( credential ) )
        {
            throw new InvalidOperationException( $"{label} kind is invalid." );
        }

        if ( credential.Kind == AiCredentialKinds.None )
        {
            if ( credential.Name is not null )
            {
                throw new InvalidOperationException( $"{label} name must be absent for kind none." );
            }

            return;
        }

        AiContractValidator.ValidateIdentifier( credential.Name, OptionLimits.Identifiers.MaximumCharacters, label + " name" );
    }

    private static bool HasInvalidAiProviders( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] AiOptions? ai )
    {
        return ai?.Providers is null || ai.Providers.Count > OptionLimits.Ai.Providers.Maximum || ai.Providers.Any( static provider => provider is null );
    }

    private static bool IsInvalidProviderDisplayName( string displayName )
    {
        return string.IsNullOrWhiteSpace( displayName ) ||
               System.Text.Encoding.UTF8.GetByteCount( displayName ) > OptionLimits.AiProvider.DisplayName.MaximumBytes ||
               displayName.Any( char.IsControl );
    }

    private static bool IsDisallowedRemoteEndpoint( AiProviderOptions provider, Uri uri, bool loopback )
    {
        return !loopback && (uri.Scheme != UrlSchemes.Https || !provider.AllowRemoteEndpoint);
    }

    private static bool IsDisallowedRemoteOllama( AiProviderOptions provider, bool loopback )
    {
        return provider is { Kind: AiAdapterKinds.Ollama, AllowRemoteEndpoint: false } && !loopback;
    }

    private static bool IsInvalidCredential( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] AiCredentialOptions? credential )
    {
        return credential is null || credential.Kind is not AiCredentialKinds.None and not AiCredentialKinds.Environment and not AiCredentialKinds.Systemd;
    }

    private static bool HasInvalidEndpointUri( AiProviderOptions provider, string configuredWithoutOptionalSlash,
        [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] out Uri? uri )
    {
        uri = default;

        return provider.BaseUrl.Length > OptionLimits.AiProvider.BaseUrl.Maximum ||
               !Uri.TryCreate( provider.BaseUrl, UriKind.Absolute, out uri ) ||
               uri.Scheme is not (UrlSchemes.Http or UrlSchemes.Https) ||
               !string.IsNullOrEmpty( uri.UserInfo ) ||
               !string.IsNullOrEmpty( uri.Query ) ||
               !string.IsNullOrEmpty( uri.Fragment ) ||
               uri.Host.Contains( '*' ) ||
               uri.AbsolutePath.Length > 1 && uri.AbsolutePath.Contains( "//", StringComparison.Ordinal ) ||
               !string.Equals( configuredWithoutOptionalSlash,
                   RemoveOptionalTrailingSlash( uri.GetComponents( UriComponents.AbsoluteUri, UriFormat.UriEscaped ) ), StringComparison.Ordinal );
    }
}
