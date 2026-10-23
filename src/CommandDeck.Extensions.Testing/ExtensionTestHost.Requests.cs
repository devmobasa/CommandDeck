using System.Text.Json;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions.Views;

namespace CommandDeck.Extensions.Testing;

public sealed partial class ExtensionTestHost
{
    private ValueTask<JsonElement> HandleAsync( ExtensionFrame frame, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );

        lock ( _gate )
        {
            return ValueTask.FromResult( frame.Method switch
            {
                ExtensionMethods.ViewsReplace or ExtensionMethods.ViewsPush => Publish( frame ),
                ExtensionMethods.CommandData => Data( frame ),
                ExtensionMethods.CopyText => Copy( frame ),
                ExtensionMethods.CommandBroker => Broker( frame ),
                _ => throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest )
            } );
        }
    }

    private JsonElement Publish( ExtensionFrame frame )
    {
        var request = frame.Params?.Deserialize( ExtensionViewJsonContext.Default.ViewUpdate );
        if ( request is null )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
        }

        if ( IsStaleRequest( request ) )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.StaleSession );
        }

        var pushing = frame.Method == ExtensionMethods.ViewsPush;
        if ( ViewRequirements.FirstMissing( request.Content, pushing, _options.HostFeatures ) is not null )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.UnsupportedFeature );
        }

        try
        {
            ViewValidation.Validate( request.Content );
        }
        catch ( InvalidDataException )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidView );
        }

        if ( HasConflictingViewRevision( request, pushing ) )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.StaleView );
        }

        if ( pushing )
        {
            if ( CannotPushView( request ) )
            {
                throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidView );
            }

            _parents.Add( _view );
        }
        else if ( HasDifferentCurrentViewId( request ) )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.StaleView );
        }

        _view = request.Content;

        return JsonSerializer.SerializeToElement( new ViewAccepted( _view.Id, ++_viewRevision ), ExtensionViewJsonContext.Default.ViewAccepted );
    }

    private JsonElement Copy( ExtensionFrame frame )
    {
        var request = frame.Params?.Deserialize( ExtensionViewJsonContext.Default.CopyTextRequest );
        if ( request is null )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
        }

        RequireInvocation( request.SessionId, request.ExecutionEpoch, request.Generation, request.InvocationId );

        RequireGrant( ExtensionPermissionIds.ClipboardWrite );
        ViewValidation.ValidateCopyText( request.Text );
        _copied.Add( request.Text );

        return Empty;
    }

    private JsonElement Data( ExtensionFrame frame )
    {
        var request = frame.Params?.Deserialize( ExtensionViewJsonContext.Default.CommandDataRequest );
        if ( request is null )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
        }

        RequireInvocation( request.SessionId, request.ExecutionEpoch, request.Generation, request.InvocationId );

        if ( request.Operation == ExtensionDataOperations.Settings )
        {
            return Result( new( 0, Clone( _options.Settings ) ) );
        }

        if ( request.Operation == ExtensionDataOperations.Read )
        {
            RequireGrant( ExtensionPermissionIds.StorageRead );

            return Result( new( _storageRevision, Clone( _storage ) ) );
        }

        if ( request.Operation is not (ExtensionDataOperations.Write or ExtensionDataOperations.Delete) )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
        }

        RequireGrant( ExtensionPermissionIds.StorageWrite );

        if ( request.ExpectedRevision != _storageRevision )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.RevisionConflict );
        }

        try
        {
            ViewValidation.ValidateId( request.Key );
        }
        catch ( InvalidDataException )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
        }

        var proposed = Clone( _storage );

        if ( request.Operation == ExtensionDataOperations.Write )
        {
            if ( request.Value is not { } value )
            {
                throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
            }

            proposed[request.Key!] = value.Clone( );
        }
        else
        {
            proposed.Remove( request.Key! );
        }

        if ( ExceedsStorageQuota( proposed ) )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.QuotaExceeded );
        }

        _storage.Clear( );

        foreach ( var (key, value) in proposed )
        {
            _storage.Add( key, value );
        }

        _storageRevision++;

        return Result( new CommandDataResult( _storageRevision, new( StringComparer.Ordinal ) ) );

        static JsonElement Result( CommandDataResult result ) =>
            JsonSerializer.SerializeToElement( result, ExtensionViewJsonContext.Default.CommandDataResult );
    }

    private JsonElement Broker( ExtensionFrame frame )
    {
        var request = frame.Params?.Deserialize( BrokerJsonContext.Default.BrokerRequest );
        if ( request is null )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
        }

        RequireInvocation( request.SessionId, request.ExecutionEpoch, request.Generation, request.InvocationId );

        BrokerResult result;

        if ( request.Operation == ExtensionBrokerOperations.SendHttp )
        {
            RequireGrant( ExtensionPermissionIds.HttpRequest );

            if ( !TryGetHttpHandler( request, out var handler ) )
            {
                throw new ExtensionApplicationException( ExtensionErrorCodes.PermissionDenied );
            }

            try
            {
                var response = handler( request.Http! );

                result = new( ExtensionBrokerOutcomes.Completed, Body: Convert.ToBase64String( response.Body.Span ), StatusCode: response.StatusCode,
                    ContentType: response.ContentType );
            }
            catch ( BrokerOutcomeException exception )
            {
                result = new( exception.Outcome );
            }
        }
        else
        {
            RequireGrant( ExtensionPermissionIds.FilesRead );

            if ( !_options.Files.TryGetValue( request.Connection, out var files ) )
            {
                throw new ExtensionApplicationException( ExtensionErrorCodes.PermissionDenied );
            }

            result = FileResult( request, files );
        }

        return JsonSerializer.SerializeToElement( result, BrokerJsonContext.Default.BrokerResult );
    }

    private static BrokerResult FileResult( BrokerRequest request, Dictionary<string, byte[]> files )
    {
        if ( request.Operation == ExtensionBrokerOperations.ReadFile )
        {
            if ( !TryReadFileBytes( files, request.Reference, out var bytes ) )
            {
                throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
            }

            return new( ExtensionBrokerOutcomes.Completed, Body: Convert.ToBase64String( bytes ) );
        }

        if ( IsInvalidFileListRequest( request ) )
        {
            throw new ExtensionApplicationException( ExtensionErrorCodes.InvalidRequest );
        }

        var entries = ListFileEntries( request, files );

        return new( ExtensionBrokerOutcomes.Completed, entries, request.Offset + entries.Length < files.Count ? request.Offset + entries.Length : null );
    }

    private bool IsStaleRequest( ViewUpdate request )
    {
        return !_active ||
               request.SessionId != _session ||
               request.ExecutionEpoch != ExtensionSessionProtocol.ExecutionEpoch ||
               request.Generation != _generation;
    }

    private bool HasConflictingViewRevision( ViewUpdate request, bool pushing )
    {
        return request.ViewId != request.Content.Id || request.ExpectedRevision != (pushing || _view?.Id != request.ViewId ? 0 : _viewRevision);
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen( false, nameof( _view ) )]
    private bool CannotPushView( ViewUpdate request )
    {
        return _view is null ||
               _parents.Count >= ExtensionViewLimits.MaximumParentViews ||
               _view.Id == request.ViewId ||
               _parents.Any( view => view.Id == request.ViewId );
    }

    private bool HasDifferentCurrentViewId( ViewUpdate request )
    {
        return _view is not null && _view.Id != request.ViewId;
    }

    private static bool ExceedsStorageQuota( Dictionary<string, JsonElement> proposed )
    {
        return proposed.Count > ExtensionStorageLimits.MaximumEntries ||
               proposed.Any( pair => System.Text.Encoding.UTF8.GetByteCount( pair.Value.GetRawText( ) ) > ExtensionStorageLimits.MaximumValueBytes ) ||
               proposed.Sum( pair => pair.Key.Length + System.Text.Encoding.UTF8.GetByteCount( pair.Value.GetRawText( ) ) ) >
               ExtensionStorageLimits.MaximumInstallationBytes;
    }

    private static bool IsInvalidFileListRequest( BrokerRequest request )
    {
        return request.Operation != ExtensionBrokerOperations.ListFiles || request.Offset < 0;
    }

    private static BrokerFileEntry[] ListFileEntries( BrokerRequest request, Dictionary<string, byte[]> files )
    {
        return files
            .OrderBy( pair => pair.Key, StringComparer.Ordinal )
            .Skip( request.Offset )
            .Take( ExtensionFileLimits.ListPageSize )
            .Select( pair => new BrokerFileEntry( pair.Key, pair.Key, false, pair.Value.Length ) )
            .ToArray( );
    }

    private bool TryGetHttpHandler( BrokerRequest request,
        [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out Func<BrokerHttpRequest, HttpResult>? handler )
    {
        return _options.Http.TryGetValue( request.Connection, out handler ) && request.Http is not null;
    }

    private static bool TryReadFileBytes( Dictionary<string, byte[]> files, string? reference,
        [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out byte[]? bytes )
    {
        bytes = null;

        return reference is not null && files.TryGetValue( reference, out bytes );
    }
}
