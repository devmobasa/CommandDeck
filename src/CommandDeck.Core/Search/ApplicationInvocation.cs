namespace CommandDeck;

public sealed class ApplicationInvocation
{
    private const string ApplicationFilterToken = "app:";

    public required string ApplicationQuery { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public static bool TryParse( ParsedQuery query, out ApplicationInvocation? invocation )
    {
        invocation = null;

        if ( CannotInvokeApplication( query ) )
        {
            return false;
        }

        var explicitApplication = IsExplicitApplicationQuery( query );
        if ( !explicitApplication && !string.IsNullOrWhiteSpace( query.ProviderFilter ) )
        {
            return false;
        }

        var arguments = query
            .Terms.Skip( 1 )
            .ToArray( );

        if ( arguments.Any( static argument => argument.Contains( '\u001f', StringComparison.Ordinal ) ) )
        {
            return false;
        }

        if ( !LooksLikeArgument( arguments[0] ) )
        {
            return false;
        }

        invocation = new ApplicationInvocation { ApplicationQuery = query.Terms[0], Arguments = arguments };

        return true;
    }

    private static bool LooksLikeArgument( string value )
    {
        if ( LooksLikePathOrOption( value ) )
        {
            return true;
        }

        return Uri.TryCreate( value, UriKind.Absolute, out var uri ) && !string.IsNullOrWhiteSpace( uri.Scheme );
    }

    private static bool CannotInvokeApplication( ParsedQuery query )
    {
        return query.IsCalculator || query.IsAiMode || query.IsCommandMode || query.Terms.Count < 2;
    }

    private static bool IsExplicitApplicationQuery( ParsedQuery query )
    {
        return query.Original.StartsWith( ApplicationFilterToken, StringComparison.OrdinalIgnoreCase ) || query.IncludesFilter( SearchFilterIds.Applications );
    }

    private static bool LooksLikePathOrOption( string value )
    {
        return value.StartsWith( '-', StringComparison.Ordinal ) ||
               value.StartsWith( '~' ) ||
               value.StartsWith( '.' ) ||
               value.Contains( Path.DirectorySeparatorChar ) ||
               value.Contains( Path.AltDirectorySeparatorChar );
    }
}
