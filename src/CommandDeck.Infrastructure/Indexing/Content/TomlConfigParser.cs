namespace CommandDeck;

internal sealed class TomlConfigParser : IStructuredConfigParser
{
    private const string MultilineBasicDelimiter = "\"\"\"";
    private const string MultilineLiteralDelimiter = "'''";

    public static TomlConfigParser Instance { get; } = new( );

    public IReadOnlyList<ConfigEntry> Parse( string text )
    {
        var lines = StructuredConfigParserSupport.Lines( text );
        List<ConfigEntry> entries = [];
        string? table = null;

        for ( var index = 0; index < lines.Length; index++ )
        {
            var source = lines[index];
            var trimmed = source.Trimmed;
            if ( trimmed.Length == 0 || trimmed.StartsWith( '#' ) )
            {
                continue;
            }

            if ( TryReadTable( trimmed, out var parsedTable ) )
            {
                table = parsedTable;

                continue;
            }

            var separator = StructuredConfigParserSupport.FindUnquoted( trimmed, '=' );
            if ( separator <= 0 )
            {
                continue;
            }

            var key = trimmed[..separator]
                .Trim( );

            var rawValue = trimmed[(separator + 1)..]
                .TrimStart( );

            var keyIndex = source.Text.IndexOf( key, StringComparison.Ordinal );
            var valueIndex = source.Text.IndexOf( rawValue, Math.Max( 0, keyIndex + key.Length ), StringComparison.Ordinal );

            var value = ReadMultilineValue( lines, ref index, rawValue );
            value = StructuredConfigParserSupport.StripInlineComment( value, '#' );

            if ( key.Length > 0 && value.Length > 0 )
            {
                entries.Add( StructuredConfigParserSupport.Entry( StructuredConfigParserSupport.StripQuoted( key ), StripTomlString( value ), table, source,
                    keyIndex, valueIndex, GetAssignmentText( source, rawValue ) ) );
            }
        }

        return entries;
    }

    private static bool TryReadTable( string line, out string? table )
    {
        table = null;

        if ( IsArraySectionHeader( line ) )
        {
            table = line[2..^2]
                .Trim( );

            return true;
        }

        if ( IsSectionHeader( line ) )
        {
            table = line[1..^1]
                .Trim( );

            return true;
        }

        return false;
    }

    private static string ReadMultilineValue( ConfigSourceLine[] lines, ref int index, string initial )
    {
        var delimiter = GetMultilineDelimiter( initial );
        if ( IsCompleteOrNonMultilineValue( initial, delimiter ) )
        {
            return initial;
        }

        List<string> parts = [initial];

        while ( ++index < lines.Length )
        {
            parts.Add( lines[index].Text );

            if ( lines[index]
                .Text.Contains( delimiter, StringComparison.Ordinal ) )
            {
                break;
            }
        }

        return string.Join( '\n', parts );
    }

    private static string StripTomlString( string value )
    {
        var trimmed = value.Trim( );

        foreach ( var delimiter in new[] { MultilineBasicDelimiter, MultilineLiteralDelimiter } )
        {
            if ( IsDelimitedString( trimmed, delimiter ) )
            {
                return trimmed[delimiter.Length..^delimiter.Length]
                    .Trim( '\r', '\n' );
            }
        }

        return StructuredConfigParserSupport.StripQuoted( trimmed );
    }

    private static bool IsArraySectionHeader( string line )
    {
        return line.StartsWith( "[[", StringComparison.Ordinal ) && line.EndsWith( "]]", StringComparison.Ordinal ) && line.Length > 4;
    }

    private static bool IsSectionHeader( string line )
    {
        return line.StartsWith( '[' ) && line.EndsWith( ']' ) && line.Length > 2;
    }

    private static bool IsCompleteOrNonMultilineValue( string initial, string delimiter )
    {
        return delimiter.Length == 0 ||
               initial[delimiter.Length..]
                   .Contains( delimiter, StringComparison.Ordinal );
    }

    private static bool IsDelimitedString( string trimmed, string delimiter )
    {
        return trimmed.StartsWith( delimiter, StringComparison.Ordinal ) &&
               trimmed.EndsWith( delimiter, StringComparison.Ordinal ) &&
               trimmed.Length >= delimiter.Length * 2;
    }

    private static string GetMultilineDelimiter( string initial )
    {
        if ( initial.StartsWith( MultilineBasicDelimiter, StringComparison.Ordinal ) )
        {
            return MultilineBasicDelimiter;
        }

        return initial.StartsWith( MultilineLiteralDelimiter, StringComparison.Ordinal ) ? MultilineLiteralDelimiter : string.Empty;
    }

    private static string? GetAssignmentText( ConfigSourceLine source, string rawValue )
    {
        if ( IsMultilineValue( rawValue ) )
        {
            return null;
        }

        return source.Trimmed;
    }

    private static bool IsMultilineValue( string rawValue )
    {
        return rawValue.StartsWith( MultilineBasicDelimiter, StringComparison.Ordinal ) ||
               rawValue.StartsWith( MultilineLiteralDelimiter, StringComparison.Ordinal );
    }
}
