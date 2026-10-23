using System.Text.Json;

namespace CommandDeck;

/// <summary>Runs association discovery in a bounded child. The daemon never performs resolver filesystem work itself.</summary>
public sealed class ClipboardActionWorker(
    string executable,
    IReadOnlyList<string> prefix,
    ClipboardResolverEnvironment environment,
    BoundedProcessRunner? runner = null,
    long maximumMemoryBytes = ClipboardActionWorker.DefaultMaximumMemoryBytes ) : IDisposable
{
    private const int MaximumDiscoveryOutputBytes = 256 * ByteUnits.BytesPerKibibyte;
    private const int MaximumDiscoveryErrorBytes = 4096;
    public const int Slots = 2;
    public const long DefaultMaximumMemoryBytes = 256L * ByteUnits.BytesPerMebibyte;
    private const string KeyVariable = "COMMANDDECK_CLIPBOARD_KEY";
    private const string TargetVariable = "COMMANDDECK_CLIPBOARD_TARGET";
    private readonly BoundedProcessRunner _run = runner ?? RunAsync;
    private readonly SemaphoreSlim _slots = new( Slots, Slots );
    private int _disposed;

    public async ValueTask<ClipboardHandlerResolution> ResolveAsync( string key, string target, CancellationToken token )
    {
        ArgumentException.ThrowIfNullOrEmpty( key );
        ArgumentException.ThrowIfNullOrEmpty( target );
        ObjectDisposedException.ThrowIf( Volatile.Read( ref _disposed ) != 0, this );

        if ( !await _slots
                .WaitAsync( 0, token )
                .ConfigureAwait( false ) )
        {
            return Unavailable( ClipboardHandlerStatuses.ActionsBusy );
        }

        return await ResolveAdmittedAsync( key, target, token )
            .ConfigureAwait( false );
    }

    private async ValueTask<ClipboardHandlerResolution> ResolveAdmittedAsync( string key, string target, CancellationToken token )
    {
        CancellationTokenSource? executionCancellation = null;
        var releaseSlot = true;

        try
        {
            Dictionary<string, string> variables = CreateEnvironment( key, target );
            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource( token );
            var execution = StartDiscovery( variables, executionCancellation.Token );
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource( token );
            deadline.CancelAfter( TimeSpan.FromSeconds( 2 ) );
            ProcessResult result;

            try
            {
                result = await execution
                    .WaitAsync( deadline.Token )
                    .ConfigureAwait( false );
            }
            catch ( OperationCanceledException ) when ( !token.IsCancellationRequested && deadline.IsCancellationRequested )
            {
                TransferDiscoveryCleanup( execution, executionCancellation );
                releaseSlot = false;

                return Unavailable( ClipboardHandlerStatuses.CheckTimedOut );
            }
            catch ( OperationCanceledException ) when ( token.IsCancellationRequested )
            {
                TransferDiscoveryCleanup( execution, executionCancellation );
                releaseSlot = false;

                throw;
            }

            token.ThrowIfCancellationRequested( );

            return ReadResolution( result );
        }
        catch ( Exception exception ) when ( exception is IOException or JsonException or System.ComponentModel.Win32Exception )
        {
            return Unavailable( ClipboardHandlerStatuses.HandlerDiscoveryUnavailable );
        }
        finally
        {
            if ( releaseSlot )
            {
                executionCancellation?.Dispose( );
                _slots.Release( );
            }
        }
    }

    /// <summary>Child entry point: inputs arrive through the cleared environment; output is the bounded public resolution.</summary>
    public static int RunChild( )
    {
        var key = Environment.GetEnvironmentVariable( KeyVariable );
        var target = Environment.GetEnvironmentVariable( TargetVariable );
        if ( string.IsNullOrEmpty( key ) ||
             string.IsNullOrEmpty( target ) ||
             !ClipboardHttpUrl.TryParseSingleHttpUrl( target, out var url, out _ ) ||
             key != "x-scheme-handler/" + url!.Scheme )
        {
            return 2;
        }

        var resolution = new ClipboardAssociationResolver( ClipboardResolverEnvironment.Capture( ) ).Resolve( key, ClipboardLaunchTarget.HttpUrl, url.Literal );

        Console.Out.Write( JsonSerializer.Serialize( resolution, CommandDeckJsonContext.Default.ClipboardHandlerResolution ) );

        Console.Out.Flush( );

        return 0;
    }

    private static bool Valid( ClipboardHandlerResolution resolution ) =>
        resolution is
        {
            Handlers: { Count: <= ClipboardAssociationResolver.MaximumHandlers },
            Status: (ClipboardHandlerStatuses.Resolved
            or ClipboardHandlerStatuses.NoAssociations
            or ClipboardHandlerStatuses.InvalidAssociations
            or ClipboardHandlerStatuses.HandlerDiscoveryUnavailable),
            Revision: { Length: <= 64 }
        } &&
        resolution.Total >= resolution.Handlers.Count &&
        resolution.Handlers.All( static handler => handler is { DesktopId.Length: > 0 and <= 256, Label.Length: > 0 and <= 256 } &&
                                                   handler.DesktopId.EndsWith( FileExtensions.Desktop, StringComparison.Ordinal ) &&
                                                   handler.Rank is ClipboardHandlerRanks.Default
                                                       or ClipboardHandlerRanks.Preferred
                                                       or ClipboardHandlerRanks.Declared &&
                                                   !handler.Label.Any( char.IsControl ) &&
                                                   handler.Reason is null or { Length: <= 64 } );

    private static void Set( Dictionary<string, string> variables, string name, string? value )
    {
        if ( value is not null )
        {
            variables[name] = value;
        }
    }

    private async Task ReleaseAfterReapingAsync( Task<ProcessResult> execution, CancellationTokenSource cancellation )
    {
        try
        {
            await execution.ConfigureAwait( false );
        }
        catch ( Exception )
        {
            // The caller already received cancellation or timeout; observing completion is the remaining responsibility.
        }
        finally
        {
            cancellation.Dispose( );
            _slots.Release( );
        }
    }

    public void Dispose( ) => Interlocked.Exchange( ref _disposed, 1 );

    private static ValueTask<ProcessResult> RunAsync( string file, IReadOnlyList<string> arguments, TimeSpan timeout, ProcessExecutionLimits limits,
        CancellationToken token ) => ProcessRunner.RunBoundedAsync( file, arguments, null, timeout, limits, token );

    private Dictionary<string, string> CreateEnvironment( string key, string target )
    {
        Dictionary<string, string> variables = new( StringComparer.Ordinal )
        {
            [KeyVariable] = key,
            [TargetVariable] = target,
            [EnvironmentVariableNames.Home] = environment.Home,
            [EnvironmentVariableNames.ExecutablePath] = environment.Path,

            // The child runs under a 256 MiB address-space limit; bound the runtime's reservations to fit it.
            [EnvironmentVariableNames.GcServer] = "0",
            [EnvironmentVariableNames.GcHeapHardLimit] = "0x4000000",
            [EnvironmentVariableNames.GcRegionRange] = "0x4000000",
            [EnvironmentVariableNames.GcRegionSize] = "0x100000",
            [EnvironmentVariableNames.TieredPgo] = "0",
            [EnvironmentVariableNames.MallocArenaMaximum] = "1"
        };

        // A framework-dependent host needs the runtime location; the published Native AOT child ignores it.
        Set( variables, EnvironmentVariableNames.DotnetRoot, Environment.GetEnvironmentVariable( EnvironmentVariableNames.DotnetRoot ) );
        Set( variables, XdgEnvironmentNames.ConfigHome, environment.ConfigHome );
        Set( variables, XdgEnvironmentNames.ConfigDirectories, environment.ConfigDirs );
        Set( variables, XdgEnvironmentNames.DataHome, environment.DataHome );
        Set( variables, XdgEnvironmentNames.DataDirectories, environment.DataDirs );
        Set( variables, XdgEnvironmentNames.CurrentDesktop, environment.CurrentDesktop );
        Set( variables, EnvironmentVariableNames.LocaleAll, environment.Locale );

        return variables;
    }

    private static ClipboardHandlerResolution ReadResolution( ProcessResult result )
    {
        if ( result.TimedOut )
        {
            return Unavailable( ClipboardHandlerStatuses.CheckTimedOut );
        }

        if ( HasDiscoveryFailed( result ) )
        {
            return Unavailable( ClipboardHandlerStatuses.HandlerDiscoveryUnavailable );
        }

        var resolution = JsonSerializer.Deserialize( result.StandardOutput, CommandDeckJsonContext.Default.ClipboardHandlerResolution );
        if ( resolution is not null && Valid( resolution ) )
        {
            return resolution;
        }

        return Unavailable( ClipboardHandlerStatuses.HandlerDiscoveryUnavailable );
    }

    private static bool HasDiscoveryFailed( ProcessResult result )
    {
        return result.ExitCode != 0 || result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static ClipboardHandlerResolution Unavailable( string status ) => new( status, [], 0, false, "", null );

    private Task<ProcessResult> StartDiscovery( Dictionary<string, string> variables, CancellationToken token )
    {
        return _run( executable, [.. prefix, ClipboardChildCommands.ClipboardActionWorker, "--resolve"], TimeSpan.FromSeconds( 2 ),
                new ProcessExecutionLimits
                {
                    MaximumStandardOutputBytes = MaximumDiscoveryOutputBytes,
                    MaximumStandardErrorBytes = MaximumDiscoveryErrorBytes,
                    MaximumMemoryBytes = maximumMemoryBytes,
                    ClearEnvironment = true,
                    EnvironmentVariables = variables
                }, token )
            .AsTask( );
    }

    private void TransferDiscoveryCleanup( Task<ProcessResult> execution, CancellationTokenSource cancellation )
    {
        cancellation.Cancel( );
        _ = ReleaseAfterReapingAsync( execution, cancellation );
    }
}
