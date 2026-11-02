namespace CommandDeck.Specs;

public static partial class Program
{
    private static void AuditGroupedCalculation( )
    {
        AssertCalculation( "1,000/2 m", "500 m" );
        AssertCalculation( "3,000/4 h", "750 h" );
        AssertCalculation( "1000/2 m", "500 m" );
        AssertCalculation( "3000/4 h", "750 h" );
        AssertCalculation( "1000/2,000 m", "0.5 m" );
        AssertCalculation( "1,000/2,000 m", "0.5 m" );
        AssertCalculation( "max(100,200)", "200" );
        AssertCalculation( "min(1,000)", "0" );
        CalculatorCurrencyEvaluation( "$8,000 / $2,000/h", "4 h", null );
        CalculatorCurrencyEvaluation( "$8000 / $2000/h", "4 h", null );
    }
}
