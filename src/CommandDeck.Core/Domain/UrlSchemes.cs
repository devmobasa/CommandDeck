namespace CommandDeck;

public static class UrlSchemes
{
    public const string Http = "http";
    public const string Https = "https";
}

public static class UrlSyntax
{
    public const string HttpPrefix = UrlSchemes.Http + "://";
    public const string HttpsPrefix = UrlSchemes.Https + "://";
}
