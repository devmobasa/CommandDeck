using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public sealed class CatalogSearchProvider : ICommandProvider
{
    private const string InvocationIdNamespace = "invocation";
    private const int MaximumInitialCandidates = 500;
    private const int MaximumCandidatesPerSource = 32;
    private const int MinimumInitialCandidates = 50;
    private const int MinimumCandidatesPerSource = 8;

    private readonly ICatalogQueryStore _catalog;
    private readonly ILauncherSearchPreferences? _preferences;
    private readonly OmarchyMenuState? _omarchyState;

    public CatalogSearchProvider( ICatalogQueryStore catalog, ILauncherSearchPreferences? preferences = null, OmarchyMenuState? omarchyState = null )
    {
        _catalog = catalog;
        _preferences = preferences;
        _omarchyState = omarchyState;
    }

    public string Id => ProviderIds.Catalog;

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        if ( request.EnabledSearchSources is { Count: 0 } )
        {
            return [];
        }

        var alias = await FindEligibleAliasAsync( query, request, cancellationToken )
            .ConfigureAwait( false );

        if ( alias is { Kind: ResultKinds.Quicklink } )
        {
            return [alias];
        }

        var isApplicationInvocation = ApplicationInvocation.TryParse( query, out var invocation );
        if ( IsApplicationAlias( alias, isApplicationInvocation, invocation ) )
        {
            return [WithLaunchArguments( alias, invocation.Arguments )];
        }

        if ( IsMissingApplicationInvocation( isApplicationInvocation, invocation ) )
        {
            var results = await SearchCatalogAsync( query, request, cancellationToken )
                .ConfigureAwait( false );

            IReadOnlyList<SearchResult> found = IncludeAlias( alias, results );

            return await ApplyOmarchyStateAsync( found, cancellationToken )
                .ConfigureAwait( false );
        }

        var applicationQuery = QueryParser.Parse( $"app: {invocation.ApplicationQuery}" );

        var applications = await _catalog
            .SearchAsync( applicationQuery, request.Limit, cancellationToken )
            .ConfigureAwait( false );

        return ApplyLaunchArguments( invocation, applications );
    }

    private async ValueTask<SearchResult?> FindEligibleAliasAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        if ( CannotFindAlias( query ) )
        {
            return null;
        }

        var alias = await _preferences
            .FindAliasAsync( query.Terms[0], cancellationToken )
            .ConfigureAwait( false );

        if ( await IsQuicklinkIneligibleAsync( query, request, alias, cancellationToken )
                .ConfigureAwait( false ) )
        {
            return null;
        }

        return alias;
    }

    private async ValueTask<bool> IsAliasEligibleAsync( SearchResult alias, ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        var enabled = await ReadEnabledSourcesAsync( request, cancellationToken )
            .ConfigureAwait( false );

        return enabled.Contains( alias.SourceId, StringComparer.Ordinal ) &&
               (query.IncludedFilters.Count == 0 || query.IncludedFilters.Any( filter => SearchFilterRegistry.Matches( filter, alias ) )) &&
               !query.ExcludedFilters.Any( filter => SearchFilterRegistry.Matches( filter, alias ) );
    }

    private async ValueTask<IReadOnlyList<SearchResult>> SearchEnabledSourcesAsync( ParsedQuery query, SearchRequest request,
        CancellationToken cancellationToken )
    {
        var enabledSources = await ReadEnabledSourcesAsync( request, cancellationToken )
            .ConfigureAwait( false );

        var sources = GetCatalogSources( enabledSources );
        if ( sources.Length == 0 )
        {
            return [];
        }

        var perSourceLimit = Math.Clamp( (request.Limit + sources.Length - 1) / sources.Length, MinimumCandidatesPerSource, MaximumCandidatesPerSource );
        var initialLimit = Math.Clamp( perSourceLimit * sources.Length, MinimumInitialCandidates, MaximumInitialCandidates );

        // Live-only sources (emoji, timezone, web search, snippets, ...) own no
        // catalog rows, so their FTS terms and supplement queries can never add a
        // row. Limits still come from every enabled source, so the rows and their
        // order stay exactly those of querying them all.
        var populated = await _catalog
            .GetPopulatedSourcesAsync( sources, cancellationToken )
            .ConfigureAwait( false );

        var catalogSources = sources
            .Where( populated.Contains )
            .ToArray( );

        if ( catalogSources.Length == 0 )
        {
            return [];
        }

        var initialBatch = await _catalog
            .SearchSourcesAsync( query, catalogSources, initialLimit, cancellationToken )
            .ConfigureAwait( false );

        var enabledSourceSet = sources.ToHashSet( StringComparer.Ordinal );
        var initial = FilterEnabledSources( initialBatch, enabledSourceSet );

        var underrepresented = GetUnderrepresentedSources( initialLimit, catalogSources, initialBatch, initial );
        if ( underrepresented.Length == 0 )
        {
            return initial;
        }

        var searches = StartSupplementSearches( query, perSourceLimit, underrepresented, cancellationToken );

        var supplements = await Task
            .WhenAll( searches )
            .ConfigureAwait( false );

        return CombineSearchBatches( initial, supplements );
    }

    internal static SearchResult WithLaunchArguments( SearchResult result, IReadOnlyList<string> arguments )
    {
        var actions = GetLaunchActions( result, arguments );
        var encodedArguments = LaunchArgumentCodec.Encode( arguments );
        Dictionary<string, string> metadata = CreateLaunchMetadata( result );

        return new SearchResult( result )
        {
            Id = StableId.Create( InvocationIdNamespace, $"{result.Id}\n{encodedArguments}" ),
            Subtitle = $"Open {string.Join( ' ', arguments )}",
            Actions = actions,
            Metadata = metadata
        };
    }

    private static CommandAction WithArguments( CommandAction action, IReadOnlyList<string> arguments )
    {
        Dictionary<string, string> values = CreateLaunchArguments( action, arguments );

        return new CommandAction
        {
            Id = action.Id,
            Title = "Open with Arguments",
            Kind = action.Kind,
            Arguments = values,
            Inputs = action.Inputs,
            Hidden = action.Hidden,
            IsDestructive = action.IsDestructive,
            RequiresConfirmation = action.RequiresConfirmation,
            CloseAfterAction = action.CloseAfterAction
        };
    }

    private static bool IsApplicationAlias( [NotNullWhen( true )] SearchResult? alias, bool isApplicationInvocation,
        [NotNullWhen( true )] ApplicationInvocation? invocation )
    {
        return isApplicationInvocation && invocation is not null && alias is { Kind: ResultKinds.Application };
    }

    private async ValueTask<IReadOnlyList<SearchResult>> SearchCatalogAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        if ( ShouldSearchEnabledSources( query, request ) )
        {
            return await SearchEnabledSourcesAsync( query, request, cancellationToken )
                .ConfigureAwait( false );
        }

        return await _catalog
            .SearchAsync( query, request.Limit, cancellationToken )
            .ConfigureAwait( false );
    }

    private static IReadOnlyList<SearchResult> IncludeAlias( SearchResult? alias, IReadOnlyList<SearchResult> results )
    {
        if ( alias is null )
        {
            return results;
        }

        return [alias, .. results.Where( result => result.Id != alias.Id )];
    }

    private async ValueTask<IReadOnlyList<SearchResult>> ApplyOmarchyStateAsync( IReadOnlyList<SearchResult> found, CancellationToken cancellationToken )
    {
        if ( _omarchyState is null )
        {
            return found;
        }

        return await _omarchyState
            .ApplyAsync( found, cancellationToken )
            .ConfigureAwait( false );
    }

    private static SearchResult[] ApplyLaunchArguments( ApplicationInvocation invocation, IReadOnlyList<SearchResult> applications )
    {
        return applications
            .Where( static result => result.Kind == ResultKinds.Application )
            .Select( result => WithLaunchArguments( result, invocation.Arguments ) )
            .ToArray( );
    }

    [MemberNotNullWhen( false, nameof( _preferences ) )]
    private bool CannotFindAlias( ParsedQuery query )
    {
        return _preferences is null || query.Terms.Count == 0;
    }

    private async ValueTask<bool> IsQuicklinkIneligibleAsync( ParsedQuery query, SearchRequest request, SearchResult? alias,
        CancellationToken cancellationToken )
    {
        return alias is { Kind: ResultKinds.Quicklink } &&
               !await IsAliasEligibleAsync( alias, query, request, cancellationToken )
                   .ConfigureAwait( false );
    }

    private static string[] GetCatalogSources( IReadOnlyList<string> enabledSources )
    {
        return enabledSources
            .Where( static source => source is not SourceIds.WindowsWorkspaces and not SourceIds.Herdr )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );
    }

    private static SearchResult[] FilterEnabledSources( IReadOnlyList<SearchResult> initialBatch, HashSet<string> enabledSourceSet )
    {
        return initialBatch
            .Where( result => enabledSourceSet.Contains( result.SourceId ) )
            .ToArray( );
    }

    private static string[] GetUnderrepresentedSources( int initialLimit, string[] catalogSources, IReadOnlyList<SearchResult> initialBatch,
        SearchResult[] initial )
    {
        if ( initialBatch.Count < initialLimit )
        {
            return [];
        }

        return catalogSources
            .Where( source => initial.Count( result => result.SourceId == source ) < MinimumCandidatesPerSource )
            .ToArray( );
    }

    // SQLite's async methods complete synchronously, so without Task.Run each
    // source's query would finish before the next one started. Each query opens
    // its own connection; the batches keep their source order for the merge.
    // There is at most one query per catalog source, which bounds the workers.
    private Task<IReadOnlyList<SearchResult>>[] StartSupplementSearches( ParsedQuery query, int perSourceLimit, string[] underrepresented,
        CancellationToken cancellationToken )
    {
        return underrepresented
            .Select( source => Task.Run( ( ) => _catalog
                .SearchSourcesAsync( query, [source], perSourceLimit, cancellationToken )
                .AsTask( ), cancellationToken ) )
            .ToArray( );
    }

    private static SearchResult[] CombineSearchBatches( SearchResult[] initial, IReadOnlyList<SearchResult>[] supplements )
    {
        return initial
            .Concat( supplements.SelectMany( static batch => batch ) )
            .DistinctBy( static result => result.Id, StringComparer.Ordinal )
            .ToArray( );
    }

    private static List<CommandAction> GetLaunchActions( SearchResult result, IReadOnlyList<string> arguments )
    {
        return result
            .Actions.Select( action => WithLaunchArgumentsIfNeeded( arguments, action ) )
            .ToList( );
    }

    private static Dictionary<string, string> CreateLaunchMetadata( SearchResult result )
    {
        return new( result.Metadata, StringComparer.Ordinal ) { [ResultMetadataKeys.CatalogResultId] = result.Id };
    }

    private static Dictionary<string, string> CreateLaunchArguments( CommandAction action, IReadOnlyList<string> arguments )
    {
        return new( action.Arguments, StringComparer.Ordinal ) { [ResultMetadataKeys.Application.LaunchArguments] = LaunchArgumentCodec.Encode( arguments ) };
    }

    private static CommandAction WithLaunchArgumentsIfNeeded( IReadOnlyList<string> arguments, CommandAction action )
    {
        if ( action.Kind == ActionKinds.LaunchDesktop )
        {
            return WithArguments( action, arguments );
        }

        return action;
    }

    private static bool IsMissingApplicationInvocation( bool isApplicationInvocation, [NotNullWhen( false )] ApplicationInvocation? invocation )
    {
        return !isApplicationInvocation || invocation is null;
    }

    private bool ShouldSearchEnabledSources( ParsedQuery query, SearchRequest request )
    {
        return query.IncludedFilters.Count == 0 &&
               query.ExcludedFilters.Count == 0 &&
               query.Terms.Count > 0 &&
               !query.HasExplicitFilter &&
               (request.EnabledSearchSources is not null || _preferences is not null);
    }

    private async ValueTask<IReadOnlyList<string>> ReadEnabledSourcesAsync( SearchRequest request, CancellationToken cancellationToken )
    {
        var sources = request.EnabledSearchSources;
        if ( sources is not null )
        {
            return sources;
        }

        var preferences = await _preferences!
            .GetPreferencesAsync( cancellationToken )
            .ConfigureAwait( false );

        return preferences
            .Sources.Where( static source => source.EnabledInSearch )
            .Select( static source => source.SourceId )
            .ToArray( );
    }
}
