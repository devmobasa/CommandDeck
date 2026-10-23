namespace CommandDeck;

public sealed partial class SearchCoordinator
{
    private const double MaximumLiveTimeAwareScore = 1.2;

    private List<RankingContribution> ContextContributions( SearchResult result, SearchRequest request )
    {
        var options = _options.Ranking.Context;
        if ( !options.Enabled )
        {
            return [];
        }

        List<RankingContribution> contributions = [];
        var applicationMatch = MatchesActiveApplication( result, request );
        var workspaceMatch = MatchesActiveWorkspace( result, request );

        if ( applicationMatch && options.ActiveApplicationBoost > 0 )
        {
            contributions.Add( new( RankingContributionKinds.Context, "Active application context", options.ActiveApplicationBoost ) );
        }

        if ( workspaceMatch && options.ActiveWorkspaceBoost > 0 )
        {
            contributions.Add( new( RankingContributionKinds.Context, "Active workspace context", options.ActiveWorkspaceBoost ) );
        }

        if ( PathMatchesContext( result, request.CurrentDirectory ) && options.CurrentDirectoryBoost > 0 )
        {
            contributions.Add( new( RankingContributionKinds.Context, "Current directory context", options.CurrentDirectoryBoost ) );
        }

        if ( PathMatchesContext( result, request.SelectedProject ) && options.SelectedProjectBoost > 0 )
        {
            contributions.Add( new( RankingContributionKinds.Context, "Selected project context", options.SelectedProjectBoost ) );
        }

        return contributions;
    }

    private static bool PathMatchesContext( SearchResult result, string? contextPath )
    {
        if ( HasInvalidContextPath( result, contextPath, out var resultPath ) )
        {
            return false;
        }

        var context = contextPath.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
        if ( HasInvalidContextPaths( resultPath, context ) )
        {
            return false;
        }

        return string.Equals( resultPath, context, StringComparison.Ordinal ) ||
               resultPath.StartsWith( context + Path.DirectorySeparatorChar, StringComparison.Ordinal );
    }

    private async ValueTask<IReadOnlyDictionary<string, UsageSignal>> ReadLiveUsageAsync( IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken )
    {
        if ( IsLiveLearningUnavailable( ) )
        {
            return new Dictionary<string, UsageSignal>( StringComparer.Ordinal );
        }

        var ids = GetLiveUsageIds( results );
        if ( ids.Length == 0 )
        {
            return new Dictionary<string, UsageSignal>( StringComparer.Ordinal );
        }
        else
        {
            return await _usageHistory
                .GetUsageSignalsAsync( ids, cancellationToken )
                .ConfigureAwait( false );
        }
    }

    private SearchResult ApplyLiveTimeAwareScore( SearchResult result, IReadOnlyDictionary<string, UsageSignal> usage, DateTimeOffset? now = null,
        ParsedQuery? query = null )
    {
        // Herdr inventory browsing keeps the provider's focused/ordinal order.
        // Accepted usage affects its text matches only.
        if ( IsEmptyHerdrQuery( result, query ) )
        {
            return result;
        }

        if ( CannotApplyLiveScore( result, usage, out var signal ) )
        {
            return result;
        }

        var boost = GetLiveBoost( result, now, signal );
        if ( boost <= 0 )
        {
            return result;
        }

        var multiplier = TimeAwareRankingPolicy.Multiplier( result.Kind, _options.Ranking.TimeAware );
        var score = Math.Clamp( result.Score + boost, 0, MaximumLiveTimeAwareScore );
        List<RankingContribution> contributions = [.. result.Ranking.Contributions];

        contributions.Add( new( RankingContributionKinds.Frecency, $"Recent {TimeAwareRankingPolicy.Category( result.Kind )} use ×{multiplier:0.##}", boost ) );
        contributions.Add( new( RankingContributionKinds.Adaptive, $"Adaptive history expires after {_options.Ranking.Adaptive.RetentionDays} days", 0 ) );

        return Copy( result, score: score,
            ranking: new RankingExplanation
            {
                FinalScore = score,
                Contributions = contributions,
                Matches = [.. result.Ranking.Matches],
                Policies = [.. result.Ranking.Policies]
            } );
    }

    private static bool MatchesActiveApplication( SearchResult result, SearchRequest request )
    {
        return !string.IsNullOrWhiteSpace( request.ActiveApplication ) &&
               ApplicationContextKeys.Any( key =>
                   result.Metadata.TryGetValue( key, out var value ) && string.Equals( value, request.ActiveApplication, StringComparison.OrdinalIgnoreCase ) );
    }

    private static bool MatchesActiveWorkspace( SearchResult result, SearchRequest request )
    {
        return !string.IsNullOrWhiteSpace( request.ActiveWorkspace ) &&
               result.Metadata.TryGetValue( ResultMetadataKeys.Workspace, out var workspace ) &&
               string.Equals( workspace, request.ActiveWorkspace, StringComparison.OrdinalIgnoreCase );
    }

    private static bool HasInvalidContextPaths( string resultPath, string context )
    {
        return context.Length == 0 || !Path.IsPathRooted( context ) || !Path.IsPathRooted( resultPath );
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen( false, nameof( _usageHistory ) )]
    private bool IsLiveLearningUnavailable( )
    {
        return _usageHistory is null || !_history.LearnFromUse || !_options.Ranking.Adaptive.Enabled || !_options.Ranking.TimeAware.Enabled;
    }

    private static string[] GetLiveUsageIds( IReadOnlyList<SearchResult> results )
    {
        return results
            .Where( static result => result.Kind is ResultKinds.Workspace or ResultKinds.Window or ResultKinds.Tab )
            .Select( static result => result.Id )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );
    }

    private static bool IsEmptyHerdrQuery( SearchResult result, ParsedQuery? query )
    {
        return result.ProviderId == ProviderIds.Herdr && query?.Text.Length == 0;
    }

    private double GetLiveBoost( SearchResult result, DateTimeOffset? now, UsageSignal signal )
    {
        return TimeAwareRankingPolicy.Score( result.Kind, signal.UseCount, signal.LastUsedUnixMilliseconds, now ?? _timeProvider.GetUtcNow( ),
            _options.Ranking.TimeAware );
    }

    private static bool HasInvalidContextPath( SearchResult result, [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? contextPath,
        [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] out string? resultPath )
    {
        resultPath = default;

        return string.IsNullOrWhiteSpace( contextPath ) ||
               !result.Metadata.TryGetValue( ResultMetadataKeys.Path, out resultPath ) ||
               string.IsNullOrWhiteSpace( resultPath );
    }

    private static bool CannotApplyLiveScore( SearchResult result, IReadOnlyDictionary<string, UsageSignal> usage, out UsageSignal signal )
    {
        signal = default;

        return result.Kind is not ResultKinds.Window and not ResultKinds.Workspace and not ResultKinds.Tab ||
               result.Ranking.Contributions.Any( static contribution => contribution.Kind == RankingContributionKinds.Frecency ) ||
               !usage.TryGetValue( result.Id, out signal );
    }
}
