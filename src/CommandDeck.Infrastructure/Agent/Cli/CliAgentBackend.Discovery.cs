using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public sealed partial class CliAgentBackend
{
    public async ValueTask<AgentModelInventory> DiscoverModelsAsync( bool includeHidden, CancellationToken cancellationToken )
    {
        try
        {
            if ( Profile.ModelsArguments is null )
            {
                return new AgentModelInventory
                {
                    BackendId = Id,
                    Models = [.. Profile.StaticModels],
                    DefaultModelId = Profile.StaticModels.FirstOrDefault( static model => model.IsDefault )
                        ?.Id
                };
            }

            var output = await RunBoundedAsync( Profile.ModelsArguments, ModelsBound, cancellationToken )
                .ConfigureAwait( false );

            var models = Profile.ParseModels( output.StandardOutput );
            if ( models.Count == 0 )
            {
                return new AgentModelInventory
                {
                    BackendId = Id,
                    Error = new AiError
                    {
                        Category = output.ExitCode == 0 ? AiErrorCategories.MalformedResponse : AiErrorCategories.Authentication,
                        Summary = DescribeModelListFailure( output )
                    }
                };
            }

            return new AgentModelInventory
            {
                BackendId = Id,
                Models = [.. models],
                DefaultModelId = models.FirstOrDefault( static model => model.IsDefault )
                    ?.Id
            };
        }
        catch ( AiOperationException exception )
        {
            return new AgentModelInventory { BackendId = Id, Error = new AiError { Category = exception.Category, Summary = exception.SafeSummary } };
        }
    }

    internal CliAgentBinary Locate( )
    {
        if ( IsBinaryUnchanged( ) )
        {
            return _binary;
        }

        try
        {
            _binary = CliAgentBinaryLocator.Locate( Profile, ConfiguredBinaryPath( ) );
            _lastError = null;

            return _binary;
        }
        catch ( AiOperationException exception )
        {
            _lastError = new AiError { Category = exception.Category, Summary = exception.SafeSummary };

            throw;
        }
    }

    private bool TryLocate( out CliAgentBinary? binary )
    {
        // A missing CLI is re-checked at most every 30 seconds so descriptors
        // stay cheap while the launcher searches on every keystroke.
        if ( HasRecentLocateFailure( ) )
        {
            binary = null;

            return false;
        }

        try
        {
            binary = Locate( );

            return true;
        }
        catch ( AiOperationException )
        {
            _locateFailedAt = _clock.GetUtcNow( );
            binary = null;

            return false;
        }
    }

    private string? ConfiguredBinaryPath( ) => Id switch
    {
        AgentBackendIds.ClaudeCode => _options.ClaudeBinaryPath,
        AgentBackendIds.Cursor => _options.CursorBinaryPath,
        AgentBackendIds.Grok => _options.GrokBinaryPath,
        _ => null
    };

    private string DescribeModelListFailure( (int ExitCode, string StandardOutput) output )
    {
        if ( output.ExitCode == 0 )
        {
            return $"{Profile.DisplayName} returned no usable model list.";
        }

        return $"{Profile.DisplayName} could not list models; sign in first.";
    }

    [MemberNotNullWhen( true, nameof( _binary ) )]
    private bool IsBinaryUnchanged( )
    {
        return _binary is not null && CodexBinaryLocator.HasSameIdentity( _binary.CanonicalPath, _binary.IdentityToken );
    }

    private bool HasRecentLocateFailure( )
    {
        return _binary is null && _clock.GetUtcNow( ) - _locateFailedAt < TimeSpan.FromSeconds( 30 );
    }
}
