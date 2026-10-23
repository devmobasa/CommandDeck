using System.Diagnostics.CodeAnalysis;
using Tmds.DBus.Protocol;

namespace CommandDeck;

public interface ISavedOutputOpener
{
    ValueTask<ActionExecutionResult> OpenAsync( SafeRegularFile file, CancellationToken cancellationToken );
}

/// <summary>Transfers file ownership to the desktop before the caller releases its descriptor.</summary>
public sealed class DesktopPortalFileOpener( string? busAddress = null ) : ISavedOutputOpener
{
    private const string RequestTokenPrefix = "commanddeck_";
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string DesktopPath = "/org/freedesktop/portal/desktop";
    private const string OpenInterface = "org.freedesktop.portal.OpenURI";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes( 2 );

    public async ValueTask<ActionExecutionResult> OpenAsync( SafeRegularFile file, CancellationToken cancellationToken )
    {
        var address = busAddress ?? DBusAddress.Session;
        if ( string.IsNullOrEmpty( address ) )
        {
            return ActionExecutionResult.Failure( "The desktop file opener is unavailable." );
        }

        using DBusConnection connection = new( address );
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        deadline.CancelAfter( RequestTimeout );

        var token = RequestTokenPrefix +
                    Guid
                        .NewGuid( )
                        .ToString( "N" );

        string? requestPath = null;

        try
        {
            await connection
                .ConnectAsync( )
                .AsTask( )
                .WaitAsync( deadline.Token )
                .ConfigureAwait( false );

            requestPath = "/org/freedesktop/portal/desktop/request/" +
                          connection.UniqueName![1..]
                              .Replace( '.', '_' ) +
                          "/" +
                          token;

            TaskCompletionSource<uint> response = new( TaskCreationOptions.RunContinuationsAsynchronously );

            using var subscription = await connection
                .AddMatchAsync( CreateResponseMatchRule( requestPath ), static ( Message message, object? _ ) => message
                    .GetBodyReader( )
                    .ReadUInt32( ), ( Notification<uint> notification ) =>
                {
                    if ( notification.Type == NotificationType.Value )
                    {
                        response.TrySetResult( notification.Value );
                    }
                }, false, ObserverFlags.None, null )
                .AsTask( )
                .WaitAsync( deadline.Token )
                .ConfigureAwait( false );

            var returnedPath = await connection
                .CallMethodAsync( BuildOpen( connection, file, token ), static ( Message message, object? _ ) => message
                    .GetBodyReader( )
                    .ReadObjectPath( )
                    .ToString( ) )
                .WaitAsync( deadline.Token )
                .ConfigureAwait( false );

            if ( returnedPath != requestPath )
            {
                return ActionExecutionResult.Failure( "The desktop file opener returned an unexpected request." );
            }

            var outcome = await response
                .Task.WaitAsync( deadline.Token )
                .ConfigureAwait( false );

            return outcome switch
            {
                0 => ActionExecutionResult.Success( "Saved output opened." ),
                1 => ActionExecutionResult.Failure( "Opening the saved output was cancelled." ),
                _ => ActionExecutionResult.Failure( "The desktop could not open the saved output." )
            };
        }
        catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
        {
            throw;
        }
        catch ( Exception exception ) when ( exception is DBusExceptionBase or IOException or InvalidOperationException or OperationCanceledException )
        {
            return ActionExecutionResult.Failure( "The desktop file opener is unavailable or did not respond." );
        }
        finally
        {
            if ( ShouldCloseRequest( deadline, requestPath ) )
            {
                try
                {
                    connection.TrySendMessage( BuildClose( connection, requestPath ) );
                }
                catch ( Exception exception ) when ( exception is DBusExceptionBase or InvalidOperationException )
                {
                    // A disconnected desktop cannot retain a usable request from this connection.
                }
            }
        }
    }

    private static MessageBuffer BuildOpen( DBusConnection connection, SafeRegularFile file, string token )
    {
        using var writer = connection.GetMessageWriter( );

        writer.WriteMethodCallHeader( destination: Service, path: DesktopPath, @interface: OpenInterface, member: "OpenFile", signature: "sha{sv}",
            flags: MessageFlags.None );

        writer.WriteString( string.Empty );
        writer.WriteHandle( file.Handle );
        writer.WriteDictionary( new Dictionary<string, VariantValue> { ["handle_token"] = token } );

        return writer.CreateMessage( );
    }

    private static MessageBuffer BuildClose( DBusConnection connection, string requestPath )
    {
        using var writer = connection.GetMessageWriter( );

        writer.WriteMethodCallHeader( destination: Service, path: requestPath, @interface: RequestInterface, member: "Close", signature: null,
            flags: MessageFlags.NoReplyExpected );

        return writer.CreateMessage( );
    }

    private static bool ShouldCloseRequest( CancellationTokenSource deadline, [NotNullWhen( true )] string? requestPath )
    {
        return deadline.IsCancellationRequested && requestPath is not null;
    }

    private static MatchRule CreateResponseMatchRule( string requestPath )
    {
        return new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Service,
            Path = requestPath,
            Interface = RequestInterface,
            Member = "Response"
        };
    }
}
