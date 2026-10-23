namespace CommandDeck;

public sealed record PathActionPayload( string Path )
{
    public Dictionary<string, string> ToArguments( ) => new( StringComparer.Ordinal ) { [ActionArgumentNames.Path] = Path };

    public static PathActionPayload Read( IReadOnlyDictionary<string, string> arguments )
    {
        var path = arguments.GetValueOrDefault( ActionArgumentNames.Path );
        if ( string.IsNullOrWhiteSpace( path ) )
        {
            throw new InvalidOperationException( "Action requires 'path'." );
        }

        return new PathActionPayload( path );
    }
}
