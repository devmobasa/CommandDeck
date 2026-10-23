namespace CommandDeck;

public sealed record TextActionPayload( string Text )
{
    public Dictionary<string, string> ToArguments( ) => new( StringComparer.Ordinal ) { [ActionArgumentNames.Text] = Text };

    public static TextActionPayload Read( IReadOnlyDictionary<string, string> arguments )
    {
        var text = arguments.GetValueOrDefault( ActionArgumentNames.Text );
        if ( text is null )
        {
            throw new InvalidOperationException( "Action requires 'text'." );
        }

        return new TextActionPayload( text );
    }
}
