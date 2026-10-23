using System.Globalization;

namespace CommandDeck.Specs;

public static partial class Program
{
    // The daemon logs its startup stages at every boot, so the line must carry
    // stage names and invariant numbers only, in the order they were marked.
    private static void StartupTimelineLogsStageNamesAndDurationsOnly( )
    {
        StartupTimeline timeline = new( );
        timeline.Mark( StartupStages.Configuration );
        Thread.Sleep( 2 );
        timeline.Mark( StartupStages.Indexer );

        var fields = timeline
            .Format( )
            .Split( ' ' );

        Equal( "event=daemon.startup", fields[0] );
        Equal( "configuration_ms indexer_ms ready_ms", string.Join( ' ', fields.Skip( 1 ).Select( static field => field.Split( '=' )[0] ) ) );

        var values = fields
            .Skip( 1 )
            .Select( static field => double.Parse( field.Split( '=' )[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture ) )
            .ToArray( );

        True( values[1] >= 1.5, "the second stage includes the pause before it" );
        True( values[2] >= values[0] + values[1] - 0.1, "ready covers every stage" );
    }
}
