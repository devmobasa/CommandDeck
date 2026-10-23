using System.Diagnostics;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void CalculatorPerformance( )
    {
        const int warmupIterations = 1000;
        const int iterations = 20000;
        const int meanBudgetMicroseconds = 250;

        string[] expressions =
            ["0.1 + 0.2", "2^70", "100 km in miles", "98.6 F to C", "sqrt(16 m^2)", "sin(30)", "80 km + 15%", "100 USD to EUR", "10 GBP + 5 EUR"];

        var rates = CalculatorTestRates( );
        Func<CurrencyRateSnapshot?> source = ( ) => rates;

        for ( var warmup = 0; warmup < warmupIterations; warmup++ )
        {
            CalculatorEngine.Evaluate( expressions[warmup % expressions.Length], source );
        }

        var started = Stopwatch.GetTimestamp( );

        for ( var i = 0; i < iterations; i++ )
        {
            var result = CalculatorEngine.Evaluate( expressions[i % expressions.Length], source );
            Equal( CalculationStatus.Success, result.Status );
        }

        var mean = Stopwatch.GetElapsedTime( started )
                       .TotalMicroseconds /
                   iterations;

        Console.WriteLine( FormattableString.Invariant( $"Calculator mean: {mean:F2} microseconds/evaluation ({iterations} evaluations)." ) );

        True( mean < meanBudgetMicroseconds );
    }
}
