using System.Text;

namespace CommandDeck;

internal static class IndexStateEncoding
{
    internal static string Encode( string? value )
    {
        if ( value is null )
        {
            return string.Empty;
        }

        return Convert.ToBase64String( Encoding.UTF8.GetBytes( value ) );
    }

    internal static string? Decode( string value )
    {
        if ( value.Length == 0 )
        {
            return null;
        }

        return Encoding.UTF8.GetString( Convert.FromBase64String( value ) );
    }
}
