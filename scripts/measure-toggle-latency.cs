#!/usr/bin/env dotnet
#:include ./ScriptExecutables.cs
#:include ../src/CommandDeck.Core/Utilities/ByteUnits.cs
#:include ../src/CommandDeck.Core/Configuration/ToolExecutables.cs
#:include ../src/CommandDeck.Core/Domain/CommandDeckIdentity.cs
#:include ../src/CommandDeck.Core/Utilities/EnvironmentVariableNames.cs
#:include ../src/CommandDeck.Core/Utilities/XdgEnvironmentNames.cs
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
#:include ./measure-toggle-latency.self-test.cs
#:include ./measure-toggle-latency.Commands.cs
#:include ./measure-toggle-latency.Presentation.cs

// Manual acceptance check for an installed CommandDeck on a live Omarchy
// session. It toggles the launcher the way the hotkey does, by dispatching the
// commanddeck:toggle global shortcut through Hyprland's request socket. For
// each open it times when Hyprland gives the launcher the keyboard (polled over
// the request socket) and, from the launcher's own report, the first frame and
// the first complete frame: opaque, in place, with settled results. It then
// closes the launcher and times when the keyboard returns. It never types keys
// and refuses to run without a Hyprland session. `--self-test` checks parsing,
// statistics and isolated cancellation.

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

[assembly: SupportedOSPlatform( "linux" )]

return await ToggleLatency.RunAsync( args );

static class ToggleLatency
{
    private const string ShellSubcommand = "shell";
    private const string SelfTestOption = "--self-test";
    internal const string OpenLayerEvent = "openlayer";
    private const int SamplePauseMilliseconds = 250;
    // The launcher's report is read through omarchy-shell, about 45 ms a call.
    private const int ReportPollMilliseconds = 50;
    public const string Namespace = "commanddeck";
    public const string ShortcutId = "commanddeck:toggle";
    public const string DispatchRequest = "dispatch hl.dsp.global(\"" + ShortcutId + "\")";
    public static readonly string[] ShellToggle = [CommandDeck.ToolExecutables.OmarchyShell, ShellSubcommand, "toggle",
         CommandDeck.CommandDeckIdentity.PluginId, "{}"];

    private const string Usage = """
        Usage:
          dotnet scripts/measure-toggle-latency.cs --live [--samples N] [--timeout-ms MS] [--path global|shell] [--monitor NAME]
          dotnet scripts/measure-toggle-latency.cs --self-test

        --live         Toggle the launcher on this Hyprland session and time it (required).
        --samples N    Open/close cycles to time, 1-200 (default 20).
        --timeout-ms   Longest wait for each open, complete frame and close, 100-10000 (default 2000).
        --path         global: Hyprland's global dispatcher, as the hotkey does (default).
                       shell: start `omarchy-shell shell toggle` for each toggle, for comparison.
        --monitor      Focus this monitor before each open, for example DP-1 (default: the focused one).
        """;

    public static async Task<int> RunAsync( string[] args )
    {
        if ( args is [SelfTestOption] )
        {
            return await SelfTest.RunAsync( );
        }

        Options options;

        try
        {
            options = Options.Parse( args );
        }
        catch ( ArgumentException exception )
        {
            Console.Error.WriteLine( exception.Message );
            Console.Error.WriteLine( Usage );

            return 2;
        }

        var refusal = HyprlandSession.Refusal( Environment.GetEnvironmentVariable, File.Exists );
        if ( refusal is not null )
        {
            Console.Error.WriteLine( $"Refusing to run: {refusal}" );

            return 2;
        }

        using RunCancellation cancellation = new( );

        try
        {
            await using var session = await HyprlandSession.ConnectAsync( Environment.GetEnvironmentVariable, cancellation.Token );

            return await MeasureAsync( session, options, cancellation.Token );
        }
        catch ( OperationCanceledException ) when ( cancellation.Token.IsCancellationRequested )
        {
            Console.Error.WriteLine( "Measurement cancelled; cleanup finished." );

            return 130;
        }
    }

    private static async Task<int> MeasureAsync( HyprlandSession session, Options options, CancellationToken cancellationToken )
    {
        if ( !LayerEvents.HasShortcut( await session.RequestAsync( "j/globalshortcuts", cancellationToken ) ) )
        {
            Console.Error.WriteLine( $"Refusing to run: {ShortcutId} is not registered; enable the plugin and restart the shell." );

            return 2;
        }

        var (start, refusal) = await ReadStartAsync( session, cancellationToken );
        if ( start is null )
        {
            Console.Error.WriteLine( $"Refusing to run: {refusal}" );

            return 2;
        }

        List<OpenSample> samples = [];

        try
        {
            for ( var sample = 1; sample <= options.Samples; sample++ )
            {
                await PrepareSampleAsync( session, options, cancellationToken );

                var (measured, failure) = await MeasureSampleAsync( session, options, start.Generation + sample, cancellationToken );
                if ( measured is null )
                {
                    Console.Error.WriteLine( $"Sample {sample}: {failure}." );

                    return 1;
                }

                samples.Add( measured );
                Console.WriteLine( measured.Describe( sample ) );
            }
        }
        finally
        {
            // Hide is idempotent even if the open timed out or the close is late.
            await HyprlandSession.HideAsync( );
        }

        Summarize( samples );

        return 0;
    }

    // The launcher's report before the first open, or why the run cannot start.
    private static async Task<(PresentationState? State, string? Refusal)> ReadStartAsync( HyprlandSession session, CancellationToken cancellationToken )
    {
        if ( LayerInteractivity.Parse( await session.RequestAsync( LayerInteractivity.Request, cancellationToken ) ) is null )
        {
            return (null, "Hyprland did not answer the Lua layer query (/repl); this check needs Hyprland's Lua configuration.");
        }

        var state = await HyprlandSession.ReadPresentationAsync( cancellationToken );
        if ( state is null )
        {
            return (null, "the running CommandDeck plugin reports no open timing; install this checkout's plugin and run omarchy-restart-shell.");
        }

        if ( state.Opened )
        {
            return (null, "CommandDeck is open; close it first.");
        }

        return (state, null);
    }

    // Closing hands the keyboard back to the last window, which may move monitor
    // focus, so the chosen monitor is focused again before each open. The pause
    // lets the launcher see the focus change before the toggle arrives.
    private static async Task PrepareSampleAsync( HyprlandSession session, Options options, CancellationToken cancellationToken )
    {
        if ( options.Monitor is not null )
        {
            await session.FocusMonitorAsync( options.Monitor, cancellationToken );
        }

        await Task.Delay( SamplePauseMilliseconds, cancellationToken );
    }

    private static async Task<(OpenSample? Sample, string? Failure)> MeasureSampleAsync( HyprlandSession session, Options options, double generation,
        CancellationToken cancellationToken )
    {
        session.DrainEvents( );
        var clock = new SampleClock( );

        var keyboard = await ToggleUntilAsync( session, options, exclusive: true, clock, cancellationToken );
        if ( keyboard is null )
        {
            return (null, $"the launcher did not take the keyboard within {options.TimeoutMilliseconds} ms");
        }

        var mapped = MappedAt( session, clock );
        var state = await WaitForCompleteFrameAsync( options, generation, cancellationToken );
        if ( state is null )
        {
            return (null, $"the launcher reported no complete frame within {options.TimeoutMilliseconds} ms");
        }

        if ( options.Monitor is not null && state.Screen != options.Monitor )
        {
            return (null, $"the launcher opened on {state.Screen}, not {options.Monitor}");
        }

        var closing = new SampleClock( );
        var close = await ToggleUntilAsync( session, options, exclusive: false, closing, cancellationToken );
        if ( close is null )
        {
            return (null, $"the launcher kept the keyboard for {options.TimeoutMilliseconds} ms after closing");
        }

        if ( clock.HasDrifted( ) )
        {
            return (null, "the wall clock moved during the sample; run the measurement again");
        }

        var sample = OpenSample.From( state, clock, keyboard.Value, mapped, close.Value );
        if ( sample is null )
        {
            return (null, "the launcher reported no frame for this open");
        }

        return (sample, null);
    }

    // A launcher that maps a new surface for each open reports openlayer; a kept surface does not.
    private static double? MappedAt( HyprlandSession session, SampleClock clock )
    {
        if ( session.FirstEvent( OpenLayerEvent, Namespace ) is { } timestamp )
        {
            return clock.Elapsed( timestamp );
        }

        return null;
    }

    // Toggles, then polls Hyprland until the launcher holds the keyboard (or no
    // longer does). Returns the milliseconds to the first poll that saw it, or null.
    private static async Task<double?> ToggleUntilAsync( HyprlandSession session, Options options, bool exclusive, SampleClock clock,
        CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeout.CancelAfter( options.TimeoutMilliseconds );

        try
        {
            await session.ToggleAsync( options.Via, timeout.Token );

            while ( true )
            {
                var mode = LayerInteractivity.Parse( await session.RequestAsync( LayerInteractivity.Request, timeout.Token ) );
                if ( mode is not null && (mode == LayerInteractivity.Exclusive) == exclusive )
                {
                    return clock.Elapsed( );
                }

                await Task.Delay( LayerInteractivity.PollIntervalMilliseconds, timeout.Token );
            }
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            return null;
        }
    }

    private static async Task<PresentationState?> WaitForCompleteFrameAsync( Options options, double generation, CancellationToken cancellationToken )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeout.CancelAfter( options.TimeoutMilliseconds );

        try
        {
            while ( true )
            {
                var state = await HyprlandSession.ReadPresentationAsync( timeout.Token );
                if ( state is not null && state.IsCompleteFor( generation ) )
                {
                    return state;
                }

                await Task.Delay( ReportPollMilliseconds, timeout.Token );
            }
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            return null;
        }
    }

    private static void Summarize( IReadOnlyList<OpenSample> samples )
    {
        var last = samples[^1].State;

        var setup = string.Create( CultureInfo.InvariantCulture, $"renderer {last.Renderer}, screen {last.Screen}, scale {last.CompleteFrameRatio:0.##}" );

        Console.WriteLine( setup );
        Console.WriteLine( Percentiles.Describe( "card       ", [.. samples.Select( static sample => sample.Card )] ) );
        Console.WriteLine( Percentiles.Describe( "actionable ", [.. samples.Select( static sample => sample.Actionable )] ) );
        Console.WriteLine( Percentiles.Describe( "first frame", [.. samples.Select( static sample => sample.FirstFrame )] ) );
        Console.WriteLine( Percentiles.Describe( "keyboard   ", [.. samples.Select( static sample => sample.Keyboard )] ) );
        Console.WriteLine( Percentiles.Describe( "close      ", [.. samples.Select( static sample => sample.Close )] ) );
    }
}

sealed record Options( int Samples, int TimeoutMilliseconds, string Via, string? Monitor = null )
{
    internal const string LiveOption = "--live";
    private const int DefaultSamples = 20;
    private const int DefaultTimeoutMilliseconds = 2000;
    internal const string GlobalPath = "global";
    internal const string SamplesOption = "--samples";
    private const int MaximumSamples = 200;
    internal const string TimeoutOption = "--timeout-ms";
    private const int MinimumTimeoutMilliseconds = 100;
    private const int MaximumTimeoutMilliseconds = 10_000;
    internal const string PathOption = "--path";
    internal const string ShellTogglePath = "shell";
    internal const string MonitorOption = "--monitor";

    public static Options Parse( IReadOnlyList<string> args )
    {
        if ( !args.Contains( LiveOption ) )
        {
            throw new ArgumentException( "Pass --live to toggle the launcher on this session." );
        }

        var samples = DefaultSamples;
        var timeout = DefaultTimeoutMilliseconds;
        var via = GlobalPath;
        string? monitor = null;

        for ( var index = 0; index < args.Count; index++ )
        {
            switch ( args[index] )
            {
                case LiveOption:
                    break;
                case SamplesOption:
                    samples = Number( args, ++index, 1, MaximumSamples );

                    break;
                case TimeoutOption:
                    timeout = Number( args, ++index, MinimumTimeoutMilliseconds, MaximumTimeoutMilliseconds );

                    break;
                case PathOption when index + 1 < args.Count && args[index + 1] is GlobalPath or ShellTogglePath:
                    via = args[++index];

                    break;
                case MonitorOption when index + 1 < args.Count && IsMonitorName( args[index + 1] ):
                    monitor = args[++index];

                    break;
                default:
                    throw new ArgumentException( $"Unknown or incomplete argument: {args[index]}" );
            }
        }

        return new( samples, timeout, via, monitor );
    }

    // Output names such as DP-1 or HDMI-A-1; the name is placed in a Lua dispatch.
    private static bool IsMonitorName( string value )
    {
        return value.Length > 0 && value.All( static character => char.IsAsciiLetterOrDigit( character ) || character is '-' or '_' or '.' );
    }

    private static int Number( IReadOnlyList<string> args, int index, int minimum, int maximum )
    {
        if ( TryReadNumber( args, index, minimum, maximum, out var value ) )
        {
            return value;
        }

        throw new ArgumentException( $"{args[index - 1]} takes a whole number from {minimum} to {maximum}." );
    }

    private static bool TryReadNumber( IReadOnlyList<string> args, int index, int minimum, int maximum, out int value )
    {
        value = 0;

        return index < args.Count && int.TryParse( args[index], NumberStyles.None, CultureInfo.InvariantCulture, out value ) && value >=
             minimum && value <= maximum;
    }
}

static class LayerEvents
{
    // Hyprland event lines are EVENT>>DATA; openlayer and closelayer carry the namespace.
    public static bool Matches( string line, string kind, string layerNamespace )
    {
        var separator = line.IndexOf( ">>", StringComparison.Ordinal );

        return separator > 0 &&
            line
                .AsSpan( 0, separator )
                .SequenceEqual( kind ) &&
            line
                .AsSpan( separator + 2 )
                .TrimEnd( '\r' )
                .SequenceEqual( layerNamespace );
    }

    public static bool HasShortcut( string json )
    {
        using var document = JsonDocument.Parse( json );

        return document.RootElement.ValueKind == JsonValueKind.Array &&
            document.RootElement
                .EnumerateArray( )
                .Any( static entry =>
                entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty( "name", out var name ) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString( ) == ToggleLatency.ShortcutId );
    }
}

static class Percentiles
{
    // Nearest rank: the smallest sample with at least q of the samples at or below it.
    public static double NearestRank( IReadOnlyList<double> samples, double quantile )
    {
        if ( HasInvalidPercentileArguments( samples, quantile ) )
        {
            throw new ArgumentException( "Percentiles need samples and a quantile in (0, 1]." );
        }

        // The tolerance keeps 0.95 * 100 from rounding up to rank 96.
        var sorted = samples
            .Order( )
            .ToArray( );
        var rank = ( int ) Math.Ceiling( (quantile * sorted.Length) - 1e-9 );

        return sorted[Math.Max( rank, 1 ) - 1];
    }

    public static string Describe( string label, IReadOnlyList<double> samples ) =>
        string.Create( CultureInfo.InvariantCulture,
            $"{label} p50 {NearestRank( samples, 0.50 ):F1} ms  p95 {NearestRank( samples, 0.95 ):F1} ms  " +
            $"min {samples.Min( ):F1} ms  max {samples.Max( ):F1} ms  ({samples.Count} samples)" );

    private static bool HasInvalidPercentileArguments( IReadOnlyList<double> samples, double quantile )
    {
        return samples.Count == 0 || quantile is <= 0 or > 1;
    }
}

sealed partial class HyprlandSession : IAsyncDisposable
{
    private readonly string _requestSocket;
    private readonly Socket _events;
    private readonly CancellationTokenSource _stop = new( );
    private readonly Task _reader;
    private readonly Channel<(long Timestamp, string Line)> _lines = Channel.CreateUnbounded<(long, string)>( );

    private HyprlandSession( string requestSocket, Socket events )
    {
        _requestSocket = requestSocket;
        _events = events;
        _reader = ReadEventsAsync( );
    }

    public ChannelReader<(long Timestamp, string Line)> Events => _lines.Reader;

    public void DrainEvents( )
    {
        while ( _lines.Reader.TryRead( out _ ) )
        {
        }
    }

    // Reads the events received so far and returns when the first matching one arrived.
    public long? FirstEvent( string kind, string layerNamespace )
    {
        long? first = null;

        while ( _lines.Reader.TryRead( out var received ) )
        {
            if ( first is null && LayerEvents.Matches( received.Line, kind, layerNamespace ) )
            {
                first = received.Timestamp;
            }
        }

        return first;
    }

    private async Task ReadEventsAsync( )
    {
        using NetworkStream stream = new( _events, ownsSocket: false );
        using StreamReader reader = new( stream, Encoding.UTF8 );

        try
        {
            while ( await reader.ReadLineAsync( _stop.Token ) is { } line )
            {
                _lines.Writer.TryWrite( (Stopwatch.GetTimestamp( ), line) );
            }
        }
        catch ( Exception exception ) when ( exception is OperationCanceledException or IOException or ObjectDisposedException )
        {
        }
        finally
        {
            _lines.Writer.TryComplete( );
        }
    }

    public async ValueTask DisposeAsync( )
    {
        await _stop.CancelAsync( );
        _events.Dispose( );
        await _reader;
        _stop.Dispose( );
    }
}
