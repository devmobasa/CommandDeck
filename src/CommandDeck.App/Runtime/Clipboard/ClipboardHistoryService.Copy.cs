using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public sealed partial class ClipboardHistoryService
{
    private const int CopyDeadlineSeconds = 10;

    private bool _copyInProgress;

    public async ValueTask<ClipboardResponse> HandleAsync( string ownerId, ProtocolRequest request, CancellationToken cancellationToken )
    {
        if ( request.Operation == ProtocolOperations.ClipboardPreview )
        {
            return await PreviewAsync( ownerId, request, cancellationToken )
                .ConfigureAwait( false );
        }

        if ( request.Operation == ProtocolOperations.ClipboardActionsPrepare )
        {
            return await PrepareOpenAsync( ownerId, request, cancellationToken )
                .ConfigureAwait( false );
        }

        if ( IsNotClipboardCopy( request.Operation, request.Clipboard ) )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            return Handle( ownerId, request );
        }

        ProtocolRequestValidator.Validate( request );
        var value = request.Clipboard;
        Owner owner;
        CancellationTokenSource lifetime;
        ValueTask<ActionExecutionResult> copying;

        lock ( _gate )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            owner = GetOwner( ownerId );
            ValidateActionGeneration( owner, request, value );
            var session = GetSession( ownerId, value.SessionId! );
            if ( session.QueryGeneration != value.QueryGeneration )
            {
                throw new InvalidOperationException( "Clipboard copy belongs to a previous query." );
            }

            Resolve( session, value.EntryId!, value.Incarnation!, value.EntryRevision );

            if ( _copyInProgress )
            {
                throw new InvalidOperationException( "A clipboard copy is still in progress." );
            }

            if ( serve is null && copyText is null )
            {
                throw new InvalidOperationException( "Clipboard copy is unavailable." );
            }

            // Native serving restores every retained offer; the text fallback only handles legacy plain entries.
            var selection = serve is null ? null : store.ReadCopySelection( value.EntryId!, value.Incarnation!, value.EntryRevision );
            var text = serve is null ? store.ReadCopyText( value.EntryId!, value.Incarnation!, value.EntryRevision ) : null;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
            lifetime.CancelAfter( TimeSpan.FromSeconds( CopyDeadlineSeconds ) );
            owner.ActiveCopy = lifetime;
            owner.CopySessionId = value.SessionId;
            owner.CopyEntryId = value.EntryId;
            _copyInProgress = true;

            try
            {
                // Start under the authority gate, before a concurrent close can revoke this generation.
                copying = StartClipboardCopyAsync( value, lifetime, selection, text );
            }
            catch
            {
                selection?.Dispose( );
                EndCopy( owner, lifetime );

                throw;
            }
        }

        try
        {
            var result = await copying.ConfigureAwait( false );

            return new( ) { Action = CreateClipboardCopyResult( result ) };
        }
        catch ( OperationCanceledException ) when ( lifetime.IsCancellationRequested )
        {
            return new( ) { Action = ActionExecutionResult.Failure( "Clipboard copy was interrupted; the clipboard may already have changed." ) };
        }
        finally
        {
            lock ( _gate )
            {
                EndCopy( owner, lifetime );
            }
        }
    }

    private async ValueTask<ActionExecutionResult> ServeAsync( ClipboardCopySelection selection, string id, string incarnation, CancellationToken token )
    {
        using ( selection )
        {
            var result = await serve!( selection.Offers, token )
                .ConfigureAwait( false );

            if ( result.Succeeded )
            {
                store.TouchCopied( id, incarnation );
            }

            return result;
        }
    }

    private void CancelCopies( string? entryId )
    {
        foreach ( var owner in _owners.Values )
        {
            if ( entryId is null || owner.CopyEntryId == entryId )
            {
                owner.ActiveCopy?.Cancel( );
            }
        }
    }

    private void EndCopy( Owner owner, CancellationTokenSource lifetime )
    {
        owner.ActiveCopy = null;
        owner.CopyEntryId = owner.CopySessionId = null;
        _copyInProgress = false;
        lifetime.Dispose( );
    }

    private async ValueTask<ClipboardResponse> PreviewAsync( string ownerId, ProtocolRequest request, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );
        var response = Handle( ownerId, request );
        var preview = response.Preview;
        if ( IsImagePreviewUnavailable( preview, projections ) )
        {
            return response;
        }

        // The isolated decode runs outside the authority gate; the store rebinds the live revision before reading bytes.
        var source = await store
            .ReadImagePreviewAsync( preview.Id, preview.Incarnation, preview.Revision, projections, cancellationToken )
            .ConfigureAwait( false );

        return new( ) { Preview = preview with { ImageSource = source } };
    }

    private static bool IsNotClipboardCopy( string operation, [NotNullWhen( false )] ClipboardRequest? clipboard )
    {
        return operation != ProtocolOperations.ClipboardAction || clipboard?.Action != ClipboardActionIds.Copy;
    }

    private ValueTask<ActionExecutionResult> StartClipboardCopyAsync( ClipboardRequest value, CancellationTokenSource lifetime,
        ClipboardCopySelection? selection, string? text )
    {
        if ( selection is null )
        {
            return copyText!( text!, lifetime.Token );
        }
        else
        {
            return ServeAsync( selection, value.EntryId!, value.Incarnation!, lifetime.Token );
        }
    }

    private static ActionExecutionResult CreateClipboardCopyResult( ActionExecutionResult result )
    {
        if ( result.Succeeded )
        {
            return ActionExecutionResult.Success( "Copied." );
        }
        else
        {
            return ActionExecutionResult.Failure( "Clipboard copy failed; the clipboard may already have changed." );
        }
    }

    private static bool IsImagePreviewUnavailable( [NotNullWhen( false )] ClipboardEntryPreview? preview,
        [NotNullWhen( false )] ClipboardPayloadProjection? projections )
    {
        return preview is null || preview.Presentation.Kind != ClipboardPresentationKinds.Images || projections is null;
    }
}
