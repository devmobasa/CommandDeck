using System.Net;
using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private const string CalculatorRatesXml =
        "<Envelope><Cube><Cube time='2026-10-21'><Cube currency='USD' rate='1.1234'/><Cube currency='GBP' rate='0.83825'/></Cube></Cube></Envelope>";

    private sealed class CalculatorClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new( 2026, 10, 21, 12, 0, 0,
            TimeSpan.Zero );

        public override DateTimeOffset GetUtcNow( ) => Now;
    }

    private sealed class CalculatorRateHandler( Func<CancellationToken, Task<HttpResponseMessage>> respond ) : HttpMessageHandler
    {
        private int _calls;
        internal int Calls => Volatile.Read( ref _calls );
        internal Uri? Destination { get; private set; }
        internal string? Method { get; private set; }
        internal KeyValuePair<string, IEnumerable<string>>[] Headers { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            Destination = request.RequestUri;
            Method = request.Method.Method;
            Headers = request.Headers.ToArray( );
            Interlocked.Increment( ref _calls );

            return respond( cancellationToken );
        }
    }

    private static HttpResponseMessage CalculatorXmlResponse( string xml = CalculatorRatesXml ) => new( HttpStatusCode.OK )
    {
        Content = new StringContent( xml, Encoding.UTF8, "application/xml" )
    };

    private static string CalculatorCachePath( ) => Path.Combine( SpecTemporaryFiles.CreateDirectory( "calculator-rates-" )
        .FullName, "private", "rates" );

    private static async Task CalculatorRatesRefreshAsync( )
    {
        var clock = new CalculatorClock( );
        TaskCompletionSource started = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource release = new( TaskCreationOptions.RunContinuationsAsynchronously );

        using CalculatorRateHandler handler = new( async cancellation =>
        {
            started.TrySetResult( );
            await release.Task.WaitAsync( cancellation );

            return CalculatorXmlResponse( );
        } );

        await using CurrencyRateService service = new( CalculatorCachePath( ), true, clock, handler );
        Equal( 0, handler.Calls );
        True( service.WhenIdleAsync( ).IsCompleted );

        Equal( CalculationStatus.Success, CalculatorEngine.Evaluate( "1 km to m", service.GetRates ).Status );
        Equal( 0, handler.Calls );
        Equal<CurrencyRateSnapshot?>( null, service.GetRates( ) );
        await started.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );
        True( !service.WhenIdleAsync( ).IsCompleted );
        Parallel.For( 0, 100, _ => Equal<CurrencyRateSnapshot?>( null, service.GetRates( ) ) );
        Equal( 1, handler.Calls );
        release.SetResult( );
        await service.WhenIdleAsync( );
        True( service.WhenIdleAsync( ).IsCompleted );
        var rates = service.GetRates( );
        True( rates is not null );
        Equal( new DateOnly( 2026, 10, 21 ), rates!.Date );
        Equal( 1.1234m, rates.PerEuro["USD"] );
        Equal( 0.83825m, rates.PerEuro["GBP"] );
        Equal( 1, handler.Calls );
        Equal( "GET", handler.Method );
        Equal( "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml", handler.Destination!.AbsoluteUri );
        Equal( 1, handler.Headers.Length );
        Equal( "User-Agent", handler.Headers[0].Key );
        Equal( "CommandDeck", string.Join( ' ', handler.Headers[0].Value ) );
        clock.Now += TimeSpan.FromHours( 6 ) - TimeSpan.FromTicks( 1 );
        service.GetRates( );
        await service.WhenIdleAsync( );
        Equal( 1, handler.Calls );
        clock.Now += TimeSpan.FromTicks( 1 );
        True( ReferenceEquals( rates, service.GetRates( ) ) );
        await service.WhenIdleAsync( );
        Equal( 2, handler.Calls );
    }

    private static async Task CalculatorRatesFailureAsync( )
    {
        var clock = new CalculatorClock( );
        var failing = false;

        using CalculatorRateHandler handler = new( _ => Task.FromResult( CreateRefreshResponse( failing ) ) );

        await using CurrencyRateService service = new( CalculatorCachePath( ), true, clock, handler );
        service.GetRates( );
        await service.WhenIdleAsync( );
        var old = service.GetRates( );
        failing = true;
        clock.Now += TimeSpan.FromHours( 6 );
        service.GetRates( );
        await service.WhenIdleAsync( );
        Equal( 2, handler.Calls );
        True( ReferenceEquals( old, service.GetRates( ) ) );
        clock.Now += TimeSpan.FromMinutes( 30 ) - TimeSpan.FromTicks( 1 );
        service.GetRates( );
        await service.WhenIdleAsync( );
        Equal( 2, handler.Calls );
        clock.Now += TimeSpan.FromTicks( 1 );
        failing = false;
        service.GetRates( );
        await service.WhenIdleAsync( );
        Equal( 3, handler.Calls );
        True( service.GetRates( ) is not null );

        static HttpResponseMessage CreateRefreshResponse( bool failing )
        {
            if ( failing )
            {
                return new HttpResponseMessage( HttpStatusCode.ServiceUnavailable );
            }

            return CalculatorXmlResponse( );
        }
    }

    private static async Task CalculatorRatesInvalidAsync( )
    {
        string[] invalid = CreateInvalidRateDocuments( );
        foreach ( var xml in invalid )
        {
            using CalculatorRateHandler handler = new( _ => Task.FromResult( CalculatorXmlResponse( xml ) ) );
            await using CurrencyRateService service = new( CalculatorCachePath( ), true, handler: handler );
            service.GetRates( );
            await service.WhenIdleAsync( );
            Equal<CurrencyRateSnapshot?>( null, service.GetRates( ) );
            Equal( 1, handler.Calls );
        }

        using CalculatorRateHandler denied = new( _ => Task.FromResult( new HttpResponseMessage( HttpStatusCode.Redirect ) ) );

        await using CurrencyRateService redirect = new( CalculatorCachePath( ), true, handler: denied );
        redirect.GetRates( );
        await redirect.WhenIdleAsync( );
        Equal<CurrencyRateSnapshot?>( null, redirect.GetRates( ) );
        Equal( 1, denied.Calls );
        using var oversized = new CalculatorBodyStream( new byte[100000] );

        using CalculatorRateHandler streamingHandler =
            new( _ => Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK ) { Content = new StreamContent( oversized ) } ) );

        await using CurrencyRateService streaming = new( CalculatorCachePath( ), true, handler: streamingHandler );

        streaming.GetRates( );
        await streaming.WhenIdleAsync( );
        Equal<CurrencyRateSnapshot?>( null, streaming.GetRates( ) );
        Equal( 65537, oversized.BytesRead );

        // The same valid document under the cap parses, so size alone rejected the larger one.
        using CalculatorRateHandler underCapHandler = new( _ => Task.FromResult( CalculatorXmlResponse( CalculatorPaddedRatesXml( 65000 ) ) ) );

        await using CurrencyRateService underCap = new( CalculatorCachePath( ), true, handler: underCapHandler );

        underCap.GetRates( );
        await underCap.WhenIdleAsync( );
        Equal( 1.1234m, underCap.GetRates( )!.PerEuro["USD"] );
        using var defaults = CurrencyRateService.CreateHandler( );
        True( !defaults.AllowAutoRedirect );
        True( !defaults.UseCookies );
        True( defaults.ActivityHeadersPropagator is null );
        Equal( TimeSpan.FromSeconds( 5 ), defaults.ConnectTimeout );

        static string[] CreateInvalidRateDocuments( )
        {
            return
            [
                "not XML", "<Cube", "<Cube/>", "<Cube time='2026-99-01'><Cube currency='USD' rate='1'/></Cube>",
                "<!DOCTYPE x [<!ENTITY y '1'>]><Cube time='2026-10-21'><Cube currency='USD' rate='&y;'/></Cube>",
                "<Cube time='2026-10-21'/>", CalculatorRatesXml.Replace( "USD", "usd", StringComparison.Ordinal ),
                CalculatorRatesXml.Replace( "USD", "US", StringComparison.Ordinal ), CalculatorRatesXml.Replace( "USD", "USDD", StringComparison.Ordinal ),
                CalculatorRatesXml.Replace( "1.1234", "0", StringComparison.Ordinal ), CalculatorRatesXml.Replace( "1.1234", "-1", StringComparison.Ordinal ),
                CalculatorRatesXml.Replace( "1.1234", "NaN", StringComparison.Ordinal ),
                CalculatorRatesXml.Replace( "1.1234", "1,23", StringComparison.Ordinal ),
                CalculatorRatesXml.Replace( "GBP", "USD", StringComparison.Ordinal ),
                CalculatorRatesXml.Replace( "rate='1.1234'", "", StringComparison.Ordinal ),
                CalculatorRatesXml.Replace( "currency='USD'", "", StringComparison.Ordinal ),
                CalculatorRatesXml.Replace( "<Cube><Cube", "<Cube time='2026-10-21'><Cube", StringComparison.Ordinal ),
                CalculatorPaddedRatesXml( 65537 )
            ];
        }
    }

    // Valid rate XML padded with a comment to exactly `length` ASCII bytes.
    private static string CalculatorPaddedRatesXml( int length ) => CalculatorRatesXml.Insert(
        "<Envelope>".Length, "<!--" + new string( 'x', length - CalculatorRatesXml.Length - 7 ) + "-->" );

    private static async Task CalculatorRatesCacheAsync( )
    {
        var path = CalculatorCachePath( );
        var clock = new CalculatorClock( );

        using ( CalculatorRateHandler handler = new( _ => Task.FromResult( CalculatorXmlResponse( ) ) ) )
        {
            await using CurrencyRateService original = new( path, true, clock, handler );
            original.GetRates( );
            await original.WhenIdleAsync( );
            True( File.Exists( path ) );
        }

        if ( OperatingSystem.IsLinux( ) )
        {
            Equal( UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode( path ) );
            Equal( UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode( Path.GetDirectoryName( path )! ) );
        }

        using CalculatorRateHandler freshHandler = new( _ => Task.FromResult( CalculatorXmlResponse( ) ) );

        await using ( CurrencyRateService fresh = new( path, true, clock, freshHandler ) )
        {
            var snapshot = fresh.GetRates( );
            True( snapshot is not null );
            Equal( 1.1234m, snapshot!.PerEuro["USD"] );
            Equal( new DateOnly( 2026, 10, 21 ), snapshot.Date );
            await fresh.WhenIdleAsync( );
            Equal( 0, freshHandler.Calls );
        }

        clock.Now += TimeSpan.FromHours( 6 );
        using CalculatorRateHandler staleHandler = new( _ => Task.FromResult( CalculatorXmlResponse( ) ) );

        await using ( CurrencyRateService stale = new( path, true, clock, staleHandler ) )
        {
            True( stale.GetRates( ) is not null );
            await stale.WhenIdleAsync( );
            Equal( 1, staleHandler.Calls );
        }

        using CalculatorRateHandler disabledHandler = new( _ => throw new InvalidOperationException( "Network disabled" ) );

        await using ( CurrencyRateService disabled = new( path, false, clock, disabledHandler ) )
        {
            True( disabled.GetRates( ) is not null );
            clock.Now += TimeSpan.FromDays( 1 );
            disabled.GetRates( );
            await disabled.WhenIdleAsync( );
            Equal( 0, disabledHandler.Calls );
        }

        await File.WriteAllTextAsync( path, "broken cache" );

        await using ( CurrencyRateService corrupt = new( path, false, clock ) )
        {
            Equal<CurrencyRateSnapshot?>( null, corrupt.GetRates( ) );
        }

        await using CurrencyRateService missing = new( path + "-missing", false, clock, disabledHandler );
        Equal<CurrencyRateSnapshot?>( null, missing.GetRates( ) );
        await missing.WhenIdleAsync( );
        Equal( 0, disabledHandler.Calls );
    }
}
