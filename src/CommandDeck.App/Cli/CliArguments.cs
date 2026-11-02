namespace CommandDeck;

public sealed class CliArguments
{
    internal const string HelpCommand = "help";

    private const string EndOfOptions = "--";
    private readonly string[] _arguments;
    private readonly int _optionEnd;

    public CliArguments( string[] arguments )
    {
        _arguments = arguments;
        var terminator = Array.IndexOf( arguments, EndOfOptions );
        _optionEnd = terminator < 0 ? arguments.Length : terminator;
    }

    public string Command => _arguments.FirstOrDefault( static arg => !arg.StartsWith( '-' ) ) ?? HelpCommand;

    public bool HasFlag( string name ) =>
        _arguments.Take( _optionEnd ).Any( value => string.Equals( value, name, StringComparison.Ordinal ) );

    public string? GetOption( string name )
    {
        for ( var index = 0; index < _optionEnd - 1; index++ )
        {
            if ( string.Equals( _arguments[index], name, StringComparison.Ordinal ) )
            {
                return _arguments[index + 1];
            }
        }

        var prefix = name + "=";
        var inline = _arguments.Take( _optionEnd ).FirstOrDefault( value => value.StartsWith( prefix, StringComparison.Ordinal ) );

        return inline is null ? null : inline[prefix.Length..];
    }

    public IReadOnlyList<string> PositionalsAfterCommand( )
    {
        var commandIndex = Array.FindIndex( _arguments, value => !value.StartsWith( '-' ) );
        if ( commandIndex < 0 )
        {
            return [];
        }

        List<string> values = [];
        for ( var index = commandIndex + 1; index < _arguments.Length; index++ )
        {
            var value = _arguments[index];
            if ( value == EndOfOptions )
            {
                values.AddRange( _arguments[(index + 1)..] );

                break;
            }

            if ( value.StartsWith( '-' ) )
            {
                if ( HasSeparateOptionValue( index, value ) )
                {
                    index++;
                }

                continue;
            }

            values.Add( value );
        }

        return values;
    }

    public string JoinPositionalsAfterCommand( ) =>
        string.Join( ' ', PositionalsAfterCommand( ) );

    // The options read with HasFlag. Any other option takes the next word as
    // its value, so an option added later keeps working before it is listed.
    private static bool IsFlag( string option ) => option is
        CliOptionNames.AllowRemote or CliOptionNames.Disabled or CliOptionNames.Draft or CliOptionNames.EraseSettings or
        CliOptionNames.Force or CliOptionNames.Hidden or CliOptionNames.Hold or CliOptionNames.Home or
        CliOptionNames.IncludeLegacy or CliOptionNames.IncludeStderr or CliOptionNames.Index or CliOptionNames.Json or
        CliOptionNames.LabelTab or CliOptionNames.Probe or CliOptionNames.Quiet or CliOptionNames.Reasoning or
        CliOptionNames.RemoteRevoke or CliOptionNames.TransportProbe or CliOptionNames.Use or CliOptionNames.Verbose or
        CliOptionNames.Yes or DaemonChildProcessContract.Capture;

    private bool HasSeparateOptionValue( int index, string value )
    {
        return !value.Contains( '=', StringComparison.Ordinal ) &&
               !IsFlag( value ) &&
               index + 1 < _arguments.Length &&
               !_arguments[index + 1]
                   .StartsWith( '-' );
    }
}
