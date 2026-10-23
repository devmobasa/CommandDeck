using System.Text.Json;

namespace CommandDeck;

internal static class JsonElementReader
{
    internal static string? ReadString( JsonElement element, string property )
    {
        if ( element.TryGetProperty( property, out var value ) && value.ValueKind == JsonValueKind.String )
        {
            return value.GetString( );
        }

        return null;
    }
}
