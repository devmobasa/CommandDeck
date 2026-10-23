using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CommandDeck;

internal sealed class RenderedExtensionViewConverter : JsonConverter<RenderedExtensionView>
{
    private const string KindField = "kind";
    private const string SearchField = "search";
    private const string ItemsField = "items";
    private const string EmptyMessageField = "emptyMessage";
    private const string PreviousPageField = "previousPage";
    private const string NextPageField = "nextPage";
    private const string AssetField = "asset";
    private const string BodyField = "body";
    private const string MetadataField = "metadata";
    private const string FieldsField = "fields";
    private const string SubmitField = "submit";
    private const string FieldErrorsField = "fieldErrors";
    private const string ValidatedFormRevisionField = "validatedFormRevision";
    private const string IdField = "id";
    private const string TitleField = "title";
    private const string ActionsField = "actions";
    private const string LoadingField = "loading";
    private const string ErrorField = "error";
    private const string DepthField = "depth";
    private const string BackHandleField = "backHandle";
    private const string ImageSourceField = "imageSource";

    public override RenderedExtensionView Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options ) =>
        throw new NotSupportedException( "Renderer payloads are host output only." );

    public override void Write( Utf8JsonWriter writer, RenderedExtensionView value, JsonSerializerOptions options )
    {
        writer.WriteStartObject( );
        writer.WriteString( KindField, value.Kind );
        Write( writer, SearchField, value.Search );
        Write( writer, ItemsField, value.Items );
        Write( writer, EmptyMessageField, value.EmptyMessage );
        Write( writer, PreviousPageField, value.PreviousPage );
        Write( writer, NextPageField, value.NextPage );
        Write( writer, AssetField, value.Asset );
        Write( writer, BodyField, value.Body );

        if ( value.Metadata is { } metadata )
        {
            Write( writer, MetadataField, metadata );
        }

        if ( value.Fields is { } fields )
        {
            Write( writer, FieldsField, fields );
        }

        Write( writer, SubmitField, value.Submit );
        Write( writer, FieldErrorsField, value.FieldErrors );

        if ( value.ValidatedFormRevision is { } revision )
        {
            writer.WriteNumber( ValidatedFormRevisionField, revision );
        }

        writer.WriteString( IdField, value.Id );
        writer.WriteString( TitleField, value.Title );
        Write( writer, ActionsField, value.Actions );
        writer.WriteBoolean( LoadingField, value.Loading );
        Write( writer, ErrorField, value.Error );
        writer.WriteNumber( DepthField, value.Depth );
        Write( writer, BackHandleField, value.BackHandle );
        Write( writer, ImageSourceField, value.ImageSource );
        Write( writer, BackHandleField, value.RestoredBackHandle );
        writer.WriteEndObject( );
    }

    private static void Write<T>( Utf8JsonWriter writer, string name, T value )
    {
        if ( value is null )
        {
            return;
        }

        writer.WritePropertyName( name );
        var metadata = ( JsonTypeInfo<T> ) RenderedExtensionJsonContext.Default.GetTypeInfo( typeof( T ) )!;
        JsonSerializer.Serialize( writer, value, metadata );
    }
}
