namespace CommandDeck;

/// <summary>
/// Finds the <c>hey</c> binary to run. Omarchy installs hey-cli through mise as
/// a wrapper script on PATH that runs <c>mise use -g</c> before every call,
/// which costs about 70 ms each time; the binary mise installed runs in about
/// 15 ms. A native binary on PATH (a package install) wins, then mise's
/// install, then the wrapper as a working last resort.
/// </summary>
public sealed class HeyExecutable
{
    public const string CommandName = "hey";
    public const string MiseToolDirectory = "github-basecamp-hey-cli";

    private readonly Func<string, string?> _environment;
    private readonly Func<string, bool> _isExecutable;
    private readonly Func<string, bool> _isNativeBinary;

    public HeyExecutable( Func<string, string?>? environment = null, Func<string, bool>? isExecutable = null, Func<string, bool>? isNativeBinary = null )
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _isExecutable = isExecutable ?? ProcessRunner.ExistsOnPath;
        _isNativeBinary = isNativeBinary ?? IsNativeBinary;
    }

    // COMMANDDECK_HEY names the binary to run, or "off" to turn the HEY
    // integration off; a path that is not an executable file resolves nothing.
    public const string OverrideVariable = "COMMANDDECK_HEY";
    public const string OverrideOff = "off";

    public string? Resolve( )
    {
        var configured = _environment( OverrideVariable );
        if ( !string.IsNullOrWhiteSpace( configured ) )
        {
            return GetConfiguredExecutable( configured );
        }

        string? wrapper = null;

        foreach ( var directory in (_environment( EnvironmentVariableNames.ExecutablePath ) ?? "").Split( Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries ) )
        {
            if ( !Path.IsPathFullyQualified( directory ) )
            {
                continue;
            }

            var candidate = Path.Combine( directory, CommandName );
            if ( !_isExecutable( candidate ) )
            {
                continue;
            }

            if ( _isNativeBinary( candidate ) )
            {
                return candidate;
            }

            wrapper ??= candidate;
        }

        var installed = MiseInstallPath( );
        if ( IsInstalledExecutable( installed ) )
        {
            return installed;
        }

        return wrapper;
    }

    // mise keeps a `latest` link beside each installed version of a tool.
    internal string? MiseInstallPath( )
    {
        var dataDirectory = _environment( EnvironmentVariableNames.MiseDataDirectory );
        if ( string.IsNullOrWhiteSpace( dataDirectory ) )
        {
            var dataHome = _environment( XdgEnvironmentNames.DataHome );
            if ( string.IsNullOrWhiteSpace( dataHome ) )
            {
                var home = _environment( EnvironmentVariableNames.Home );
                if ( string.IsNullOrWhiteSpace( home ) )
                {
                    return null;
                }

                dataHome = Path.Combine( home, ".local", "share" );
            }

            dataDirectory = Path.Combine( dataHome, "mise" );
        }

        return GetMiseExecutable( dataDirectory );
    }

    private static bool IsNativeBinary( string path )
    {
        try
        {
            using var stream = File.OpenRead( path );
            Span<byte> header = stackalloc byte[CommandDeck.ExtensionProtocol.ExtensionTargets.ElfMagic.Length];

            return stream.Read( header ) == header.Length && header.SequenceEqual( CommandDeck.ExtensionProtocol.ExtensionTargets.ElfMagic );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            return false;
        }
    }

    private string? GetConfiguredExecutable( string configured )
    {
        if ( IsConfiguredExecutable( configured ) )
        {
            return configured;
        }

        return null;
    }

    private bool IsInstalledExecutable( string? installed )
    {
        return installed is not null && _isExecutable( installed );
    }

    private static string? GetMiseExecutable( string dataDirectory )
    {
        if ( Path.IsPathFullyQualified( dataDirectory ) )
        {
            return Path.Combine( dataDirectory, "installs", MiseToolDirectory, "latest", CommandName );
        }

        return null;
    }

    private bool IsConfiguredExecutable( string configured )
    {
        return configured != OverrideOff && Path.IsPathFullyQualified( configured ) && _isExecutable( configured );
    }
}
