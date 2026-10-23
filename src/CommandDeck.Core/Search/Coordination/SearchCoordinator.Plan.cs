namespace CommandDeck;

public sealed partial class SearchCoordinator
{
    private const int MaximumRegistryRetries = 3;
    private const int ProviderCandidateMultiplier = 4;

    // The plan freezes the inputs used for eligibility and ranking. Preferences,
    // aliases, and hidden IDs are separate store reads, not one atomic database snapshot.
    private sealed record SearchPlan(
        ParsedQuery Parsed,
        bool IsInlineCalculator,
        LauncherPreferences Preferences,
        SearchRequest EffectiveRequest,
        SearchRequest ProviderRequest,
        SearchRequest FolderRequest,
        IReadOnlyList<ICommandProvider> SelectedProviders,
        HashSet<string> EnabledSources,
        int Limit );

    private async ValueTask<SearchPlan> BuildPlanAsync( SearchRequest request, SearchPurpose purpose, CancellationToken cancellationToken )
    {
        request.IsPrivacyClassified = false;
        request.ContainsPrivateQuery = false;
        var limit = Math.Clamp( request.Limit <= 0 ? _options.DefaultLimit : request.Limit, 1, _options.MaximumLimit );
        var historyVersion = _history.Version;

        var preferences = await LoadPreferencesAsync( cancellationToken )
            .ConfigureAwait( false );

        var registry = _contributions?.Snapshot;
        for ( var attempt = 0; HasRegistryChanged( preferences, registry ); attempt++ )
        {
            if ( attempt == MaximumRegistryRetries )
            {
                throw new InvalidOperationException( "The source registry changed during search. Try again." );
            }

            preferences = await _preferences!
                .GetPreferencesAsync( cancellationToken )
                .ConfigureAwait( false );

            registry = _contributions?.Snapshot;
        }

        // Settings choices apply from this search on, wherever
        // they were saved from.
        if ( _preferences is not null )
        {
            _history.ApplyRead( preferences, historyVersion );
        }

        var filterAliases = await LoadFilterAliasesAsync( cancellationToken )
            .ConfigureAwait( false );

        var aliasMap = filterAliases.ToDictionary( static value => value.Alias, static value => value.FilterId, StringComparer.OrdinalIgnoreCase );
        foreach ( var engine in _options.WebEngines )
        {
            if ( !string.IsNullOrWhiteSpace( engine.Alias ) )
            {
                aliasMap.TryAdd( NormalizeFilterAlias( engine.Alias ), SearchFilterIds.WebSearch );
            }
        }

        var parsed = QueryParser.Parse( request.Query, request.Expression, aliasMap, registry );
        var isInlineCalculator = CalculatorInput.IsInlineCandidate( parsed );
        request.ContainsPrivateQuery = PrivateSources.ReferencesPrivateSource( parsed );
        request.IsPrivacyClassified = true;
        var enabledSources = GetEnabledSources( preferences );
        var effectiveRequest = WithoutDisabledContext( request );

        var providerLimit = Math.Min( _options.MaximumLimit, Math.Max( limit, limit * ProviderCandidateMultiplier ) );

        var providerRequest = CreateProviderRequest( request, effectiveRequest, enabledSources, registry, providerLimit );

        var folderRequest = CreateProviderRequest( request, effectiveRequest, enabledSources, registry, request.Limit );

        var selected = SelectProviders( parsed, preferences, request, purpose, isInlineCalculator )
            .ToArray( );

        return new SearchPlan( parsed, isInlineCalculator, preferences, effectiveRequest, providerRequest, folderRequest,
            selected, enabledSources, limit );
    }

    private async ValueTask<IReadOnlyList<FilterAlias>> LoadFilterAliasesAsync( CancellationToken cancellationToken )
    {
        IReadOnlyList<FilterAlias> filterAliases;
        if ( _preferences is null )
        {
            filterAliases = [];
        }
        else
        {
            filterAliases = await _preferences
                .GetFilterAliasesAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        return filterAliases;
    }

    private async ValueTask<LauncherPreferences> LoadPreferencesAsync( CancellationToken cancellationToken )
    {
        LauncherPreferences preferences;
        if ( _preferences is null )
        {
            preferences = LauncherPreferences.CreateDefault( );
        }
        else
        {
            preferences = await _preferences
                .GetPreferencesAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        return preferences;
    }

    private static SearchRequest
        CreateProviderRequest( SearchRequest request, SearchRequest effectiveRequest, HashSet<string> enabledSources, ContributionRegistrySnapshot? registry,
            int limit ) => new( )
            {
                SearchOwner = request.SearchOwner,
                SearchGeneration = request.SearchGeneration,
                EnabledSearchSources = enabledSources.ToArray( ),
                ContributionSnapshot = registry,
                BrowseDirectory = request.BrowseDirectory,
                HerdrWorkspaceId = request.HerdrWorkspaceId,
                OmarchyMenuId = request.OmarchyMenuId,
                BrowseOffset = request.BrowseOffset,
                Query = request.Query,
                Expression = request.Expression,
                Limit = limit,
                ActiveApplication = effectiveRequest.ActiveApplication,
                ActiveWorkspace = effectiveRequest.ActiveWorkspace,
                CurrentDirectory = effectiveRequest.CurrentDirectory,
                SelectedProject = effectiveRequest.SelectedProject
            };

    private static HashSet<string> GetEnabledSources( LauncherPreferences preferences )
    {
        return preferences
            .Sources.Where( static value => value.EnabledInSearch )
            .Select( static value => value.SourceId )
            .ToHashSet( StringComparer.Ordinal );
    }

    private static bool HasRegistryChanged( LauncherPreferences preferences, ContributionRegistrySnapshot? registry ) =>
        registry is not null && preferences.RegistryRevision != registry.Revision;

    private static string NormalizeFilterAlias( string alias ) => alias
        .Trim( )
        .TrimEnd( ':' );
}
