namespace CommandDeck;

public static class ProcessEnvironmentDefaults
{
    public const string SystemExecutablePath = "/usr/bin:/bin";
    public const string Utf8Locale = "C.UTF-8";

    public static Dictionary<string, string> CreateEnvironment( string? executablePath, bool includeLocaleAll = true )
    {
        Dictionary<string, string> environment = new( StringComparer.Ordinal )
        {
            [EnvironmentVariableNames.ExecutablePath] = executablePath ?? SystemExecutablePath,
            [EnvironmentVariableNames.Language] = Utf8Locale
        };

        if ( includeLocaleAll )
        {
            environment[EnvironmentVariableNames.LocaleAll] = Utf8Locale;
        }

        return environment;
    }
}
