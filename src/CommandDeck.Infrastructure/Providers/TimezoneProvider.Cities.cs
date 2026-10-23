namespace CommandDeck;

public sealed partial class TimezoneProvider
{
    private readonly record struct CityZone( string Display, string Iana );

    // (IANA id, display name, alias tokens). Several display names may share one
    // zone (Seattle and Los Angeles are both America/Los_Angeles). The display
    // name is itself a lookup key, so it need not be repeated in the aliases.
    private static readonly (string Iana, string Display, string[] Aliases)[] CityData =
    [
        // North America
        ("America/New_York", "New York", ["nyc", "new york city", "ny"]),
        ("America/New_York", "Boston", []),
        ("America/New_York", "Washington", ["washington dc", "dc"]),
        ("America/New_York", "Philadelphia", ["philly"]),
        ("America/New_York", "Atlanta", []),
        ("America/New_York", "Miami", []),
        ("America/Toronto", "Toronto", ["montreal", "ottawa"]),
        ("America/Chicago", "Chicago", []),
        ("America/Chicago", "Dallas", []),
        ("America/Chicago", "Houston", []),
        ("America/Chicago", "Austin", []),
        ("America/Winnipeg", "Winnipeg", []),
        ("America/Denver", "Denver", ["salt lake city"]),
        ("America/Phoenix", "Phoenix", []),
        ("America/Los_Angeles", "Los Angeles", ["la"]),
        ("America/Los_Angeles", "San Francisco", ["sf", "san fran"]),
        ("America/Los_Angeles", "Seattle", []),
        ("America/Los_Angeles", "Las Vegas", ["vegas"]),
        ("America/Los_Angeles", "Portland", []),
        ("America/Vancouver", "Vancouver", []),
        ("America/Edmonton", "Calgary", ["edmonton"]),
        ("America/Anchorage", "Anchorage", ["alaska"]),
        ("Pacific/Honolulu", "Honolulu", ["hawaii"]),
        ("America/Mexico_City", "Mexico City", ["mexico"]),

        // South America
        ("America/Sao_Paulo", "Sao Paulo", ["são paulo"]),
        ("America/Sao_Paulo", "Rio de Janeiro", ["rio"]),
        ("America/Argentina/Buenos_Aires", "Buenos Aires", ["argentina"]),
        ("America/Santiago", "Santiago", ["chile"]),
        ("America/Lima", "Lima", ["peru"]),
        ("America/Bogota", "Bogota", ["bogotá", "colombia"]),

        // Europe
        ("Europe/London", "London", ["uk", "britain", "england"]),
        ("Europe/Dublin", "Dublin", ["ireland"]),
        ("Europe/Lisbon", "Lisbon", ["portugal"]),
        ("Europe/Paris", "Paris", ["france"]),
        ("Europe/Madrid", "Madrid", ["spain", "barcelona"]),
        ("Europe/Berlin", "Berlin", ["germany", "munich", "frankfurt", "hamburg"]),
        ("Europe/Amsterdam", "Amsterdam", ["netherlands"]),
        ("Europe/Brussels", "Brussels", ["belgium"]),
        ("Europe/Zurich", "Zurich", ["zürich", "geneva", "switzerland"]),
        ("Europe/Rome", "Rome", ["italy", "milan"]),
        ("Europe/Vienna", "Vienna", ["austria"]),
        ("Europe/Stockholm", "Stockholm", ["sweden"]),
        ("Europe/Oslo", "Oslo", ["norway"]),
        ("Europe/Copenhagen", "Copenhagen", ["denmark"]),
        ("Europe/Helsinki", "Helsinki", ["finland"]),
        ("Europe/Warsaw", "Warsaw", ["poland"]),
        ("Europe/Prague", "Prague", ["czechia"]),
        ("Europe/Budapest", "Budapest", ["hungary"]),
        ("Europe/Athens", "Athens", ["greece"]),
        ("Europe/Istanbul", "Istanbul", ["turkey", "ankara"]),
        ("Europe/Moscow", "Moscow", ["russia"]),
        ("Europe/Kyiv", "Kyiv", ["kiev", "ukraine"]),
        ("Atlantic/Reykjavik", "Reykjavik", ["iceland"]),

        // Africa and the Middle East
        ("Africa/Cairo", "Cairo", ["egypt"]),
        ("Africa/Lagos", "Lagos", ["nigeria"]),
        ("Africa/Nairobi", "Nairobi", ["kenya"]),
        ("Africa/Johannesburg", "Johannesburg", ["south africa", "cape town"]),
        ("Africa/Casablanca", "Casablanca", ["morocco"]),
        ("Asia/Dubai", "Dubai", ["uae", "abu dhabi"]),
        ("Asia/Qatar", "Doha", ["qatar"]),
        ("Asia/Riyadh", "Riyadh", ["saudi arabia"]),
        ("Asia/Tehran", "Tehran", ["iran"]),
        ("Asia/Jerusalem", "Jerusalem", ["tel aviv", "israel"]),
        ("Asia/Baghdad", "Baghdad", ["iraq"]),

        // Asia
        ("Asia/Karachi", "Karachi", ["pakistan", "islamabad", "lahore"]),
        ("Asia/Kolkata", "Mumbai", ["bombay"]),
        ("Asia/Kolkata", "New Delhi", ["delhi", "india"]),
        ("Asia/Kolkata", "Bengaluru", ["bangalore"]),
        ("Asia/Kolkata", "Kolkata", ["calcutta"]),
        ("Asia/Kathmandu", "Kathmandu", ["nepal"]),
        ("Asia/Dhaka", "Dhaka", ["bangladesh"]),
        ("Asia/Colombo", "Colombo", ["sri lanka"]),
        ("Asia/Bangkok", "Bangkok", ["thailand"]),
        ("Asia/Jakarta", "Jakarta", ["indonesia"]),
        ("Asia/Ho_Chi_Minh", "Ho Chi Minh City", ["ho chi minh", "saigon", "hanoi", "vietnam"]),
        ("Asia/Kuala_Lumpur", "Kuala Lumpur", ["kl", "malaysia"]),
        ("Asia/Singapore", "Singapore", []),
        ("Asia/Manila", "Manila", ["philippines"]),
        ("Asia/Hong_Kong", "Hong Kong", ["hk"]),
        ("Asia/Shanghai", "Shanghai", ["beijing", "china"]),
        ("Asia/Taipei", "Taipei", ["taiwan"]),
        ("Asia/Seoul", "Seoul", ["korea", "south korea"]),
        ("Asia/Tokyo", "Tokyo", ["japan", "osaka", "kyoto"]),
        ("Asia/Ulaanbaatar", "Ulaanbaatar", ["mongolia"]),
        ("Asia/Almaty", "Almaty", ["kazakhstan"]),
        ("Asia/Tashkent", "Tashkent", ["uzbekistan"]),

        // Oceania
        ("Australia/Sydney", "Sydney", []),
        ("Australia/Melbourne", "Melbourne", []),
        ("Australia/Brisbane", "Brisbane", []),
        ("Australia/Perth", "Perth", []),
        ("Australia/Adelaide", "Adelaide", []),
        ("Pacific/Auckland", "Auckland", ["new zealand", "wellington"]),
        ("Pacific/Fiji", "Suva", ["fiji"]),

        // Reference
        ("UTC", "UTC", ["gmt", "zulu"])
    ];

    private static readonly Dictionary<string, CityZone> CityAliases = BuildAliases( );

    private static Dictionary<string, CityZone> BuildAliases( )
    {
        Dictionary<string, CityZone> map = new( StringComparer.Ordinal );

        foreach ( (var iana, var display, var aliases) in CityData )
        {
            CityZone zone = new( display, iana );
            map[Normalize( display )] = zone;

            foreach ( var alias in aliases )
            {
                map[Normalize( alias )] = zone;
            }
        }

        return map;
    }
}
