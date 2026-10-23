using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Extensions;

/// <summary>Opt-in search implementation on a command. Search receives no interactive context or broker handles.</summary>
public interface IExtensionSearchProvider
{
    string SearchId { get; }
    ValueTask<ExtensionSearchResponse> SearchAsync( SearchContext context, CancellationToken cancellationToken );
}

public sealed record SearchContext( string Query, int Limit, JsonElement? Cache, bool Global );

internal sealed class ExtensionSearchRuntime( IReadOnlyList<IExtensionCommand> commands, TextWriter diagnostics )
{
    private const int MaximumQueryCharacters = 2048;
    private const int MaximumCacheCharacters = 16 * 1024;

    private enum Phase { Inactive, AwaitInitialized, Ready, Searched }

    private static readonly JsonElement EmptyObject = JsonElement.Parse( "{}" );
    private IExtensionSearchProvider? _provider;
    private bool _global;
    private Phase _phase;
    public bool Active => _phase != Phase.Inactive;

    public JsonElement Initialize( InitializeRequest request )
    {
        if ( HasInvalidInitialization( request ) )
        {
            throw new InvalidDataException( "Invalid search initialization." );
        }

        var value = FindSearchProvider( request );
        if ( value is null )
        {
            throw new InvalidDataException( "The search provider is unavailable." );
        }

        _provider = value;
        _global = request.Purpose == ExtensionPurposes.GlobalSearch;
        _phase = Phase.AwaitInitialized;

        return CreateInitializationResponse( );
    }

    public async ValueTask<JsonElement> HandleAsync( ExtensionFrame frame, CancellationToken token )
    {
        if ( IsInitializedNotification( frame ) )
        {
            _phase = Phase.Ready;

            return EmptyObject;
        }

        if ( IsUnexpectedSearchQuery( frame ) )
        {
            throw new InvalidDataException( "Invalid search operation." );
        }

        _phase = Phase.Searched;
        var input = frame.Params?.Deserialize( ExtensionSearchJsonContext.Default.ExtensionSearchRequest );
        if ( input is null )
        {
            throw new InvalidDataException( "Missing search input." );
        }

        if ( HasInvalidSearchInput( input ) )
        {
            throw new InvalidDataException( "Search input exceeds its limits." );
        }

        ExtensionSearchResponse result;

        try
        {
            result = await _provider!
                .SearchAsync( new( input.Query, input.Limit, input.Cache?.Clone( ), _global ), token )
                .ConfigureAwait( false );
        }
        catch ( Exception exception )
        {
            SdkDiagnostics.Log( diagnostics, _provider!.SearchId, nameof( IExtensionSearchProvider.SearchAsync ), exception );

            throw;
        }

        ExtensionSearchValidation.Validate( result );

        return JsonSerializer.SerializeToElement( result, ExtensionSearchJsonContext.Default.ExtensionSearchResponse );
    }

    private static bool HasInvalidInitialization( InitializeRequest request )
    {
        return request.Protocol is not { Major: 1, Minor: 0 } ||
               request.ExecutionEpoch != ExtensionSessionProtocol.ExecutionEpoch ||
               string.IsNullOrEmpty( request.SessionId ) ||
               request.Purpose is not (ExtensionPurposes.ScopedSearch or ExtensionPurposes.GlobalSearch) ||
               request.Limits?.MaximumFrameBytes != ExtensionLimits.MaximumFrameBytes ||
               request.HostFeatures is null ||
               !request.HostFeatures.SequenceEqual( new[] { ExtensionFeatures.Search } );
    }

    private bool IsInitializedNotification( ExtensionFrame frame )
    {
        return frame.Method == ExtensionMethods.Initialized && _phase == Phase.AwaitInitialized;
    }

    private bool IsUnexpectedSearchQuery( ExtensionFrame frame )
    {
        return frame.Method != ExtensionMethods.SearchQuery || _phase != Phase.Ready;
    }

    private static bool HasInvalidSearchInput( ExtensionSearchRequest input )
    {
        return input.Query is null ||
               input.Query.Length > MaximumQueryCharacters ||
               input.Limit is < 1 or > ExtensionSearchValidation.MaximumItems ||
               input.Cache?.GetRawText( )
                   .Length >
               MaximumCacheCharacters;
    }

    private IExtensionSearchProvider? FindSearchProvider( InitializeRequest request )
    {
        return commands
            .OfType<IExtensionSearchProvider>( )
            .SingleOrDefault( provider => provider.SearchId == request.CommandId );
    }

    private JsonElement CreateInitializationResponse( )
    {
        return JsonSerializer.SerializeToElement( new InitializeResult( new( ), ExtensionApp.SdkVersion, commands
            .Select( command => command.Id )
            .ToArray( ) ), ExtensionJsonContext.Default.InitializeResult );
    }
}
