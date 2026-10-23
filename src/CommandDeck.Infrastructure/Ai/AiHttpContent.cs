using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CommandDeck;

internal static class AiHttpContent
{
    private const int StreamIdleTimeoutSeconds = 30;
    private const int MaximumInitialBufferBytes = 64 * ByteUnits.BytesPerKibibyte;
    private const int ReadBlockBytes = 16 * ByteUnits.BytesPerKibibyte;
    private const int FirstRedirectStatus = 300;
    private const int RedirectStatusEnd = 400;

    internal static async ValueTask<AiIdleTimeoutStream> OpenGenerationStreamAsync( HttpResponseMessage response, CancellationToken cancellationToken )
    {
        var stream = await response
            .Content.ReadAsStreamAsync( cancellationToken )
            .ConfigureAwait( false );

        return new AiIdleTimeoutStream( stream, TimeSpan.FromSeconds( StreamIdleTimeoutSeconds ) );
    }

    internal static void EnsurePreparedRequest( AiPreparedProviderRequest request, string providerId )
    {
        if ( !string.Equals( request.ProviderId, providerId, StringComparison.Ordinal ) || request.Body.Length > AiLimits.MaximumProviderPayloadBytes )
        {
            throw new InvalidOperationException( "The prepared AI request is invalid for this provider." );
        }
    }

    public static async ValueTask<T?> ReadBoundedJsonAsync<T>( HttpContent content, JsonTypeInfo<T> typeInfo, int maximumBytes,
        CancellationToken cancellationToken )
    {
        await using var input = await content
            .ReadAsStreamAsync( cancellationToken )
            .ConfigureAwait( false );

        using MemoryStream buffer = new( Math.Min( maximumBytes, MaximumInitialBufferBytes ) );
        var block = new byte[ReadBlockBytes];

        while ( true )
        {
            var count = await input
                .ReadAsync( block, cancellationToken )
                .ConfigureAwait( false );

            if ( count == 0 )
            {
                break;
            }

            if ( buffer.Length + count > maximumBytes )
            {
                throw new AiProviderException( AiErrorCategories.ResponseTooLarge, safeSummary: "The AI provider response exceeded its byte limit." );
            }

            buffer.Write( block, 0, count );
        }

        return JsonSerializer.Deserialize( buffer
            .GetBuffer( )
            .AsSpan( 0, ( int ) buffer.Length ), typeInfo );
    }

    public static void RejectRedirect( HttpResponseMessage response )
    {
        if ( ( int ) response.StatusCode is >= FirstRedirectStatus and < RedirectStatusEnd )
        {
            var source = response.RequestMessage?.RequestUri;
            var target = response.Headers.Location;
            if ( IsRelativeRedirect( source, target ) )
            {
                target = new Uri( source, target );
            }

            var sourceOrigin = source?.GetLeftPart( UriPartial.Authority ) ?? "configured endpoint";
            var targetOrigin = target?.GetLeftPart( UriPartial.Authority ) ?? "undisclosed target";

            throw new AiProviderException( AiErrorCategories.RedirectRejected, safeSummary: $"Redirect from {sourceOrigin} to {targetOrigin} was rejected." );
        }
    }

    public static void EnsureSuccess( HttpResponseMessage response )
    {
        RejectRedirect( response );

        if ( response.IsSuccessStatusCode )
        {
            return;
        }

        var category = ClassifyStatus( response );
        int? retryAfter = ReadRetryAfterSeconds( response );
        var summary = DescribeFailure( category );

        throw new AiProviderException( category, retryAfter, summary );
    }

    private static bool IsRelativeRedirect( [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] Uri? source,
        [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] Uri? target )
    {
        return source is not null && target is { IsAbsoluteUri: false };
    }

    private static string ClassifyStatus( HttpResponseMessage response )
    {
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => AiErrorCategories.Authentication,
            System.Net.HttpStatusCode.Forbidden => AiErrorCategories.Permission,
            System.Net.HttpStatusCode.TooManyRequests => AiErrorCategories.RateLimited,
            _ => AiErrorCategories.Unavailable
        };
    }

    private static int? ReadRetryAfterSeconds( HttpResponseMessage response )
    {
        if ( response.Headers.RetryAfter?.Delta is { } delay )
        {
            return Math.Max( 0, ( int ) Math.Ceiling( delay.TotalSeconds ) );
        }
        else
        {
            return null;
        }
    }

    private static string DescribeFailure( string category )
    {
        return category switch
        {
            AiErrorCategories.Authentication => "The AI provider rejected its credential.",
            AiErrorCategories.Permission => "The AI provider denied this operation.",
            AiErrorCategories.RateLimited => "The AI provider rate limit was reached.",
            _ => "The AI provider returned an unavailable status."
        };
    }
}

public sealed class AiProviderException( string category, int? retryAfterSeconds = null, string? safeSummary = null )
    : AiOperationException( category, retryAfterSeconds, safeSummary ?? "The AI provider request failed." );
