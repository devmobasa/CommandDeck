using System.Diagnostics;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditAssistantStartupAsync( bool cli )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-startup-" );
        try
        {
            var executable = WriteAuditLauncher( root.FullName, "child", "echo $$ > \"$0.pid\"\nexec /usr/bin/sleep 30\n" );
            {
                using CancellationTokenSource cancelled = new( );
                cancelled.Cancel( );
                try
                {
                    await ThrowsAsync<OperationCanceledException>( async ( ) =>
                    {
                        if ( cli )
                        {
                            await using var handle = await new BubblewrapCliAgentLauncher( executable ).LaunchAsync( new( )
                            {
                                BinaryPath = executable,
                                InstallPath = root.FullName,
                                HomePath = root.FullName,
                                WorkingDirectory = root.FullName
                            }, cancelled.Token );
                        }
                        else
                        {
                            BubblewrapCodexLauncher launcher = new( executable );
                            await using var handle = await launcher.LaunchAsync( AuditLaunchRequest( root.FullName ), cancelled.Token );
                        }
                    } );

                    await Task.Delay( 300 );
                    if ( File.Exists( executable + ".pid" ) )
                    {
                        True( !IsAuditChildRunning( executable ), "cancellation returned with a running child" );
                    }
                }
                finally
                {
                    await ReapAuditChildAsync( executable );
                    File.Delete( executable + ".pid" );
                }
            }
        }
        finally
        {
            root.Delete( recursive: true );
        }
    }

    private static async Task AuditUnitStartupAsync( )
    {
        var root = SpecTemporaryFiles.CreateDirectory( "commanddeck-unit-startup-" );
        try
        {
            var submit = WriteAuditLauncher( root.FullName, "submit", "echo $$ > \"$0.pid\"\nexec /usr/bin/sleep 30\n" );
            var control = WriteAuditLauncher( root.FullName, "control", "printf '%s\\n' \"$@\" > \"$0.called\"\n" );
            using CancellationTokenSource cancelled = new( );
            var launch = new TransientUnitCodexLauncher( submit, control ).LaunchAsync( AuditLaunchRequest( root.FullName ), cancelled.Token ).AsTask( );
            try
            {
                using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 5 ) );
                while ( !File.Exists( submit + ".pid" ) )
                {
                    await Task.Delay( 10, deadline.Token );
                }

                cancelled.Cancel( );
                await ThrowsAsync<OperationCanceledException>( async ( ) => await launch );
                True( File.Exists( control + ".called" ), "accepted unit was not stopped" );
                Equal( "--user\nstop\ncommanddeck-codex-audit\n", await File.ReadAllTextAsync( control + ".called" ) );
            }
            finally
            {
                cancelled.Cancel( );
                await ReapAuditChildAsync( submit );
                if ( !launch.IsCompleted )
                {
                    await ThrowsAsync<OperationCanceledException>( async ( ) => await launch );
                }
            }
        }
        finally
        {
            root.Delete( recursive: true );
        }
    }

    private static CodexLaunchRequest AuditLaunchRequest( string root ) => new( )
    {
        BinaryPath = "/bin/false",
        RuntimeHome = root,
        WorkingDirectory = root,
        Generation = "audit",
        SocketPath = Path.Combine( root, "agent.sock" )
    };

    private static string WriteAuditLauncher( string root, string name, string script )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "The launcher fixture requires Linux." );
        }

        var path = Path.Combine( root, name );
        File.WriteAllText( path, "#!/bin/sh\n" + script );
        File.SetUnixFileMode( path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

        return path;
    }

    private static bool IsAuditChildRunning( string executable )
    {
        try
        {
            using var process = Process.GetProcessById( int.Parse( File.ReadAllText( executable + ".pid" ).Trim( ) ) );

            return !process.HasExited;
        }
        catch ( ArgumentException )
        {
            return false;
        }
    }

    private static async Task ReapAuditChildAsync( string executable )
    {
        if ( !File.Exists( executable + ".pid" ) )
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById( int.Parse( File.ReadAllText( executable + ".pid" ).Trim( ) ) );
            if ( !process.HasExited )
            {
                process.Kill( entireProcessTree: true );
            }

            await process.WaitForExitAsync( );
        }
        catch ( ArgumentException )
        {
        }
    }
}
