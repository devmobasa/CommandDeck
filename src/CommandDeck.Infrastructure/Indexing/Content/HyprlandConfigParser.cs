namespace CommandDeck;

internal sealed class HyprlandConfigParser : IStructuredConfigParser
{
    public static HyprlandConfigParser Instance { get; } = new( );

    public IReadOnlyList<ConfigEntry> Parse( string text )
    {
        var lines = StructuredConfigParserSupport.Lines( text );
        List<ConfigEntry> entries = [];
        List<string> blocks = [];

        foreach ( var source in lines )
        {
            var trimmed = source.Trimmed;
            if ( trimmed.Length == 0 || trimmed.StartsWith( '#' ) )
            {
                continue;
            }

            if ( IsSectionStart( trimmed ) )
            {
                blocks.Add( trimmed[..^1]
                    .Trim( ) );

                continue;
            }

            if ( trimmed.StartsWith( '}' ) )
            {
                if ( blocks.Count > 0 )
                {
                    blocks.RemoveAt( blocks.Count - 1 );
                }

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

            var value = StructuredConfigParserSupport.StripInlineComment( rawValue, '#' );
            if ( IsEmptyAssignment( key, value ) )
            {
                continue;
            }

            var keyIndex = source.Text.IndexOf( key, StringComparison.Ordinal );
            var valueIndex = source.Text.IndexOf( rawValue, Math.Max( 0, keyIndex + key.Length ), StringComparison.Ordinal );

            entries.Add( StructuredConfigParserSupport.Entry( key, value, blocks.Count == 0 ? null : string.Join( '.', blocks ), source, keyIndex, valueIndex,
                source.Trimmed ) );
        }

        return entries;
    }

    private static bool IsSectionStart( string trimmed )
    {
        return trimmed.EndsWith( '{' ) && trimmed.Length > 1;
    }

    private static bool IsEmptyAssignment( string key, string value )
    {
        return key.Length == 0 || value.Length == 0;
    }
}
