using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private const string ActionPayloadFixturePrefix = "commanddeck-action-payload-";
    private const string ActionPayloadFixtureDirectory = "COMMANDDECK_SPEC_ACTION_PAYLOAD_DIRECTORY";

    private static async Task CopyActionPreservesEmptyAndLiteralStdinAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( ActionPayloadFixturePrefix, preservePrefix: true ).FullName;

        var originalPath = Environment.GetEnvironmentVariable( "PATH" );
        var originalFixture = Environment.GetEnvironmentVariable( ActionPayloadFixtureDirectory );

        try
        {
            File.CreateSymbolicLink( Path.Combine( root, "wl-copy" ), Environment.ProcessPath! );
            Environment.SetEnvironmentVariable( "PATH", root );
            Environment.SetEnvironmentVariable( ActionPayloadFixtureDirectory, root );
            LinuxActionExecutor executor = new( );
            SearchResult row = CreateRow( );
            foreach ( var text in new[] { string.Empty, " \t\r\n ", "café\r\n{clipboard} $(literal); 'quoted'" } )
            {
                var action = CreateAction( text );
                var outcome = await executor.ExecuteAsync( row, action, string.Empty, default );
                True( outcome.Succeeded, "Empty and whitespace text are valid clipboard payloads." );
                var bytes = await File.ReadAllBytesAsync( Path.Combine( root, "stdin" ) );

                True( Encoding
                    .UTF8.GetBytes( text )
                    .SequenceEqual( bytes ) );
            }

            File.Delete( Path.Combine( root, "stdin" ) );

            foreach ( var (kind, key) in CreateMissingPayloadExpectations( ) )
            {
                var missing = await executor.ExecuteAsync( row, new CommandAction { Id = "missing", Title = "Missing", Kind = kind }, string.Empty, default );

                True( !missing.Succeeded );
                Equal( $"Action requires '{key}'.", missing.Message );
            }

            True( !File.Exists( Path.Combine( root, "stdin" ) ), "Missing payloads never start the clipboard child." );
        }
        finally
        {
            Environment.SetEnvironmentVariable( "PATH", originalPath );
            Environment.SetEnvironmentVariable( ActionPayloadFixtureDirectory, originalFixture );
            Directory.Delete( root, recursive: true );
        }

        static SearchResult CreateRow( )
        {
            return new( )
            {
                Id = "config:literal",
                Title = "Literal",
                Kind = ResultKinds.Configuration,
                SourceId = SourceIds.Configuration
            };
        }

        static CommandAction CreateAction( string text )
        {
            return new CommandAction
            {
                Id = "copy",
                Title = "Copy",
                Kind = ActionKinds.CopyText,
                Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Text] = text }
            };
        }

        static (string, string)[] CreateMissingPayloadExpectations( )
        {
            return new[]
            {
                (ActionKinds.CopyText, "text"), (ActionKinds.OpenPath, "path"), (ActionKinds.OpenUrl, "urlTemplate"),
                (ActionKinds.SnippetPreview, "template")
            };
        }
    }

    private static async Task UrlActionPreservesAliasAndEscapedArgvAsync( )
    {
        Equal( "{query}", UrlTemplateTokens.Query );

        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.CreateDirectory( ActionPayloadFixturePrefix, preservePrefix: true ).FullName;

        var originalPath = Environment.GetEnvironmentVariable( "PATH" );
        var originalFixture = Environment.GetEnvironmentVariable( ActionPayloadFixtureDirectory );

        try
        {
            File.CreateSymbolicLink( Path.Combine( root, "uwsm-app" ), Environment.ProcessPath! );
            Environment.SetEnvironmentVariable( "PATH", root );
            Environment.SetEnvironmentVariable( ActionPayloadFixtureDirectory, root );
            SearchResult row = CreateRow( );
            CommandAction action = CreateAction( );

            var outcome = await new LinuxActionExecutor( ).ExecuteAsync( row, action, "GO café &$(literal)", default );

            True( outcome.Succeeded );

            Equal( "https://example.invalid/?q=caf%C3%A9%20%26%24%28literal%29&again=caf%C3%A9%20%26%24%28literal%29",
                await File.ReadAllTextAsync( Path.Combine( root, "url" ) ) );
        }
        finally
        {
            Environment.SetEnvironmentVariable( "PATH", originalPath );
            Environment.SetEnvironmentVariable( ActionPayloadFixtureDirectory, originalFixture );
            Directory.Delete( root, recursive: true );
        }

        static SearchResult CreateRow( )
        {
            return new( )
            {
                Id = "quicklink:literal",
                Title = "Literal",
                Kind = ResultKinds.Quicklink,
                SourceId = SourceIds.CommandsQuicklinks
            };
        }

        static CommandAction CreateAction( )
        {
            return new( )
            {
                Id = "open",
                Title = "Open",
                Kind = ActionKinds.OpenUrl,
                Arguments = new( StringComparer.Ordinal )
                {
                    [ActionArgumentNames.UrlTemplate] = "https://example.invalid/?q={QUERY}&again={query}",
                    [ActionArgumentNames.Alias] = "go"
                }
            };
        }
    }

    private static async Task ApplicationArgumentsPreserveHiddenInputsAsync( )
    {
        SearchResult row = CreateRow( );
        CatalogSearchProvider provider = new( new ActionPayloadCatalog( row ) );

        var results = await provider.SearchAsync( QueryParser.Parse( "viewer ~/literal.txt" ), new SearchRequest { Query = "viewer ~/literal.txt" }, default );

        var action = results
            .Single( )
            .Actions.Single( );

        True( action.Hidden == true );
        Equal( "note", action.Inputs.Single( ).Name );
        Equal( "literal", action.Inputs.Single( ).DefaultValue );

        Equal( "~/literal.txt", LaunchArgumentCodec
            .Decode( action.Arguments["launchArguments"] )
            .Single( ) );

        Equal( "app:viewer", results
            .Single( )
            .Metadata["catalogResultId"] );

        static SearchResult CreateRow( )
        {
            return new( )
            {
                Id = "app:viewer",
                Title = "Viewer",
                Kind = ResultKinds.Application,
                SourceId = SourceIds.Applications,
                ProviderId = ProviderIds.Catalog,
                Actions =
                [
                    new CommandAction
                    {
                        Id = "launch",
                        Title = "Launch",
                        Kind = ActionKinds.LaunchDesktop,
                        Hidden = true,
                        Inputs = [new ActionInput { Name = "note", DefaultValue = "literal" }],
                        Arguments = new( StringComparer.Ordinal ) { [ResultMetadataKeys.Application.Exec] = "viewer %U" }
                    }
                ]
            };
        }
    }

    private sealed class ActionPayloadCatalog( SearchResult result ) : ICatalogQueryStore
    {
        public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, int limit, CancellationToken cancellationToken ) =>
            ValueTask.FromResult<IReadOnlyList<SearchResult>>( [result] );

        public ValueTask<IReadOnlyList<SearchResult>> SearchSourcesAsync( ParsedQuery query, IReadOnlyList<string> sourceIds, int limit,
            CancellationToken cancellationToken ) =>
            throw new InvalidOperationException( "Application invocation should use the application query." );

        public ValueTask<IReadOnlyList<SearchResult>> GetHomeResultsAsync( LauncherPreferences preferences, int limit, CancellationToken cancellationToken ) =>
            throw new InvalidOperationException( "Application invocation should not read Home." );
    }

    private static int RunActionPayloadFixture( string fileName, string? value )
    {
        var root = Environment.GetEnvironmentVariable( ActionPayloadFixtureDirectory );
        if ( IsInvalidFixtureDirectory( root ) )
        {
            return 2;
        }

        if ( value is null )
        {
            using var output = File.Create( Path.Combine( root, fileName ) );

            Console
                .OpenStandardInput( )
                .CopyTo( output );
        }
        else
        {
            File.WriteAllText( Path.Combine( root, fileName ), value );
        }

        return 0;

        static bool IsInvalidFixtureDirectory( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? root )
        {
            return root is null ||
                   !Path
                       .GetFileName( root )
                       .StartsWith( ActionPayloadFixturePrefix, StringComparison.Ordinal ) ||
                   !Directory.Exists( root );
        }
    }
}
