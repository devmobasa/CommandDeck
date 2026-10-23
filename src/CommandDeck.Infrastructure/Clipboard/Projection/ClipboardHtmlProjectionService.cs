using System.Text;
using System.Text.Json;

namespace CommandDeck;

/// <summary>One isolated projection worker; raw markup is never returned as a preview fallback.</summary>
public sealed class ClipboardHtmlProjectionService( string nativeExecutable, BoundedProcessRunner? runner = null )
{
    private const string SandboxUnavailableReason = "SandboxUnavailable";
    private const string WorkerFailedReason = "WorkerFailed";
    private const string InvalidWorkerResponseReason = "InvalidWorkerResponse";
    private const int MaximumWorkerOutputBytes = 256 * ByteUnits.BytesPerKibibyte;
    private const int MaximumWorkerErrorBytes = 4096;
    private const long MaximumWorkerMemoryBytes = 192L * ByteUnits.BytesPerMebibyte;

    private static readonly UTF8Encoding StrictUtf8 = new( false, true );
    private readonly BoundedProcessRunner _run = runner ?? RunAsync;

    public async ValueTask<ClipboardHtmlProjection> ProjectAsync( ReadOnlyMemory<byte> html, CancellationToken token )
    {
        using var worker = ClipboardProjectionWorker.TryAcquire( token );
        if ( worker is null )
        {
            return Unavailable( ClipboardProjectionWorker.BusyReason );
        }

        return await ProjectAdmittedAsync( html, token )
            .ConfigureAwait( false );
    }

    internal async ValueTask<ClipboardHtmlProjection> ProjectAdmittedAsync( ReadOnlyMemory<byte> html, CancellationToken token )
    {
        token.ThrowIfCancellationRequested( );

        if ( !OperatingSystem.IsLinux( ) || html.Length is 0 or > ClipboardHtmlProjector.MaximumInputBytes )
        {
            return Unavailable( ClipboardHtmlProjector.InputLimitReason );
        }

        try
        {
            _ = StrictUtf8.GetCharCount( html.Span );
        }
        catch ( DecoderFallbackException )
        {
            return Unavailable( ClipboardHtmlProjector.InvalidEncodingReason );
        }

        if ( AreWorkersUnavailable( ) )
        {
            return Unavailable( SandboxUnavailableReason );
        }

        var root = Path.Combine( Path.GetTempPath( ), "commanddeck-clipboard-html-" +
                                                      Guid
                                                          .NewGuid( )
                                                          .ToString( "N" ) );

        try
        {
            Directory.CreateDirectory( root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

            var snapshot = Path.Combine( root, "input" );
            using ( FileStream stream = CreateSnapshot( snapshot ) )
            {
                await stream
                    .WriteAsync( html, token )
                    .ConfigureAwait( false );
            }

            var result = await _run( LinuxExecutablePaths.Bubblewrap, BuildArguments( Path.GetFullPath( nativeExecutable ), snapshot ),
                    TimeSpan.FromSeconds( 2 ),
                    new ProcessExecutionLimits
                    {
                        MaximumStandardOutputBytes = MaximumWorkerOutputBytes,
                        MaximumStandardErrorBytes = MaximumWorkerErrorBytes,
                        MaximumMemoryBytes = MaximumWorkerMemoryBytes,
                        ClearEnvironment = true
                    }, token )
                .ConfigureAwait( false );

            token.ThrowIfCancellationRequested( );

            if ( HasWorkerFailed( result ) )
            {
                return Unavailable( WorkerFailedReason );
            }

            var projection = JsonSerializer.Deserialize( result.StandardOutput, CommandDeckJsonContext.Default.ClipboardHtmlProjection );
            if ( projection is null ||
                 projection.Text is null ||
                 StrictUtf8.GetByteCount( projection.Text ) > ClipboardHtmlProjector.MaximumTextBytes ||
                 !projection.Available && projection.Text.Length != 0 )
            {
                return Unavailable( InvalidWorkerResponseReason );
            }

            return projection;
        }
        catch ( Exception exception ) when ( exception is IOException or JsonException or EncoderFallbackException )
        {
            return Unavailable( WorkerFailedReason );
        }
        finally
        {
            if ( Directory.Exists( root ) )
            {
                Directory.Delete( root, recursive: true );
            }
        }
    }

    internal static IReadOnlyList<string> BuildArguments( string executable, string snapshot ) =>
    [
        "--unshare-all", "--unshare-user", "--disable-userns", "--die-with-parent", "--new-session",
        "--cap-drop", "ALL", "--clearenv",
        "--ro-bind", "/usr", "/usr", "--ro-bind-try", "/lib", "/lib", "--ro-bind-try", "/lib64", "/lib64",
        "--ro-bind-try", "/etc/ld.so.cache", "/etc/ld.so.cache",
        "--ro-bind", executable, "/worker", "--ro-bind", snapshot, "/input",
        "--proc", "/proc", "--dev", "/dev", "--size", "1048576", "--tmpfs", "/tmp", "--remount-ro", "/",
        "--setenv", EnvironmentVariableNames.Home, "/tmp", "--setenv", EnvironmentVariableNames.TemporaryDirectory, "/tmp", "--setenv",
        EnvironmentVariableNames.ExecutablePath, "/usr/bin",
        "--setenv", EnvironmentVariableNames.MallocArenaMaximum, "1", "--setenv", EnvironmentVariableNames.GcServer, "0",
        "--setenv", EnvironmentVariableNames.GcHeapHardLimit, "2000000", "--setenv", EnvironmentVariableNames.GcRegionRange, "4000000",
        "--setenv", EnvironmentVariableNames.GcRegionSize, "100000", "--chdir", "/tmp", "--", "/worker", ClipboardChildCommands.ClipboardHtmlProject
    ];

    private static ValueTask<ProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, TimeSpan timeout, ProcessExecutionLimits limits,
        CancellationToken token ) => ProcessRunner.RunBoundedAsync( file, arguments, null, timeout, limits, token );

    private bool AreWorkersUnavailable( )
    {
        return !File.Exists( LinuxExecutablePaths.Bubblewrap ) || !File.Exists( LinuxExecutablePaths.ResourceLimitPath ) || !File.Exists( nativeExecutable );
    }

    [System.Runtime.Versioning.SupportedOSPlatform( "linux" )]
    private static FileStream CreateSnapshot( string snapshot )
    {
        return new( snapshot, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        } );
    }

    private static bool HasWorkerFailed( ProcessResult result )
    {
        return result.ExitCode != 0 || result.TimedOut || result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static ClipboardHtmlProjection Unavailable( string reason ) => new( false, "", false, reason );
}
