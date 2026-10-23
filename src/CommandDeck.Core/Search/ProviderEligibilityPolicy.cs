namespace CommandDeck;

// Product participation rules are separate from scheduling and final result filtering.
public static class ProviderEligibilityPolicy
{
    public static IEnumerable<ICommandProvider> Select( IReadOnlyList<ICommandProvider> providers, ParsedQuery query, LauncherPreferences preferences,
        string? herdrWorkspaceId, string? omarchyMenuId = null )
    {
        var liveSourcesEnabled = IsSearchSourceEnabled( preferences, SourceIds.WindowsWorkspaces );

        // A disabled Herdr source never schedules the provider, even through an
        // explicit herdr: filter; the provider and final gate repeat the check.
        var herdrEnabled = IsSearchSourceEnabled( preferences, SourceIds.Herdr );
        var herdrExcluded = !herdrEnabled || !IncludesHerdr( query );
        var candidates = providers.Where( provider => !herdrExcluded || provider.Id != ProviderIds.Herdr );

        // HEY mail is searched only when asked for (hey:, mail:, or @hey); plain
        // words get one trailing "Search HEY for" row. A disabled HEY source stops both.
        var heyEnabled = IsSearchSourceEnabled( preferences, SourceIds.Hey );

        candidates = candidates.Where( provider =>
            provider.Id != ProviderIds.Hey || heyEnabled && (HeySearch.IsRequested( query ) || HeySearch.OffersFallback( query )) );

        candidates = candidates.Where( provider => ServesScope( provider, herdrWorkspaceId, omarchyMenuId ) );

        // Within a submenu, source filters only narrow its rows; the
        // coordinator's final gate applies them.
        if ( omarchyMenuId is not null )
        {
            return candidates;
        }

        var includedProviders = GetIncludedProviderIds( query );
        if ( includedProviders.Length > 0 )
        {
            return candidates.Where( provider => includedProviders.Contains( provider.Id, StringComparer.OrdinalIgnoreCase ) );
        }

        if ( !string.IsNullOrWhiteSpace( query.ProviderFilter ) )
        {
            return candidates.Where( provider => string.Equals( provider.Id, query.ProviderFilter, StringComparison.OrdinalIgnoreCase ) );
        }

        if ( query.IsCommandMode )
        {
            return candidates.Where( provider =>
                provider.Id is ProviderIds.Catalog or ProviderIds.Extensions or ProviderIds.DesktopControls or ProviderIds.RunCommand ||
                liveSourcesEnabled && provider.Id == ProviderIds.Hyprland );
        }

        var applicationsIncluded = IncludesApplications( query );

        return candidates.Where( provider =>
            provider.Id is not (ProviderIds.Assistant or ProviderIds.RunCommand) &&
            (applicationsIncluded || provider.Id != ProviderIds.ApplicationsFastPath) &&
            (query.HasExplicitFilter || liveSourcesEnabled || provider.Id != ProviderIds.Hyprland) );
    }

    // A Herdr space is browsed by the Herdr provider alone. An Omarchy submenu
    // is listed by its own provider alone, which never answers another search.
    private static bool ServesScope( ICommandProvider provider, string? herdrWorkspaceId, string? omarchyMenuId ) =>
        (herdrWorkspaceId is null || provider.Id == ProviderIds.Herdr) &&
        (omarchyMenuId is null ? provider.Id != ProviderIds.OmarchyMenu : provider.Id == ProviderIds.OmarchyMenu);

    // A negative herdr: filter, or -hspace: together with -htab:, discards every
    // Herdr row, so the two list processes are not worth starting.
    private static bool IncludesHerdr( ParsedQuery query )
    {
        var excluded = GetExcludedHerdrFilters( query );
        if ( excluded.Any( static filter => filter.Kind is null ) )
        {
            return false;
        }

        return !(excluded.Any( static filter => filter.Kind == ResultKinds.Workspace ) && excluded.Any( static filter => filter.Kind == ResultKinds.Tab ));
    }

    // The application-only catalog lookup only pays off when its results survive
    // the filter gate; a `file:` or `-app:` query would discard them.
    private static bool IncludesApplications( ParsedQuery query )
    {
        if ( ExcludesApplications( query ) )
        {
            return false;
        }

        return !query.ExcludedFilters.Any( static filter => filter is { SourceId: SourceIds.Applications, Kind: null or ResultKinds.Application } );
    }

    private static string[] GetIncludedProviderIds( ParsedQuery query )
    {
        return query
            .IncludedFilters.Select( static value => value.ProviderId )
            .Where( static value => !string.IsNullOrWhiteSpace( value ) )
            .Select( static value => value! )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToArray( );
    }

    private static FilterDescriptor[] GetExcludedHerdrFilters( ParsedQuery query )
    {
        return query
            .ExcludedFilters.Where( static filter => filter is { SourceId: SourceIds.Herdr, Id: not HerdrSearch.AttentionFilterId } )
            .ToArray( );
    }

    private static bool ExcludesApplications( ParsedQuery query )
    {
        return query.IncludedFilters.Count > 0 && !query.IncludedFilters.Any( static filter => filter.SourceId == SourceIds.Applications );
    }

    private static bool IsSearchSourceEnabled( LauncherPreferences preferences, string sourceId ) =>
        preferences.Sources.Any( source => source.SourceId == sourceId && source.EnabledInSearch );
}
