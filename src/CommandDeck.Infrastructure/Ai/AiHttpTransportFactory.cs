namespace CommandDeck;

public interface IAiHttpTransportFactory
{
    HttpClient Create( AiProviderOptions options );
}

public sealed class AiHttpTransportFactory : IAiHttpTransportFactory
{
    private const int ConnectTimeoutSeconds = 10;

    public HttpClient Create( AiProviderOptions options )
    {
        SocketsHttpHandler handler = CreateHandler( );

        return CreateClient( options, handler );
    }

    private static Uri NormalizeBaseAddress( string value ) => new( value.TrimEnd( '/' ) + "/", UriKind.Absolute );

    private static SocketsHttpHandler CreateHandler( )
    {
        return new( )
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds( ConnectTimeoutSeconds )
        };
    }

    private static HttpClient CreateClient( AiProviderOptions options, SocketsHttpHandler handler )
    {
        return new HttpClient( handler ) { BaseAddress = NormalizeBaseAddress( options.BaseUrl ), Timeout = Timeout.InfiniteTimeSpan };
    }
}
