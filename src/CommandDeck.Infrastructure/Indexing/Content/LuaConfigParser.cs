namespace CommandDeck;

internal sealed class LuaConfigParser : IStructuredConfigParser
{
    private const string BlockOpen = "[[";
    private const string BlockClose = "]]";

    public static LuaConfigParser Instance { get; } = new( );

    public IReadOnlyList<ConfigEntry> Parse( string text )
    {
        var lines = StructuredConfigParserSupport.Lines( text );
        List<ConfigEntry> entries = [];

        for ( var index = 0; index < lines.Length; index++ )
        {
            var source = lines[index];
            var trimmed = source.Trimmed;
            if ( trimmed.Length == 0 || trimmed.StartsWith( "--", StringComparison.Ordinal ) )
            {
                continue;
            }

            var assignment = RemoveLocalPrefix( trimmed );
            var separator = StructuredConfigParserSupport.FindUnquoted( assignment, '=' );
            if ( separator <= 0 )
            {
                continue;
            }

            var key = assignment[..separator]
                .Trim( );

            if ( !IsLuaIdentifierPath( key ) )
            {
                continue;
            }

            var rawValue = assignment[(separator + 1)..]
                .TrimStart( );

            var keyIndex = source.Text.IndexOf( key, StringComparison.Ordinal );
            var valueIndex = source.Text.IndexOf( rawValue, Math.Max( 0, keyIndex + key.Length ), StringComparison.Ordinal );

            var value = ReadLongString( lines, ref index, rawValue );
            value = StripFieldSeparator( StripLuaComment( value ) );

            if ( value.Length == 0 )
            {
                continue;
            }

            var dot = key.LastIndexOf( '.' );

            entries.Add( StructuredConfigParserSupport.Entry( key, StripLuaString( value ), dot > 0 ? key[..dot] : null, source, keyIndex, valueIndex,
                GetAssignmentText( source, rawValue ) ) );
        }

        return entries;
    }

    private static bool IsLuaIdentifierPath( string value ) =>
        value
            .Split( '.' )
            .All( static segment => segment.Length > 0 &&
                                    (char.IsLetter( segment[0] ) || segment[0] == '_') &&
                                    segment
                                        .Skip( 1 )
                                        .All( static character => char.IsLetterOrDigit( character ) || character == '_' ) );

    private static string ReadLongString( ConfigSourceLine[] lines, ref int index, string initial )
    {
        if ( IsCompleteOrNonBlockValue( initial ) )
        {
            return initial;
        }

        List<string> values = [initial];

        while ( ++index < lines.Length )
        {
            values.Add( lines[index].Text );

            if ( lines[index]
                .Text.Contains( BlockClose, StringComparison.Ordinal ) )
            {
                break;
            }
        }

        return string.Join( '\n', values );
    }

    private static string StripLuaComment( string value )
    {
        var comment = value.IndexOf( " --", StringComparison.Ordinal );
        if ( comment < 0 )
        {
            return value.TrimEnd( );
        }

        return value[..comment]
            .TrimEnd( );
    }

    private static string StripFieldSeparator( string value )
    {
        if ( HasNoFieldSeparator( value ) )
        {
            return value;
        }

        return value[..^1]
            .TrimEnd( );
    }

    private static string StripLuaString( string value )
    {
        var trimmed = value.Trim( );
        if ( IsBracketedString( trimmed ) )
        {
            return trimmed[BlockOpen.Length..^BlockClose.Length]
                .Trim( '\r', '\n' );
        }

        return StructuredConfigParserSupport.StripQuoted( trimmed );
    }

    private static bool IsCompleteOrNonBlockValue( string initial )
    {
        return !initial.StartsWith( BlockOpen, StringComparison.Ordinal ) ||
               initial[BlockOpen.Length..]
                   .Contains( BlockClose, StringComparison.Ordinal );
    }

    // Table fields end with a separator that is not part of the value.
    private static bool HasNoFieldSeparator( string value )
    {
        return value.Length < 2 || value[^1] is not (',' or ';');
    }

    private static bool IsBracketedString( string trimmed )
    {
        return trimmed.StartsWith( BlockOpen, StringComparison.Ordinal ) &&
               trimmed.EndsWith( BlockClose, StringComparison.Ordinal ) &&
               trimmed.Length >= BlockOpen.Length + BlockClose.Length;
    }

    private static string RemoveLocalPrefix( string trimmed )
    {
        if ( trimmed.StartsWith( "local ", StringComparison.Ordinal ) )
        {
            return trimmed[6..]
                .TrimStart( );
        }

        return trimmed;
    }

    private static string? GetAssignmentText( ConfigSourceLine source, string rawValue )
    {
        if ( IsLongBracketValue( rawValue ) )
        {
            return null;
        }

        return source.Trimmed;
    }

    private static bool IsLongBracketValue( string rawValue )
    {
        return rawValue.StartsWith( BlockOpen, StringComparison.Ordinal ) || rawValue.StartsWith( "[=", StringComparison.Ordinal );
    }
}
