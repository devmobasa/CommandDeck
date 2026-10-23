using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static string ExplainRelevanceHit( SearchResult result )
    {
        var text = string.Join( ", ", result
            .Ranking.Contributions.Take( 4 )
            .Select( static contribution => $"{contribution.Kind} {contribution.Value:0.###}" ) );

        if ( result.Ranking.Placement is { } placement )
        {
            text = string.IsNullOrEmpty( text ) ? placement.Tier : $"{placement.Tier}; {text}";
        }

        if ( text.Length > MaximumRelevanceExplanationCharacters )
        {
            text = text[..MaximumRelevanceExplanationCharacters];
        }

        return text;
    }

    private static string FormatRelevanceFailures( IReadOnlyList<RelevanceCaseOutcome> outcomes )
    {
        List<string> lines = ["Relevance corpus failed."];
        var shown = 0;
        foreach ( var outcome in outcomes )
        {
            if ( outcome.Passed )
            {
                continue;
            }

            shown++;

            if ( shown > 8 )
            {
                lines.Add( "Further relevance failures were omitted from this message." );

                break;
            }

            var prefix = outcome.Case.Critical ? "critical " : string.Empty;
            lines.Add( $"{prefix}case '{outcome.Case.Id}': {outcome.Case.Rationale}" );

            foreach ( var step in outcome.Steps.Where( static step => !step.Passed ) )
            {
                lines.Add( $"  query '{step.Query}': {step.Detail}" );

                foreach ( var hit in step.Visible.Take( 9 ) )
                {
                    lines.Add( $"    {hit.Rank}. {hit.Id} [{hit.Section}] {hit.Score:0.###} {hit.Explanation}" );
                }
            }
        }

        return string.Join( Environment.NewLine, lines );
    }

    private static void WriteRelevanceReport( int fixtureVersion, RelevanceCorpusMeasurement measurement, IReadOnlyList<RelevanceCaseOutcome> outcomes )
    {
        var path = Environment.GetEnvironmentVariable( "COMMANDDECK_RELEVANCE_REPORT" );
        if ( string.IsNullOrWhiteSpace( path ) )
        {
            return;
        }

        using var stream = File.Create( path );
        using Utf8JsonWriter json = new( stream, new JsonWriterOptions { Indented = true } );
        json.WriteStartObject( );
        json.WriteNumber( "fixtureVersion", fixtureVersion );
        json.WriteString( "commit", ReadRelevanceCommitId( ) );
        json.WriteNumber( "caseCount", measurement.CaseCount );
        json.WriteNumber( "passed", measurement.Passed );
        json.WriteNumber( "topOneAccuracy", measurement.TopOneAccuracy );
        json.WriteNumber( "meanReciprocalRank", measurement.MeanReciprocalRank );
        json.WriteNumber( "visibleRecall", measurement.VisibleRecall );
        json.WriteStartArray( "cases" );
        var written = 0;
        foreach ( var outcome in outcomes )
        {
            written++;

            if ( written > 64 )
            {
                break;
            }

            json.WriteStartObject( );
            json.WriteString( "id", outcome.Case.Id );
            json.WriteBoolean( "passed", outcome.Passed );
            json.WriteBoolean( "critical", outcome.Case.Critical );

            if ( outcome.Case.DependsOn is not null )
            {
                json.WriteString( "dependsOn", outcome.Case.DependsOn );
            }

            json.WriteStartArray( "steps" );

            foreach ( var step in outcome.Steps.Take( 8 ) )
            {
                json.WriteStartObject( );
                json.WriteString( "query", step.Query );
                json.WriteBoolean( "passed", step.Passed );
                json.WriteNumber( "acceptableRank", step.AcceptableRank );
                json.WriteString( "firstId", step.Visible.Count == 0 ? string.Empty : step.Visible[0].Id );
                json.WriteString( "detail", step.Detail );

                if ( !step.Passed )
                {
                    json.WriteStartArray( "visible" );

                    foreach ( var hit in step.Visible.Take( 9 ) )
                    {
                        json.WriteStartObject( );
                        json.WriteString( "id", hit.Id );
                        json.WriteNumber( "rank", hit.Rank );
                        json.WriteString( "section", hit.Section );
                        json.WriteNumber( "score", hit.Score );
                        json.WriteString( "explanation", hit.Explanation );
                        json.WriteEndObject( );
                    }

                    json.WriteEndArray( );
                }

                json.WriteEndObject( );
            }

            json.WriteEndArray( );
            json.WriteEndObject( );
        }

        json.WriteEndArray( );
        json.WriteBoolean( "truncated", outcomes.Count > 64 );
        json.WriteEndObject( );
    }

    private static string ReadRelevanceCommitId( )
    {
        var git = Path.Combine( FindRepositoryRoot( ), ".git" );
        if ( File.Exists( git ) )
        {
            var pointer = File
                .ReadAllText( git )
                .Trim( );

            const string prefix = "gitdir: ";
            if ( !pointer.StartsWith( prefix, StringComparison.Ordinal ) )
            {
                return "unknown";
            }

            git = Path.GetFullPath( pointer[prefix.Length..], FindRepositoryRoot( ) );
        }

        if ( !Directory.Exists( git ) )
        {
            return "unknown";
        }

        var head = File
            .ReadAllText( Path.Combine( git, "HEAD" ) )
            .Trim( );

        if ( head.StartsWith( "ref: ", StringComparison.Ordinal ) )
        {
            var refPath = Path.Combine( git, head[5..] );
            head = ReadHeadReference( refPath );
        }

        if ( head.Length > 40 )
        {
            head = head[..40];
        }

        return head;

        static string ReadHeadReference( string refPath )
        {
            if ( File.Exists( refPath ) )
            {
                return File
                    .ReadAllText( refPath )
                    .Trim( );
            }

            return "unknown";
        }
    }
}
