using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommandDeck.Extensions.Views;

namespace CommandDeck.ExtensionProtocol;

// Mutable wire members preserve omitted defaults under the .NET 10 source generator.
public sealed class ListItemConverter : JsonConverter<ListItem>
{
    public override ListItem Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
    {
        var wire = JsonSerializer.Deserialize( ref reader, ListItemWireContext.Default.ListItemWire );
        if ( wire is null )
        {
            throw new JsonException( "Missing list item." );
        }

        return CreateModel( wire );
    }

    public override void Write( Utf8JsonWriter writer, ListItem value, JsonSerializerOptions options ) =>
        JsonSerializer.Serialize( writer, CreateWire( value ), ListItemWireContext.Default.ListItemWire );

    private static ListItem CreateModel( ListItemWire wire )
    {
        return new ListItem
        {
            Id = wire.Id,
            Title = wire.Title,
            Subtitle = wire.Subtitle,
            Asset = wire.Asset,
            Actions = wire.Actions is null ? default : wire.Actions.ToImmutableArray( )
        };
    }

    private static ListItemWire CreateWire( ListItem value )
    {
        return new ListItemWire
        {
            Id = value.Id,
            Title = value.Title,
            Subtitle = value.Subtitle,
            Asset = value.Asset,
            Actions = value.Actions.IsDefault ? null! : value.Actions.ToArray( )
        };
    }
}

internal sealed class ListItemWire
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Asset { get; set; }
    public ViewAction[] Actions { get; set; } = [];
}

[JsonSourceGenerationOptions( PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, GenerationMode = JsonSourceGenerationMode.Metadata )]
[JsonSerializable( typeof( ListItemWire ) )]
internal sealed partial class ListItemWireContext : JsonSerializerContext;
