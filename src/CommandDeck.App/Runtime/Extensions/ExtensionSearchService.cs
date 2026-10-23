using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck;

/// <summary>A bounded batch provider. Search children receive one query and one read-only cache value, with no callable host methods.</summary>
public sealed partial class ExtensionSearchService : ICommandProvider, IAsyncDisposable
{
    private const int FilteredSearchDeadlineMilliseconds = 1500;
    private const int SearchDeadlineMilliseconds = 300;
    private const int MaximumOwners = 32;
    private const int MaximumSearchQueryCharacters = 2048;

    private readonly ExtensionPackageRegistry _registry;
    private readonly IExtensionSearchWorker _worker;

    internal ExtensionSearchService( ExtensionPackageRegistry registry, IExtensionSearchWorker worker )
    {
        _registry = registry;
        _worker = worker;
    }

    private readonly SemaphoreSlim _slots = new( 2, 2 );
    private readonly object _gate = new( );
    private readonly Dictionary<string, SearchState> _owners = new( StringComparer.Ordinal );
    private readonly HashSet<Task> _workers = [];
    private readonly CancellationTokenSource _lifetime = new( );
    private long _generation;
    private int _rotation;
    public string Id => ProviderIds.Extensions;

    public long Begin( string? owner )
    {
        if ( owner is null )
        {
            return 0;
        }

        lock ( _gate )
        {
            if ( _owners.Remove( owner, out var old ) )
            {
                old.Dispose( );
            }

            foreach ( var stale in GetExpiredExtensionOwners( ) )
            {
                _owners[stale]
                    .Dispose( );

                _owners.Remove( stale );
            }

            if ( CannotStartExtensionOwner( ) )
            {
                return 0;
            }

            var state = new SearchState( ++_generation );
            _owners.Add( owner, state );

            return state.Generation;
        }
    }

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        SearchState? state;

        lock ( _gate )
        {
            state = request.SearchOwner is not null ? _owners.GetValueOrDefault( request.SearchOwner ) : null;
        }

        if ( CannotSearchExtensions( query, request, state ) )
        {
            return [];
        }

        var document = _registry.Document;
        if ( IsExtensionRegistryUnavailable( document ) )
        {
            return [];
        }

        var candidates = GetEligibleSearchProviders( query, request, document );
        if ( candidates.Length == 0 )
        {
            return [];
        }

        // Rotate before admission so a cold or hostile provider cannot permanently starve later packages.
        var start = ( int ) (( uint ) Interlocked.Increment( ref _rotation ) % ( uint ) candidates.Length);
        var selected = RotateSearchProviders( candidates, start );
        List<Task<IReadOnlyList<SearchResult>>> work = [];
        foreach ( var candidate in selected )
        {
            var task = RunProviderAsync( state, candidate.Package, candidate.Provider, query, cancellationToken );

            lock ( _gate )
            {
                _workers.Add( task );
            }

            work.Add( task );
        }

        try
        {
            return await CollectSearchResultsAsync( request, work )
                .ConfigureAwait( false );
        }
        finally
        {
            lock ( _gate )
            {
                foreach ( var task in work )
                {
                    _workers.Remove( task );
                }
            }
        }
    }

    private static bool Eligible( ParsedQuery query, ExtensionInstallation package, ExtensionProtocol.ExtensionSearchProvider provider )
    {
        var source = ExtensionStableIds.SourceId( package.Key );
        var id = ExtensionStableIds.SearchProviderId( package.Key, provider.Id );
        if ( query.ExcludedFilters.Any( filter => filter.Id == source || filter.Id == id ) )
        {
            return false;
        }

        if ( query.IncludedFilters.Count > 0 )
        {
            return query.IncludedFilters.Any( filter => filter.Id == source || filter.Id == id );
        }

        return !query.HasExplicitFilter && package is { GlobalSearchEnabled: true, Origin: not ExtensionOrigins.Development };
    }

    private async Task<IReadOnlyList<SearchResult>> RunProviderAsync( SearchState state, ExtensionInstallation package,
        ExtensionProtocol.ExtensionSearchProvider provider, ParsedQuery query, CancellationToken caller )
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource( caller, state.Token, _lifetime.Token );

        operation.CancelAfter( TimeSpan.FromMilliseconds( query.IncludedFilters.Count > 0 ? FilteredSearchDeadlineMilliseconds : SearchDeadlineMilliseconds ) );

        var token = operation.Token;
        var admitted = false;

        try
        {
            // A replacement query waits for cancelled children to finish cleanup within its budget.
            // Dropping it immediately here would leave the final typed query permanently empty.
            await _slots
                .WaitAsync( token )
                .ConfigureAwait( false );

            admitted = true;
            var cached = _registry.DataStore.CacheValue( package.Key, provider.CacheKey );
            var current = _registry.Find( package.Key );
            if ( IsSearchProviderStaleOrIneligible( package, provider, query, current ) )
            {
                throw new InvalidOperationException( "Search authority changed." );
            }

            var input = new ExtensionSearchRequest( query.Text, ExtensionSearchValidation.MaximumItems, cached.Value );

            var response = await _worker
                .QueryAsync( new( package, provider, input, query.IncludedFilters.Count == 0 ), token )
                .ConfigureAwait( false );

            ExtensionSearchValidation.Validate( response );
            token.ThrowIfCancellationRequested( );

            return await _registry
                .WithExecutionAsync<IReadOnlyList<SearchResult>>( package.Key, current =>
                {
                    if ( !Current( package, current ) ||
                         _registry.DataStore.CacheValue( package.Key, provider.CacheKey )
                             .Revision !=
                         cached.Revision ||
                         !Eligible( query, current, provider ) )
                    {
                        return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
                    }

                    lock ( _gate )
                    {
                        token.ThrowIfCancellationRequested( );

                        return ValueTask.FromResult<IReadOnlyList<SearchResult>>( response
                            .Items.Select( item => Lease( state, package, provider, item ) )
                            .ToArray( ) );
                    }
                }, token )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is OperationCanceledException
                                                or IOException
                                                or InvalidDataException
                                                or ExtensionRpcException
                                                or InvalidOperationException
                                                or JsonException
                                                or PlatformNotSupportedException )
        {
            return [];
        } // Never expose query-bearing child diagnostics to logs or another command.
        finally
        {
            if ( admitted )
            {
                _slots.Release( );
            }
        }
    }

    private static bool Current( ExtensionInstallation expected, ExtensionInstallation current ) =>
        expected.Digest == current.Digest && expected.GrantRevision == current.GrantRevision;

    private string[] GetExpiredExtensionOwners( )
    {
        return _owners
            .Where( pair => pair.Value.Expires < DateTimeOffset.UtcNow )
            .Select( pair => pair.Key )
            .ToArray( );
    }

    private bool CannotStartExtensionOwner( )
    {
        return _owners.Count >= MaximumOwners || _lifetime.IsCancellationRequested;
    }

    private static bool CannotSearchExtensions( ParsedQuery query, SearchRequest request, [NotNullWhen( false )] SearchState? state )
    {
        return state is null ||
               state.Generation != request.SearchGeneration ||
               query.Text.Length is < 1 or > MaximumSearchQueryCharacters ||
               query.IsAiMode ||
               query.IsCalculator ||
               request.BrowseDirectory is not null;
    }

    private bool IsExtensionRegistryUnavailable( ExtensionRegistryDocument document )
    {
        return !document.Enabled || _registry.Error is not null;
    }

    private static (ExtensionInstallation Package, ExtensionSearchProvider Provider)[] GetEligibleSearchProviders( ParsedQuery query, SearchRequest request,
        ExtensionRegistryDocument document )
    {
        return document
            .Installations.Where( package =>
                package is { Enabled: true, Removed: false } &&
                request.EnabledSearchSources?.Contains( ExtensionStableIds.SourceId( package.Key ), StringComparer.Ordinal ) == true )
            .SelectMany( package => package.Manifest.SearchProviders.Select( provider => (Package: package, Provider: provider) ) )
            .Where( candidate => Eligible( query, candidate.Package, candidate.Provider ) )
            .ToArray( );
    }

    private static (ExtensionInstallation Package, ExtensionSearchProvider Provider)[] RotateSearchProviders(
        (ExtensionInstallation Package, ExtensionSearchProvider Provider)[] candidates, int start )
    {
        return candidates
            .Skip( start )
            .Concat( candidates.Take( start ) )
            .Take( 2 )
            .ToArray( );
    }

    private async static ValueTask<SearchResult[]> CollectSearchResultsAsync( SearchRequest request, List<Task<IReadOnlyList<SearchResult>>> work )
    {
        return (await Task
                .WhenAll( work )
                .ConfigureAwait( false ))
            .SelectMany( rows => rows )
            .Take( request.Limit )
            .ToArray( );
    }

    private static bool IsSearchProviderStaleOrIneligible( ExtensionInstallation package, ExtensionSearchProvider provider, ParsedQuery query,
        ExtensionInstallation current )
    {
        return !Current( package, current ) || !Eligible( query, current, provider );
    }
}

internal sealed class ExtensionSearchWorker( ExtensionPackageRegistry registry, IExtensionLauncher supervisor ) : IExtensionSearchWorker
{
    private const string SearchOwnerPrefix = "search:";

    public async Task<ExtensionSearchResponse> QueryAsync( ExtensionSearchQuery query, CancellationToken token )
    {
        var (package, provider, input, global) = query;
        var target = ExtensionRuntimeSupport.Require( package.Manifest );

        var process = await supervisor
            .StartAsync( new( registry.ArtifactDirectory( package ), target.Entrypoint, SearchOwnerPrefix + package.Key, ExtensionLaunchProfile.Search ),
                token )
            .ConfigureAwait( false );

        ExtensionPeer? peer = null;

        try
        {
            peer = new ExtensionPeer( process.Input, process.Output, true, [],
                ( _, _ ) => throw new InvalidDataException( "Search cannot call host methods." ) );

            peer.Start( );

            var handshake = await peer
                .CallAsync( ExtensionMethods.Initialize, JsonSerializer.SerializeToElement( new InitializeRequest( new( ), Guid
                            .NewGuid( )
                            .ToString( AppFormats.CompactGuid ), ExtensionSessionProtocol.ExecutionEpoch, provider.Id,
                        global ? ExtensionPurposes.GlobalSearch : ExtensionPurposes.ScopedSearch, [ExtensionFeatures.Search], new( ) ),
                    ExtensionJsonContext.Default.InitializeRequest ), token )
                .ConfigureAwait( false );

            var initialized = handshake.Deserialize( ExtensionJsonContext.Default.InitializeResult );
            if ( HasUnexpectedSearchInitialization( package, initialized ) )
            {
                throw new InvalidDataException( "Search handshake does not match the installed package." );
            }

            peer.Notify( ExtensionMethods.Initialized );

            var result = await peer
                .CallAsync( ExtensionMethods.SearchQuery, JsonSerializer.SerializeToElement( input, ExtensionSearchJsonContext.Default.ExtensionSearchRequest ),
                    token )
                .ConfigureAwait( false );

            var response = result.Deserialize( ExtensionSearchJsonContext.Default.ExtensionSearchResponse );
            if ( response is null )
            {
                throw new InvalidDataException( "Missing search results." );
            }

            ExtensionSearchValidation.Validate( response );

            return response;
        }
        finally
        {
            // Stop descendants before draining protocol reads that ignore stream cancellation.
            try
            {
                await process
                    .DisposeAsync( )
                    .ConfigureAwait( false );
            }
            finally
            {
                if ( peer is not null )
                {
                    await peer
                        .DisposeAsync( )
                        .ConfigureAwait( false );
                }
            }
        }
    }

    private static bool HasUnexpectedSearchInitialization( ExtensionInstallation package, InitializeResult? initialized )
    {
        return initialized?.Protocol is not { Major: 1, Minor: 0 } ||
               initialized.Commands is null ||
               !initialized
                   .Commands.Order( StringComparer.Ordinal )
                   .SequenceEqual( package
                       .Manifest.Commands.Select( command => command.Id )
                       .Order( StringComparer.Ordinal ) );
    }
}
