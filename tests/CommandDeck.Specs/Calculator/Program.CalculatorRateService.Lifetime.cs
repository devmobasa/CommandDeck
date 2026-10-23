using System.Net;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task CalculatorRatesCancellationAsync( )
    {
        TaskCompletionSource started = new( TaskCreationOptions.RunContinuationsAsynchronously );

        static async Task<HttpResponseMessage> NeverAsync( CancellationToken cancellation )
        {
            await Task.Delay( Timeout.InfiniteTimeSpan, cancellation );

            throw new InvalidOperationException( );
        }

        using CalculatorRateHandler timeoutHandler = new( NeverAsync );

        await using ( CurrencyRateService timeout = new( CalculatorCachePath( ), true, handler: timeoutHandler,
                         requestTimeout: TimeSpan.FromMilliseconds( 30 ) ) )
        {
            timeout.GetRates( );

            await timeout
                .WhenIdleAsync( )
                .WaitAsync( TimeSpan.FromSeconds( 5 ) );

            Equal<CurrencyRateSnapshot?>( null, timeout.GetRates( ) );
            True( timeout.WhenIdleAsync( ).IsCompleted );
            Equal( 1, timeoutHandler.Calls );
        }

        using CalculatorRateHandler slowBodyHandler = new( _ => Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK )
        {
            Content = new StreamContent( new CalculatorBodyStream( [], stall: true ) )
        } ) );

        await using ( CurrencyRateService slowBody = new( CalculatorCachePath( ), true, handler: slowBodyHandler,
                         requestTimeout: TimeSpan.FromMilliseconds( 30 ) ) )
        {
            slowBody.GetRates( );

            await slowBody
                .WhenIdleAsync( )
                .WaitAsync( TimeSpan.FromSeconds( 5 ) );

            Equal<CurrencyRateSnapshot?>( null, slowBody.GetRates( ) );
            Equal( 1, slowBodyHandler.Calls );
        }

        using CalculatorRateHandler disposeHandler = new( token =>
        {
            started.SetResult( );

            return NeverAsync( token );
        } );

        var service = new CurrencyRateService( CalculatorCachePath( ), true, handler: disposeHandler );
        service.GetRates( );
        await started.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

        await service
            .DisposeAsync( )
            .AsTask( )
            .WaitAsync( TimeSpan.FromSeconds( 5 ) );

        True( service.WhenIdleAsync( ).IsCompleted );
        Equal<CurrencyRateSnapshot?>( null, service.GetRates( ) );
        await service.DisposeAsync( );
    }

    private static async Task CalculatorRatesOverlappingDisposalAsync( )
    {
        TaskCompletionSource started = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource cancelled = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource release = new( TaskCreationOptions.RunContinuationsAsynchronously );

        using CalculatorRateHandler handler = new( async token =>
        {
            started.SetResult( );

            try
            {
                await Task.Delay( Timeout.InfiniteTimeSpan, token );
            }
            finally
            {
                cancelled.SetResult( );
                await release.Task;
            }

            return CalculatorXmlResponse( );
        } );

        await using CurrencyRateService service = new( CalculatorCachePath( ), true, handler: handler );
        service.GetRates( );
        await started.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

        var first = service
            .DisposeAsync( )
            .AsTask( );

        await cancelled.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

        var second = service
            .DisposeAsync( )
            .AsTask( );

        var firstCompleted = first.IsCompleted;
        var secondCompleted = second.IsCompleted;
        release.SetResult( );

        await Task
            .WhenAll( first, second )
            .WaitAsync( TimeSpan.FromSeconds( 5 ) );

        True( !firstCompleted );
        True( !secondCompleted );
        True( service.WhenIdleAsync( ).IsCompleted );
        Equal<CurrencyRateSnapshot?>( null, service.GetRates( ) );
        Equal( 1, handler.Calls );
    }

    private sealed class CalculatorBodyStream( byte[] bytes, bool stall = false ) : MemoryStream( bytes )
    {
        public override bool CanSeek => false;
        internal int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default )
        {
            if ( stall )
            {
                await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );
            }

            var read = await base.ReadAsync( buffer, cancellationToken );
            BytesRead += read;

            return read;
        }
    }
}
