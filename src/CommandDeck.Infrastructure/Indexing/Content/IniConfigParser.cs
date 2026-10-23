namespace CommandDeck;

internal sealed class IniConfigParser : IStructuredConfigParser
{
    public static IniConfigParser Instance { get; } = new( );

    public IReadOnlyList<ConfigEntry> Parse( string text )
    {
        var lines = StructuredConfigParserSupport.Lines( text );
        List<ConfigEntry> entries = [];
        string? section = null;

        for ( var index = 0; index < lines.Length; index++ )
        {
            var source = lines[index];
            var trimmed = source.Trimmed;
            if ( trimmed.Length == 0 || trimmed.StartsWith( '#' ) || trimmed.StartsWith( ';' ) )
            {
                continue;
            }

            if ( IsSectionHeader( trimmed ) )
            {
                section = trimmed[1..^1]
                    .Trim( );

                continue;
            }

            var assignment = RemoveExportPrefix( trimmed );
            var separator = StructuredConfigParserSupport.FindUnquoted( assignment, '=' );
            if ( separator <= 0 )
            {
                continue;
            }

            var key = assignment[..separator]
                .Trim( );

            var rawValue = assignment[(separator + 1)..]
                .TrimStart( );

            var keyIndex = source.Text.IndexOf( key, StringComparison.Ordinal );
            var valueIndex = source.Text.IndexOf( rawValue, Math.Max( 0, keyIndex + key.Length ), StringComparison.Ordinal );

            var value = ReadContinuation( lines, ref index, rawValue );
            value = StructuredConfigParserSupport.StripInlineComment( value, '#', ';' );

            if ( key.Length > 0 && value.Length > 0 )
            {
                entries.Add( StructuredConfigParserSupport.Entry( key, StructuredConfigParserSupport.StripQuoted( value ), section, source, keyIndex,
                    valueIndex, rawValue.EndsWith( '\\' ) ? null : source.Trimmed ) );
            }
        }

        return entries;
    }

    private static string ReadContinuation( ConfigSourceLine[] lines, ref int index, string initial )
    {
        var value = initial;
        while ( value.EndsWith( '\\' ) && index + 1 < lines.Length )
        {
            value = value[..^1]
                        .TrimEnd( ) +
                    " " +
                    lines[++index].Trimmed;
        }

        return value;
    }

    private static bool IsSectionHeader( string trimmed )
    {
        return trimmed.StartsWith( '[' ) && trimmed.EndsWith( ']' ) && trimmed.Length > 2;
    }

    private static string RemoveExportPrefix( string trimmed )
    {
        if ( trimmed.StartsWith( "export ", StringComparison.Ordinal ) )
        {
            return trimmed[7..]
                .TrimStart( );
        }

        return trimmed;
    }
}
