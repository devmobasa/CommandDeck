using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.ExtensionProbe;

internal static partial class Program
{
    private static async Task Main( string[] args )
    {
        if ( args.Length > 0 )
        {
            if ( args[0] == ProbeOptions.Descendant )
            {
                using var grandchild = StartGrandchild( );
            }

            await Task.Delay( Timeout.Infinite );

            return;
        }

        ExtensionPeer? peer = null;
        var initialized = false;
        var ready = false;
        TaskCompletionSource finished = new( TaskCreationOptions.RunContinuationsAsynchronously );

        peer = new ExtensionPeer( Console.OpenStandardInput( ), Console.OpenStandardOutput( ), false,
            [ExtensionMethods.Initialize, ExtensionMethods.Initialized, ExtensionMethods.CommandOpen, ExtensionMethods.Shutdown], async ( frame, ct ) =>
            {
                switch ( frame.Method )
                {
                    case ExtensionMethods.Initialize when !initialized:
                        var request = frame.Params!.Value.Deserialize( ExtensionJsonContext.Default.InitializeRequest )!;

                        if ( HasInvalidInitialization( request ) )
                        {
                            throw new InvalidDataException( "Unsupported probe initialization." );
                        }

                        initialized = true;

                        return JsonSerializer.SerializeToElement( new InitializeResult( new ProtocolVersion( ), "0.1.0-preview.1", ["probe"] ), ExtensionJsonContext.Default.InitializeResult );
                    case ExtensionMethods.Initialized when initialized:
                        ready = true;

                        return JsonElement.Parse( "{}" );
                    case ExtensionMethods.CommandOpen when ready:
                        var storage = await peer!.CallAsync( ExtensionProbeContract.StorageGetMethod, JsonElement.Parse( "{\"key\":\"probe\"}" ), ct );

                        var report = Probe( frame.Params!.Value, storage );
                        var view = await peer.CallAsync( ExtensionMethods.ViewsReplace, report, ct );

                        return view;
                    case ExtensionMethods.Shutdown:
                        finished.TrySetResult( );

                        return JsonElement.Parse( "{}" );
                    default:
                        throw new InvalidDataException( "Invalid probe state." );
                }
            } );

        peer.Start( );

        await using ( peer )
        {
            await Task.WhenAny( finished.Task, peer.Completion );
        }
    }

    private static JsonElement Probe( JsonElement parameters, JsonElement storage )
    {
        var hostFile = parameters
            .GetProperty( ExtensionProbeFields.HostFile )
            .GetString( )!;
        var hostSocket = parameters
            .GetProperty( ExtensionProbeFields.HostSocket )
            .GetString( )!;
        var port = parameters
            .GetProperty( ExtensionProbeFields.Port )
            .GetInt32( );

        var fileDenied = Open( hostFile, 0 ) < 0;

        var homeDenied = !Directory.Exists( parameters
            .GetProperty( ExtensionProbeFields.HostHome )
            .GetString( )! );

        var socketDenied = ProbeUnixSocketDenied( hostSocket );
        var networkDenied = ProbeTcpSocketDenied( port );

        var packageWriteDenied = AttemptDenied( ( ) => File.WriteAllText( "/extension/escape.txt", "not allowed" ) );

        File.WriteAllText( "/tmp/private.txt", "ephemeral" );
        var temporaryWorks = File.ReadAllText( "/tmp/private.txt" ) == "ephemeral";
        var environmentClean = Environment.GetEnvironmentVariable( ExtensionProbeContract.SecretEnvironmentVariable ) is null;
        var ptraceDenied = Ptrace( 0, 0, 0, 0 ) == -1;
        using var descendant = StartDescendant( );
        Dictionary<string, bool> results = new( StringComparer.Ordinal )
        {
            [ExtensionProbeFields.FileDenied] = fileDenied,
            [ExtensionProbeFields.HomeDenied] = homeDenied,
            [ExtensionProbeFields.SocketDenied] = socketDenied,
            [ExtensionProbeFields.NetworkDenied] = networkDenied,
            [ExtensionProbeFields.PackageWriteDenied] = packageWriteDenied,
            [ExtensionProbeFields.TemporaryWorks] = temporaryWorks,
            [ExtensionProbeFields.EnvironmentClean] = environmentClean,
            [ExtensionProbeFields.PtraceDenied] = ptraceDenied
        };

        using MemoryStream bytes = new( );

        using ( Utf8JsonWriter writer = new( bytes ) )
        {
            writer.WriteStartObject( );
            writer.WriteString( "kind", ExtensionViewKinds.List );
            writer.WriteString( "title", "Extension sandbox probe" );
            foreach ( var field in ExtensionProbeFields.RequiredReportFields )
            {
                writer.WriteBoolean( field, results[field] );
            }

            writer.WritePropertyName( "storage" );
            storage.WriteTo( writer );
            writer.WriteEndObject( );
        }

        return JsonElement.Parse( bytes.ToArray( ) );
    }

    private static bool AttemptDenied( Action action )
    {
        try
        {
            action( );

            return false;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or SocketException )
        {
            return true;
        }
    }

    [LibraryImport( CommandDeck.NativeLibraryNames.LibC, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8 )]
    private static partial int Open( string path, int flags );

    [LibraryImport( CommandDeck.NativeLibraryNames.LibC, EntryPoint = "ptrace" )]
    private static partial long Ptrace( int request, int pid, nint address, nint data );

    private static Process? StartGrandchild( )
    {
        return Process.Start( new ProcessStartInfo( Environment.ProcessPath! )
        {
            ArgumentList = { "--grandchild" },
            UseShellExecute = false
        } );
    }

    private static bool ProbeUnixSocketDenied( string hostSocket )
    {
        return AttemptDenied( ( ) =>
        {
            using Socket socket = new( AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified );
            socket.Connect( new UnixDomainSocketEndPoint( hostSocket ) );
        } );
    }

    private static bool ProbeTcpSocketDenied( int port )
    {
        return AttemptDenied( ( ) =>
        {
            using Socket socket = new( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
            socket.Connect( new IPEndPoint( IPAddress.Loopback, port ) );
        } );
    }

    private static Process? StartDescendant( )
    {
        return Process.Start( new ProcessStartInfo( Environment.ProcessPath! )
        {
            ArgumentList = { ProbeOptions.Descendant },
            UseShellExecute = false
        } );
    }

    private static bool HasInvalidInitialization( InitializeRequest request )
    {
        return request.Protocol.Major != 1 || request.CommandId != "probe";
    }
}

internal static class ProbeOptions
{
    internal const string Descendant = "--descendant";
}
