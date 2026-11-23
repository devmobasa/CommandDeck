using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CommandDeck;

/// <summary>
/// Picks the Hyprland window of the Herdr desktop app that the CLI changed.
/// The CLI talks to the listening socket in the default home, so only a
/// window of the process listening there counts, whatever its class (a custom
/// HERDR_APP_ID changes it) and whatever other herdr-app windows show. If that
/// process cannot be verified, no window is chosen rather than the wrong one.
/// </summary>
internal static class HerdrAppWindows
{
    private const string HomeFolder = "herdr-app";
    private const string SocketFile = "herdr-app.sock";
    private const int UnknownProcess = -1;

    // SOL_SOCKET and SO_PEERCRED on Linux; struct ucred is pid, uid, gid.
    private const int SocketLevel = 1;
    private const int PeerCredentialOption = 17;
    private const int PeerCredentialBytes = 12;

    // Unverified: the process listening on the socket is not known.
    internal readonly record struct Choice( string? Address, bool Unverified );

    private readonly record struct Window( string Address, int Process, long History, bool Shown );

    // listener is the verified process listening on the socket, or null.
    internal static Choice Choose( string clientsJson, int? listener )
    {
        if ( listener is not { } owner )
        {
            return new( null, Unverified: true );
        }

        return new( MostRecent( ReadWindows( clientsJson ).Where( window => window.Process == owner && window.Shown ) ), Unverified: false );
    }

    // The default home the CLI uses: $XDG_CONFIG_HOME/herdr-app when it is an
    // absolute path, otherwise ~/.config/herdr-app, where ~ falls back to the
    // account's home directory when HOME is unset.
    internal static string? SocketPath( Func<string, string?> environment, Func<string?>? accountHome = null )
    {
        var config = environment( XdgEnvironmentNames.ConfigHome );
        if ( string.IsNullOrEmpty( config ) || !Path.IsPathRooted( config ) )
        {
            var home = environment( EnvironmentVariableNames.Home );
            if ( string.IsNullOrEmpty( home ) )
            {
                home = (accountHome ?? AccountHome)( );
            }

            if ( string.IsNullOrEmpty( home ) )
            {
                return null;
            }

            config = Path.Combine( home, ".config" );
        }

        return Path.Combine( config, HomeFolder, SocketFile );
    }

    internal static int? ReadListener( ) => SocketPath( Environment.GetEnvironmentVariable ) is { } path ? ReadListener( path ) : null;

    // Asks the kernel which process listens on the socket, as the CLI reaches
    // it: a connection's peer credentials name the listener, whatever spelling
    // the path has and however often it was bound. The connection is closed
    // at once and sends nothing.
    internal static int? ReadListener( string socketPath )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return null;
        }

        try
        {
            using Socket socket = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified ) { Blocking = false };
            socket.Connect( new UnixDomainSocketEndPoint( socketPath ) );

            Span<byte> credentials = stackalloc byte[PeerCredentialBytes];
            var length = socket.GetRawSocketOption( SocketLevel, PeerCredentialOption, credentials );
            var process = length >= sizeof( int ) ? MemoryMarshal.Read<int>( credentials ) : 0;

            return process > 0 ? process : null;
        }
        catch ( Exception exception ) when ( exception is SocketException or IOException or ArgumentException )
        {
            return null;
        }
    }

    private static List<Window> ReadWindows( string json )
    {
        List<Window> windows = [];

        try
        {
            using var document = JsonDocument.Parse( json );
            if ( document.RootElement.ValueKind != JsonValueKind.Array )
            {
                return windows;
            }

            foreach ( var client in document.RootElement.EnumerateArray( ) )
            {
                if ( client.ValueKind == JsonValueKind.Object && Text( client, "address" ) is { Length: > 0 } address )
                {
                    windows.Add( new Window( address, Process( client ), History( client ), IsShown( client ) ) );
                }
            }
        }
        catch ( JsonException )
        {
            windows.Clear( );
        }

        return windows;
    }

    private static bool IsShown( JsonElement client ) =>
        !(client.TryGetProperty( "mapped", out var mapped ) && mapped.ValueKind == JsonValueKind.False) &&
        !(client.TryGetProperty( "hidden", out var hidden ) && hidden.ValueKind == JsonValueKind.True);

    private static int Process( JsonElement client ) =>
        client.TryGetProperty( "pid", out var pid ) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32( out var number ) && number > 0
            ? number
            : UnknownProcess;

    // Hyprland numbers focus history from 0, the window focused last.
    private static long History( JsonElement client ) =>
        client.TryGetProperty( "focusHistoryID", out var history ) && history.ValueKind == JsonValueKind.Number &&
        history.TryGetInt64( out var number ) && number >= 0
            ? number
            : long.MaxValue;

    private static string? MostRecent( IEnumerable<Window> windows ) =>
        windows
            .OrderBy( static window => window.History )
            .Select( static window => window.Address )
            .FirstOrDefault( );

    private static string? Text( JsonElement client, string property )
    {
        if ( !client.TryGetProperty( property, out var value ) || value.ValueKind != JsonValueKind.String )
        {
            return null;
        }

        try
        {
            return value.GetString( );
        }
        catch ( InvalidOperationException )
        {
            return null;
        }
    }

    private static string? AccountHome( ) => Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
}
