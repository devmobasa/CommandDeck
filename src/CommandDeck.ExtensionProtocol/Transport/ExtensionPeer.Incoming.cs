using System.Globalization;
using System.Text.Json;

namespace CommandDeck.ExtensionProtocol;

public sealed partial class ExtensionPeer
{
    private async Task ReadLoopAsync( )
    {
        try
        {
            ExtensionFraming framing = new( _input );
            long windowStart = Environment.TickCount64;
            var frameCount = 0;
            var bytesInWindow = 0;

            while ( !_lifetime.IsCancellationRequested )
            {
                var frame = await framing
                    .ReadAsync( _lifetime.Token )
                    .ConfigureAwait( false );

                if ( frame is null )
                {
                    throw new EndOfStreamException( "The extension pipe closed." );
                }

                if ( Environment.TickCount64 - windowStart >= RateWindowMilliseconds )
                {
                    windowStart = Environment.TickCount64;
                    frameCount = 0;
                    bytesInWindow = 0;
                }

                bytesInWindow += framing.LastFrameBytes;
                frameCount++;

                if ( ExceedsIncomingRateLimit( frameCount, bytesInWindow ) )
                {
                    throw new InvalidDataException( "The extension frame rate limit was reached." );
                }

                Receive( frame );
            }
        }
        catch ( Exception exception ) when ( exception is IOException
                                                or InvalidDataException
                                                or JsonException
                                                or ArgumentException
                                                or OperationCanceledException
                                                or ObjectDisposedException )
        {
            Stop( exception );
        }
    }

    private void Receive( ExtensionFrame frame )
    {
        if ( frame.Method is null )
        {
            CompleteOutgoing( frame );

            return;
        }

        if ( frame.Method == ExtensionMethods.CancelRequest )
        {
            ReceiveCancellation( frame );

            return;
        }

        if ( !_allowedMethods.Contains( frame.Method ) )
        {
            throw new InvalidDataException( "The extension method is not supported." );
        }

        lock ( _gate )
        {
            if ( frame.Id is { } id )
            {
                if ( HasInvalidRemoteRequestId( id, out var serial ) )
                {
                    throw new InvalidDataException( "The extension request ID is repeated or forged." );
                }

                _lastRemoteId = serial;

                if ( _incoming.Count >= ExtensionLimits.MaximumPendingCalls )
                {
                    QueueControl( new ExtensionFrame { Id = id, Error = new ExtensionError( SessionErrorCode, "The incoming request limit was reached." ) } );

                    return;
                }

                _incoming.Add( id, CancellationTokenSource.CreateLinkedTokenSource( _lifetime.Token ) );
            }

            if ( !_requests.Writer.TryWrite( frame ) )
            {
                throw new InvalidDataException( "The extension application queue is full." );
            }
        }
    }

    private void ReceiveCancellation( ExtensionFrame frame )
    {
        if ( frame.Id is not null )
        {
            throw new InvalidDataException( "Cancellation must be a notification." );
        }

        var cancellation = frame.Params?.Deserialize( ExtensionJsonContext.Default.CancelRequest );
        var id = cancellation?.Id;
        if ( HasInvalidCancellationId( id ) )
        {
            throw new InvalidDataException( "The cancellation ID is invalid." );
        }

        lock ( _gate )
        {
            if ( _incoming.TryGetValue( id, out var source ) )
            {
                // Run arbitrary token registrations outside the receive loop and admission gate.
                _ = source.CancelAsync( );
            }
        }
    }

    private static bool ExceedsIncomingRateLimit( int frameCount, int bytesInWindow )
    {
        return frameCount > MaximumFramesPerWindow || bytesInWindow > MaximumBytesPerWindow;
    }

    private bool HasInvalidRemoteRequestId( string id, out long serial )
    {
        serial = 0;

        return !id.StartsWith( _remotePrefix, StringComparison.Ordinal ) ||
               !long.TryParse( id.AsSpan( _remotePrefix.Length ), NumberStyles.None, CultureInfo.InvariantCulture, out serial ) ||
               serial <= _lastRemoteId;
    }

    private bool HasInvalidCancellationId( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? id )
    {
        return id is null || !id.StartsWith( _remotePrefix, StringComparison.Ordinal );
    }
}
