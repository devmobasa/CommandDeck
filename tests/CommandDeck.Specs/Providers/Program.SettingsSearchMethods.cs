using System.Text.RegularExpressions;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task SettingsRowsOpenTheCardThatHoldsTheSettingAsync( )
    {
        SearchCoordinator search = new( [new SettingsProvider( )], new SearchOptions( ) );

        async Task<IReadOnlyList<SearchResult>> FindAsync( string query ) =>
            await search.SearchAsync( new SearchRequest { Query = query, Limit = 50 }, default );

        var transparency = (await FindAsync( "transparency" )).Single( static row => row.Id == "settings:customization.transparency" );
        Equal( "setting", transparency.Kind );
        Equal( "Transparency", transparency.Title );
        Equal( "Settings › Customization", transparency.Subtitle );

        // Its own section is Settings; a lone strong match may lead as a Top Match.
        var direct = await new SettingsProvider( ).SearchAsync( QueryParser.Parse( "transparency" ),
            new SearchRequest { Query = "transparency", Limit = 50 }, default );

        Equal( "Settings", direct.Single( ).Section!.Title );
        var open = transparency.Actions.Single( );
        Equal( "open-settings", open.Kind );
        Equal( "customization.transparency", open.Arguments["settingId"] );

        // Words inside a card find it, and AI cards name their section.
        Equal( "settings:search.behavior", (await FindAsync( "reduce motion" ))[0].Id );
        var trust = (await FindAsync( "assistant trust" )).Single( );
        Equal( "settings:ai.assistant.trust", trust.Id );
        Equal( "Settings › AI › Assistant", trust.Subtitle );

        foreach ( var query in new[] { "interface font", "typeface", "monospace", "sans", "mono" } )
        {
            var font = (await FindAsync( query )).Single( static row => row.Id == "settings:customization.font" );
            Equal( "Appearance", font.Title );
            Equal( "customization.font", font.Actions.Single( ).Arguments["settingId"] );
        }

        var providers = (await FindAsync( "quick answer providers" )).Single( static row => row.Id == "settings:ai.quick.providers" );
        Equal( "Providers", providers.Title );
        Equal( "ai.quick.providers", providers.Actions.Single( ).Arguments["settingId"] );
        var desktop = (await FindAsync( "Hyprland bindings" )).Single( static row => row.Id == "settings:shortcuts.desktop" );
        Equal( "Desktop", desktop.Title );

        // Short queries, filters, calculator, and command mode list nothing.
        foreach ( var query in new[] { "tr", "app: transparency", "= 2 + 2", "> transparency" } )
        {
            True( !(await FindAsync( query )).Any( static row => row.SourceId == SourceIds.Settings ), query );
        }

        // Settings opens in the launcher; the daemon never runs the action.
        var refused = await new LinuxActionExecutor( ).ExecuteAsync( transparency, open, "", default );
        True( !refused.Succeeded );
        Equal( "This action is handled by the launcher Settings panel.", refused.Message );
    }

    // Every Settings card carries exactly one index ID, with the index's title,
    // and every index entry names a QML page and AI section that exist.
    private static void SettingsCardsAndTheSettingsIndexAgree( )
    {
        var root = FindRepositoryRoot( );
        var names = ReadConstants( typeof( SettingIds ) ).ToDictionary( static field => field.Name, static field => field.Value, StringComparer.Ordinal );
        Dictionary<string, int> tagged = new( StringComparer.Ordinal );
        Regex tag = new( @"^(?<indent>\s*)settingId:\s*Protocol\.SettingIds\.(?<name>\w+)\s*$", RegexOptions.Multiline );

        foreach ( var file in Directory.EnumerateFiles( Path.Combine( root, "qml", "common", "Settings" ), "*.qml", SearchOption.AllDirectories ) )
        {
            var lines = File.ReadAllLines( file );

            for ( var index = 0; index < lines.Length; index++ )
            {
                var match = tag.Match( lines[index] );
                if ( !match.Success )
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                True( names.ContainsKey( name ), $"{Path.GetFileName( file )} tags an unknown setting {name}" );
                tagged[name] = tagged.GetValueOrDefault( name ) + 1;
                Equal( SettingsIndex.Find( names[name] )!.Title, CardTitle( lines, index, match.Groups["indent"].Value ) );
            }
        }

        Equal( string.Join( ',', names.Keys.Order( StringComparer.Ordinal ) ), string.Join( ',', tagged.Keys.Order( StringComparer.Ordinal ) ) );
        True( tagged.Values.All( static count => count == 1 ), "each setting tags one card" );

        var constants = File.ReadAllText( Path.Combine( root, "qml", "common", "UiConstants.js" ) );
        var pages = ReadJsValues( constants, "SettingsPage" );
        var sections = ReadJsValues( constants, "AiHubSection" );
        Equal( names.Count, SettingsIndex.Entries.Select( static entry => entry.Id ).Distinct( StringComparer.Ordinal ).Count( ) );

        foreach ( var entry in SettingsIndex.Entries )
        {
            True( pages.Contains( entry.Page ), entry.Id );
            True( entry.Section.Length == 0 || entry.Page == SettingsIndex.AiPage && sections.Contains( entry.Section ), entry.Id );
            True( entry.Keywords.Length > 0 && entry.Description.Length > 0, entry.Id );
        }

        // The card's own title line, at the tag's indentation inside its block.
        static string CardTitle( string[] lines, int tagLine, string indent )
        {
            Regex title = new( $"^{Regex.Escape( indent )}title:\\s*\"(?<title>[^\"]*)\"\\s*$" );
            for ( var index = tagLine; index >= 0 && LeadingSpaces( lines[index] ) >= indent.Length; index-- )
            {
                if ( title.Match( lines[index] ) is { Success: true } before )
                {
                    return before.Groups["title"].Value;
                }
            }

            for ( var index = tagLine; index < lines.Length && (lines[index].Length == 0 || LeadingSpaces( lines[index] ) >= indent.Length); index++ )
            {
                if ( title.Match( lines[index] ) is { Success: true } after )
                {
                    return after.Groups["title"].Value;
                }
            }

            return string.Empty;
        }

        static int LeadingSpaces( string line ) => line.Length - line.TrimStart( ' ' ).Length;

        static HashSet<string> ReadJsValues( string script, string name )
        {
            var body = Regex.Match( script, $@"var {name} = Object\.freeze\(\{{(?<body>[^}}]*)\}}\)" ).Groups["body"].Value;

            return Regex
                .Matches( body, "\"(?<value>[^\"]*)\"" )
                .Select( static value => value.Groups["value"].Value )
                .ToHashSet( StringComparer.Ordinal );
        }
    }
}
