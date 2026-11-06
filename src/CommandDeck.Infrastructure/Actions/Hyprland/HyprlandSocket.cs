using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace CommandDeck;

// Hyprland's request socket answers the same JSON as `hyprctl -j`, without a
// process: about 0.05 ms a query instead of about 5 ms, and the daemon does not
// fork while the desktop is busy.
public static class HyprlandSocket
{
    private const int MaximumReplyBytes = 16 * ByteUnits.BytesPerMebibyte;
    private const int ReadBufferBytes = 64 * ByteUnits.BytesPerKibibyte;

    // The session's request socket, or null outside a Hyprland session. The
    // signature names one directory under the runtime directory, never a path.
    public static string? RequestPath( Func<string, string?> environment )
    {
        var instance = environment( EnvironmentVariableNames.HyprlandInstanceSignature );
        var runtime = environment( XdgEnvironmentNames.RuntimeDirectory );
        if ( string.IsNullOrEmpty( instance ) || string.IsNullOrEmpty( runtime ) || !Path.IsPathFullyQualified( runtime ) ||
             instance is "." or ".." || instance.IndexOfAny( ['/', '\0'] ) >= 0 )
        {
            return null;
        }

        return Path.Combine( runtime, "hypr", instance, ".socket.sock" );
    }

    // `hyprctl -j REQUEST` through the socket, or through the fallback with the
    // rest of the budget. Both share one budget, and hyprctl asks the same
    // socket, so a socket that did not answer in time leaves nothing to fall
    // back to; one that is missing or refuses at once does.
    internal static async Task<string> QueryAsync( string request, Func<string, string?> environment,
        Func<string, TimeSpan, CancellationToken, Task<string>> fallback, TimeSpan budget, CancellationToken cancellationToken )
    {
        var started = Stopwatch.GetTimestamp( );
        if ( RequestPath( environment ) is { } path )
        {
            var (reply, timedOut) = await ExchangeAsync( path, "j/" + request, budget, cancellationToken ).ConfigureAwait( false );
            if ( reply is not null || timedOut )
            {
                return reply ?? "";
            }
        }

        var remaining = budget - Stopwatch.GetElapsedTime( started );

        return remaining > TimeSpan.Zero ? await fallback( request, remaining, cancellationToken ).ConfigureAwait( false ) : "";
    }

    // The reply, or null when the socket cannot answer within the timeout, or
    // belongs to another user. The caller's own cancellation still throws.
    public static async ValueTask<string?> RequestAsync( string path, string request, TimeSpan timeout, CancellationToken cancellationToken ) =>
        (await ExchangeAsync( path, request, timeout, cancellationToken ).ConfigureAwait( false )).Reply;

    private static async ValueTask<(string? Reply, bool TimedOut)> ExchangeAsync( string path, string request, TimeSpan timeout,
        CancellationToken cancellationToken )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        deadline.CancelAfter( timeout );

        try
        {
            using Socket socket = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
            await socket.ConnectAsync( new UnixDomainSocketEndPoint( path ), deadline.Token ).ConfigureAwait( false );
            UnixPeerIdentity.Verify( socket, UnixPeerIdentity.CurrentUserId( ) );
            await socket.SendAsync( Encoding.UTF8.GetBytes( request ), SocketFlags.None, deadline.Token ).ConfigureAwait( false );

            return (await ReadReplyAsync( socket, deadline.Token ).ConfigureAwait( false ), false);
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            return (null, true);
        }
        // An endpoint path too long for a Unix socket is refused as an argument.
        catch ( Exception exception ) when ( exception is SocketException or IOException or ArgumentException )
        {
            return (null, false);
        }
    }

    private static async ValueTask<string?> ReadReplyAsync( Socket socket, CancellationToken cancellationToken )
    {
        using MemoryStream reply = new( );
        var buffer = new byte[ReadBufferBytes];
        int read;

        while ( (read = await socket.ReceiveAsync( buffer, SocketFlags.None, cancellationToken ).ConfigureAwait( false )) > 0 )
        {
            if ( reply.Length + read > MaximumReplyBytes )
            {
                return null;
            }

            reply.Write( buffer, 0, read );
        }

        return Encoding.UTF8.GetString( reply.GetBuffer( ), 0, ( int ) reply.Length );
    }
}
