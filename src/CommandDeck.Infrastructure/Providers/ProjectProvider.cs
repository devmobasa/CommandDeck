namespace CommandDeck;

public sealed class ProjectProvider( IReadOnlyList<ProjectOptions> projects ) : ICommandProvider
{
    private const int MaximumGitErrorBytes = ByteUnits.BytesPerKibibyte;
    private const int GitTimeoutMilliseconds = 200;

    private const int ProjectScore = 2;
    private const string CopyPathActionId = "copy-path";
    private const string TerminalActionId = "terminal";
    private const string ResultIdNamespace = "project";
    private const string EditorActionId = "editor";
    private const int MaximumGitOutputBytes = 8 * ByteUnits.BytesPerKibibyte;
    private const int MaximumProjects = 100;
    private const int MaximumResults = 20;

    public string Id => ProviderIds.Projects;

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        if ( !query.IncludedFilters.Any( static filter => filter.SourceId == SourceIds.Projects ) )
        {
            return [];
        }

        var selected = FindProjects( query, request );

        return await Task
            .WhenAll( selected.Select( project => BuildAsync( project, cancellationToken ) ) )
            .ConfigureAwait( false );
    }

    private static async Task<SearchResult> BuildAsync( ProjectOptions project, CancellationToken cancellationToken )
    {
        var path = Path.GetFullPath( Environment.ExpandEnvironmentVariables( ResolveProjectPath( project ) ) );

        var exists = Directory.Exists( path );
        var status = exists ? "Git status unavailable" : "Checkout is missing";

        if ( exists )
        {
            try
            {
                var git = await ProcessRunner
                    .RunBoundedAsync( "git", ["--no-optional-locks", "-C", path, "status", "--short", "--branch", "--untracked-files=no"], null,
                        TimeSpan.FromMilliseconds( GitTimeoutMilliseconds ), CreateGitLimits( ), cancellationToken )
                    .ConfigureAwait( false );

                if ( git.AnyOutputLimitExceeded )
                {
                    status = git.StandardOutput.Trim( ) + "\n(status truncated)";
                }
                else if ( HasGitStatus( git ) )
                {
                    status = git.StandardOutput.Trim( );
                }
            }
            catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
            {
                status = "Git status unavailable";
            }
        }

        List<CommandAction> actions = [];

        if ( exists )
        {
            actions.Add( Argv( EditorActionId, "Open in " + project.Editor, path, [
                project.Editor, .. project.EditorArguments.Select( argument => argument.Replace( EditorArgumentTokens.Path, path, StringComparison.Ordinal ) )
            ] ) );

            actions.Add( Argv( TerminalActionId, "Open terminal here", path, [ToolExecutables.TerminalLauncher, "--dir=" + path] ) );
            actions.Add( HerdrProjectAction.Create( path ) );
        }

        actions.Add( CreateCopyPathAction( path ) );

        return new SearchResult
        {
            Id = StableId.Create( ResultIdNamespace, path ),
            Title = project.Title,
            Subtitle = path,
            Kind = ResultKinds.CustomCommand,
            ProviderId = ProviderIds.Projects,
            SourceId = SourceIds.Projects,
            Icon = "folder-development",
            Score = ProjectScore,
            DetailMarkdown = path + "\n\n" + status,
            Section = ResultSections.Projects,
            Metadata = new( StringComparer.Ordinal ) { [ResultMetadataKeys.Path] = path },
            Actions = actions
        };
    }

    private static CommandAction Argv( string id, string title, string path, IReadOnlyList<string> argv )
    {
        if ( HasInvalidLaunchArguments( argv ) )
        {
            throw new InvalidOperationException( "Project commands cannot contain control separators." );
        }

        return new CommandAction
        {
            Id = id,
            Title = title,
            Kind = ActionKinds.RunArgv,
            Arguments = new( StringComparer.Ordinal )
            {
                [ActionArgumentNames.Argv] = string.Join( '\u001f', argv ),
                [ActionArgumentNames.WorkingDirectory] = path
            }
        };
    }

    private IEnumerable<ProjectOptions> FindProjects( ParsedQuery query, SearchRequest request )
    {
        return projects
            .Take( MaximumProjects )
            .Where( project => project.Title.Contains( query.Text, StringComparison.OrdinalIgnoreCase ) ||
                               project.Path.Contains( query.Text, StringComparison.OrdinalIgnoreCase ) )
            .Take( Math.Clamp( request.Limit, 1, MaximumResults ) );
    }

    private static ProcessExecutionLimits CreateGitLimits( )
    {
        return new ProcessExecutionLimits
        {
            MaximumStandardOutputBytes = MaximumGitOutputBytes,
            MaximumStandardErrorBytes = MaximumGitErrorBytes,
            MaximumMemoryBytes = long.MaxValue,
            ClearEnvironment = false
        };
    }

    private static bool HasGitStatus( ProcessResult git )
    {
        return git is { ExitCode: 0, TimedOut: false };
    }

    private static CommandAction CreateCopyPathAction( string path )
    {
        return new CommandAction
        {
            Id = CopyPathActionId,
            Title = "Copy project path",
            Kind = ActionKinds.CopyText,
            Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Text] = path }
        };
    }

    private static string ResolveProjectPath( ProjectOptions project )
    {
        if ( project.Path.StartsWith( "~/", StringComparison.Ordinal ) )
        {
            return Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), project.Path[2..] );
        }

        return project.Path;
    }

    private static bool HasInvalidLaunchArguments( IReadOnlyList<string> argv )
    {
        return argv.Any( static argument => argument.Contains( '\u001f' ) || argument.Contains( '\0' ) );
    }
}
