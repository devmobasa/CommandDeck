namespace CommandDeck;

/// <summary>
/// Built-in desktop commands found from plain search: typing "volume" or "left
/// half" offers the matching controls, and <c>ctl:</c> lists them all. A command
/// whose tool is not on PATH is left out rather than shown as an error. Bare Home
/// and one-letter queries do no work.
/// </summary>
public sealed partial class DesktopControlsProvider : ICommandProvider
{
    private const string WindowResultPrefix = "window-";
    private const string UnavailableResultSuffix = "-unavailable";

    private const string RunActionId = "run";
    private const double MultiTermScoreWeight = 0.9;

    private const int MinimumQueryCharacters = 2;
    private const double MinimumTermScore = 0.53;
    private const double KeywordWeight = 0.85;
    private const double BrowseScore = 0.5;

    private readonly Func<string, bool> _toolAvailable;
    private readonly ExternalProcessRunner _hyprland;
    private readonly Func<string, string?> _hyprlandEnvironment;
    private readonly ExternalProcessRunner _stateProbe;

    public DesktopControlsProvider( Func<string, bool>? toolAvailable = null, ExternalProcessRunner? hyprlandRunner = null,
        ExternalProcessRunner? stateRunner = null )
    {
        _toolAvailable = toolAvailable ?? ProcessRunner.ExistsOnPath;
        _hyprland = hyprlandRunner ?? DefaultRunAsync;
        _hyprlandEnvironment = HyprlandSocket.EnvironmentFor( hyprlandRunner );
        _stateProbe = stateRunner ?? DefaultRunAsync;
    }

    public string Id => ProviderIds.DesktopControls;

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( query );
        ArgumentNullException.ThrowIfNull( request );

        cancellationToken.ThrowIfCancellationRequested( );
        var text = query.Text.Trim( );
        var browsing = query.IncludedFilters.Any( static filter => filter.SourceId == SourceIds.DesktopControls );
        if ( ShouldSkipSearch( query, request, text, browsing ) )
        {
            return [];
        }

        Dictionary<string, bool> tools = new( StringComparer.Ordinal );
        List<SearchResult> results = [];

        List<(DesktopControl Control, double Score)> matches = [];
        foreach ( var control in DesktopControlCatalog.System )
        {
            var score = Score( text, control.Title, control.SearchKeywords );
            if ( score > 0 && control.RequiredTools.All( tool => GetOrCacheToolAvailability( tool, tools ) ) )
            {
                matches.Add( (control, score) );
            }
        }

        // State probes run while the window rows read Hyprland.
        var statesTask = ReadStatesAsync( matches.Select( static match => match.Control ), cancellationToken )
            .AsTask( );

        List<SearchResult> others = [];
        AddColorPickerRow( text, tools, others );

        await AddWindowRowsAsync( text, tools, others, cancellationToken )
            .ConfigureAwait( false );

        var states = await statesTask.ConfigureAwait( false );
        foreach ( (var control, var score) in matches )
        {
            results.Add( BuildRow( control, score, states.GetValueOrDefault( control.Id ) ) );
        }

        results.AddRange( others );

        return results;
    }

    private static double Score( string text, string title, string keywords ) =>
        text.Length == 0 ? BrowseScore : Match( text, title, keywords );

    // A disabled or excluded source, calculator, and assistant input never list
    // desktop commands.
    internal static bool Participates( ParsedQuery query, SearchRequest request ) =>
        query is { IsCalculator: false, IsAiMode: false } &&
        (request.EnabledSearchSources is null || request.EnabledSearchSources.Contains( SourceIds.DesktopControls )) &&
        !query.ExcludedFilters.Any( static filter => (filter is { SourceId: SourceIds.DesktopControls, Kind: null }) );

    // Every word must match the title or, more weakly, the keywords; a whole-title
    // match keeps its own score. Subsequence noise below a typo match is ignored.
    internal static double Match( string text, string title, string keywords )
    {
        var whole = FuzzyMatcher.Score( text, title );
        var terms = text.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        var total = 0.0;

        foreach ( var term in terms )
        {
            var best = Math.Max( FuzzyMatcher.Score( term, title ), FuzzyMatcher.Score( term, keywords ) * KeywordWeight );
            if ( best < MinimumTermScore )
            {
                return whole >= MinimumTermScore ? whole : 0;
            }

            total += best;
        }

        var average = terms.Length == 0 ? 0 : total / terms.Length * MultiTermScoreWeight;

        return Math.Max( whole >= MinimumTermScore ? whole : 0, average );
    }

    private bool GetOrCacheToolAvailability( string tool, Dictionary<string, bool> tools )
    {
        if ( !tools.TryGetValue( tool, out var available ) )
        {
            available = _toolAvailable( tool );
            tools[tool] = available;
        }

        return available;
    }

    // The color picker is asked for by name, so a missing tool is reported on
    // its row instead of hiding it.
    private void AddColorPickerRow( string text, Dictionary<string, bool> tools, List<SearchResult> results )
    {
        var picker = DesktopControlCatalog.ColorPicker;
        var score = Score( text, picker.Title, picker.Keywords );
        if ( score <= 0 )
        {
            return;
        }

        var missing = picker.RequiredTools.FirstOrDefault( tool => !GetOrCacheToolAvailability( tool, tools ) );

        results.Add( CreatePickerResult( picker, score, missing ) );
    }

    // Each probe runs once per search, in parallel; two radios share rfkill's
    // listing. Only matched toggles are probed, so other searches start nothing.
    private async ValueTask<Dictionary<string, bool?>> ReadStatesAsync( IEnumerable<DesktopControl> controls, CancellationToken cancellationToken )
    {
        var toggles = controls
            .Where( static control => control.State is not null )
            .ToArray( );

        if ( toggles.Length == 0 )
        {
            return [];
        }

        var probes = toggles
            .Select( static control => control.State! )
            .DistinctBy( static state => state.ProbeKey )
            .ToDictionary( static state => state.ProbeKey, state => ProbeAsync( state.ProbeArgv, cancellationToken ), StringComparer.Ordinal );

        await Task
            .WhenAll( probes.Values )
            .ConfigureAwait( false );

        return toggles.ToDictionary( static control => control.Id,
            control => probes[control.State!.ProbeKey].Result is { } output ? control.State.ReadIsOn( output ) : null, StringComparer.Ordinal );
    }

    private async Task<string?> ProbeAsync( IReadOnlyList<string> argv, CancellationToken cancellationToken )
    {
        try
        {
            var result = await _stateProbe( argv[0], argv.Skip( 1 ).ToArray( ), HyprctlTimeout, cancellationToken )
                .ConfigureAwait( false );

            return GetSuccessfulOutput( result );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return null;
        }
    }

    private static SearchResult BuildRow( DesktopControl control, double score, bool? isOn = null )
    {
        var command = string.Join( ' ', control.Argv );
        var title = control.State is { } state && isOn is { } on ? state.Title( on ) : control.Title;
        var subtitle = control.State is { } described && isOn is { } current ? described.Describe( current ) : control.Tool;

        return new SearchResult
        {
            Id = DesktopControlResultIds.For( control.Id ),
            Kind = ResultKinds.CustomCommand,
            Title = title,
            Subtitle = $"{control.Category} · {subtitle}",
            Icon = control.Icon,
            ProviderId = ProviderIds.DesktopControls,
            SourceId = SourceIds.DesktopControls,
            Score = score,
            Details =
            [
                new DetailField( "Category", control.Category ),
                new DetailField( "Command", command, Monospace: true )
            ],
            Actions =
            [
                new CommandAction
                {
                    Id = RunActionId,
                    Title = title,
                    Kind = ActionKinds.RunArgv,
                    CloseAfterAction = control.CloseAfterAction,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal ) { [ActionArgumentNames.Argv] = string.Join( '\u001f', control.Argv ) }
                }
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal ) { ["control"] = control.Id }
        };
    }

    private static bool ShouldSkipSearch( ParsedQuery query, SearchRequest request, string text, bool browsing )
    {
        return !Participates( query, request ) || !browsing && text.Length < MinimumQueryCharacters;
    }

    private static SearchResult CreatePickerResult( DesktopControl picker, double score, string? missing )
    {
        if ( missing is null )
        {
            return BuildRow( picker, score );
        }

        return new SearchResult
        {
            Id = DesktopControlResultIds.For( picker.Id + UnavailableResultSuffix ),
            Kind = ResultKinds.Status,
            Title = picker.Title,
            Subtitle = $"Unavailable: install {missing} to pick a color from the screen",
            Icon = picker.Icon,
            ProviderId = ProviderIds.DesktopControls,
            SourceId = SourceIds.DesktopControls,
            Score = score,
            Details = [new DetailField( "Missing tool", missing, Monospace: true )]
        };
    }
}

public static class DesktopControlResultIds
{
    public static string For( string controlId ) => "desktop:" + controlId;
}
