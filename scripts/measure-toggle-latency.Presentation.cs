using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

// What the launcher reports about its latest open (PresentationTiming.qml).
// Times are Date.now() milliseconds since the Unix epoch; 0 means not seen.
sealed record PresentationState(
    double Generation, bool Opened, double OpenedAt, double FirstFrameAt, double CompleteFrameAt, double KeyboardReadyAt, double ActionableAt,
    int FramesBeforeComplete, double FirstFrameRatio, double CompleteFrameRatio, string Screen, string Renderer )
{
    public const string Method = "presentationState";

    // An old plugin answers "unknown", which is not a report.
    public static PresentationState? Parse( string reply )
    {
        try
        {
            using var document = JsonDocument.Parse( reply );

            return FromElement( document.RootElement );
        }
        catch ( JsonException )
        {
            return null;
        }
    }

    public bool IsCompleteFor( double generation )
    {
        return Generation == generation && CompleteFrameAt > 0 && KeyboardReadyAt > 0 && ActionableAt > 0;
    }

    private static PresentationState? FromElement( JsonElement root )
    {
        if ( !TryReadGeneration( root, out var generation ) )
        {
            return null;
        }

        return new( generation, IsTrue( root, "opened" ), Number( root, "openedAt" ), Number( root, "firstFrameAt" ),
            Number( root, "completeFrameAt" ), Number( root, "keyboardReadyAt" ), Number( root, "actionableAt" ),
            ( int ) Number( root, "framesBeforeComplete" ),
            Number( root, "firstFrameRatio" ), Number( root, "completeFrameRatio" ), Text( root, "screen" ), Text( root, "renderer" ) );
    }

    // Every report carries its open's number; anything else is not a report.
    private static bool TryReadGeneration( JsonElement root, out double generation )
    {
        generation = 0;

        if ( root.ValueKind != JsonValueKind.Object || !root.TryGetProperty( "generation", out var value ) || value.ValueKind != JsonValueKind.Number )
        {
            return false;
        }

        generation = value.GetDouble( );

        return true;
    }

    private static double Number( JsonElement root, string name )
    {
        if ( root.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number )
        {
            return value.GetDouble( );
        }

        return 0;
    }

    private static bool IsTrue( JsonElement root, string name )
    {
        return root.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.True;
    }

    private static string Text( JsonElement root, string name )
    {
        if ( root.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String )
        {
            return value.GetString( ) ?? "";
        }

        return "";
    }
}

// The keyboard mode Hyprland has committed for the launcher layer. A mapped
// layer sends no event when it takes or returns the keyboard, so it is polled.
static class LayerInteractivity
{
    // zwlr_layer_surface_v1 keyboard_interactivity: 0 none, 1 exclusive, 2 on demand.
    public const int Exclusive = 1;
    public const int NoLayer = -1;
    public const int PollIntervalMilliseconds = 1;

    // The highest mode of any launcher layer, or -1 when none is mapped.
    public const string Request = "/repl local mode = -1 for _, l in ipairs(hl.get_layers()) do " +
        "if l.namespace == '" + ToggleLatency.Namespace + "' and l.interactivity > mode then mode = l.interactivity end " +
        "end return mode";

    public static int? Parse( string reply )
    {
        if ( int.TryParse( reply.Trim( ), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mode ) )
        {
            return mode;
        }

        return null;
    }
}

// Times one open or close twice: on the monotonic clock for script deadlines,
// and on the wall clock that Date.now() in the launcher uses.
sealed class SampleClock
{
    // Wall-clock and monotonic time may disagree by this much before a sample is rejected.
    private const double MaximumDriftMilliseconds = 2;

    private readonly long _started = Stopwatch.GetTimestamp( );
    private readonly double _startedUnixMilliseconds = UnixMilliseconds( );

    public double Elapsed( )
    {
        return Stopwatch
            .GetElapsedTime( _started )
            .TotalMilliseconds;
    }

    public double Elapsed( long timestamp )
    {
        return Stopwatch
            .GetElapsedTime( _started, timestamp )
            .TotalMilliseconds;
    }

    // Milliseconds from the start to a launcher time, or null when it was not seen.
    public double? Until( double unixMilliseconds )
    {
        if ( unixMilliseconds <= 0 )
        {
            return null;
        }

        return unixMilliseconds - _startedUnixMilliseconds;
    }

    public bool HasDrifted( )
    {
        return Math.Abs( UnixMilliseconds( ) - _startedUnixMilliseconds - Elapsed( ) ) > MaximumDriftMilliseconds;
    }

    private static double UnixMilliseconds( )
    {
        return (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
    }
}

// One open and close. Card, actionable, first frame and shortcut come from the
// launcher's clock (1 ms resolution); keyboard and close are polled from Hyprland.
sealed record OpenSample(
    double Card, double Actionable, double FirstFrame, double Keyboard, double Close, double? Shortcut, double? Mapped, PresentationState State )
{
    public static OpenSample? From( PresentationState state, SampleClock clock, double keyboard, double? mapped, double close )
    {
        var card = clock.Until( state.CompleteFrameAt );
        var actionable = clock.Until( state.ActionableAt );
        var firstFrame = clock.Until( state.FirstFrameAt );
        if ( card is null || actionable is null || firstFrame is null )
        {
            return null;
        }

        return new( card.Value, actionable.Value, firstFrame.Value, keyboard, close, clock.Until( state.OpenedAt ), mapped, state );
    }

    public string Describe( int sample )
    {
        var mapped = Mapped is { } value ? string.Create( CultureInfo.InvariantCulture, $", surface mapped {value:F1} ms" ) : "";

        return string.Create( CultureInfo.InvariantCulture,
            $"sample {sample,3}: card {Card,6:F1} ms   actionable {Actionable,6:F1} ms   first frame {FirstFrame,6:F1} ms   " +
            $"keyboard {Keyboard,6:F1} ms   close {Close,5:F1} ms" +
            $"   (shortcut {Shortcut ?? 0:F1} ms{mapped}, {State.FramesBeforeComplete} frames before, " +
            $"scale {State.FirstFrameRatio:0.##} -> {State.CompleteFrameRatio:0.##}, {State.Screen})" );
    }
}
