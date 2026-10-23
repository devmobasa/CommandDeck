using System.Globalization;
using System.Text.Json;

namespace CommandDeck;

// A client as `hyprctl -j clients` or `activewindow` reports it. Fullscreen is
// Hyprland's mode bitmask: 1 maximized, 2 fullscreen.
internal sealed record HyprlandWindow( string Address, string Class, string Title, int MonitorId, bool Floating, int Fullscreen, int Width, int Height )
{
    public string Selector => HyprlandWindowState.AddressPrefix + Address;
}

internal sealed record HyprlandMonitor( int Id, string Name, bool Disabled, MonitorLayout Layout );

// Reads the compositor JSON that window commands depend on. Malformed or
// incomplete entries are skipped, never guessed.
internal static class HyprlandWindowState
{
    internal const string AddressPrefix = "address:";
    private const int MaximumAddressHexCharacters = 16;
    private const int ReservedEdgeCount = 4;
    private const int PairElementCount = 2;
    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create( "0123456789abcdef" );

    // A literal window address, never a class or title pattern that could match
    // a different window.
    public static bool IsWindowSelector( string selector ) =>
        selector.StartsWith( AddressPrefix + "0x", StringComparison.Ordinal ) &&
        selector.Length > AddressPrefix.Length + 2 &&
        selector.Length <= AddressPrefix.Length + 2 + MaximumAddressHexCharacters &&
        selector
            .AsSpan( AddressPrefix.Length + 2 )
            .IndexOfAnyExcept( HexDigits ) <
        0;

    public static HyprlandWindow? ParseActiveWindow( string json ) =>
        Parse( json, static root => root.ValueKind == JsonValueKind.Object ? ReadWindow( root ) : null );

    public static IReadOnlyList<HyprlandWindow> ParseClients( string json ) =>
        Parse( json, static root => ReadArray( root, ReadWindow ) ) ?? [];

    public static IReadOnlyList<HyprlandMonitor> ParseMonitors( string json ) =>
        Parse( json, static root => ReadArray( root, ReadMonitor ) ) ?? [];

    private static T? Parse<T>( string json, Func<JsonElement, T?> read ) where T : class
    {
        if ( string.IsNullOrWhiteSpace( json ) )
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse( json );

            return read( document.RootElement );
        }
        catch ( JsonException )
        {
            return null;
        }
    }

    private static List<T> ReadArray<T>( JsonElement root, Func<JsonElement, T?> read ) where T : class
    {
        List<T> values = [];

        if ( root.ValueKind != JsonValueKind.Array )
        {
            return values;
        }

        foreach ( var element in root.EnumerateArray( ) )
        {
            if ( element.ValueKind != JsonValueKind.Object )
            {
                continue;
            }

            if ( read( element ) is { } value )
            {
                values.Add( value );
            }
        }

        return values;
    }

    private static HyprlandWindow? ReadWindow( JsonElement element )
    {
        var address = String( element, "address" );
        if ( address is null || !IsWindowSelector( AddressPrefix + address ) || Integer( element, "monitor" ) is not { } monitor )
        {
            return null;
        }

        var size = Pair( element, "size" );

        return new HyprlandWindow( address, String( element, "class" ) ?? String( element, "initialClass" ) ?? "window",
            String( element, "title" ) ?? string.Empty, monitor,
            element.TryGetProperty( "floating", out var floating ) && floating.ValueKind == JsonValueKind.True, Integer( element, "fullscreen" ) ?? 0,
            size.First, size.Second );
    }

    private static HyprlandMonitor? ReadMonitor( JsonElement element )
    {
        var name = String( element, "name" );
        var reserved = Reserved( element );
        if ( name is null || reserved is null )
        {
            return null;
        }

        var layout = ReadMonitorLayout( element, reserved, out var id );
        if ( layout is null )
        {
            return null;
        }

        var disabled = element.TryGetProperty( "disabled", out var flag ) && flag.ValueKind == JsonValueKind.True;

        return new HyprlandMonitor( id, name, disabled, layout );
    }

    // Left, top, right, and bottom, in the order hyprctl prints them.
    private static int[]? Reserved( JsonElement element )
    {
        if ( CannotReadReservedArray( element, out var reserved ) )
        {
            return null;
        }

        var edges = ReadReservedEdges( reserved );

        return edges.All( static edge => edge >= 0 ) ? edges : null;
    }

    private static (int First, int Second) Pair( JsonElement element, string property )
    {
        if ( CannotReadPairArray( element, property, out var pair ) )
        {
            return (0, 0);
        }

        var first = 0;
        if ( pair[0].ValueKind == JsonValueKind.Number &&
             pair[0]
                 .TryGetInt32( out var a ) )
        {
            first = a;
        }

        var second = 0;
        if ( pair[1].ValueKind == JsonValueKind.Number &&
             pair[1]
                 .TryGetInt32( out var b ) )
        {
            second = b;
        }

        return (first, second);
    }

    private static string? String( JsonElement element, string property )
    {
        if ( !element.TryGetProperty( property, out var value ) || value.ValueKind != JsonValueKind.String )
        {
            return null;
        }

        return value.GetString( );
    }

    private static int? Integer( JsonElement element, string property )
    {
        if ( !element.TryGetProperty( property, out var value ) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32( out var number ) )
        {
            return null;
        }

        return number;
    }

    internal static string Number( int value ) => value.ToString( CultureInfo.InvariantCulture );

    private static int[] ReadReservedEdges( JsonElement reserved )
    {
        return reserved
            .EnumerateArray( )
            .Select( ReadReservedEdge )
            .ToArray( );
    }

    private static bool CannotReadReservedArray( JsonElement element, out JsonElement reserved )
    {
        reserved = default;

        return !element.TryGetProperty( "reserved", out reserved ) ||
               reserved.ValueKind != JsonValueKind.Array ||
               reserved.GetArrayLength( ) != ReservedEdgeCount;
    }

    private static bool CannotReadPairArray( JsonElement element, string property, out JsonElement pair )
    {
        pair = default;

        return !element.TryGetProperty( property, out pair ) || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength( ) != PairElementCount;
    }

    private static int ReadReservedEdge( JsonElement edge )
    {
        if ( edge.ValueKind != JsonValueKind.Number || !edge.TryGetInt32( out var value ) )
        {
            return -1;
        }

        return value >= 0 ? value : -1;
    }

    private static MonitorLayout? ReadMonitorLayout( JsonElement element, int[] reserved, out int id )
    {
        id = default;

        if ( Integer( element, "id" ) is not { } monitorId )
        {
            return null;
        }

        id = monitorId;

        if ( Integer( element, "x" ) is not { } x ||
             Integer( element, "y" ) is not { } y ||
             Integer( element, "width" ) is not { } width ||
             Integer( element, "height" ) is not { } height ||
             !element.TryGetProperty( "scale", out var scale ) ||
             scale.ValueKind != JsonValueKind.Number )
        {
            return null;
        }

        return new MonitorLayout( x, y, width, height, scale.GetDouble( ), Integer( element, "transform" ) ?? 0,
            reserved[0], reserved[1], reserved[2], reserved[3] );
    }
}
