using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CommandDeck;

public sealed class CalculatorProvider : ICommandProvider
{
    private const string CalculatorPrefix = "calculator:";

    private const string CopyActionId = "copy";

    private readonly Func<CurrencyRateSnapshot?>? _rates;
    private readonly Func<bool> _inlineEnabled;
    private readonly TimeProvider _clock;

    public CalculatorProvider( CalculatorOptions? options = null, TimeProvider? clock = null, Func<bool>? inlineEnabled = null,
        Func<CurrencyRateSnapshot?>? rates = null )
    {
        var resolved = options ?? new( );
        _inlineEnabled = inlineEnabled ?? (( ) => resolved.Inline);
        _clock = clock ?? TimeProvider.System;
        _rates = rates;
    }

    public string Id => ProviderIds.Calculator;

    public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );
        var inline = CalculatorInput.IsInlineCandidate( query );
        var text = query.IsCalculator ? query.Text : query.Original;
        if ( ShouldSkipExpression( query, inline, text ) )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        if ( TryEvaluateDate( text, out var date ) )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [CreateResult( text, date.Title, date.Subtitle, date.CopyText )] );
        }

        if ( DateCounting.IsCandidate( text ) )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        var result = CalculatorEngine.Evaluate( text, _rates );
        if ( result.Status != CalculationStatus.Success )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [
            CreateResult( text, result.Text!, exact: result.PreciseText, ratesDate: result.RatesDate )
        ] );
    }

    private SearchResult CreateResult( string expression, string formatted, string? subtitle = null, string? copyText = null, string? exact = null,
        DateOnly? ratesDate = null ) => new( )
        {
            Id = $"{CalculatorPrefix}{expression}",
            Kind = ResultKinds.Calculator,
            Title = formatted,
            Subtitle = subtitle ?? expression,
            Icon = "accessories-calculator",
            ProviderId = Id,
            SourceId = SourceIds.Calculator,
            Score = 1,
            Details = CreateCalculationDetails( expression, exact, ratesDate ),
            Actions =
        [
            new CommandAction
            {
                Id = CopyActionId,
                Title = "Copy Result",
                Kind = ActionKinds.CopyText,
                Arguments = new Dictionary<string, string>( StringComparer.Ordinal ) { [ActionArgumentNames.Text] = copyText ?? formatted }
            }
        ]
        };

    private static List<DetailField> CreateCalculationDetails( string expression, string? exact, DateOnly? ratesDate )
    {
        List<DetailField> details = [new DetailField( "Expression", expression, Monospace: true )];
        if ( exact is not null )
        {
            details.Add( new DetailField( "Exact value", exact, Monospace: true ) );
        }

        if ( ratesDate is not null )
        {
            var date = ratesDate.Value.ToString( "yyyy-MM-dd", CultureInfo.InvariantCulture );
            details.Add( new DetailField( "Exchange rates", "ECB reference rates, " + date ) );
        }

        return details;
    }

    private bool ShouldSkipExpression( ParsedQuery query, bool inline, string text )
    {
        return (!query.IsCalculator && !(inline && _inlineEnabled( ))) || string.IsNullOrWhiteSpace( text );
    }

    private bool TryEvaluateDate( string text, [NotNullWhen( true )] out DateCount? date )
    {
        date = default;

        return DateCounting.TryEvaluate( text, _clock, out date ) && date is not null;
    }
}
