using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Extensions.Views;

public static class ViewValidation
{
    private const int MaximumLabelBytes = 512;
    private const int MaximumTextBytes = 2048;
    private const int MaximumListItems = 100;
    private const int MaximumFormChoices = 100;
    private const int MaximumPageTokenBytes = 256;
    private const int MaximumItemIdBytes = 256;
    private const int MaximumChoiceValueBytes = 256;
    private const int MaximumBodyBytes = 65536;
    private const int MaximumCopyTextBytes = 65536;
    private const int MaximumFormFields = 32;
    private const int MaximumMetadataItems = 32;
    private const int MaximumFieldBytes = 8192;
    private const string PngExtension = ".png";
    private const int MaximumAssetPathCharacters = 128;

    public static void Validate( ExtensionView view )
    {
        ArgumentNullException.ThrowIfNull( view );

        Id( view.Id, "id" );
        Text( view.Title, MaximumLabelBytes, "title" );
        OptionalText( view.Error, MaximumTextBytes, "error" );
        Actions( view.Actions, "actions" );

        switch ( view )
        {
            case ListView list:
                ValidateList( list );

                break;
            case DetailView detail:
                ValidateDetail( detail );

                break;
            case FormView form:
                ValidateForm( form );

                break;
            default:
                Invalid( "kind", "unsupported kind" );

                break;
        }

        if ( JsonSerializer.SerializeToUtf8Bytes( view, ExtensionViewJsonContext.Default.ExtensionView )
                 .Length >
             ExtensionViewLimits.MaximumEncodedViewBytes )
        {
            Invalid( "view", "too long" );
        }
    }

    private static void ValidateList( ListView view )
    {
        if ( view.Search is null )
        {
            Invalid( "search", "missing" );
        }

        if ( !Enum.IsDefined( view.Search.Mode ) )
        {
            Invalid( "search.mode", "unsupported kind" );
        }

        Collection( view.Items, MaximumListItems, "items" );
        Text( view.Search.Placeholder, MaximumLabelBytes, "search.placeholder" );
        Text( view.EmptyMessage, MaximumTextBytes, "emptyMessage" );
        OptionalText( view.PreviousPage, MaximumPageTokenBytes, "previousPage" );
        OptionalText( view.NextPage, MaximumPageTokenBytes, "nextPage" );
        HashSet<string> ids = new( StringComparer.Ordinal );

        for ( var index = 0; index < view.Items.Length; index++ )
        {
            var item = view.Items[index];
            var path = $"items[{index}]";

            if ( item is null )
            {
                Invalid( path, "missing" );
            }

            Text( item.Id, MaximumItemIdBytes, path + ".id" );

            if ( string.IsNullOrWhiteSpace( item.Id ) )
            {
                Invalid( path + ".id", "missing" );
            }

            if ( !ids.Add( item.Id ) )
            {
                Invalid( path + ".id", "duplicate ID" );
            }

            Text( item.Title, MaximumLabelBytes, path + ".title" );
            OptionalText( item.Subtitle, MaximumTextBytes, path + ".subtitle" );
            Asset( item.Asset, path + ".asset" );
            Actions( item.Actions, path + ".actions" );
        }
    }

    private static void ValidateDetail( DetailView view )
    {
        Asset( view.Asset, "asset" );
        Text( view.Body, MaximumBodyBytes, "body" );
        Collection( view.Metadata, MaximumMetadataItems, "metadata" );

        for ( var index = 0; index < view.Metadata.Length; index++ )
        {
            var field = view.Metadata[index];
            var path = $"metadata[{index}]";

            if ( field is null )
            {
                Invalid( path, "missing" );
            }

            Text( field.Label, MaximumLabelBytes, path + ".label" );
            Text( field.Value, MaximumTextBytes, path + ".value" );
        }
    }

    private static void ValidateForm( FormView view )
    {
        Collection( view.Fields, MaximumFormFields, "fields" );

        if ( view.FieldErrors is null )
        {
            Invalid( "fieldErrors", "missing" );
        }

        if ( view.FieldErrors.Count > MaximumFormFields )
        {
            Invalid( "fieldErrors", "too many" );
        }

        if ( view.Submit is null )
        {
            Invalid( "submit", "missing" );
        }

        Action( view.Submit, "submit" );
        HashSet<string> ids = new( StringComparer.Ordinal );

        for ( var index = 0; index < view.Fields.Length; index++ )
        {
            var field = view.Fields[index];
            var path = $"fields[{index}]";

            if ( field is null )
            {
                Invalid( path, "missing" );
            }

            Id( field.Id, path + ".id" );

            if ( !ids.Add( field.Id ) )
            {
                Invalid( path + ".id", "duplicate ID" );
            }

            ValidateField( field, path );
        }

        var errorIndex = 0;

        foreach ( var (key, error) in view.FieldErrors )
        {
            // Never put an author-supplied key or value in the diagnostic path.
            var path = $"fieldErrors[{errorIndex++}]";

            if ( !ids.Contains( key ) )
            {
                Invalid( path, "unsupported kind" );
            }

            Text( error, MaximumTextBytes, path );
        }
    }

    private static void ValidateField( FormField field, string path )
    {
        if ( field.MaxLength is < 1 or > MaximumFieldBytes )
        {
            Invalid( path + ".maxLength", "too long" );
        }

        Collection( field.Choices, MaximumFormChoices, path + ".choices" );
        Text( field.Label, MaximumLabelBytes, path + ".label" );

        if ( field.Kind is not (ExtensionFormFieldKinds.Text
            or ExtensionFormFieldKinds.Multiline
            or ExtensionFormFieldKinds.Choice
            or ExtensionFormFieldKinds.Checkbox) )
        {
            Invalid( path + ".kind", "unsupported kind" );
        }

        HashSet<string> choices = new( StringComparer.Ordinal );

        for ( var index = 0; index < field.Choices.Length; index++ )
        {
            var choice = field.Choices[index];
            var choicePath = $"{path}.choices[{index}]";

            if ( choice is null )
            {
                Invalid( choicePath, "missing" );
            }

            Text( choice.Value, MaximumChoiceValueBytes, choicePath + ".value" );
            Text( choice.Label, MaximumLabelBytes, choicePath + ".label" );

            if ( !choices.Add( choice.Value ) )
            {
                Invalid( choicePath + ".value", "duplicate ID" );
            }
        }

        if ( field.DefaultValue is { } value )
        {
            Value( field, value, path + ".defaultValue" );
        }
    }

    public static void ValidateValue( FormField field, JsonElement value ) => Value( field, value, "value" );

    private static void Value( FormField field, JsonElement value, string path )
    {
        if ( field.Kind == ExtensionFormFieldKinds.Checkbox )
        {
            if ( value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) )
            {
                Invalid( path, "unsupported kind" );
            }

            return;
        }

        if ( value.ValueKind != JsonValueKind.String )
        {
            Invalid( path, "unsupported kind" );
        }

        var text = value.GetString( )!;
        Text( text, field.MaxLength, path );

        if ( IsRequiredTextMissing( field, text ) )
        {
            Invalid( path, "missing" );
        }

        if ( IsChoiceUnknown( field, text ) )
        {
            Invalid( path, "unsupported kind" );
        }
    }

    private static void Actions( ImmutableArray<ViewAction> actions, string path )
    {
        Collection( actions, 16, path );
        HashSet<string> ids = new( StringComparer.Ordinal );

        for ( var index = 0; index < actions.Length; index++ )
        {
            var action = actions[index];
            var actionPath = $"{path}[{index}]";

            if ( action is null )
            {
                Invalid( actionPath, "missing" );
            }

            Action( action, actionPath );

            if ( !ids.Add( action.Id ) )
            {
                Invalid( actionPath + ".id", "duplicate ID" );
            }
        }
    }

    private static void Action( ViewAction action, string path )
    {
        Id( action.Id, path + ".id" );
        Text( action.Title, MaximumLabelBytes, path + ".title" );

        switch ( action )
        {
            case CallbackAction:
                break;
            case CopyTextAction copy:
                Text( copy.Text, MaximumCopyTextBytes, path + ".text" );

                break;
            case OpenUrlAction open:
                Text( open.Url, 4096, path + ".url" );

                if ( HasInvalidOpenUrl( open ) )
                {
                    Invalid( path + ".url", "invalid URL" );
                }

                break;
            default:
                Invalid( path + ".kind", "unsupported kind" );

                break;
        }
    }

    public static void ValidateCopyText( string? text ) => Text( text, MaximumCopyTextBytes, "text" );
    public static void ValidateId( string? id ) => Id( id, "id" );

    private static void Id( string? id, string path )
    {
        if ( string.IsNullOrEmpty( id ) )
        {
            Invalid( path, "missing" );
        }

        if ( id.Length > 64 )
        {
            Invalid( path, "too long" );
        }

        if ( id.Any( c => !char.IsAsciiLetterOrDigit( c ) && c is not '-' and not '_' and not '.' ) )
        {
            Invalid( path, "unsupported kind" );
        }
    }

    public static void ValidateAsset( string? asset ) => Asset( asset, "asset" );

    private static void Asset( string? asset, string path )
    {
        if ( asset is null )
        {
            return;
        }

        if ( HasInvalidAssetPath( asset ) )
        {
            Invalid( path, "unsupported kind" );
        }
    }

    private static void Collection<T>( ImmutableArray<T> values, int maximum, string path )
    {
        if ( values.IsDefault )
        {
            Invalid( path, "missing" );
        }

        if ( values.Length > maximum )
        {
            Invalid( path, "too many" );
        }
    }

    private static void OptionalText( string? value, int maximumBytes, string path )
    {
        if ( value is not null )
        {
            Text( value, maximumBytes, path );
        }
    }

    private static void Text( string? value, int maximumBytes, string path )
    {
        if ( value is null )
        {
            Invalid( path, "missing" );
        }

        if ( Encoding.UTF8.GetByteCount( value ) > maximumBytes )
        {
            Invalid( path, "too long" );
        }

        if ( value.Any( c => char.IsControl( c ) && c is not '\n' and not '\t' ) )
        {
            Invalid( path, "control character" );
        }
    }

    [DoesNotReturn]
    private static void Invalid( string path, string reason )
    {
        var diagnostic = new ExtensionViewValidationException( path, reason );

        throw new InvalidDataException( diagnostic.Message, diagnostic );
    }

    private static bool IsRequiredTextMissing( FormField field, string text )
    {
        return field.Required && string.IsNullOrWhiteSpace( text );
    }

    private static bool IsChoiceUnknown( FormField field, string text )
    {
        return field.Kind == ExtensionFormFieldKinds.Choice && !field.Choices.Any( choice => choice.Value == text );
    }

    private static bool HasInvalidOpenUrl( OpenUrlAction open )
    {
        return !Uri.TryCreate( open.Url, UriKind.Absolute, out var uri ) ||
               uri.Scheme is not (UrlSchemes.Http or UrlSchemes.Https) ||
               !string.IsNullOrEmpty( uri.UserInfo );
    }

    private static bool HasInvalidAssetPath( string asset )
    {
        return !asset.StartsWith( ExtensionPackageFiles.AssetPrefix, StringComparison.Ordinal ) ||
               !asset.EndsWith( PngExtension, StringComparison.Ordinal ) ||
               asset.Length > MaximumAssetPathCharacters ||
               asset[ExtensionPackageFiles.AssetPrefix.Length..].Length <= PngExtension.Length ||
               asset[ExtensionPackageFiles.AssetPrefix.Length..]
                   .Any( c => !char.IsAsciiLetterOrDigit( c ) && c is not '-' and not '_' and not '.' ) ||
               asset.Contains( "..", StringComparison.Ordinal );
    }
}
