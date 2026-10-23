namespace CommandDeck.Specs;

public static partial class Program
{
    // favorite.move swaps a cached bookmark with the shown bookmark it names,
    // so a bookmark ahead of both (one a view leaves out) keeps its place.
    private static async Task VerifyFavoriteMoveProtocolAsync( UnixProtocolClient client, string firstFavoriteId, CancellationToken cancellationToken )
    {
        await client.SendAsync( new ProtocolRequest { RequestId = 150, Operation = ProtocolOperations.Search, Query = "context-report" },
            CancellationToken.None );

        List<string> reports = [];
        await foreach ( var line in client.ReadRawMessagesAsync( cancellationToken ) )
        {
            var message = DeserializeMessage( line );
            if ( message is { Type: "result", RequestId: 150, Result: { Kind: "file", CanCustomize: true } } )
            {
                reports.Add( message.Result.Id );
            }

            if ( message is { Type: "done", RequestId: 150 } )
            {
                break;
            }
        }

        Equal( 2, reports.Count );

        async Task<string> SendAsync( ProtocolRequest request, string type )
        {
            await client.SendAsync( request, CancellationToken.None );
            var reply = await ReadMessageAsync( client, request.RequestId, type, cancellationToken );

            ArgumentNullException.ThrowIfNull( reply );

            return string.Join( ' ', reply
                                         .Preferences?.Customizations.Where( static value => value.IsFavorite )
                                         .Select( static value => value.StableId ) ??
                                     [] );
        }

        async Task BookmarkAsync( long requestId, string resultId, bool enabled ) =>
            await SendAsync( new ProtocolRequest
            {
                RequestId = requestId,
                Operation = ProtocolOperations.FavoriteSet,
                ResultId = resultId,
                Enabled = enabled
            }, "customization-result" );

        Task<string> MoveAsync( long requestId, string resultId, string targetResultId ) =>
            SendAsync( new ProtocolRequest
            {
                RequestId = requestId,
                Operation = ProtocolOperations.FavoriteMove,
                ResultId = resultId,
                TargetResultId = targetResultId
            }, "preferences-result" );

        await BookmarkAsync( 151, reports[0], true );
        await BookmarkAsync( 152, reports[1], true );

        Equal( $"{firstFavoriteId} {reports[1]} {reports[0]}", await MoveAsync( 153, reports[1], reports[0] ) );
        Equal( $"{firstFavoriteId} {reports[0]} {reports[1]}", await MoveAsync( 154, reports[1], reports[0] ) );

        await BookmarkAsync( 155, reports[1], false );
        await client.SendAsync( CreateFavoriteMoveRequest( reports ), CancellationToken.None );
        var refused = await ReadMessageAsync( client, 156, "error", cancellationToken );
        True( refused?.Error?.Contains( "bookmarked", StringComparison.Ordinal ) == true );
        await BookmarkAsync( 157, reports[0], false );

        static ProtocolRequest CreateFavoriteMoveRequest( List<string> reports )
        {
            return new ProtocolRequest
            {
                RequestId = 156,
                Operation = ProtocolOperations.FavoriteMove,
                ResultId = reports[0],
                TargetResultId = reports[1]
            };
        }
    }

    private static SearchResult CreateProtocolUnavailableFavorite( )
    {
        return new( )
        {
            Id = "file:protocol-orphan",
            Kind = ResultKinds.File,
            Title = "Unavailable protocol favorite",
            ProviderId = ProviderIds.Files,
            SourceId = SourceIds.Files,
            CanCustomize = true
        };
    }

    private static SearchResult CreateProtocolOversizedFavorite( )
    {
        return new( )
        {
            Id = "file:oversized-export",
            Kind = ResultKinds.File,
            Title = new string( '\u00e9', 140_000 ),
            ProviderId = ProviderIds.Files,
            SourceId = SourceIds.Files,
            CanCustomize = true
        };
    }

    private static ProtocolRequest CreateProtocolFavoriteSetRequest( SearchResult result )
    {
        return new ProtocolRequest
        {
            RequestId = 9,
            Operation = ProtocolOperations.FavoriteSet,
            ResultId = result.Id,
            Enabled = true
        };
    }

    private static ProtocolRequest CreateProtocolAliasSetRequest( SearchResult result )
    {
        return new ProtocolRequest
        {
            RequestId = 10,
            Operation = ProtocolOperations.AliasSet,
            ResultId = result.Id,
            Alias = "note"
        };
    }

    private static ProtocolRequest CreateProtocolFavoriteReorderRequest( SearchResult result )
    {
        return new ProtocolRequest { RequestId = 108, Operation = ProtocolOperations.FavoriteReorder, StableIds = [result.Id] };
    }

    private static ProtocolRequest CreateProtocolFavoriteRemoveOrphanRequest( )
    {
        return new ProtocolRequest
        {
            RequestId = 109,
            Operation = ProtocolOperations.FavoriteRemoveOrphan,
            CustomizationId = "customization:file:protocol-orphan"
        };
    }

    private static ProtocolRequest CreateProtocolFavoriteToggleRequest( SearchResult result )
    {
        return new ProtocolRequest { RequestId = 101, Operation = ProtocolOperations.FavoriteSet, ResultId = result.Id };
    }

    private static ProtocolRequest CreateProtocolForgedFavoriteRequest( )
    {
        return new ProtocolRequest
        {
            RequestId = 13,
            Operation = ProtocolOperations.FavoriteSet,
            ResultId = "file:forged",
            Enabled = true
        };
    }
}
