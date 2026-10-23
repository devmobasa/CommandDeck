using System.Collections.Immutable;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Extensions.Views;

public static class FormValidation
{
    public static void ValidateSubmission( ImmutableArray<FormField> fields, Dictionary<string, JsonElement>? values )
    {
        if ( HasInvalidSubmittedFields( fields, values ) )
        {
            throw new InvalidDataException( "Submit exactly the declared form fields." );
        }

        foreach ( var field in fields )
        {
            if ( !values.TryGetValue( field.Id, out var value ) )
            {
                throw new InvalidDataException( "A form field is missing." );
            }

            ViewValidation.ValidateValue( field, value );

            if ( IsRequiredCheckboxUnchecked( field, value ) )
            {
                throw new InvalidDataException( "A required checkbox must be checked." );
            }
        }
    }

    private static bool HasInvalidSubmittedFields( ImmutableArray<FormField> fields,
        [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] Dictionary<string, JsonElement>? values )
    {
        return values is null ||
               values.Count != fields.Length ||
               values
                   .Keys.Except( fields.Select( field => field.Id ), StringComparer.Ordinal )
                   .Any( );
    }

    private static bool IsRequiredCheckboxUnchecked( FormField field, JsonElement value )
    {
        return field is { Required: true, Kind: ExtensionFormFieldKinds.Checkbox } && value.ValueKind != JsonValueKind.True;
    }
}
