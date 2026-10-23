using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck;

public static partial class Program
{
    private const int ProbeShutdownDelayMilliseconds = 200;
    private const string ProbePackageId = "commanddeck.probe";
    private const string ProbeCommandId = "probe";
    private const string ProbeSessionId = "probe";

    private static async Task<int> RunExtensionProbeAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        var artifactPath = arguments.GetOption( DaemonChildProcessContract.Artifact );
        if ( artifactPath is null )
        {
            throw new ArgumentException( "--artifact is required." );
        }

        var artifact = Path.GetFullPath( artifactPath );
        var entrypoint = arguments.GetOption( DaemonChildProcessContract.Entrypoint ) ?? "CommandDeck.ExtensionProbe";

        var root = Path.Combine( Path.GetTempPath( ), "commanddeck-extension-probe-" +
                                                      Guid
                                                          .NewGuid( )
                                                          .ToString( AppFormats.CompactGuid ) );

        Directory.CreateDirectory( root );

        try
        {
            return await RunExtensionProbeCoreAsync( artifact, entrypoint, root, arguments.HasFlag( CliOptionNames.Hold ), cancellationToken )
                .ConfigureAwait( false );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task<int> RunExtensionProbeCoreAsync( string artifact, string entrypoint, string root, bool hold, CancellationToken cancellationToken )
    {
        var sentinel = Path.Combine( root, "host-sentinel" );
        var socketPath = Path.Combine( root, "host.sock" );

        await File
            .WriteAllTextAsync( sentinel, "synthetic host secret", cancellationToken )
            .ConfigureAwait( false );

        using Socket socket = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
        socket.Bind( new UnixDomainSocketEndPoint( socketPath ) );
        socket.Listen( );
        using TcpListener listener = new( IPAddress.Loopback, 0 );
        listener.Start( );
        var supervisor = CreateProbeSupervisor( );
        var watch = Stopwatch.StartNew( );
        var allocated = GC.GetTotalAllocatedBytes( );

        var process = ( ExtensionProcess ) await supervisor
            .StartAsync( new( artifact, entrypoint, ProbePackageId, ExtensionLaunchProfile.Interactive ), cancellationToken )
            .ConfigureAwait( false );

        JsonElement? report = null;
        long rss;
        long tasks;

        try
        {
            (report, rss, tasks) = await RunProbeSessionAsync( process, sentinel, socketPath, listener, watch, hold,
                    cancellationToken )
                .ConfigureAwait( false );
        }
        catch
        {
            Console.Error.WriteLine( process.ReadDiagnostics( ) );

            throw;
        }
        finally
        {
            await process
                .DisposeAsync( )
                .ConfigureAwait( false );
        }

        var stopped = !Directory.Exists( LinuxCgroupPaths.RootDirectory + process.ControlGroup );
        if ( !stopped )
        {
            throw new InvalidDataException( "The probe left a populated resource group." );
        }

        WriteProbeReport( report!.Value, watch.Elapsed, allocated, rss, tasks, stopped );

        return 0;
    }

    private static async Task InitializeProbeAsync( ExtensionPeer peer, CancellationToken cancellationToken )
    {
        var initialize = new InitializeRequest( new ProtocolVersion( ), ProbeSessionId, 1, ProbeCommandId, ExtensionPurposes.Interactive,
            [ExtensionFeatures.Commands, ExtensionFeatures.ListView], new SessionLimits( ) );

        var response = await peer
            .CallAsync( CommandDeck.ExtensionProtocol.ExtensionMethods.Initialize,
                JsonSerializer.SerializeToElement( initialize, ExtensionJsonContext.Default.InitializeRequest ), cancellationToken )
            .ConfigureAwait( false );

        var initialized = response.Deserialize( ExtensionJsonContext.Default.InitializeResult );
        if ( initialized is not { Protocol.Major: 1, Commands: [ProbeCommandId] } )
        {
            throw new InvalidDataException( "The probe handshake is invalid." );
        }

        peer.Notify( CommandDeck.ExtensionProtocol.ExtensionMethods.Initialized );
    }

    private static JsonElement CreateProbeInput( string sentinel, string socketPath, TcpListener listener )
    {
        using MemoryStream input = new( );

        using ( Utf8JsonWriter writer = new( input ) )
        {
            writer.WriteStartObject( );
            writer.WriteString( ExtensionProbeFields.HostFile, sentinel );
            writer.WriteString( ExtensionProbeFields.HostSocket, socketPath );
            writer.WriteString( ExtensionProbeFields.HostHome, Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ) );

            writer.WriteNumber( ExtensionProbeFields.Port, (( IPEndPoint ) listener.LocalEndpoint).Port );
            writer.WriteEndObject( );
        }

        return JsonElement.Parse( input.ToArray( ) );
    }

    private static void ValidateProbeReport( JsonElement? report )
    {
        if ( report is null )
        {
            throw new InvalidDataException( "The probe did not produce a view." );
        }

        foreach ( var name in ExtensionProbeFields.RequiredReportFields )
        {
            if ( !report
                    .Value.GetProperty( name )
                    .GetBoolean( ) )
            {
                throw new InvalidDataException( $"Sandbox assertion failed: {name}." );
            }
        }

        if ( report
                 .Value.GetProperty( "storage" )
                 .GetProperty( "value" )
                 .GetString( ) !=
             "nested-rpc-ok" )
        {
            throw new InvalidDataException( "The nested RPC response was lost." );
        }
    }

    private static void WriteProbeReport( JsonElement report, TimeSpan elapsed, long allocated, long rss, long tasks, bool stopped )
    {
        using Utf8JsonWriter output = new( Console.OpenStandardOutput( ) );
        output.WriteStartObject( );
        output.WriteString( "runtimeProfile", ExtensionSandbox.RuntimeProfile );

        output.WriteBoolean( "hostAot", !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported );

        output.WriteNumber( "initialViewMilliseconds", elapsed.TotalMilliseconds );
        output.WriteNumber( "hostAllocatedBytes", GC.GetTotalAllocatedBytes( ) - allocated );
        output.WriteNumber( "cgroupMemoryBytes", rss );
        output.WriteNumber( "cgroupTasks", tasks );
        output.WriteBoolean( "descendantsStopped", stopped );
        output.WritePropertyName( "report" );
        report.WriteTo( output );
        output.WriteEndObject( );
        output.Flush( );
        Console.WriteLine( );
    }

    private static async Task<(JsonElement? Report, long Rss, long Tasks)> RunProbeSessionAsync( ExtensionProcess process, string sentinel, string socketPath,
        TcpListener listener, Stopwatch watch, bool hold, CancellationToken cancellationToken )
    {
        JsonElement? report = null;
        await using var peer = CreateProbePeer( process, value => report = value );
        peer.Start( );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeout.CancelAfter( TimeSpan.FromSeconds( 5 ) );

        await InitializeProbeAsync( peer, timeout.Token )
            .ConfigureAwait( false );

        var input = CreateProbeInput( sentinel, socketPath, listener );

        await peer
            .CallAsync( CommandDeck.ExtensionProtocol.ExtensionMethods.CommandOpen, input, timeout.Token )
            .ConfigureAwait( false );

        watch.Stop( );
        ValidateProbeReport( report );

        await Task
            .Delay( ProbeShutdownDelayMilliseconds, timeout.Token )
            .ConfigureAwait( false );

        var rss = long.Parse( await File
            .ReadAllTextAsync( LinuxCgroupPaths.RootDirectory + process.ControlGroup + "/memory.current", timeout.Token )
            .ConfigureAwait( false ), System.Globalization.CultureInfo.InvariantCulture );

        var tasks = long.Parse( await File
            .ReadAllTextAsync( LinuxCgroupPaths.RootDirectory + process.ControlGroup + "/pids.current", timeout.Token )
            .ConfigureAwait( false ), System.Globalization.CultureInfo.InvariantCulture );

        await HoldProbeAsync( process, hold, cancellationToken )
            .ConfigureAwait( false );

        return (report, rss, tasks);
    }

    private static ExtensionPeer CreateProbePeer( ExtensionProcess process, Action<JsonElement?> receiveReport )
    {
        return new( process.Input, process.Output, true, [ExtensionProbeContract.StorageGetMethod, CommandDeck.ExtensionProtocol.ExtensionMethods.ViewsReplace],
            ( frame, _ ) =>
            {
                if ( frame.Method == ExtensionProbeContract.StorageGetMethod )
                {
                    return ValueTask.FromResult( JsonElement.Parse( "{\"value\":\"nested-rpc-ok\"}" ) );
                }

                receiveReport( frame.Params );

                return ValueTask.FromResult( JsonElement.Parse( "{\"viewId\":\"probe\",\"revision\":1}" ) );
            } );
    }

    private static async Task HoldProbeAsync( ExtensionProcess process, bool hold, CancellationToken cancellationToken )
    {
        if ( hold )
        {
            Console.WriteLine( ExtensionProbeContract.ReadyPrefix + process.ControlGroup );

            await Console
                .Out.FlushAsync( cancellationToken )
                .ConfigureAwait( false );

            await Task
                .Delay( Timeout.Infinite, cancellationToken )
                .ConfigureAwait( false );
        }
    }

    private static ExtensionSupervisor CreateProbeSupervisor( )
    {
        var executable = Environment.ProcessPath!;

        return new ExtensionSupervisor( executable, GetProcessPrefix( executable ) );
    }
}
