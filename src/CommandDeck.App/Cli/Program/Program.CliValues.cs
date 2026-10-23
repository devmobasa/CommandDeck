namespace CommandDeck;

public static partial class Program
{
    private static string ReadCliAction( IReadOnlyList<string> values, int index, string fallback )
    {
        if ( values.Count <= index )
        {
            return fallback;
        }

        return values[index]
            .ToLowerInvariant( );
    }

    private static string ReadHotkeyAction( IReadOnlyList<string> values )
    {
        if ( values.Count != 1 )
        {
            return string.Empty;
        }

        return values[0]
            .ToLowerInvariant( );
    }

    private static string[] GetProcessPrefix( string executable )
    {
        if ( Path.GetFileNameWithoutExtension( executable ) == DotnetHost.ExecutableName )
        {
            var commandLine = Environment.GetCommandLineArgs( );

            return [commandLine[0]];
        }

        return [];
    }
}
