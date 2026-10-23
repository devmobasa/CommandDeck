using System.Globalization;
using System.Text;

namespace CommandDeck;

public static class AiContractValidator
{
    public static void ValidateDraft( AiDraft draft )
    {
        ArgumentNullException.ThrowIfNull( draft );

        ValidateIdentifier( draft.ProviderId, AiLimits.MaximumProviderIdCharacters, "providerId" );

        ValidateUtf8( draft.ModelId, AiLimits.MaximumModelIdBytes, "modelId", allowEmpty: false, allowLineBreaks: false );
        ValidateUtf8( draft.UserText, AiLimits.MaximumUserPromptBytes, "userText", allowEmpty: false );

        if ( HasInvalidAttachments( draft ) )
        {
            throw new InvalidOperationException( "attachments must contain at most four objects." );
        }

        var aggregate = Encoding.UTF8.GetByteCount( draft.UserText );
        foreach ( var attachment in draft.Attachments )
        {
            if ( attachment.Kind is not AiAttachmentKinds.Text and not AiAttachmentKinds.Result and not AiAttachmentKinds.File )
            {
                throw new InvalidOperationException( "attachment kind is unsupported." );
            }

            if ( attachment.Kind == AiAttachmentKinds.Text )
            {
                ValidateUtf8( attachment.Text, AiLimits.MaximumAttachmentBytes, "attachment text", false );
                aggregate += Encoding.UTF8.GetByteCount( attachment.Text! );
            }
            else if ( string.IsNullOrWhiteSpace( attachment.ResultId ) )
            {
                throw new InvalidOperationException( "result and file attachments require resultId." );
            }
        }

        if ( aggregate > AiLimits.MaximumTransmittedTextBytes )
        {
            throw new InvalidOperationException( "AI transmitted text exceeds 128 KiB." );
        }
    }

    public static void ValidateIdentifier( string? value, int maximumLength, string label )
    {
        if ( IsInvalidIdentifier( value, maximumLength ) )
        {
            throw new InvalidOperationException( $"{label} must use 1-{maximumLength} ASCII letters, digits, dots, underscores, or hyphens." );
        }
    }

    public static void ValidateUtf8( string? value, int maximumBytes, string label, bool allowEmpty, bool allowLineBreaks = true )
    {
        if ( IsInvalidText( value, maximumBytes, allowEmpty, allowLineBreaks ) )
        {
            throw new InvalidOperationException( $"{label} is outside supported UTF-8 bounds." );
        }
    }

    public static void ValidateVisibleText( string? value, int maximumTextElements, string label )
    {
        var trimmed = value?.Trim( ) ?? string.Empty;
        var elementStarts = StringInfo.ParseCombiningCharacters( trimmed );
        var containsVisibleRune = false;
        var containsProhibitedRune = false;
        foreach ( var rune in trimmed.EnumerateRunes( ) )
        {
            var category = Rune.GetUnicodeCategory( rune );
            if ( category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned )
            {
                containsProhibitedRune = true;
            }

            if ( IsVisibleRune( category ) )
            {
                containsVisibleRune = true;
            }
        }

        if ( IsInvalidDisplayName( maximumTextElements, trimmed, elementStarts, containsVisibleRune, containsProhibitedRune ) )
        {
            throw new InvalidOperationException( $"{label} must contain 1-{maximumTextElements} visible characters." );
        }
    }

    private static bool IsVisibleRune( UnicodeCategory category ) => category is not UnicodeCategory.SpaceSeparator
        and not UnicodeCategory.LineSeparator
        and not UnicodeCategory.ParagraphSeparator
        and not UnicodeCategory.NonSpacingMark
        and not UnicodeCategory.SpacingCombiningMark
        and not UnicodeCategory.EnclosingMark;

    private static bool HasInvalidAttachments( AiDraft draft )
    {
        return draft.Attachments is null ||
               draft.Attachments.Count > AiLimits.MaximumAttachments ||
               draft.Attachments.Any( static attachment => attachment is null );
    }

    private static bool IsInvalidIdentifier( string? value, int maximumLength )
    {
        return string.IsNullOrEmpty( value ) ||
               value.Length > maximumLength ||
               value.Any( static character => !char.IsAscii( character ) || !(char.IsAsciiLetterOrDigit( character ) || character is '.' or '_' or '-') );
    }

    private static bool IsInvalidText( string? value, int maximumBytes, bool allowEmpty, bool allowLineBreaks )
    {
        return value is null ||
               !allowEmpty && value.Length == 0 ||
               value.Any( character => char.IsControl( character ) && (!allowLineBreaks || character is not '\n' and not '\r' and not '\t') ) ||
               Encoding.UTF8.GetByteCount( value ) > maximumBytes;
    }

    private static bool IsInvalidDisplayName( int maximumTextElements, string trimmed, int[] elementStarts, bool containsVisibleRune,
        bool containsProhibitedRune )
    {
        return trimmed.Length == 0 || elementStarts.Length > maximumTextElements || containsProhibitedRune || !containsVisibleRune;
    }
}
