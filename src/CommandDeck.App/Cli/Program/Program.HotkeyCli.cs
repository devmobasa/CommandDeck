namespace CommandDeck;

public static partial class Program
{
    private const string InstallCommand = "install";
    private const string CheckCommand = "check";

    private const string HotkeyUsage =
        "Use 'commanddeck hotkey install [--chord CHORD]', 'commanddeck hotkey check [--chord CHORD]', or 'commanddeck hotkey remove'.";

    // Edits only CommandDeck's Hyprland module and its require line; the caller
    // reloads Hyprland afterwards. `check` validates the chord and the Lua
    // configuration and changes nothing, so installers can test it first.
    private static int RunHotkey( CliArguments arguments )
    {
        var positionals = arguments.PositionalsAfterCommand( );
        var action = ReadHotkeyAction( positionals );
        if ( action is not (InstallCommand or CheckCommand or RemoveCommand) )
        {
            Console.Error.WriteLine( HotkeyUsage );

            return 2;
        }

        HyprlandHotkeyInstaller installer = new( HyprlandHotkeyPaths.FromEnvironment( Environment.GetEnvironmentVariable ) );
        var chord = arguments.GetOption( CliOptionNames.Chord );
        IReadOnlyList<string> messages;

        try
        {
            messages = action switch
            {
                InstallCommand => installer.Install( chord ),
                CheckCommand => [HotkeyCheckMessage( installer, chord )],
                _ => installer.Remove( )
            };
        }
        catch ( InvalidOperationException exception )
        {
            Console.Error.WriteLine( exception.Message );

            return 2;
        }

        foreach ( var message in messages )
        {
            Console.WriteLine( message );
        }

        return 0;
    }

    private static string HotkeyCheckMessage( HyprlandHotkeyInstaller installer, string? chord )
    {
        var normalized = installer.Check( chord );
        if ( chord is null )
        {
            return "Saved shortcuts will be preserved; fresh installs use CTRL + SPACE. Nothing was changed.";
        }

        return $"The {normalized} hotkey can be installed. Nothing was changed.";
    }

}
