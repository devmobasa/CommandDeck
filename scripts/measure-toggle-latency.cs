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

// Manual acceptance check for an installed CommandDeck on a live Omarchy
// session. It toggles the launcher the way the hotkey does, by dispatching the
// commanddeck:toggle global shortcut through Hyprland's request socket, and
// times how long the launcher's layer surface takes to map, as reported by
// `openlayer>>commanddeck` on Hyprland's event socket. It then closes the
// launcher again. It never types keys and refuses to run without a Hyprland
// session. `--self-test` checks parsing, statistics and isolated cancellation.

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
    internal const string CloseLayerEvent = "closelayer";
    private const int SamplePauseMilliseconds = 250;
    public const string Namespace = "commanddeck";
    public const string ShortcutId = "commanddeck:toggle";
    public const string DispatchRequest = "dispatch hl.dsp.global(\"" + ShortcutId + "\")";
    public static readonly string[] ShellToggle = [CommandDeck.ToolExecutables.OmarchyShell, ShellSubcommand, "toggle", CommandDeck.CommandDeckIdentity.PluginId, "{}"];

    private const string Usage = """
        Usage:
          dotnet scripts/measure-toggle-latency.cs --live [--samples N] [--timeout-ms MS] [--path global|shell]
          dotnet scripts/measure-toggle-latency.cs --self-test

        --live         Toggle the launcher on this Hyprland session and time it (required).
        --samples N    Open/close cycles to time, 1-200 (default 20).
        --timeout-ms   Longest wait for each toggle and layer event, 100-10000 (default 2000).
        --path         global: Hyprland's global dispatcher, as the hotkey does (default).
                       shell: start `omarchy-shell shell toggle` for each toggle, for comparison.
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
        if ( !LayerEvents.HasShortcut( await session.RequestAsync( "j/globalshortcuts" ) ) )
        {
            Console.Error.WriteLine( $"Refusing to run: {ShortcutId} is not registered; enable the plugin and restart the shell." );

            return 2;
        }

        if ( LayerEvents.HasLayer( await session.RequestAsync( "j/layers" ), Namespace ) )
        {
            Console.Error.WriteLine( "Refusing to run: CommandDeck is open; close it first." );

            return 2;
        }

        List<double> opens = [];
        List<double> closes = [];

        try
        {
            for ( var sample = 1; sample <= options.Samples; sample++ )
            {
                var opened = await ToggleAsync( session, options, OpenLayerEvent, cancellationToken );

                var closed = opened is not null ? await ToggleAsync( session, options, CloseLayerEvent, cancellationToken ) : null;
                if ( opened is null || closed is null )
                {
                    Console.Error.WriteLine( $"Sample {sample}: no {(opened is null ? OpenLayerEvent : CloseLayerEvent)}>>{Namespace} event within {options.TimeoutMilliseconds} ms." );

                    return 1;
                }

                opens.Add( opened.Value );
                closes.Add( closed.Value );

                Console.WriteLine( $"sample {sample,3}: open {opened.Value,7:F1} ms   close {closed.Value,7:F1} ms" );

                await Task.Delay( SamplePauseMilliseconds, cancellationToken );
            }
        }
        finally
        {
            // Hide is idempotent even if the open event timed out or the close event is late.
            await HyprlandSession.HideAsync( );
        }

        Console.WriteLine( Percentiles.Describe( "open ", opens ) );
        Console.WriteLine( Percentiles.Describe( "close", closes ) );

        return 0;
    }

    // Milliseconds from the toggle request to the matching layer event, or null.
    private static async Task<double?> ToggleAsync( HyprlandSession session, Options options, string kind, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );
        session.DrainEvents( );
        var started = Stopwatch.GetTimestamp( );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeout.CancelAfter( options.TimeoutMilliseconds );

        try
        {
            await session.ToggleAsync( options.Via, timeout.Token );

            while ( true )
            {
                var received = await session.Events.ReadAsync( timeout.Token );
                if ( LayerEvents.Matches( received.Line, kind, Namespace ) )
                {
                    return Stopwatch.GetElapsedTime( started, received.Timestamp ).TotalMilliseconds;
                }
            }
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            return null;
        }
    }
}

sealed class RunCancellation : IDisposable
{
    private readonly CancellationTokenSource _stop = new( );
    private readonly PosixSignalRegistration _terminate;

    public RunCancellation( )
    {
        Console.CancelKeyPress += Cancel;

        _terminate = PosixSignalRegistration.Create( PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            _stop.Cancel( );
        } );
    }

    public CancellationToken Token => _stop.Token;

    private void Cancel( object? sender, ConsoleCancelEventArgs args )
    {
        args.Cancel = true;
        _stop.Cancel( );
    }

    public void Dispose( )
    {
        Console.CancelKeyPress -= Cancel;
        _terminate.Dispose( );
        _stop.Dispose( );
    }
}

sealed record Options( int Samples, int TimeoutMilliseconds, string Via )
{
    internal const string LiveOption = "--live";
    private const int DefaultSamples = 20;
    private const int DefaultTimeoutMilliseconds = 2000;
    private const string GlobalPath = "global";
    internal const string SamplesOption = "--samples";
    private const int MaximumSamples = 200;
    internal const string TimeoutOption = "--timeout-ms";
    private const int MinimumTimeoutMilliseconds = 100;
    private const int MaximumTimeoutMilliseconds = 10_000;
    internal const string PathOption = "--path";
    internal const string ShellTogglePath = "shell";

    public static Options Parse( IReadOnlyList<string> args )
    {
        if ( !args.Contains( LiveOption ) )
        {
            throw new ArgumentException( "Pass --live to toggle the launcher on this session." );
        }

        var samples = DefaultSamples;
        var timeout = DefaultTimeoutMilliseconds;
        var via = GlobalPath;

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
                default:
                    throw new ArgumentException( $"Unknown or incomplete argument: {args[index]}" );
            }
        }

        return new( samples, timeout, via );
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

        return index < args.Count && int.TryParse( args[index], NumberStyles.None, CultureInfo.InvariantCulture, out value ) && value >= minimum && value <= maximum;
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

    // `hyprctl -j layers` nests surfaces under monitors and levels.
    public static bool HasLayer( string json, string layerNamespace )
    {
        using var document = JsonDocument.Parse( json );

        return Contains( document.RootElement, layerNamespace );
    }

    private static bool ContainsProperty( JsonProperty property, string layerNamespace )
    {
        if ( property is { Name: "namespace", Value.ValueKind: JsonValueKind.String } )
        {
            return property.Value.GetString( ) == layerNamespace;
        }

        return Contains( property.Value, layerNamespace );
    }

    private static bool Contains( JsonElement element, string layerNamespace ) => element.ValueKind switch
    {
        JsonValueKind.Object => element
            .EnumerateObject( )
            .Any( property =>
            ContainsProperty( property, layerNamespace ) ),
        JsonValueKind.Array => element
            .EnumerateArray( )
            .Any( item => Contains( item, layerNamespace ) ),
        _ => false
    };
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
