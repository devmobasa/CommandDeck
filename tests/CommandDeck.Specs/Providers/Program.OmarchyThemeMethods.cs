namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task OmarchyThemesListMarkTheCurrentOneAndApplyItAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var user = Path.Combine( root, "user" );
            var omarchy = Path.Combine( root, "omarchy" );
            var current = Path.Combine( root, "theme.name" );
            WriteTheme( omarchy, "catppuccin", "preview.png" );
            WriteTheme( omarchy, "catppuccin-latte", "backgrounds/b.jpg", "backgrounds/a.png", "backgrounds/notes.txt" );
            WriteTheme( omarchy, "tokyo-night", "preview.png" );
            WriteTheme( user, "tokyo-night", "colors.toml" );
            WriteTheme( user, "my-theme", "Preview.JPG" );
            WriteTheme( user, "Bad Name", "preview.png" );
            WriteTheme( user, ".hidden", "preview.png" );
            File.WriteAllText( Path.Combine( user, "not-a-theme" ), "" );
            File.WriteAllText( current, "catppuccin-latte\n" );
            OmarchyThemeProvider provider = new( new OmarchyThemePaths( user, omarchy, current ) );

            // Plain search offers one row that opens the theme list, without reading it.
            var browse = await SearchThemesAsync( provider, "theme" );
            Equal( OmarchyThemeProvider.BrowseResultId, browse.Single( ).Id );
            Equal( ActionKinds.ApplyQuery, browse[0].Actions[0].Kind );
            Equal( SearchFilterIds.Themes, browse[0].Actions[0].Arguments[ActionArgumentNames.FilterId] );
            Equal( "", browse[0].Actions[0].Arguments[ActionArgumentNames.Query] );
            var named = await SearchThemesAsync( provider, "theme tokyo" );
            Equal( "Switch Theme: tokyo", named.Single( ).Title );
            Equal( "tokyo", named[0].Actions[0].Arguments[ActionArgumentNames.Query] );

            // The list holds valid theme folders by display name; a user theme replaces Omarchy's.
            var listed = await SearchThemesAsync( provider, "theme:" );
            Equal( "Catppuccin|Catppuccin Latte|My Theme|Tokyo Night", string.Join( '|', listed.Select( static row => row.Title ) ) );
            True( listed.All( static row => row.Kind == ResultKinds.Theme && row.SourceId == SourceIds.Themes ) );
            Equal( "Your theme", listed[3].Subtitle );
            Equal( Path.Combine( user, "tokyo-night" ), listed[3].Details.Single( static field => field.Label == "Folder" ).Value );

            // Previews: preview.*, any letter case; else the first background image; a user
            // theme without one shows the Omarchy theme it replaces.
            Equal( Path.Combine( omarchy, "catppuccin", "preview.png" ), listed[0].Metadata[ResultMetadataKeys.ThemePreview] );
            Equal( Path.Combine( omarchy, "catppuccin-latte", "backgrounds", "a.png" ), listed[1].Metadata[ResultMetadataKeys.ThemePreview] );
            Equal( Path.Combine( user, "my-theme", "Preview.JPG" ), listed[2].Metadata[ResultMetadataKeys.ThemePreview] );
            Equal( Path.Combine( omarchy, "tokyo-night", "preview.png" ), listed[3].Metadata[ResultMetadataKeys.ThemePreview] );

            // Only the theme in use is marked; it reapplies, the others apply.
            Equal( "Catppuccin Latte", listed.Single( static row => row.Metadata.ContainsKey( ResultMetadataKeys.ThemeCurrent ) ).Title );
            Equal( "Reapply Theme", listed[1].Actions[0].Title );
            Equal( "Current theme", listed[1].Details.Single( static field => field.Label == "Status" ).Value );
            var apply = listed[0].Actions[0];
            Equal( "Apply Theme", apply.Title );
            Equal( ActionKinds.LaunchArgv, apply.Kind );
            True( apply.CloseAfterAction );
            Equal( "omarchy-theme-set|catppuccin", string.Join( '|', LaunchArgumentCodec.Decode( apply.Arguments[ActionArgumentNames.Argv] ) ) );

            // Typing filters the list; an empty one has no rows.
            Equal( "Catppuccin Latte", (await SearchThemesAsync( provider, "theme: latte" ))[0].Title );
            Equal( 0, (await SearchThemesAsync( provider, "theme: zzzz" )).Count );

            // A turned-off source, or no theme folder at all, shows nothing.
            Equal( 0, (await SearchThemesAsync( provider, "theme", [SourceIds.Applications] )).Count );
            OmarchyThemeProvider missing = new( new OmarchyThemePaths( Path.Combine( root, "none" ), Path.Combine( root, "none2" ), current ) );
            Equal( 0, (await SearchThemesAsync( missing, "theme" )).Count );
            Equal( "Retro 82", OmarchyThemeCatalog.DisplayName( "retro-82" ) );

            // An unreadable or invalid current name marks nothing.
            File.WriteAllText( current, "../etc\n" );
            True( (await SearchThemesAsync( provider, "theme:" )).All( static row => !row.Metadata.ContainsKey( ResultMetadataKeys.ThemeCurrent ) ) );

            // A pipe in place of the name file is refused at once instead of waiting for a writer.
            File.Delete( current );
            Equal( 0, (await ProcessRunner.RunAsync( "mkfifo", [current], null, null, TimeSpan.FromSeconds( 2 ), default )).ExitCode );
            True( (await SearchThemesAsync( provider, "theme:" )).All( static row => !row.Metadata.ContainsKey( ResultMetadataKeys.ThemeCurrent ) ) );

            // A name the setter would read differently, such as one ending in a newline, is not listed.
            True( !OmarchyThemeCatalog.IsValidName( "tokyo-night\n" ) );
            WriteTheme( user, "tokyo-night\n", "preview.png" );
            True( (await SearchThemesAsync( provider, "theme:" )).All( static row => !row.Metadata[ResultMetadataKeys.ThemeName].Contains( '\n' ) ) );

            // Previews stay inside the theme folder they were found in.
            Equal( Path.Combine( omarchy, "catppuccin-latte" ),
                OmarchyThemeCatalog.PreviewRoot( Path.Combine( omarchy, "catppuccin-latte", "backgrounds", "a.png" ) ) );
            Equal( Path.Combine( user, "my-theme" ), OmarchyThemeCatalog.PreviewRoot( Path.Combine( user, "my-theme", "Preview.JPG" ) ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task OmarchyThemesRankEveryMatchBeforeTheLimitAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var omarchy = Path.Combine( root, "omarchy" );
            foreach ( var name in new[] { "a-tokyo-night", "b-tokyo-night", "c-tokyo-night", "d-tokyo-night", "tokyo-night" } )
            {
                WriteTheme( omarchy, name, "preview.png" );
            }

            OmarchyThemeProvider provider = new( new OmarchyThemePaths( Path.Combine( root, "user" ), omarchy, Path.Combine( root, "theme.name" ) ) );

            // Four names earlier in the alphabet also match; the exact one still comes first.
            Equal( "Tokyo Night", (await SearchThemesAsync( provider, "theme: tokyo-night", limit: 1 )).Single( ).Title );
            // Before anything is typed, the list stays alphabetical.
            var first = await SearchThemesAsync( provider, "theme:", limit: 2 );
            Equal( "A Tokyo Night|B Tokyo Night", string.Join( '|', first.Select( static row => row.Title ) ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task ThemePreviewRendersOnlyImagesAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var theme = Directory.CreateDirectory( Path.Combine( root, "theme" ) ).FullName;
            var text = Path.Combine( theme, "preview.png" );
            File.WriteAllText( text, "not an image" );
            FilePreviewService previews = new( new IndexingOptions( ), new PrivacyOptions { NeverIndex = [] } );

            Equal( "Preview is only available for images.", (await previews.ReadImageAsync( text, theme, CancellationToken.None )).Error );
            var missing = await previews.ReadImageAsync( Path.Combine( theme, "missing.png" ), theme, CancellationToken.None );
            Equal( "Preview is unavailable for this file.", missing.Error );

            // A link out of the theme folder is refused before the file is read,
            // so a downloaded theme cannot show a picture from elsewhere.
            var outside = Path.Combine( root, "private.png" );
            await File.WriteAllBytesAsync( outside, Convert.FromBase64String( PreviewPngBase64 ) );
            var escaping = Path.Combine( theme, "escaping.png" );
            File.CreateSymbolicLink( escaping, outside );
            Equal( "Preview is unavailable for this file.", (await previews.ReadImageAsync( escaping, theme, CancellationToken.None )).Error );

            // The never-index rules apply to theme previews too.
            FilePreviewService denying = new( new IndexingOptions( ), new PrivacyOptions { NeverIndex = [theme] } );
            Equal( "Preview is unavailable for this file.", (await denying.ReadImageAsync( text, theme, CancellationToken.None )).Error );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static async Task ThemePreviewRendersAnImageInALinkedThemeFolderAsync( )
    {
        if ( !ImagePreviewToolsAvailable( ) )
        {
            SkipSpec( "ImageMagick or Bubblewrap is not installed; theme preview boundaries still run separately." );
        }

        var root = CreateCliSpecRoot( );

        try
        {
            // A theme installed as a link to a checkout previews the image inside that checkout.
            var checkout = Directory.CreateDirectory( Path.Combine( root, "checkout" ) ).FullName;
            await File.WriteAllBytesAsync( Path.Combine( checkout, "preview.png" ), Convert.FromBase64String( PreviewPngBase64 ) );
            var themes = Directory.CreateDirectory( Path.Combine( root, "themes" ) ).FullName;
            var linked = Path.Combine( themes, "linked" );
            Directory.CreateSymbolicLink( linked, checkout );
            FilePreviewService previews = new( new IndexingOptions( ), new PrivacyOptions { NeverIndex = [] } );
            var image = Path.Combine( linked, "preview.png" );

            var outcome = await previews.ReadImageAsync( image, OmarchyThemeCatalog.PreviewRoot( image ), CancellationToken.None );

            True( outcome.Succeeded, outcome.Error );
            Equal( "png", outcome.Preview!.Language );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private static void WriteTheme( string themes, string name, params string[] files )
    {
        var directory = Directory.CreateDirectory( Path.Combine( themes, name ) ).FullName;
        foreach ( var file in files )
        {
            var path = Path.Combine( directory, file );
            Directory.CreateDirectory( Path.GetDirectoryName( path )! );
            File.WriteAllText( path, "" );
        }
    }

    private static async Task<IReadOnlyList<SearchResult>> SearchThemesAsync( OmarchyThemeProvider provider, string query, string[]? sources = null,
        int limit = 50 ) =>
        await provider.SearchAsync( QueryParser.Parse( query ), new SearchRequest { Query = query, Limit = limit, EnabledSearchSources = sources },
            CancellationToken.None );
}
