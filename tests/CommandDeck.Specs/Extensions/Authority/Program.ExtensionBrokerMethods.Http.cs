using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ExtensionHttpConnectionsBoundScopeSecretsAndEffectsAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            await using var server = new BrokerTestServer( );
            using var broker = new ExtensionConnections( Path.Combine( root, "journal" ), new ExtensionSecretResolver( ) );

            var setup = new ExtensionConnectionSetup
            {
                Name = "tasks",
                Kind = ExtensionConnectionKinds.Http,
                Origin = server.Origin,
                Route = "/tasks",
                Methods = ["GET", "POST"],
                PrivateNetwork = true,
                CredentialKind = ExtensionCredentialKinds.SessionBearer,
                Credential = "host-secret"
            };

            await broker.ConfigureAsync( "one", setup, CancellationToken.None );

            var get = new BrokerRequest( "s", 1, 1, "i", ExtensionBrokerOperations.SendHttp, "tasks",
                Http: new( "GET", "/tasks" ) );

            async Task<BrokerResult> Send( BrokerRequest input, bool approve = true, bool secrets = true ) =>
                await broker.ExecuteAsync( "one", input, secrets, ( _, _ ) => ValueTask.FromResult( approve ), CancellationToken.None );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await Send( get, secrets: false ) );
            Equal( "completed", (await Send( get )).Outcome );
            Equal( "Bearer host-secret", server.Authorization );
            var oldDirectory = Environment.GetEnvironmentVariable( "CREDENTIALS_DIRECTORY" );

            try
            {
                await File.WriteAllTextAsync( Path.Combine( root, "task-token" ), "systemd-token" );
                Environment.SetEnvironmentVariable( "CREDENTIALS_DIRECTORY", root );

                await broker.ConfigureAsync( "one",
                    setup with { Name = "systemd", CredentialKind = ExtensionCredentialKinds.SystemdBearer, Credential = "task-token" },
                    CancellationToken.None );
            }
            finally
            {
                Environment.SetEnvironmentVariable( "CREDENTIALS_DIRECTORY", oldDirectory );
            }

            Equal( "completed", (await Send( get with { Connection = "systemd" } )).Outcome );
            Equal( "Bearer systemd-token", server.Authorization );

            True( !JsonSerializer
                .Serialize( broker.List( "one" ), CommandDeckJsonContext.Default.ExtensionConnectionSummaryArray )
                .Contains( "host-secret", StringComparison.Ordinal ) );

            foreach ( var route in new[]
                     {
                         "/other", "//evil/tasks", "/tasks/../other", "/tasks/%2e%2e/other", "/tasks\\other", "/tasks/..  ", "/tasks/../. ", "/tasks/..  "
                     } )
            {
                await ThrowsAsync<InvalidOperationException>( async ( ) => await Send( get with { Http = new( "GET", route ) } ) );
            }

            Equal( "redirect-denied", (await Send( get with { Http = new( "GET", "/tasks/redirect" ) } )).Outcome );
            Equal( "connection-unavailable", (await Send( get with { Http = new( "GET", "/tasks/flood" ) } )).Outcome );
            Equal( "connection-unavailable", (await Send( get with { Http = new( "GET", "/tasks/gzip" ) } )).Outcome );

            var post = get with { Http = new( "POST", "/tasks", Convert.ToBase64String( "create"u8 ), "text/plain", "unique-mutation-001" ) };

            Equal( "cancelled", (await Send( post, false )).Outcome );
            Equal( 0, server.Writes );
            Equal( "completed", (await Send( post )).Outcome );
            Equal( 1, server.Writes );
            Equal( "unknown", (await Send( post )).Outcome );
            Equal( 1, server.Writes );

            var dropped = post with { Http = post.Http! with { Path = "/tasks/drop", IdempotencyKey = "unique-mutation-drop" } };

            Equal( "unknown", (await Send( dropped )).Outcome );
            Equal( 2, server.Writes );
            Equal( "unknown", (await Send( dropped )).Outcome );
            Equal( 2, server.Writes );

            using var restarted = new ExtensionConnections( Path.Combine( root, "journal" ), new ExtensionSecretResolver( ) );

            await restarted.ConfigureAsync( "one", setup, CancellationToken.None );

            Equal( "unknown", (await restarted.ExecuteAsync( "one", post, true, ( _, _ ) => ValueTask.FromResult( true ), CancellationToken.None )).Outcome );

            var legacyKey = "legacy-mutation-key";
            var legacyName = Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( Encoding.UTF8.GetBytes( "one\0tasks\0" + legacyKey ) ) );

            await File.WriteAllTextAsync( Path.Combine( root, "journal", legacyName ), "attempted" );
            var legacy = post with { Http = post.Http! with { IdempotencyKey = legacyKey } };

            Equal( "unknown", (await restarted.ExecuteAsync( "one", legacy, true, ( _, _ ) => ValueTask.FromResult( true ), CancellationToken.None )).Outcome );
            Equal( 2, server.Writes );
            var pending = get with { Http = new( "POST", "/tasks", IdempotencyKey: "unique-mutation-002" ) };

            Equal( "cancelled", (await broker.ExecuteAsync( "one", pending, true, ( _, _ ) =>
            {
                broker.Revoke( "one" );

                return ValueTask.FromResult( true );
            }, CancellationToken.None )).Outcome );

            Equal( 2, server.Writes );

            await broker.ConfigureAsync( "one", setup with
            {
                Name = "public",
                Origin = "https://localhost",
                PrivateNetwork = false,
                CredentialKind = ExtensionCredentialKinds.None
            }, CancellationToken.None );

            Equal( "connection-unavailable", (await Send( get with { Connection = "public" } )).Outcome );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await broker.ConfigureAsync( "one", setup with { Origin = "http://localhost" }, CancellationToken.None ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private sealed class BrokerTestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new( IPAddress.Loopback, 0 );
        private readonly CancellationTokenSource _lifetime = new( );
        private readonly Task _run;
        public string Origin { get; }
        public string? Authorization { get; private set; }
        public int Writes { get; private set; }
        public int TokenRequests { get; private set; }
        public int Revocations { get; private set; }
        public string? ExpectedChallenge { get; set; }

        public BrokerTestServer( )
        {
            _listener.Start( );
            Origin = "http://127.0.0.1:" + (( IPEndPoint ) _listener.LocalEndpoint).Port;
            _run = RunAsync( );
        }

        private async Task RunAsync( )
        {
            try
            {
                while ( !_lifetime.IsCancellationRequested )
                {
                    using var client = await _listener.AcceptTcpClientAsync( _lifetime.Token );
                    await using var stream = client.GetStream( );
                    using var reader = new StreamReader( stream, Encoding.ASCII, false, 1024, true );
                    var first = await reader.ReadLineAsync( _lifetime.Token ) ?? "";
                    string? line;
                    var length = 0;
                    while ( !string.IsNullOrEmpty( line = await reader.ReadLineAsync( _lifetime.Token ) ) )
                    {
                        if ( line.StartsWith( "Authorization: ", StringComparison.Ordinal ) )
                        {
                            Authorization = line[15..];
                        }

                        if ( line.StartsWith( "Content-Length: ", StringComparison.Ordinal ) )
                        {
                            length = int.Parse( line[16..] );
                        }
                    }

                    var body = new char[length];
                    if ( length > 0 )
                    {
                        await reader.ReadBlockAsync( body, _lifetime.Token );
                    }

                    if ( first.Contains( "/oauth/", StringComparison.Ordinal ) )
                    {
                        var responseBody = OAuthResponse( first, new string( body ) );

                        await stream.WriteAsync(
                            Encoding.ASCII.GetBytes( "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                                                     responseBody.Length +
                                                     "\r\n\r\n" +
                                                     responseBody ), _lifetime.Token );

                        continue;
                    }

                    if ( first.StartsWith( "POST", StringComparison.Ordinal ) )
                    {
                        Writes++;
                    }

                    if ( first.Contains( "/tasks/drop", StringComparison.Ordinal ) )
                    {
                        continue;
                    }

                    if ( first.Contains( "/tasks/gzip", StringComparison.Ordinal ) )
                    {
                        using var compressed = new MemoryStream( );

                        using ( var gzip = new System.IO.Compression.GZipStream( compressed, System.IO.Compression.CompressionMode.Compress, true ) )
                        {
                            gzip.Write( new byte[131073] );
                        }

                        var bytes = compressed.ToArray( );

                        await stream.WriteAsync(
                            Encoding.ASCII.GetBytes( "HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: " + bytes.Length + "\r\n\r\n" ),
                            _lifetime.Token );

                        await stream.WriteAsync( bytes, _lifetime.Token );

                        continue;
                    }

                    var response = HttpFixtureResponse( first );
                    await stream.WriteAsync( Encoding.ASCII.GetBytes( response ), _lifetime.Token );
                }
            }
            catch ( Exception exception ) when ( exception is OperationCanceledException or SocketException or IOException ) { }
        }

        private static string HttpFixtureResponse( string first )
        {
            if ( first.Contains( "redirect", StringComparison.Ordinal ) )
            {
                return "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1/private\r\nContent-Length: 0\r\n\r\n";
            }

            if ( first.Contains( "flood", StringComparison.Ordinal ) )
            {
                return "HTTP/1.1 200 OK\r\nContent-Length: 131073\r\n\r\n" + new string( 'x', 131073 );
            }

            return "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok";
        }

        private string OAuthResponse( string first, string body )
        {
            if ( first.Contains( "revoke", StringComparison.Ordinal ) )
            {
                Revocations++;

                return "{}";
            }

            TokenRequests++;
            var values = ParseOAuthQuery( body );
            if ( values.GetValueOrDefault( "grant_type" ) == "authorization_code" )
            {
                var actual = Convert
                    .ToBase64String( System.Security.Cryptography.SHA256.HashData( Encoding.ASCII.GetBytes( values["code_verifier"] ) ) )
                    .TrimEnd( '=' )
                    .Replace( '+', '-' )
                    .Replace( '/', '_' );

                Equal( ExpectedChallenge, actual );

                return "{\"access_token\":\"oauth-first\",\"refresh_token\":\"refresh-first\",\"token_type\":\"Bearer\",\"expires_in\":1}";
            }

            Equal( "refresh-first", values["refresh_token"] );

            return "{\"access_token\":\"oauth-refreshed\",\"refresh_token\":\"refresh-rotated\",\"token_type\":\"Bearer\",\"expires_in\":120}";
        }

        public async ValueTask DisposeAsync( )
        {
            _lifetime.Cancel( );
            _listener.Stop( );
            await _run;
            _lifetime.Dispose( );
        }
    }
}
