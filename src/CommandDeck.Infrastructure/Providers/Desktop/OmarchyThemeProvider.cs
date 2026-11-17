namespace CommandDeck;

/// <summary>
/// The installed Omarchy themes, listed only through <c>theme:</c>, so an
/// ordinary keystroke reads no theme folder. Plain search for "theme" offers one
/// Switch Theme row that opens the list. A theme row previews the theme's image
/// and marks the theme in use; Enter applies it with <c>omarchy-theme-set</c>,
/// started detached in the user session as Omarchy's own picker starts it.
/// </summary>
public sealed class OmarchyThemeProvider( OmarchyThemePaths? paths = null ) : ICommandProvider
{
    public const string ApplyActionId = "apply";
    public const string OpenFolderActionId = "open-folder";
    public const string CopyNameActionId = "copy-name";
    public const string BrowseResultId = "theme:browse";
    public const string ResultIdPrefix = "theme:";
    public const string ApplyExecutable = "omarchy-theme-set";

    private const string BrowseActionId = "browse";
    private const string BrowseTitle = "Switch Theme";
    private const string BrowseKeywords = "theme themes switch change set style color colors colour scheme palette appearance look dark light";
    private const string ThemePrefix = "theme ";
    private const double MinimumFuzzyScore = 0.53;
    private const int MinimumQueryCharacters = 2;
    private const int MaximumResults = 200;
    private const double ListScore = 0.9;
    private const double RankStep = 0.0001;

    private readonly OmarchyThemePaths _paths = paths ?? OmarchyThemePaths.Default( );

    public string Id => ProviderIds.Themes;

    public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( query );
        ArgumentNullException.ThrowIfNull( request );

        cancellationToken.ThrowIfCancellationRequested( );

        if ( ShouldSkipSearch( query, request ) || !OmarchyThemeCatalog.IsAvailable( _paths ) )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        var text = query.Text.Trim( );
        var listing = query.IncludedFilters.Any( static filter => filter.SourceId == SourceIds.Themes );
        IReadOnlyList<SearchResult> results = listing
            ? List( text, Math.Clamp( request.Limit, 1, MaximumResults ), cancellationToken )
            : Browse( text );

        return ValueTask.FromResult( results );
    }

    // "theme tokyo" opens the list already filtered; "theme" or "switch theme"
    // opens it whole.
    private static IReadOnlyList<SearchResult> Browse( string text )
    {
        if ( text.Length < MinimumQueryCharacters )
        {
            return [];
        }

        if ( text.StartsWith( ThemePrefix, StringComparison.OrdinalIgnoreCase ) && text.Length > ThemePrefix.Length )
        {
            var name = text[ThemePrefix.Length..]
                .Trim( );

            return [BrowseRow( $"{BrowseTitle}: {name}", name, 1 )];
        }

        var score = DesktopControlsProvider.Match( text, BrowseTitle, BrowseKeywords );

        return score > 0 ? [BrowseRow( BrowseTitle, string.Empty, score )] : [];
    }

    private List<SearchResult> List( string text, int limit, CancellationToken cancellationToken )
    {
        var themes = OmarchyThemeCatalog.Read( _paths, cancellationToken );

        // Every theme is scored before the limit applies, so weaker matches
        // earlier in the alphabet cannot push out the best one. Equal scores,
        // and the whole list before anything is typed, keep alphabetical order.
        return themes
            .Select( ( theme, rank ) => (Theme: theme, Score: text.Length == 0 ? ListScore : Match( text, theme ), Rank: rank) )
            .Where( static candidate => candidate.Score > 0 )
            .Select( static candidate => (candidate.Theme, Score: candidate.Score - (candidate.Rank * RankStep)) )
            .OrderByDescending( static candidate => candidate.Score )
            .Take( limit )
            .Select( static candidate => BuildRow( candidate.Theme, candidate.Score ) )
            .ToList( );
    }

    private static double Match( string text, OmarchyTheme theme )
    {
        var score = Math.Max( FuzzyMatcher.Score( text, theme.DisplayName ), FuzzyMatcher.Score( text, theme.Name ) );

        return score >= MinimumFuzzyScore ? score : 0;
    }

    private static SearchResult BuildRow( OmarchyTheme theme, double score )
    {
        Dictionary<string, string> metadata = new( StringComparer.Ordinal ) { [ResultMetadataKeys.ThemeName] = theme.Name };
        if ( theme.PreviewPath is not null )
        {
            metadata[ResultMetadataKeys.ThemePreview] = theme.PreviewPath;
        }

        if ( theme.IsCurrent )
        {
            metadata[ResultMetadataKeys.ThemeCurrent] = "true";
        }

        return new SearchResult
        {
            Id = ResultIdPrefix + theme.Name,
            Kind = ResultKinds.Theme,
            Title = theme.DisplayName,
            Subtitle = theme.IsUserTheme ? "Your theme" : null,
            Icon = "preferences-desktop-theme",
            ProviderId = ProviderIds.Themes,
            SourceId = SourceIds.Themes,
            Score = score,
            Details =
            [
                new DetailField( "Theme", theme.DisplayName ),
                new DetailField( "Status", theme.IsCurrent ? "Current theme" : "Installed" ),
                new DetailField( "Source", theme.IsUserTheme ? "Your themes" : "Omarchy" ),
                new DetailField( "Folder", theme.Directory, Monospace: true )
            ],
            Actions =
            [
                new CommandAction
                {
                    Id = ApplyActionId,
                    Title = theme.IsCurrent ? "Reapply Theme" : "Apply Theme",
                    Kind = ActionKinds.LaunchArgv,
                    CloseAfterAction = true,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                    {
                        [ActionArgumentNames.Argv] = LaunchArgumentCodec.Encode( [ApplyExecutable, theme.Name] ),
                        [ActionArgumentNames.Terminal] = "false"
                    }
                },
                new CommandAction
                {
                    Id = OpenFolderActionId,
                    Title = "Open Theme Folder",
                    Kind = ActionKinds.OpenPath,
                    Arguments = new PathActionPayload( theme.Directory ).ToArguments( )
                },
                new CommandAction
                {
                    Id = CopyNameActionId,
                    Title = "Copy Theme Name",
                    Kind = ActionKinds.CopyText,
                    Arguments = new TextActionPayload( theme.Name ).ToArguments( )
                }
            ],
            Metadata = metadata
        };
    }

    private static SearchResult BrowseRow( string title, string query, double score ) => new( )
    {
        Id = BrowseResultId,
        Kind = ResultKinds.CustomCommand,
        Title = title,
        Subtitle = "Omarchy",
        Icon = "preferences-desktop-theme",
        ProviderId = ProviderIds.Themes,
        SourceId = SourceIds.Themes,
        Score = score,
        Actions =
        [
            new CommandAction
            {
                Id = BrowseActionId,
                Title = "Switch Theme",
                Kind = ActionKinds.ApplyQuery,
                Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                {
                    [ActionArgumentNames.Query] = query, [ActionArgumentNames.FilterId] = SearchFilterIds.Themes
                }
            }
        ]
    };

    private static bool ShouldSkipSearch( ParsedQuery query, SearchRequest request )
    {
        return query.IsCalculator ||
               query.IsAiMode ||
               request.EnabledSearchSources is not null && !request.EnabledSearchSources.Contains( SourceIds.Themes ) ||
               query.ExcludedFilters.Any( static filter => filter.SourceId == SourceIds.Themes );
    }
}
