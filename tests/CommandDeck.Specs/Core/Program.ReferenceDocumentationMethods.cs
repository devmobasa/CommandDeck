using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandDeck.Specs;

public static partial class Program
{
    private const string CapabilityBegin = "<!-- BEGIN GENERATED CAPABILITY TABLE -->";
    private const string CapabilityEnd = "<!-- END GENERATED CAPABILITY TABLE -->";
    private const string OptionBegin = "<!-- BEGIN GENERATED OPTION TABLE -->";
    private const string OptionEnd = "<!-- END GENERATED OPTION TABLE -->";

    private static void GeneratedReferenceTablesMatchAuthoritativeCode( )
    {
        var root = FindRepositoryRoot( );

        AssertGeneratedSection( File.ReadAllText( Path.Combine( root, "docs/permissions.md" ) ), CapabilityBegin, CapabilityEnd, BuildCapabilitySection( ) );
        AssertGeneratedSection( File.ReadAllText( Path.Combine( root, "docs/configuration.md" ) ), OptionBegin, OptionEnd, BuildOptionSection( ) );

        GeneratedConfigSchemaMatchesOptionsModel( );
        GeneratedQuicklinkRulesMatchValidators( );
    }

    private static void WriteReferenceDocumentation( string root )
    {
        ReplaceGeneratedSection( Path.Combine( root, "docs/permissions.md" ), CapabilityBegin, CapabilityEnd, BuildCapabilitySection( ) );
        ReplaceGeneratedSection( Path.Combine( root, "docs/configuration.md" ), OptionBegin, OptionEnd, BuildOptionSection( ) );

        WriteConfigSchemaArtifact( root );
        File.WriteAllText( Path.Combine( root, "qml/common/QuicklinkRules.js" ), BuildQuicklinkRules( ) );
    }

    private static string BuildCapabilitySection( )
    {
        StringBuilder markdown = new( );
        markdown.AppendLine( CapabilityBegin );
        markdown.AppendLine( "<!-- Generated from CapabilityRegistry. Do not edit this table. -->" );
        markdown.AppendLine( "| Capability | Default | Data read | Data written/transmitted | User-visible trigger | Control |" );
        markdown.AppendLine( "|---|---:|---|---|---|---|" );

        foreach ( var capability in CapabilityRegistry.All )
        {
            markdown
                .Append( "| " )
                .Append( EscapeCell( capability.Name ) )
                .Append( " | " )
                .Append( EscapeCell( capability.DefaultState ) )
                .Append( " | " )
                .Append( EscapeCell( capability.DataRead ) )
                .Append( " | " )
                .Append( EscapeCell( capability.DataWrittenOrTransmitted ) )
                .Append( " | " )
                .Append( EscapeCell( capability.UserVisibleTrigger ) )
                .Append( " | " )
                .Append( EscapeCell( capability.Control ) )
                .AppendLine( " |" );
        }

        markdown.AppendLine( CapabilityEnd );

        return markdown
            .ToString( )
            .TrimEnd( );
    }

    private static string BuildOptionSection( )
    {
        StringBuilder markdown = new( );
        markdown.AppendLine( OptionBegin );
        markdown.AppendLine( "<!-- Generated from CommandDeckOptions and its option types. Do not edit this table. -->" );
        markdown.AppendLine( "| Option | Type | Default |" );
        markdown.AppendLine( "|---|---|---|" );

        foreach ( var option in EnumerateOptions( ) )
        {
            markdown
                .Append( "| `" )
                .Append( option.Path )
                .Append( "` | `" )
                .Append( option.Type )
                .Append( "` | " )
                .Append( EscapeCell( option.Default ) )
                .AppendLine( " |" );
        }

        markdown.AppendLine( OptionEnd );

        return markdown
            .ToString( )
            .TrimEnd( );
    }

    private static List<OptionReference> EnumerateOptions( )
    {
        List<OptionReference> options = [];
        VisitOptions( typeof( CommandDeckOptions ), CommandDeckOptions.CreateDefault( ), null, options );

        return options;
    }

    private static void VisitOptions( Type ownerType, object? owner, string? prefix, List<OptionReference> options )
    {
        foreach ( var property in ownerType.GetProperties( BindingFlags.Public | BindingFlags.Instance ) )
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName( property.Name );
            var path = prefix is null ? name : $"{prefix}.{name}";
            var value = owner is null ? null : property.GetValue( owner );
            var propertyType = property.PropertyType;
            options.Add( new OptionReference( path, FormatType( property ), FormatDefault( value ) ) );
            var nestedType = NestedOptionType( propertyType );
            if ( nestedType is null )
            {
                continue;
            }

            var nested = GetNestedSample( value, nestedType );
            var nestedPrefix = IsList( propertyType ) ? $"{path}[]" : path;
            VisitOptions( nestedType, nested, nestedPrefix, options );
        }
    }

    private static Type? NestedOptionType( Type type )
    {
        var candidate = IsList( type )
            ? type
                .GetGenericArguments( )[0]
            : type;

        if ( IsNestedOptionsType( candidate ) )
        {
            return candidate;
        }
        else
        {
            return null;
        }

        static bool IsNestedOptionsType( Type candidate )
        {
            return candidate.Namespace == typeof( CommandDeckOptions ).Namespace && candidate.Name.EndsWith( "Options", StringComparison.Ordinal );
        }
    }

    private static object? GetNestedSample( object? value, Type nestedType )
    {
        if ( IsOptionList( value ) )
        {
            var first = (( System.Collections.IEnumerable ) value!)
                .Cast<object?>( )
                .FirstOrDefault( );

            if ( first is not null )
            {
                return first;
            }
        }
        else if ( value is not null )
        {
            return value;
        }

        if ( nestedType == typeof( FileTypeIndexPolicyOptions ) )
        {
            return new FileTypeIndexPolicyOptions { Pattern = "*.ext" };
        }

        if ( nestedType == typeof( CustomCommandOptions ) )
        {
            return new CustomCommandOptions { Id = "example", Title = "Example" };
        }

        return null;
    }

    private static bool IsOptionList( object? value ) => value is System.Collections.IEnumerable && IsList( value.GetType( ) );

    private static bool IsList( Type type ) => type.IsGenericType && type.GetGenericTypeDefinition( ) == typeof( List<> );

    private static string FormatType( Type type )
    {
        var nullable = Nullable.GetUnderlyingType( type );
        if ( nullable is not null )
        {
            return $"{FormatType( nullable )}?";
        }

        if ( IsList( type ) )
        {
            return $"{FormatType( type.GetGenericArguments( )[0] )}[]";
        }

        if ( IsDictionaryType( type ) )
        {
            return "object";
        }

        if ( type == typeof( string ) )
        {
            return "string";
        }

        if ( type == typeof( bool ) )
        {
            return "boolean";
        }

        if ( IsIntegerType( type ) )
        {
            return "integer";
        }

        if ( type == typeof( double ) )
        {
            return "number";
        }

        return type.Name;

        static bool IsDictionaryType( Type type ) => type.IsGenericType && type.GetGenericTypeDefinition( ) == typeof( Dictionary<,> );

        static bool IsIntegerType( Type type ) => type == typeof( int ) || type == typeof( long );
    }

    private static string FormatType( PropertyInfo property )
    {
        var type = FormatType( property.PropertyType );
        var nullability = new NullabilityInfoContext( ).Create( property );
        if ( IsNullableReferenceProperty( property, nullability ) )
        {
            return $"{type}?";
        }
        else
        {
            return type;
        }

        static bool IsNullableReferenceProperty( PropertyInfo property, NullabilityInfo nullability )
        {
            return !property.PropertyType.IsValueType && nullability.ReadState == NullabilityState.Nullable;
        }
    }

    private static string FormatDefault( object? value ) => value switch
    {
        null => "`null`",
        string text => $"`{text.Replace( "`", "\\`", StringComparison.Ordinal )}`",
        bool flag => flag ? "`true`" : "`false`",
        int or long => $"`{Convert.ToString( value, CultureInfo.InvariantCulture )}`",
        System.Collections.ICollection collection => $"{collection.Count} default item(s)",
        _ when value
            .GetType( )
            .Name.EndsWith( "Options", StringComparison.Ordinal ) => "object",
        _ => $"`{value}`"
    };

    private static string EscapeCell( string value ) => value
        .Replace( "|", "\\|", StringComparison.Ordinal )
        .Replace( "\r", " ", StringComparison.Ordinal )
        .Replace( "\n", " ", StringComparison.Ordinal );

    private static void AssertGeneratedSection( string document, string begin, string end, string expected ) => Equal( NormalizeArtifact( expected ),
        NormalizeArtifact( ReadGeneratedSection( document, begin, end ) ) );

    private static string ReadGeneratedSection( string document, string begin, string end )
    {
        var start = document.IndexOf( begin, StringComparison.Ordinal );
        var finish = document.IndexOf( end, StringComparison.Ordinal );
        True( start >= 0 && finish >= start );

        return document[start..(finish + end.Length)];
    }

    private static void ReplaceGeneratedSection( string path, string begin, string end, string replacement )
    {
        var document = File.ReadAllText( path );
        var current = ReadGeneratedSection( document, begin, end );
        File.WriteAllText( path, document.Replace( current, replacement, StringComparison.Ordinal ) );
    }

    private sealed record OptionReference( string Path, string Type, string Default );

    private static bool IsNullableSchemaReference( PropertyInfo? property, Type effective ) =>
        property is not null &&
        !effective.IsValueType &&
        SchemaNullability.Create( property )
            .ReadState ==
        NullabilityState.Nullable;

    private static Type? SchemaNestedOptionType( Type type )
    {
        var nested = NestedOptionType( type );
        if ( nested is not null )
        {
            return nested;
        }

        if ( IsSchemaOptionType( type ) )
        {
            return type;
        }

        return null;
    }

    private static bool IsSchemaOptionType( Type type ) =>
        type.Namespace == typeof( CommandDeckOptions ).Namespace && type.Name.EndsWith( "Options", StringComparison.Ordinal );

    private static bool TryReadSchemaString( JsonNode? node, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out string? text )
    {
        text = null;

        return node is JsonValue value && value.TryGetValue( out text );
    }

    private static bool IsNullSchemaType( JsonNode? node ) =>
        TryReadSchemaString( node, out var name ) && string.Equals( name, "null", StringComparison.Ordinal );

    private static bool TryReadPropertySchema( JsonObject? properties, string name, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] out JsonNode? schema )
    {
        schema = null;

        return properties is not null && properties.TryGetPropertyValue( name, out schema ) && schema is not null;
    }
}
