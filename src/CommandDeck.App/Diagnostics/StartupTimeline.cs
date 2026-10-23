using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CommandDeck;

// Startup stage durations for one daemon start, logged as a single line with
// stage names and milliseconds only: no paths, no configuration, no content.
internal sealed class StartupTimeline
{
    private readonly long _started = Stopwatch.GetTimestamp( );
    private readonly List<(string Stage, TimeSpan Duration)> _stages = [];
    private long _last;

    // Records the time since the previous mark, or since the timeline began.
    public void Mark( string stage )
    {
        var now = Stopwatch.GetTimestamp( );
        _stages.Add( (stage, Stopwatch.GetElapsedTime( _last == 0 ? _started : _last, now )) );
        _last = now;
    }

    public string Format( )
    {
        StringBuilder line = new( "event=daemon.startup" );

        foreach ( (var stage, var duration) in _stages )
        {
            line.Append( CultureInfo.InvariantCulture, $" {stage}_ms={duration.TotalMilliseconds:F1}" );
        }

        return line
            .Append( CultureInfo.InvariantCulture, $" ready_ms={Stopwatch.GetElapsedTime( _started ).TotalMilliseconds:F1}" )
            .ToString( );
    }
}

// The daemon's startup stages, in the order it marks them. Each name becomes a
// `<name>_ms` field of the logged line, so a rename changes what people grep.
internal static class StartupStages
{
    public const string DisplaySizing = "display_sizing";
    public const string Configuration = "configuration";
    public const string Catalog = "catalog";
    public const string Extensions = "extensions";
    public const string Indexer = "indexer";
    public const string ClipboardStore = "clipboard_store";
    public const string Services = "services";
    public const string SchemaCheck = "schema_check";
    public const string ClipboardCapture = "clipboard_capture";
    public const string Server = "server";
}
