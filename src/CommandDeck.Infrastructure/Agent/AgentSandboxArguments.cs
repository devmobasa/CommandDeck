namespace CommandDeck;

internal static class AgentSandboxArguments
{
    private const string XdgDirectoryName = "xdg";
    private const string ResolverDirectory = "/run/systemd/resolve";

    private static readonly (string Variable, string? Directory)[] XdgLayout =
    [
        (XdgEnvironmentNames.RuntimeDirectory, null),
        (XdgEnvironmentNames.ConfigHome, "config"),
        (XdgEnvironmentNames.CacheHome, "cache"),
        (XdgEnvironmentNames.DataHome, "data"),
        (XdgEnvironmentNames.StateHome, "state")
    ];

    internal static string XdgDirectory( string home ) => Path.Combine( home, XdgDirectoryName );

    internal static void AddResolverBind( List<string> arguments )
    {
        if ( Directory.Exists( ResolverDirectory ) )
        {
            arguments.AddRange( ["--ro-bind", ResolverDirectory, ResolverDirectory] );
        }
    }

    internal static void AddXdgEnvironment( List<string> arguments, string home )
    {
        var root = XdgDirectory( home );
        foreach ( var (variable, directory) in XdgLayout )
        {
            var path = directory is null ? root : Path.Combine( root, directory );
            arguments.AddRange( ["--setenv", variable, path] );
        }
    }

    internal static List<string> Create( )
    {
        return
        [
            "--unshare-all", "--share-net", "--die-with-parent", "--new-session",
            "--ro-bind", "/usr", "/usr",
            "--ro-bind", "/etc", "/etc",
            "--symlink", "usr/lib", "/lib",
            "--symlink", "usr/lib64", "/lib64",
            "--symlink", "usr/bin", "/bin",
            "--symlink", "usr/sbin", "/sbin",
            "--proc", "/proc",
            "--dev", "/dev",
            "--tmpfs", "/tmp",
            "--tmpfs", "/var",
            "--tmpfs", "/home",
            "--tmpfs", "/root",
            "--tmpfs", "/run",
            "--tmpfs", "/opt"
        ];
    }
}
