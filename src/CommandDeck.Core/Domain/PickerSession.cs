using System.Text;

namespace CommandDeck;

public sealed record PickerChoice( string Value, string Label );

public static class PickerLimits
{
    public const int MaximumInputBytes = ByteUnits.BytesPerMebibyte;
}

public sealed class PickerSession
{
    private const int MaximumPromptCharacters = 256;
    private const int MaximumChoices = 10000;
    private const int MaximumChoiceCharacters = 4096;

    public string Prompt { get; init; } = "Choose an item";
    public List<PickerChoice> Choices { get; init; } = [];

    public static PickerSession Parse( ReadOnlySpan<byte> input, string prompt, bool labelTab )
    {
        if ( input.Length > PickerLimits.MaximumInputBytes )
        {
            throw new InvalidOperationException( "Picker input exceeds 1 MiB." );
        }

        if ( prompt.Length > MaximumPromptCharacters )
        {
            throw new InvalidOperationException( "Picker prompt exceeds 256 characters." );
        }

        var text = new UTF8Encoding( false, true ).GetString( input );
        if ( text.Contains( '\0' ) )
        {
            throw new InvalidOperationException( "Picker input cannot contain NUL bytes." );
        }

        List<PickerChoice> choices = [];
        using StringReader reader = new( text );
        while ( reader.ReadLine( ) is { } line )
        {
            if ( ExceedsPickerLimits( choices, line ) )
            {
                throw new InvalidOperationException( "Picker input allows at most 10000 choices of 4096 characters each." );
            }

            var tab = labelTab ? line.IndexOf( '\t' ) : -1;
            choices.Add( tab < 0 ? new( line, line ) : new( line[..tab], line[(tab + 1)..] ) );
        }

        return new PickerSession { Prompt = prompt, Choices = choices };
    }

    private static bool ExceedsPickerLimits( List<PickerChoice> choices, string line )
    {
        return choices.Count >= MaximumChoices || line.Length > MaximumChoiceCharacters;
    }
}
