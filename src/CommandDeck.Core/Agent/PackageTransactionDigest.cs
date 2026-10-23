using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

public static class PackageTransactionDigest
{
    public static string SerializeRow( string name, string version, string repository ) => $"{name}|{version}|{repository}";

    public static string Compute( string operation, IEnumerable<string> rows )
    {
        var lines = rows.Order( StringComparer.Ordinal );
        StringBuilder builder = new( );
        builder
            .Append( operation )
            .Append( '\n' );

        foreach ( var line in lines )
        {
            builder
                .Append( line )
                .Append( '\n' );
        }

        return Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( builder.ToString( ) ) ) );
    }
}
