using System.Text.Json;

namespace CommandDeck;

public static partial class OpenAiSseReader
{
    private static AiProviderStreamEvent ParseEvent( string payload )
    {
        using var document = JsonDocument.Parse( payload );
        var root = document.RootElement;
        if ( root.ValueKind != JsonValueKind.Object )
        {
            throw new InvalidDataException( "OpenAI stream event must be a JSON object." );
        }

        if ( AiProviderOutputPolicy.ContainsUnsupportedField( root ) )
        {
            throw AiProviderOutputPolicy.Unsupported( );
        }

        if ( HasStructuredError( root ) )
        {
            throw new AiProviderException( AiErrorCategories.Unavailable, safeSummary: "The AI provider returned a structured error event." );
        }

        var hasChoices = root.TryGetProperty( "choices", out var choices );
        string? text = null;
        string? finish = null;
        var meaningful = false;

        if ( hasChoices )
        {
            if ( choices.ValueKind != JsonValueKind.Array )
            {
                throw new InvalidDataException( "OpenAI stream choices must be an array." );
            }

            if ( choices.GetArrayLength( ) > 0 )
            {
                (text, finish) = ParseChoice( choices[0] );

                for ( var index = 1; index < choices.GetArrayLength( ); index++ )
                {
                    _ = ParseChoice( choices[index] );
                }

                if ( choices.GetArrayLength( ) > 1 )
                {
                    throw new InvalidDataException( "OpenAI stream returned multiple choices, which are unsupported." );
                }

                meaningful = true;
            }
        }

        var hasUsage = root.TryGetProperty( "usage", out var usageValue );
        if ( HasInvalidUsage( hasUsage, usageValue ) )
        {
            throw new InvalidDataException( "OpenAI stream usage must be an object or null." );
        }

        var usage = ReadUsage( hasUsage, usageValue );
        meaningful |= usage is not null;

        if ( !meaningful )
        {
            throw new InvalidDataException( "OpenAI stream event contains no supported data." );
        }

        return new AiProviderStreamEvent { Text = text, FinishCategory = finish, Usage = usage };
    }

    private static (string? Text, string? Finish) ParseChoice( JsonElement choice )
    {
        if ( choice.ValueKind != JsonValueKind.Object )
        {
            throw new InvalidDataException( "OpenAI stream choice must be an object." );
        }

        var hasDelta = choice.TryGetProperty( "delta", out var delta );
        var hasFinish = choice.TryGetProperty( "finish_reason", out var reason );
        if ( !hasDelta && !hasFinish )
        {
            throw new InvalidDataException( "OpenAI stream choice has no delta or finish reason." );
        }

        var text = hasDelta ? ParseDelta( delta ) : null;

        if ( HasInvalidFinishReason( hasFinish, reason ) )
        {
            throw new InvalidDataException( "OpenAI stream finish reason must be text or null." );
        }

        var finish = ReadFinishReason( hasFinish, reason );
        if ( AiProviderOutputPolicy.IsUnsupportedFinishReason( finish ) )
        {
            throw AiProviderOutputPolicy.Unsupported( );
        }

        return (text, finish);
    }

    private static string? ParseDelta( JsonElement delta )
    {
        if ( delta.ValueKind != JsonValueKind.Object )
        {
            throw new InvalidDataException( "OpenAI stream delta must be an object." );
        }

        if ( !delta.TryGetProperty( "content", out var content ) )
        {
            return null;
        }

        if ( content.ValueKind is not JsonValueKind.String and not JsonValueKind.Null )
        {
            throw new InvalidDataException( "OpenAI stream content must be text or null." );
        }

        return content.ValueKind == JsonValueKind.String ? content.GetString( ) : null;
    }

    private static bool HasStructuredError( JsonElement root )
    {
        return root.TryGetProperty( "error", out var error ) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    private static bool HasInvalidUsage( bool hasUsage, JsonElement usageValue )
    {
        return hasUsage && usageValue.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null;
    }

    private static AiUsage? ReadUsage( bool hasUsage, JsonElement usageValue )
    {
        if ( hasUsage && usageValue.ValueKind == JsonValueKind.Object )
        {
            return new AiUsage
            {
                InputTokens = AiStreamValues.ReadNonNegativeCount( usageValue, "prompt_tokens" ),
                OutputTokens = AiStreamValues.ReadNonNegativeCount( usageValue, "completion_tokens" ),
                TotalTokens = AiStreamValues.ReadNonNegativeCount( usageValue, "total_tokens" )
            };
        }
        else
        {
            return null;
        }
    }

    private static bool HasInvalidFinishReason( bool hasFinish, JsonElement reason )
    {
        return hasFinish && reason.ValueKind is not JsonValueKind.String and not JsonValueKind.Null;
    }

    private static string? ReadFinishReason( bool hasFinish, JsonElement reason )
    {
        if ( hasFinish && reason.ValueKind == JsonValueKind.String )
        {
            return reason.GetString( );
        }
        else
        {
            return null;
        }
    }
}
