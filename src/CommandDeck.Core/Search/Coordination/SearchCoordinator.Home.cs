namespace CommandDeck;

public sealed partial class SearchCoordinator
{
    private const string ApplySavedSearchActionId = "apply";
    private const string OmarchyRootResultId = "omarchy-route:root";
    private const string OmarchyHomeQuery = "oma:";
    private const int OmarchyHomeLimit = 8;

    // The live Windows & Workspaces and Herdr sections each show at most this many rows.
    private const int LiveHomeSectionLimit = 4;

    private async ValueTask<SearchOutcome> BuildHomeAsync( SearchRequest request, LauncherPreferences preferences, int limit,
        CancellationToken cancellationToken )
    {
        // The catalog rows and the live Windows & Workspaces and Herdr sections do
        // not depend on each other, so they run together and Home waits for the
        // slowest instead of their sum. The live providers start first: they run
        // on worker threads, while the catalog query completes synchronously.
        // Rows are still assembled in section order.
        var windowSection = ReadWindowHomeSectionAsync( request, preferences, cancellationToken );
        var herdrSection = ReadHerdrHomeSectionAsync( preferences, cancellationToken );

        var catalogRows = _catalog!
            .GetHomeResultsAsync( preferences, limit, cancellationToken )
            .AsTask( );

        await Task
            .WhenAll( catalogRows, windowSection, herdrSection )
            .ConfigureAwait( false );

        List<SearchResult> home = [.. await catalogRows.ConfigureAwait( false )];
        if ( _preferences is not null )
        {
            var savedSearches = await _preferences
                .GetSavedSearchesAsync( cancellationToken )
                .ConfigureAwait( false );

            home.AddRange( GetSavedHomeResults( savedSearches ) );
        }

        var ids = GetHomeIds( home );
        var windows = await windowSection.ConfigureAwait( false );
        AddHomeSection( home, ids, windows.Results, ResultSections.WindowsWorkspaces );
        var herdr = await herdrSection.ConfigureAwait( false );
        AddHomeSection( home, ids, herdr.Results, ResultSections.Herdr );
        var incompleteProviderCount = windows.IncompleteProviderCount + herdr.IncompleteProviderCount;
        var omarchyPreference = preferences.Sources.FirstOrDefault( static value => value.SourceId == SourceIds.OmarchyControl );
        if ( omarchyPreference?.EnabledOnHome == true )
        {
            var omarchy = await SearchOmarchyHomeAsync( cancellationToken )
                .ConfigureAwait( false );

            foreach ( var result in GetOmarchyRootResults( omarchy ) )
            {
                if ( ids.Add( result.Id ) )
                {
                    home.Add( WithSection( result, ResultSections.Omarchy ) );
                }
            }
        }

        if ( HomeSupplement is { } supplement )
        {
            foreach ( var result in await supplement( cancellationToken )
                         .ConfigureAwait( false ) )
            {
                if ( ids.Add( result.Id ) )
                {
                    home.Add( result );
                }
            }
        }

        // SQL-backed Home rows are already scoped; the supplement and live rows
        // are not, so the hidden set is applied once more before the budget.
        var hidden = await HiddenStableIdsAsync( cancellationToken )
            .ConfigureAwait( false );

        return new SearchOutcome( OrderVisibleHomeResults( home, hidden, limit ), incompleteProviderCount );
    }

    private static void AddHomeSection( List<SearchResult> home, HashSet<string> ids, IReadOnlyList<SearchResult> results, ResultSection section )
    {
        foreach ( var result in results )
        {
            if ( ids.Add( result.Id ) )
            {
                home.Add( WithSection( result, section ) );
            }
        }
    }

    private async Task<SearchOutcome> ReadWindowHomeSectionAsync( SearchRequest request, LauncherPreferences preferences, CancellationToken cancellationToken )
    {
        var livePreference = preferences.Sources.FirstOrDefault( static value => value.SourceId == SourceIds.WindowsWorkspaces );
        var hyprland = _providers.FirstOrDefault( static value => value.Id == ProviderIds.Hyprland );
        if ( livePreference?.EnabledOnHome != true || hyprland is null )
        {
            return new SearchOutcome( [], 0 );
        }

        var live = await RunLiveHomeProviderAsync( request, hyprland, cancellationToken )
            .ConfigureAwait( false );

        var liveUsage = await ReadLiveUsageAsync( live.Results, cancellationToken )
            .ConfigureAwait( false );

        return new SearchOutcome( [
            .. live
                .Results.Take( LiveHomeSectionLimit )
                .Select( result => ApplyLiveTimeAwareScore( result, liveUsage ) )
        ], live.IncompleteProviderCount );
    }

    // Off by default; only an explicit Home preference runs the Herdr inventory
    // on the launcher-open path, and it is bounded to four rows.
    private async Task<SearchOutcome> ReadHerdrHomeSectionAsync( LauncherPreferences preferences, CancellationToken cancellationToken )
    {
        var preference = preferences.Sources.FirstOrDefault( static value => value.SourceId == SourceIds.Herdr );
        var herdr = _providers.FirstOrDefault( static value => value.Id == ProviderIds.Herdr );
        if ( preference?.EnabledOnHome != true || herdr is null )
        {
            return new SearchOutcome( [], 0 );
        }

        var live = await RunHerdrHomeProviderAsync( herdr, cancellationToken )
            .ConfigureAwait( false );

        return new SearchOutcome( [.. live.Results.Take( LiveHomeSectionLimit )], live.IncompleteProviderCount );
    }

    private static HashSet<string> GetHomeIds( List<SearchResult> home )
    {
        return home
            .Select( static value => value.Id )
            .ToHashSet( StringComparer.Ordinal );
    }

    private async ValueTask<IReadOnlyList<SearchResult>> SearchOmarchyHomeAsync( CancellationToken cancellationToken )
    {
        return await _catalog!
            .SearchAsync( QueryParser.Parse( OmarchyHomeQuery ), OmarchyHomeLimit, cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask<SearchOutcome> RunLiveHomeProviderAsync( SearchRequest request, ICommandProvider hyprland, CancellationToken cancellationToken )
    {
        return await RunProviderAsync( hyprland, QueryParser.Parse( string.Empty ),
                new SearchRequest
                {
                    Limit = LiveHomeSectionLimit,
                    ActiveApplication = request.ActiveApplication,
                    ActiveWorkspace = request.ActiveWorkspace,
                    CurrentDirectory = request.CurrentDirectory,
                    SelectedProject = request.SelectedProject
                }, cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask<SearchOutcome> RunHerdrHomeProviderAsync( ICommandProvider herdr, CancellationToken cancellationToken )
    {
        return await RunProviderAsync( herdr, QueryParser.Parse( string.Empty ),
                new SearchRequest { Limit = LiveHomeSectionLimit, EnabledSearchSources = [SourceIds.Herdr] }, cancellationToken )
            .ConfigureAwait( false );
    }

    private static IEnumerable<SearchResult> GetSavedHomeResults( IReadOnlyList<SavedSearch> savedSearches ) => savedSearches
        .Where( static value => value.ShowOnHome )
        .Select( CreateSavedHomeResult );

    private static SearchResult CreateSavedHomeResult( SavedSearch value ) => new SearchResult
    {
        Id = value.Id,
        Kind = ResultKinds.SavedSearch,
        Title = value.Name,
        Subtitle = GetSavedSearchSubtitle( value ),
        Icon = "system-search",
        ProviderId = ProviderIds.SavedSearch,
        SourceId = SourceIds.CommandsQuicklinks,
        Section = ResultSections.SavedSearches,
        SavedSearch = value,
        Actions =
        [
            new CommandAction { Id = ApplySavedSearchActionId, Title = "Apply Search", Kind = ActionKinds.ApplySavedSearch }
        ]
    };

    private static string GetSavedSearchSubtitle( SavedSearch value )
    {
        if ( value.Shortcut is null )
        {
            return "Saved search";
        }

        return $"Saved search · {value.Shortcut}";
    }

    private static IEnumerable<SearchResult> GetOmarchyRootResults( IReadOnlyList<SearchResult> results ) => results
        .Where( static value => value.Id == OmarchyRootResultId )
        .Take( 1 );

    private static SearchResult[] OrderVisibleHomeResults( List<SearchResult> home, IReadOnlySet<string> hidden, int limit ) => home
        .Where( value => !hidden.Contains( UsageId( value ) ) )
        .OrderBy( static value => value.Section?.Order ?? int.MaxValue )
        .Take( limit )
        .ToArray( );
}
