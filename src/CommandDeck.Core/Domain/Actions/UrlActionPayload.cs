namespace CommandDeck;

public static class UrlTemplateTokens
{
    public const string Query = "{query}";

    public static string Expand( string template, string query ) =>
        template.Replace( Query, Uri.EscapeDataString( query ), StringComparison.OrdinalIgnoreCase );
}

public sealed record UrlActionPayload( string UrlTemplate, string? Alias = null )
{
    public Dictionary<string, string> ToArguments( ) => new( StringComparer.Ordinal )
    {
        [ActionArgumentNames.UrlTemplate] = UrlTemplate,
        [ActionArgumentNames.Alias] = Alias ?? string.Empty
    };

    public static UrlActionPayload Read( IReadOnlyDictionary<string, string> arguments )
    {
        var template = arguments.GetValueOrDefault( ActionArgumentNames.UrlTemplate );
        if ( string.IsNullOrWhiteSpace( template ) )
        {
            throw new InvalidOperationException( "Action requires 'urlTemplate'." );
        }

        return new UrlActionPayload( template, arguments.GetValueOrDefault( ActionArgumentNames.Alias ) );
    }

    public string Expand( string query )
    {
        if ( HasQueryAliasPrefix( query ) )
        {
            query = query[(Alias.Length + 1)..];
        }

        return UrlTemplateTokens.Expand( UrlTemplate, query );
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen( true, nameof( Alias ) )]
    private bool HasQueryAliasPrefix( string query )
    {
        return !string.IsNullOrWhiteSpace( Alias ) && query.StartsWith( Alias + " ", StringComparison.OrdinalIgnoreCase );
    }
}
