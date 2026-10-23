using System.Text;

namespace CommandDeck;

public static class PreferencesTextFormatter
{
    private const string OnName = "on";
    private const string OffName = "off";
    private const string NoneName = "none";
    private const string AvailableName = "available";
    private const string MissingName = "missing";

    // A Settings choice, or the configuration's value while none is made.
    private static string Choice( bool? chosen, bool configured )
    {
        if ( chosen is { } value )
        {
            return (value ? OnName : OffName);
        }
        else
        {
            return (configured ? OnName : OffName) + " (configuration)";
        }
    }

    public static IReadOnlyList<string> Format( PreferencesSnapshot snapshot )
    {
        ArgumentNullException.ThrowIfNull( snapshot );

        List<string> lines = [];
        foreach ( var source in snapshot.Preferences.Sources )
        {
            lines.Add( $"{source.Position}. {source.SourceId}: " +
                       $"search={(source.EnabledInSearch ? OnName : OffName)}, " +
                       $"home={(source.EnabledOnHome ? OnName : OffName)}" );
        }

        lines.Add( $"Home folder only: {(snapshot.Preferences.SearchHomeOnly ? OnName : OffName)}" );
        lines.Add( $"Reduce motion: {(snapshot.Preferences.ReduceMotion ? OnName : OffName)}" );
        var defaults = snapshot.Defaults ?? new PreferenceDefaults( );

        lines.Add( $"Remember searches: {Choice( snapshot.Preferences.RememberSearches, defaults.RememberSearches )}" );
        lines.Add( $"Learn from use: {Choice( snapshot.Preferences.LearnFromUse, defaults.LearnFromUse )}" );
        lines.Add( $"Omarchy menu state: {Choice( snapshot.Preferences.OmarchyMenuState, defaults.OmarchyMenuState )}" );
        lines.Add( $"Inline calculator: {Choice( snapshot.Preferences.InlineCalculator, defaults.InlineCalculator )}" );
        lines.Add( $"Top Matches: {Choice( snapshot.Preferences.TopMatches, defaults.TopMatches )}" );
        lines.Add( $"Web fallback: {EscapeTerminalText( snapshot.Preferences.WebFallbackQuicklinkId ?? NoneName )}" );
        lines.Add( $"Preferred editor: {snapshot.Preferences.PreferredEditor}" );
        lines.Add( $"HEY threads open in: {snapshot.Preferences.HeyOpenTarget}" );
        lines.Add( $"Graphical editor: {EscapeTerminalText( snapshot.Preferences.GraphicalEditor.Executable )}" );
        lines.Add( $"Terminal editor: {EscapeTerminalText( snapshot.Preferences.TerminalEditor.Executable )}" );
        lines.Add( $"Bookmarks: {snapshot.Customizations.Count}" );

        foreach ( var bookmark in snapshot.Customizations )
        {
            var aliases = bookmark.Aliases.Count == 0 ? NoneName : string.Join( ", ", bookmark.Aliases );
            var availability = bookmark.IsAvailable ? AvailableName : MissingName;

            lines.Add( $"  {bookmark.Position}. {EscapeTerminalText( bookmark.Title )} " + $"[{availability}; aliases: {aliases}]" );
        }

        return lines;
    }

    public static string EscapeTerminalText( string value )
    {
        StringBuilder escaped = new( value.Length );
        foreach ( var character in value )
        {
            switch ( character )
            {
                case '\n':
                    escaped.Append( "\\n" );

                    break;
                case '\r':
                    escaped.Append( "\\r" );

                    break;
                case '\t':
                    escaped.Append( "\\t" );

                    break;
                default:
                    if ( char.IsControl( character ) )
                    {
                        escaped.Append( $"\\u{( int ) character:X4}" );
                    }
                    else
                    {
                        escaped.Append( character );
                    }

                    break;
            }
        }

        return escaped.ToString( );
    }
}
