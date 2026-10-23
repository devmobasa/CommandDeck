using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

public static class ExtensionOrigins
{
    public const string Local = "local";
    public const string Development = "development";
}

public static class ExtensionStableIds
{
    private const string SourcePrefix = "extension:";
    internal const string SearchProviderPrefix = "extension-search:";
    internal const string FilterTokenPrefix = "ext";
    internal const int FilterKeyCharacters = 10;

    private const string SearchItemPrefix = "extension-item:";
    public static string SourceId( string key ) => SourcePrefix + key;
    public static string CommandId( string key, string commandId ) => SourceId( key ) + ":" + commandId;
    public static string SearchProviderId( string key, string provider ) => SearchProviderPrefix + key + ":" + provider;

    public static string SearchItemId( string key, string provider, string itemId ) =>
        SearchItemPrefix + key + ":" + provider + ":" + Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( itemId ) ) );

    public static string FilterToken( string key, string? provider = null ) =>
        FilterTokenPrefix + key[..FilterKeyCharacters] + (provider is null ? "" : "-" + provider) + ":";
}
