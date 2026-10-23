namespace CommandDeck;

public static class PathExpansion
{
    public static string Expand( string path )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace( path );

        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        var stateHome = GetStateHome( home );
        var configHome = GetConfigHome( home );
        var cacheHome = GetCacheHome( home );
        var runtimeDir = GetRuntimeDirectory( );
        var expanded = ExpandEnvironmentPaths( path, home, stateHome, configHome, cacheHome, runtimeDir );
        if ( expanded == "~" )
        {
            expanded = home;
        }
        else if ( expanded.StartsWith( "~/", StringComparison.Ordinal ) )
        {
            expanded = Path.Combine( home, expanded[2..] );
        }

        return Path.GetFullPath( expanded );
    }

    public static string GetDefaultConfigPath( )
    {
        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        var configHome = GetConfigHome( home );

        return Path.Combine( configHome, "commanddeck", "commanddeck.jsonc" );
    }

    public static string GetDefaultSocketPath( )
    {
        var runtimeDir = GetRuntimeDirectory( );
        Directory.CreateDirectory( runtimeDir );

        return Path.Combine( runtimeDir, "commanddeck.sock" );
    }

    private static string GetStateHome( string home )
    {
        return Environment.GetEnvironmentVariable( XdgEnvironmentNames.StateHome ) ?? Path.Combine( home, ".local", "state" );
    }

    public static string GetConfigHome( string home )
    {
        return Environment.GetEnvironmentVariable( XdgEnvironmentNames.ConfigHome ) ?? Path.Combine( home, ".config" );
    }

    private static string GetCacheHome( string home )
    {
        return Environment.GetEnvironmentVariable( XdgEnvironmentNames.CacheHome ) ?? Path.Combine( home, ".cache" );
    }

    private static string GetRuntimeDirectory( )
    {
        return Environment.GetEnvironmentVariable( XdgEnvironmentNames.RuntimeDirectory ) ??
               Path.Combine( Path.GetTempPath( ), $"commanddeck-{Environment.UserName}" );
    }

    private static string ExpandEnvironmentPaths( string path, string home, string stateHome, string configHome, string cacheHome, string runtimeDir )
    {
        return path
            .Replace( "$XDG_STATE_HOME", stateHome, StringComparison.Ordinal )
            .Replace( "${XDG_STATE_HOME}", stateHome, StringComparison.Ordinal )
            .Replace( "$XDG_CONFIG_HOME", configHome, StringComparison.Ordinal )
            .Replace( "${XDG_CONFIG_HOME}", configHome, StringComparison.Ordinal )
            .Replace( "$XDG_CACHE_HOME", cacheHome, StringComparison.Ordinal )
            .Replace( "${XDG_CACHE_HOME}", cacheHome, StringComparison.Ordinal )
            .Replace( "$XDG_RUNTIME_DIR", runtimeDir, StringComparison.Ordinal )
            .Replace( "${XDG_RUNTIME_DIR}", runtimeDir, StringComparison.Ordinal )
            .Replace( "$HOME", home, StringComparison.Ordinal )
            .Replace( "${HOME}", home, StringComparison.Ordinal );
    }
}
