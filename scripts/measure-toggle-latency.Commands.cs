using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

sealed partial class HyprlandSession
{

    public static string? Refusal( Func<string, string?> environment, Func<string, bool> exists )
    {
        var instance = environment( CommandDeck.EnvironmentVariableNames.HyprlandInstanceSignature );
        var runtime = environment( CommandDeck.XdgEnvironmentNames.RuntimeDirectory );
        if ( IsSessionEnvironmentMissing( instance, runtime ) )
        {
            return "no Hyprland session (HYPRLAND_INSTANCE_SIGNATURE and XDG_RUNTIME_DIR are required).";
        }

        var directory = Path.Combine( runtime, "hypr", instance );
        if ( exists( Path.Combine( directory, ".socket.sock" ) ) && exists( Path.Combine( directory, ".socket2.sock" ) ) )
        {
            return null;
        }

        return $"Hyprland's sockets are missing in {directory}.";
    }

    public static async Task<HyprlandSession> ConnectAsync( Func<string, string?> environment, CancellationToken cancellationToken )
    {
        var directory = Path.Combine( environment( CommandDeck.XdgEnvironmentNames.RuntimeDirectory )!, "hypr", environment( CommandDeck.EnvironmentVariableNames.HyprlandInstanceSignature )! );
        Socket events = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );

        await events.ConnectAsync( new UnixDomainSocketEndPoint( Path.Combine( directory, ".socket2.sock" ) ), cancellationToken );

        return new( Path.Combine( directory, ".socket.sock" ), events );
    }

    public async Task<string> RequestAsync( string request )
    {
        using Socket socket = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 2 ) );
        await socket.ConnectAsync( new UnixDomainSocketEndPoint( _requestSocket ), timeout.Token );
        await socket.SendAsync( Encoding.UTF8.GetBytes( request ), SocketFlags.None, timeout.Token );
        using MemoryStream reply = new( );
        var buffer = new byte[64 * CommandDeck.ByteUnits.BytesPerKibibyte];
        int read;
        while ( (read = await socket.ReceiveAsync( buffer, SocketFlags.None, timeout.Token )) > 0 && reply.Length < 8 * CommandDeck.ByteUnits.BytesPerMebibyte )
        {
            reply.Write( buffer, 0, read );
        }

        return Encoding.UTF8.GetString( reply.ToArray( ) );
    }

    public async Task ToggleAsync( string via, CancellationToken cancellationToken )
    {
        if ( via == Options.ShellTogglePath )
        {
            await RunShellAsync( ToggleLatency.ShellToggle[1..], cancellationToken: cancellationToken );

            return;
        }

        var reply = (await RequestAsync( ToggleLatency.DispatchRequest )).Trim( );
        if ( reply != "ok" )
        {
            throw new InvalidOperationException( $"Hyprland rejected `{ToggleLatency.DispatchRequest}`: {reply}" );
        }
    }

    public static async Task HideAsync( )
    {
        for ( var attempt = 0; ; attempt++ )
        {
            try
            {
                await RunShellAsync( ["shell", "hide", CommandDeck.CommandDeckIdentity.PluginId], isolated: true );

                return;
            }
            catch ( Exception exception ) when ( attempt == 0 && exception is
                InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or IOException )
            {
                Console.Error.WriteLine( $"Hide failed; retrying once: {exception.Message}" );
            }
        }
    }

    private static async Task RunShellAsync( string[] arguments, bool isolated = false, CancellationToken cancellationToken = default )
    {
        ProcessStartInfo start = CreateToggleStartInfo( isolated );

        if ( isolated )
        {
            start.ArgumentList.Add( "--wait" );
            start.ArgumentList.Add( ToggleLatency.ShellToggle[0] );
        }

        foreach ( var argument in arguments )
        {
            start.ArgumentList.Add( argument );
        }

        using var process = Process.Start( start );
        if ( process is null )
        {
            throw new InvalidOperationException( "omarchy-shell did not start." );
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );

        if ( isolated )
        {
            timeout.CancelAfter( TimeSpan.FromSeconds( 2 ) );
        }

        var output = process.StandardOutput.ReadToEndAsync( timeout.Token );
        var error = process.StandardError.ReadToEndAsync( timeout.Token );

        try
        {
            await process.WaitForExitAsync( timeout.Token );
            await Task.WhenAll( output, error );
        }
        catch ( OperationCanceledException ) when ( timeout.IsCancellationRequested )
        {
            if ( !process.HasExited )
            {
                process.Kill( entireProcessTree: true );
            }

            await process
                .WaitForExitAsync( CancellationToken.None )
                .WaitAsync( TimeSpan.FromSeconds( 2 ), CancellationToken.None );

            try
            {
                await Task.WhenAll( output, error );
            }
            catch ( OperationCanceledException )
            {
            }

            cancellationToken.ThrowIfCancellationRequested( );

            throw new TimeoutException( "omarchy-shell did not finish within two seconds." );
        }

        if ( process.ExitCode != 0 )
        {
            throw new InvalidOperationException( $"omarchy-shell exited with {process.ExitCode}: {await error}{await output}" );
        }
    }

    private static ProcessStartInfo CreateToggleStartInfo( bool isolated )
    {
        return new( isolated ? "/usr/bin/setsid" : ToggleLatency.ShellToggle[0] )
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private static bool IsSessionEnvironmentMissing( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? instance, [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? runtime )
    {
        return string.IsNullOrEmpty( instance ) || string.IsNullOrEmpty( runtime );
    }
}
