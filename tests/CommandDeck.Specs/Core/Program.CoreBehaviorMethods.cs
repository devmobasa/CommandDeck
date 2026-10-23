namespace CommandDeck.Specs;

public static partial class Program
{
    private static void FuzzyMatchTiersRemainMonotone( )
    {
        var exact = FuzzyMatcher.Score( "fire", "fire" );
        var prefix = FuzzyMatcher.Score( "fire", "Firefox" );
        var boundary = FuzzyMatcher.Score( "fire", "Open Firefox" );
        var acronym = FuzzyMatcher.Score( "fire", "Fast Index Runtime Engine" );
        var typo = FuzzyMatcher.Score( "fire", "fier" );
        var subsequence = FuzzyMatcher.Score( "fire", "fxxixxrxxe" );
        if ( HasUnexpectedScoreTiers( exact, prefix, boundary, acronym, typo, subsequence ) )
        {
            throw new InvalidOperationException( $"Unexpected tiers: exact={exact:F3}, prefix={prefix:F3}, " +
                                                 $"boundary={boundary:F3}, acronym={acronym:F3}, " +
                                                 $"typo={typo:F3}, subsequence={subsequence:F3}." );
        }

        static bool HasUnexpectedScoreTiers( double exact, double prefix, double boundary, double acronym, double typo, double subsequence )
        {
            return !(exact > prefix && prefix > boundary && boundary > acronym && acronym > typo && typo > subsequence);
        }
    }

    private static void DesktopEntryExpandsEmbeddedFieldCodes( )
    {
        var command = DesktopEntryCommand.Create( "launcher --name=%c --desktop=%k file=%f literal=%%", "Example App", "example-icon",
            "/usr/share/applications/example.desktop", ["~/Desktop/test.txt"] );

        Equal( "launcher", command.Executable );
        Equal( 4, command.Arguments.Count );
        Equal( "--name=Example App", command.Arguments[0] );
        Equal( "--desktop=/usr/share/applications/example.desktop", command.Arguments[1] );
        Equal( $"file={PathExpansion.Expand( "~/Desktop/test.txt" )}", command.Arguments[2] );
        Equal( "literal=%", command.Arguments[3] );

        Throws<InvalidOperationException>( ( ) => DesktopEntryCommand.Create(
            "launcher files=%F", "Example App", "example-icon", "/usr/share/applications/example.desktop", ["one.txt", "two.txt"] ) );
    }

    private static void WebFallbackDependsOnExecutableResultsAsync( )
    {
        SearchResult status = new( )
        {
            Id = "status:offline",
            Kind = ResultKinds.Status,
            Title = "Provider unavailable",
            SourceId = SourceIds.Status
        };

        SearchResult actionable = new( status )
        {
            Id = "quicklink:search",
            Kind = ResultKinds.Quicklink,
            Actions =
            [
                new CommandAction { Id = "open", Title = "Open", Kind = ActionKinds.OpenUrl }
            ]
        };

        True( !SearchResultCapabilities.IsExecutable( status ) );
        True( SearchResultCapabilities.IsExecutable( actionable ) );
    }

    private static void PreferencesTextEscapesBookmarkControlsAsync( )
    {
        var preferences = LauncherPreferences.CreateDefault( );

        PreferencesSnapshot snapshot = new( )
        {
            Preferences = new LauncherPreferences { Sources = preferences.Sources, WebFallbackQuicklinkId = "web\n\u001b[31m" },
            Customizations =
            [
                new CustomizationSummary
                {
                    Id = "customization:file:notes",
                    StableId = "file:notes",
                    Title = "Notes\n\u001b[31mOwned\r",
                    SourceId = SourceIds.Files,
                    Position = 1,
                    IsAvailable = true,
                    IsFavorite = true,
                    Aliases = ["note", "scratch"]
                }
            ]
        };

        var lines = PreferencesTextFormatter.Format( snapshot );
        var output = string.Join( '\n', lines );
        True( output.Contains( "Notes\\n\\u001B[31mOwned\\r", StringComparison.Ordinal ) );
        True( output.Contains( "Web fallback: web\\n\\u001B[31m", StringComparison.Ordinal ) );
        True( output.Contains( "note, scratch", StringComparison.Ordinal ) );
        True( !output.Contains( '\u001b' ) );
        Equal( lines.Count - 1, output.Count( character => character == '\n' ) );
    }

    private static async Task ConfigurationRejectsUnsafeQuicklinkIds( )
    {
        var path = SpecTemporaryFiles.PathFor( $"commanddeck-unsafe-quicklink-{Guid.NewGuid( ):N}.json" );

        try
        {
            CommandDeckOptions options = new( )
            {
                Quicklinks =
                [
                    new QuicklinkOptions { Id = "web\n\u001b[31m", Title = "Unsafe", UrlTemplate = "https://example.test/?q=" + UrlTemplateTokens.Query }
                ]
            };

            await File.WriteAllTextAsync( path, System.Text.Json.JsonSerializer.Serialize( options, CommandDeckJsonContext.Default.CommandDeckOptions ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await ConfigurationLoader.LoadAsync( path, CancellationToken.None ) );
        }
        finally
        {
            File.Delete( path );
        }
    }
}
