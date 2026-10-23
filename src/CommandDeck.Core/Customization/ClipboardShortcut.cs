namespace CommandDeck;

/// <summary>The in-launcher clipboard chord; empty disables it without changing desktop bindings.</summary>
public static class ClipboardShortcut
{
    private const int MaximumShortcutCharacters = 64;
    public const string ReservedCopyChord = "CTRL+SHIFT+C";

    public const string Default = "CTRL+SHIFT+V";

    public static string Parse( string? value )
    {
        if ( string.IsNullOrWhiteSpace( value ) )
        {
            return string.Empty;
        }

        if ( IsInvalidShortcutText( value ) )
        {
            throw new InvalidOperationException( "Clipboard shortcut must be Ctrl+Shift+letter or Ctrl+Alt+letter." );
        }

        var chord = SavedSearchValidator.NormalizeShortcut( value )!;
        var parts = chord.Split( '+' );
        if ( IsInvalidClipboardChord( chord, parts ) )
        {
            throw new InvalidOperationException( "Use Ctrl+Shift+letter or Ctrl+Alt+letter; Ctrl+Shift+C is reserved for copying file paths." );
        }

        return chord;
    }

    private static bool IsInvalidShortcutText( string value )
    {
        return value.Length > MaximumShortcutCharacters || !value.Any( char.IsAsciiLetter );
    }

    private static bool IsInvalidClipboardChord( string chord, string[] parts )
    {
        return parts is not [ShortcutModifiers.Control, ShortcutModifiers.Shift or ShortcutModifiers.Alt, { Length: 1 }] ||
               !char.IsAsciiLetterUpper( parts[2][0] ) ||
               chord == ReservedCopyChord;
    }
}
