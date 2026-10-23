using System.Text;
using System.Text.Json;

namespace CommandDeck;

public sealed class AiIdleTimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly TimeSpan _idleTimeout;

    public AiIdleTimeoutStream( Stream inner, TimeSpan idleTimeout )
    {
        ArgumentNullException.ThrowIfNull( inner );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual( idleTimeout, TimeSpan.Zero );

        _inner = inner;
        _idleTimeout = idleTimeout;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException( );

    public override long Position
    {
        get => throw new NotSupportedException( );
        set => throw new NotSupportedException( );
    }

    public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken cancellationToken = default )
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        idle.CancelAfter( _idleTimeout );

        try
        {
            return await _inner
                .ReadAsync( buffer, idle.Token )
                .ConfigureAwait( false );
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            throw new TimeoutException( "The AI provider stream exceeded its idle timeout." );
        }
    }

    public override int Read( byte[] buffer, int offset, int count ) =>
        throw new NotSupportedException( );

    public override void Flush( ) => throw new NotSupportedException( );
    public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException( );
    public override void SetLength( long value ) => throw new NotSupportedException( );

    public override void Write( byte[] buffer, int offset, int count ) =>
        throw new NotSupportedException( );

    protected override void Dispose( bool disposing )
    {
        if ( disposing )
        {
            _inner.Dispose( );
        }

        base.Dispose( disposing );
    }

    public override async ValueTask DisposeAsync( )
    {
        await _inner
            .DisposeAsync( )
            .ConfigureAwait( false );

        await base
            .DisposeAsync( )
            .ConfigureAwait( false );

        GC.SuppressFinalize( this );
    }
}

public static class OllamaNdjsonReader
{
    private const int MaximumLineBytes = 64 * ByteUnits.BytesPerKibibyte;

    public static async IAsyncEnumerable<AiProviderStreamEvent> ReadAsync( Stream stream, string expectedModel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken )
    {
        BoundedLineReader lines = new( stream, MaximumLineBytes );
        var terminal = false;

        while ( await lines
                   .ReadLineAsync( cancellationToken )
                   .ConfigureAwait( false ) is { } line )
        {
            if ( string.IsNullOrWhiteSpace( line ) )
            {
                continue;
            }

            var frame = ParseFrame( line, expectedModel );
            if ( !string.IsNullOrEmpty( frame.Text ) )
            {
                yield return new AiProviderStreamEvent { Text = frame.Text };
            }

            if ( frame.Done )
            {
                terminal = true;

                yield return new AiProviderStreamEvent { Terminal = true, FinishCategory = frame.Finish, Usage = frame.Usage };

                break;
            }
        }

        if ( !terminal )
        {
            throw new EndOfStreamException( "Ollama stream ended before its terminal object." );
        }
    }

    private static OllamaStreamFrame ParseFrame( string line, string expectedModel )
    {
        using var document = JsonDocument.Parse( line );
        var root = document.RootElement;
        if ( root.ValueKind != JsonValueKind.Object )
        {
            throw new InvalidDataException( "Ollama stream event must be a JSON object." );
        }

        if ( AiProviderOutputPolicy.ContainsUnsupportedField( root ) )
        {
            throw AiProviderOutputPolicy.Unsupported( );
        }

        ValidateModel( root, expectedModel );
        var text = ReadMessageContent( root );
        var done = ReadBoolean( root, "done" );
        var finish = ReadOptionalString( root, "done_reason" ) ?? "stop";
        if ( AiProviderOutputPolicy.IsUnsupportedFinishReason( finish ) )
        {
            throw AiProviderOutputPolicy.Unsupported( );
        }

        var input = AiStreamValues.ReadNonNegativeCount( root, "prompt_eval_count" );
        var output = AiStreamValues.ReadNonNegativeCount( root, "eval_count" );

        return CreateFrame( text, done, finish, input, output );
    }

    private static void ValidateModel( JsonElement root, string expectedModel )
    {
        if ( !root.TryGetProperty( "model", out var model ) )
        {
            return;
        }

        if ( model.ValueKind != JsonValueKind.String )
        {
            throw new InvalidDataException( "Ollama stream model must be text." );
        }

        if ( !string.Equals( model.GetString( ), expectedModel, StringComparison.Ordinal ) )
        {
            throw new InvalidDataException( "AI provider returned a different model." );
        }
    }

    private static string? ReadMessageContent( JsonElement root )
    {
        if ( !root.TryGetProperty( "message", out var message ) )
        {
            return null;
        }

        if ( message.ValueKind != JsonValueKind.Object )
        {
            throw new InvalidDataException( "AI provider message must be an object." );
        }

        if ( !message.TryGetProperty( "content", out var content ) )
        {
            return null;
        }

        if ( content.ValueKind is not JsonValueKind.String and not JsonValueKind.Null )
        {
            throw new InvalidDataException( "Ollama stream content must be text or null." );
        }

        return content.ValueKind == JsonValueKind.String ? content.GetString( ) : null;
    }

    private static bool ReadBoolean( JsonElement root, string name )
    {
        if ( !root.TryGetProperty( name, out var value ) )
        {
            return false;
        }

        if ( value.ValueKind is not JsonValueKind.True and not JsonValueKind.False )
        {
            throw new InvalidDataException( $"Ollama stream {name} must be a boolean." );
        }

        return value.ValueKind == JsonValueKind.True;
    }

    private static string? ReadOptionalString( JsonElement root, string name )
    {
        if ( !root.TryGetProperty( name, out var value ) )
        {
            return null;
        }

        if ( value.ValueKind is not JsonValueKind.String and not JsonValueKind.Null )
        {
            throw new InvalidDataException( $"Ollama stream {name} must be text or null." );
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString( ) : null;
    }

    private static long? Sum( long? left, long? right ) =>
        left is null || right is null ? null : checked(left + right);

    private readonly record struct OllamaStreamFrame( string? Text, bool Done, string Finish, AiUsage Usage );

    private static OllamaStreamFrame CreateFrame( string? text, bool done, string finish, long? input, long? output )
    {
        return new OllamaStreamFrame( text, done, finish, new AiUsage { InputTokens = input, OutputTokens = output, TotalTokens = Sum( input, output ) } );
    }
}

public static partial class OpenAiSseReader
{
    private const int MaximumEventBytes = 64 * ByteUnits.BytesPerKibibyte;

    public static async IAsyncEnumerable<AiProviderStreamEvent> ReadAsync( Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken )
    {
        BoundedLineReader lines = new( stream, MaximumEventBytes );
        StringBuilder data = new( );
        string? eventName = null;
        var terminal = false;

        while ( await lines
                   .ReadLineAsync( cancellationToken )
                   .ConfigureAwait( false ) is { } line )
        {
            if ( line.Length == 0 )
            {
                if ( data.Length == 0 )
                {
                    continue;
                }

                var payload = data.ToString( );
                data.Clear( );

                if ( string.Equals( eventName, "error", StringComparison.OrdinalIgnoreCase ) )
                {
                    throw new AiProviderException( AiErrorCategories.Unavailable );
                }

                eventName = null;

                if ( payload == OpenAiStreamFraming.DonePayload )
                {
                    terminal = true;

                    yield return new AiProviderStreamEvent { Terminal = true };

                    break;
                }

                var parsed = ParseEvent( payload );

                yield return parsed;

                continue;
            }

            if ( line.StartsWith( ':' ) )
            {
                continue;
            }

            if ( line.StartsWith( "event:", StringComparison.Ordinal ) )
            {
                eventName = line[6..]
                    .Trim( );

                continue;
            }

            if ( line.StartsWith( OpenAiStreamFraming.DataPrefix, StringComparison.Ordinal ) )
            {
                if ( data.Length > 0 )
                {
                    data.Append( '\n' );
                }

                data.Append( line
                    .AsSpan( OpenAiStreamFraming.DataPrefix.Length )
                    .TrimStart( ) );

                if ( Encoding.UTF8.GetByteCount( data.ToString( ) ) > MaximumEventBytes )
                {
                    throw new PayloadTooLargeException( "SSE event exceeds its byte limit." );
                }
            }
        }

        if ( !terminal )
        {
            throw new EndOfStreamException( "OpenAI stream ended before [DONE]." );
        }
    }
}

internal static class AiProviderOutputPolicy
{
    private const string ToolCallsField = "tool_calls";
    private const string FunctionCallField = "function_call";

    public static bool ContainsUnsupportedField( JsonElement value )
    {
        switch ( value.ValueKind )
        {
            case JsonValueKind.Object:
                foreach ( var property in value.EnumerateObject( ) )
                {
                    if ( IsToolProperty( property ) || ContainsUnsupportedField( property.Value ) )
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach ( var item in value.EnumerateArray( ) )
                {
                    if ( ContainsUnsupportedField( item ) )
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    public static bool IsUnsupportedFinishReason( string? value ) =>
        string.Equals( value, ToolCallsField, StringComparison.OrdinalIgnoreCase ) ||
        string.Equals( value, FunctionCallField, StringComparison.OrdinalIgnoreCase );

    public static AiProviderException Unsupported( ) => new( AiErrorCategories.UnsupportedProviderOutput,
        safeSummary: "The AI provider returned unsupported tool or function output." );

    private static bool IsToolProperty( JsonProperty property )
    {
        return property.NameEquals( ToolCallsField ) || property.NameEquals( FunctionCallField );
    }
}

internal static class AiStreamValues
{
    internal static long? ReadNonNegativeCount( JsonElement root, string name )
    {
        if ( root.TryGetProperty( name, out var value ) && value.TryGetInt64( out var number ) && number >= 0 )
        {
            return number;
        }

        return null;
    }
}
