using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

public static class StableId
{
    private const int HashPrefixBytes = 12;

    public static string Create( string prefix, string key )
    {
        var hash = SHA256.HashData( Encoding.UTF8.GetBytes( key ) );

        return $"{prefix}:{FormatHash( hash )}";
    }

    private static string FormatHash( byte[] hash )
    {
        return Convert
            .ToHexString( hash.AsSpan( 0, HashPrefixBytes ) )
            .ToLowerInvariant( );
    }
}
