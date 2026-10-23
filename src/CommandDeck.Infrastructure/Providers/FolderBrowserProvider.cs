namespace CommandDeck;

public sealed class FolderBrowserProvider : ICommandProvider
{
    private const int ReservedRows = 3;
    private const int ParentScore = 10;
    private const int ErrorScore = 3;

    private const string PreviousPageResultPrefix = "folder-previous:";
    private const string ErrorResultPrefix = "folder-error:";
    private const string NextPageResultPrefix = "folder-next:";
    private const string CopyPathActionId = "copy-path";
    private const string PreviousPageActionId = "previous";
    private const string CollectActionId = "collect";
    private const string ResultIdNamespace = "browse";
    private const string RevealActionId = "reveal";
    private const string OffsetArgument = "offset";
    private const string OpenActionId = "open";
    private const string NextPageActionId = "next";
    private const int MaximumScannedEntries = 50000;
    private const double DirectoryScore = 2.5;
    private const double NextPageScore = 0.1;
    private const int MaximumPageEntries = 97;
    private const int FileScore = 2;
    private const int PreviousPageScore = 9;

    public string Id => ProviderIds.Folders;

    public ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );

        if ( !query.IncludedFilters.Any( static filter => filter.SourceId == SourceIds.Folders ) )
        {
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>( [] );
        }

        var text = query.Text;
        var path = request.BrowseDirectory ?? Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );

        if ( IsExplicitPath( text ) )
        {
            path = text;
            text = "";
        }

        path = Path.GetFullPath( PathExpansion.Expand( path ) );
        List<SearchResult> results = [];
        var parent = Path.GetDirectoryName( path.TrimEnd( Path.DirectorySeparatorChar ) );
        if ( parent is not null )
        {
            results.Add( new SearchResult( Build( parent, true ) )
            {
                Id = ResultIdPrefixes.FolderParent + path,
                Title = "Up one folder",
                Score = ParentScore
            } );
        }

        try
        {
            var limit = Math.Clamp( request.Limit - ReservedRows, 1, MaximumPageEntries );

            if ( request.BrowseOffset > 0 )
            {
                results.Add( CreatePreviousPageResult( request, text, path, limit ) );
            }

            var entries = ReadPageEntries( request, text, path, limit );
            foreach ( var entry in entries.Take( limit ) )
            {
                cancellationToken.ThrowIfCancellationRequested( );
                results.Add( Build( entry.Path, entry.Directory ) );
            }

            if ( entries.Length > limit )
            {
                results.Add( CreateNextPageResult( request, text, path, limit ) );
            }
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            results.Add( CreateFolderError( path, exception ) );
        }

        return ValueTask.FromResult<IReadOnlyList<SearchResult>>( results );
    }

    private static SearchResult Build( string path, bool directory ) => new( )
    {
        Id = StableId.Create( ResultIdNamespace, path ),
        Title = Path.GetFileName( path ),
        Subtitle = path,
        Kind = ResultKinds.File,
        ProviderId = ProviderIds.Folders,
        SourceId = SourceIds.Folders,
        Icon = directory ? "folder" : "text-x-generic",
        Score = directory ? DirectoryScore : FileScore,
        Metadata = new( StringComparer.Ordinal ) { [ResultMetadataKeys.Path] = path, [ResultMetadataKeys.IsDirectory] = directory ? "true" : "false" },
        Actions =
        [
            new CommandAction
            {
                Id = OpenActionId,
                Title = directory ? "Browse folder" : "Open file",
                Kind = directory ? ActionKinds.BrowseFolder : ActionKinds.OpenPath,
                Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Path] = path }
            },
            new CommandAction
            {
                Id = CollectActionId,
                Title = "Toggle file selection",
                Kind = ActionKinds.CollectFile,
                Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Path] = path }
            },
            new CommandAction
            {
                Id = CopyPathActionId,
                Title = "Copy path",
                Kind = ActionKinds.CopyText,
                Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Text] = path }
            },
            new CommandAction
            {
                Id = RevealActionId,
                Title = "Open in file manager",
                Kind = ActionKinds.RevealPath,
                Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Path] = path }
            },
            .. directory ? new[] { HerdrProjectAction.Create( path ) } : []
        ]
    };

    private static bool IsExplicitPath( string text )
    {
        return text.StartsWith( '/' ) || text.StartsWith( "~/", StringComparison.Ordinal );
    }

    private SearchResult CreatePreviousPageResult( SearchRequest request, string text, string path, int limit )
    {
        return new SearchResult
        {
            Id = PreviousPageResultPrefix + path,
            Title = "Previous page",
            Subtitle = path,
            Kind = ResultKinds.CustomCommand,
            ProviderId = Id,
            SourceId = SourceIds.Folders,
            Score = PreviousPageScore,
            Actions =
            [
                new CommandAction
                {
                    Id = PreviousPageActionId,
                    Title = "Previous page",
                    Kind = ActionKinds.BrowseFolder,
                    Arguments = new( StringComparer.Ordinal )
                    {
                        [ActionArgumentNames.Path] = path,
                        [OffsetArgument] = Math
                            .Max( 0, request.BrowseOffset - limit )
                            .ToString( ),
                        [ActionArgumentNames.Query] = text
                    }
                }
            ]
        };
    }

    private static (string Path, bool Directory)[] ReadPageEntries( SearchRequest request, string text, string path, int limit )
    {
        return Directory
            .EnumerateFileSystemEntries( path )
            .Take( MaximumScannedEntries )
            .Where( entry => Path
                .GetFileName( entry )
                .Contains( text, StringComparison.OrdinalIgnoreCase ) )
            .Select( entry => (Path: entry, Directory: Directory.Exists( entry )) )
            .OrderByDescending( static entry => entry.Directory )
            .ThenBy( static entry => entry.Path, StringComparer.OrdinalIgnoreCase )
            .Skip( request.BrowseOffset )
            .Take( limit + 1 )
            .ToArray( );
    }

    private SearchResult CreateNextPageResult( SearchRequest request, string text, string path, int limit )
    {
        return new SearchResult
        {
            Id = NextPageResultPrefix + path,
            Title = "Next page",
            Subtitle = path,
            Kind = ResultKinds.CustomCommand,
            ProviderId = Id,
            SourceId = SourceIds.Folders,
            Score = NextPageScore,
            Actions =
            [
                new CommandAction
                {
                    Id = NextPageActionId,
                    Title = "Next page",
                    Kind = ActionKinds.BrowseFolder,
                    Arguments = new( StringComparer.Ordinal )
                    {
                        [ActionArgumentNames.Path] = path, [OffsetArgument] = (request.BrowseOffset + limit).ToString( ), [ActionArgumentNames.Query] = text
                    }
                }
            ]
        };
    }

    private SearchResult CreateFolderError( string path, Exception exception )
    {
        return new SearchResult
        {
            Id = ErrorResultPrefix + path,
            Title = "Folder unavailable",
            Subtitle = path,
            Kind = ResultKinds.Status,
            ProviderId = Id,
            SourceId = SourceIds.Folders,
            Score = ErrorScore,
            DetailMarkdown = exception.Message
        };
    }
}
