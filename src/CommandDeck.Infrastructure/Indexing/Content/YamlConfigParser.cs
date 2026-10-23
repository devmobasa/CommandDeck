namespace CommandDeck;

internal sealed class YamlConfigParser : IStructuredConfigParser
{
    private const string LiteralBlockMarker = "|";
    private const string FoldedBlockMarker = ">";

    public static YamlConfigParser Instance { get; } = new( );

    public IReadOnlyList<ConfigEntry> Parse( string text )
    {
        var lines = StructuredConfigParserSupport.Lines( text );
        List<ConfigEntry> entries = [];
        List<(int Indent, string Key)> parents = [];

        for ( var index = 0; index < lines.Length; index++ )
        {
            var source = lines[index];
            var trimmed = source.Trimmed;
            if ( IsEmptyOrDocumentMarker( trimmed ) )
            {
                continue;
            }

            var indent = CountIndent( source.Text );
            var candidate = RemoveListPrefix( trimmed );
            var separator = StructuredConfigParserSupport.FindUnquoted( candidate, ':' );
            if ( separator <= 0 )
            {
                continue;
            }

            while ( parents.Count > 0 && parents[^1].Indent >= indent )
            {
                parents.RemoveAt( parents.Count - 1 );
            }

            var key = StructuredConfigParserSupport.StripQuoted( candidate[..separator]
                .Trim( ) );

            var rawValue = candidate[(separator + 1)..]
                .TrimStart( );

            if ( rawValue.Length == 0 )
            {
                parents.Add( (indent, key) );

                continue;
            }

            var keyIndex = source.Text.IndexOf( key, StringComparison.Ordinal );
            var valueIndex = source.Text.IndexOf( rawValue, Math.Max( 0, keyIndex + key.Length ), StringComparison.Ordinal );

            string value;

            if ( rawValue is LiteralBlockMarker or FoldedBlockMarker )
            {
                value = ReadBlockScalar( lines, ref index, indent, rawValue == FoldedBlockMarker );
            }
            else
            {
                value = StructuredConfigParserSupport.StripInlineComment( rawValue, '#' );
            }

            var section = string.Join( '.', parents.Select( static parent => parent.Key ) );

            entries.Add( StructuredConfigParserSupport.Entry( key, StructuredConfigParserSupport.StripQuoted( value ), section, source, keyIndex, valueIndex,
                rawValue is LiteralBlockMarker or FoldedBlockMarker ? null : source.Trimmed ) );
        }

        return entries;
    }

    private static string ReadBlockScalar( ConfigSourceLine[] lines, ref int index, int parentIndent, bool folded )
    {
        List<string> values = [];

        while ( index + 1 < lines.Length )
        {
            var next = lines[index + 1];
            if ( IsFollowingSibling( parentIndent, next ) )
            {
                break;
            }

            index++;
            values.Add( next.Trimmed );
        }

        return string.Join( folded ? " " : "\n", values );
    }

    private static int CountIndent( string line )
    {
        var index = 0;
        while ( index < line.Length && line[index] == ' ' )
        {
            index++;
        }

        return index;
    }

    private static bool IsEmptyOrDocumentMarker( string trimmed )
    {
        return trimmed.Length == 0 || trimmed.StartsWith( '#' ) || trimmed == "---" || trimmed == "...";
    }

    private static bool IsFollowingSibling( int parentIndent, ConfigSourceLine next )
    {
        return next.Trimmed.Length > 0 && CountIndent( next.Text ) <= parentIndent;
    }

    private static string RemoveListPrefix( string trimmed )
    {
        if ( trimmed.StartsWith( "- ", StringComparison.Ordinal ) )
        {
            return trimmed[2..]
                .TrimStart( );
        }

        return trimmed;
    }
}
