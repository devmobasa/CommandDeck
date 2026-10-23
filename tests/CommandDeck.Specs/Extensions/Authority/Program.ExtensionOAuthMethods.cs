using System.Net;
using System.Net.Sockets;
using System.Text;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static Dictionary<string, string> ParseOAuthQuery( string query ) => query
        .TrimStart( '?' )
        .Split( '&' )
        .Select( pair => pair.Split( '=', 2 ) )
        .ToDictionary( pair => Uri.UnescapeDataString( pair[0] ), pair => Uri.UnescapeDataString( pair[1] ) );

    private static ExtensionConnectionSetup OAuthConnectionSetup( BrokerTestServer server ) => new( )
    {
        Name = "tasks",
        Kind = ExtensionConnectionKinds.Http,
        Origin = server.Origin,
        PrivateNetwork = true,
        Route = "/tasks",
        CredentialKind = ExtensionCredentialKinds.OAuth,
        OAuth = new( server.Origin, server.Origin + "/oauth/authorize", server.Origin + "/oauth/token", "public-client", "tasks",
            server.Origin + "/oauth/revoke" )
    };

    private static async Task ExtensionOAuthBindsStatePkceRefreshAndDisconnectAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            await using var server = new BrokerTestServer( );
            using var broker = new ExtensionConnections( Path.Combine( root, "journal" ), new ExtensionSecretResolver( ) );

            var setup = OAuthConnectionSetup( server );
            await broker.ConfigureAsync( "one", setup, CancellationToken.None );
            using var browser = new HttpClient( );
            var badLogin = new Uri( await broker.BeginLoginAsync( "one", "tasks", CancellationToken.None ) );
            var bad = ParseOAuthQuery( badLogin.Query );

            try
            {
                using var ignored = await browser.GetAsync( bad["redirect_uri"] + "?code=x&state=wrong&iss=" + Uri.EscapeDataString( server.Origin ) );
            }
            catch ( HttpRequestException ) { }

            using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 5 ) );
            while ( !broker
                       .List( "one" )[0]
                       .CredentialStatus.StartsWith( "Login failed", StringComparison.Ordinal ) )
            {
                await Task.Delay( 10, timeout.Token );
            }

            Equal( 0, server.TokenRequests );
            var login = new Uri( await broker.BeginLoginAsync( "one", "tasks", CancellationToken.None ) );
            var values = ParseOAuthQuery( login.Query );
            server.ExpectedChallenge = values["code_challenge"];
            Equal( "S256", values["code_challenge_method"] );
            True( values["state"] != bad["state"] );

            using var completed =
                await browser.GetAsync( values["redirect_uri"] + "?code=issued&state=" + values["state"] + "&iss=" + Uri.EscapeDataString( server.Origin ) );

            Equal( HttpStatusCode.OK, completed.StatusCode );
            Equal( 1, server.TokenRequests );
            await Task.Delay( 1100 );

            var request = new BrokerRequest( "s", 1, 1, "i", ExtensionBrokerOperations.SendHttp, "tasks",
                Http: new( "GET", "/tasks" ) );

            await Task.WhenAll( Enumerable
                .Range( 0, 3 )
                .Select( async _ => Equal( "completed",
                    (await broker.ExecuteAsync( "one", request, true, ( _, _ ) => ValueTask.FromResult( false ), CancellationToken.None )).Outcome ) ) );

            Equal( 2, server.TokenRequests );
            Equal( "Bearer oauth-refreshed", server.Authorization );
            var message = await broker.DisconnectAsync( "one", "tasks", true, CancellationToken.None );
            True( message.Contains( "acknowledged", StringComparison.Ordinal ) );
            Equal( 1, server.Revocations );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await broker.ExecuteAsync( "one", request, true, ( _, _ ) => ValueTask.FromResult( false ), CancellationToken.None ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task ExtensionOAuthCallbackServesOneConnectionAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            await using var server = new BrokerTestServer( );
            using var broker = new ExtensionConnections( Path.Combine( root, "journal" ), new ExtensionSecretResolver( ) );

            await broker.ConfigureAsync( "one", OAuthConnectionSetup( server ), CancellationToken.None );

            var login = ParseOAuthQuery( new Uri( await broker.BeginLoginAsync( "one", "tasks", CancellationToken.None ) ).Query );

            server.ExpectedChallenge = login["code_challenge"];
            var redirect = new Uri( login["redirect_uri"] );

            // The browser's connection reaches the callback first.
            using TcpClient browser = new( );
            await browser.ConnectAsync( IPAddress.Loopback, redirect.Port );

            // Another connection, such as the browser retrying, is refused or
            // closed at once rather than parked behind the pending callback.
            True( await CallbackTurnsAwayAnotherConnectionAsync( redirect.Port ) );

            // The pending callback still completes the login.
            var stream = browser.GetStream( );

            var callback = $"GET {redirect.AbsolutePath}?code=issued&state={login["state"]}&iss={Uri.EscapeDataString( server.Origin )} HTTP/1.1\r\n" +
                           $"Host: {redirect.Authority}\r\n\r\n";

            await stream.WriteAsync( Encoding.ASCII.GetBytes( callback ) );
            using StreamReader reader = new( stream, Encoding.ASCII );
            Equal( "HTTP/1.1 200 OK", await reader.ReadLineAsync( ) );
            Equal( 1, server.TokenRequests );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task<bool> CallbackTurnsAwayAnotherConnectionAsync( int port )
    {
        try
        {
            using TcpClient other = new( );
            await other.ConnectAsync( IPAddress.Loopback, port );
            using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 5 ) );

            return await other
                       .GetStream( )
                       .ReadAsync( new byte[1], timeout.Token ) ==
                   0;
        }
        catch ( Exception exception ) when ( exception is SocketException or IOException )
        {
            return true;
        }
        catch ( OperationCanceledException )
        {
            return false;
        }
    }
}
