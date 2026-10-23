using System.Security.Cryptography;
using System.Text.Json;

namespace CommandDeck;

public sealed class ScriptCommandProvider( IReadOnlyList<string> directories ) : ICommandProvider
{
    private const int MaximumDirectories = 16;
    private const int MaximumInputs = 16;
    private const int MaximumArguments = 64;
    private const int MaximumInputNameCharacters = 64;
    private const int MaximumErrorBytes = 16 * ByteUnits.BytesPerKibibyte;

    private const string ManifestPattern = "*.commanddeck.json";
    private const string ManifestArgument = "manifest";
    private const int MaximumOutputBytes = 64 * ByteUnits.BytesPerKibibyte;
    private const int MaximumManifestBytes = 64 * ByteUnits.BytesPerKibibyte;
    private const string DigestArgument = "digest";
    private const string ResultIdNamespace = "script";
    private const string RunActionId = "run";
    private const int MaximumScripts = 100;
    private const int MaximumTimeoutSeconds = 300;
    private const int ResultScore = 2;

    public string Id => ProviderIds.Scripts;

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        if ( !query.IncludedFilters.Any( static filter => filter.SourceId == SourceIds.Scripts ) )
        {
            return [];
        }

        List<SearchResult> results = [];

        foreach ( var directory in directories.Take( MaximumDirectories ) )
        {
            var expanded = PathExpansion.Expand( directory );
            if ( !Directory.Exists( expanded ) )
            {
                continue;
            }

            string[] files;

            try
            {
                files = Directory
                    .EnumerateFiles( expanded, ManifestPattern )
                    .Take( MaximumScripts )
                    .ToArray( );
            }
            catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
            {
                continue;
            }

            foreach ( var file in files )
            {
                cancellationToken.ThrowIfCancellationRequested( );

                try
                {
                    var (manifest, digest) = await ReadAsync( file, cancellationToken )
                        .ConfigureAwait( false );

                    if ( DoesNotMatchQuery( query, manifest ) )
                    {
                        continue;
                    }

                    results.Add( CreateScriptResult( file, manifest, digest ) );

                    if ( results.Count >= Math.Clamp( request.Limit, 1, MaximumScripts ) )
                    {
                        return results;
                    }
                }
                catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException )
                {
                    // A broken manifest must not prevent other commands from being discovered.
                }
            }
        }

        return results;
    }

    public static async ValueTask<ActionExecutionResult> ExecuteAsync( CommandAction action, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( action );

        var path = action.Arguments[ManifestArgument];

        var (manifest, digest) = await ReadAsync( path, cancellationToken )
            .ConfigureAwait( false );

        if ( digest != action.Arguments[DigestArgument] )
        {
            return ActionExecutionResult.Failure( "The script manifest changed. Search again before running it." );
        }

        var directory = Path.GetDirectoryName( path )!;
        var executable = ResolveExecutable( manifest, directory );

        var arguments = manifest.Arguments.Select( argument => ExpandArgument( argument, manifest.Inputs, action.Arguments ) );

        var result = await ProcessRunner
            .RunBoundedAsync( executable, arguments, ResolveWorkingDirectory( manifest.WorkingDirectory, directory ),
                TimeSpan.FromSeconds( manifest.TimeoutSeconds ), CreateExecutionLimits( ), cancellationToken )
            .ConfigureAwait( false );

        var output = result.StandardOutput + (result.StandardError.Length > 0 ? "\n\nstderr:\n" + result.StandardError : "");
        if ( output.Length == 0 )
        {
            output = "(No output)";
        }

        var exceeded = result.OutputLimitExceeded || result.StandardErrorLimitExceeded;
        var message = DescribeExecution( result, exceeded );

        return CreateExecutionResult( result, output, exceeded, message );
    }

    private static string ResolveWorkingDirectory( string? configured, string manifestDirectory )
    {
        if ( configured is null )
        {
            return manifestDirectory;
        }

        return ResolveScriptDirectory( configured, manifestDirectory );
    }

    private static string ExpandArgument( string argument, List<ActionInput> inputs, Dictionary<string, string> values )
    {
        // Whole-argument substitution keeps user input in one argv entry, including empty/spaced values.
        var input = inputs.FirstOrDefault( input => argument == "{" + input.Name + "}" );

        return input is null ? argument : values[ActionArgumentNames.InputPrefix + input.Name];
    }

    private static async Task<(ScriptCommandManifest Manifest, string Digest)> ReadAsync( string path, CancellationToken cancellationToken )
    {
        await using var stream = File.OpenRead( path );
        if ( stream.Length > MaximumManifestBytes )
        {
            throw new InvalidOperationException( "Script manifest exceeds 64 KiB." );
        }

        var bytes = new byte[checked(( int ) stream.Length)];

        await stream
            .ReadExactlyAsync( bytes, cancellationToken )
            .ConfigureAwait( false );

        var manifest = JsonSerializer.Deserialize( bytes, CommandDeckJsonContext.Default.ScriptCommandManifest );
        if ( manifest is null )
        {
            throw new InvalidOperationException( "Empty script manifest." );
        }

        Validate( manifest );

        return (manifest, Convert.ToHexString( SHA256.HashData( bytes ) ));
    }

    private static void Validate( ScriptCommandManifest manifest )
    {
        if ( IsInvalidManifest( manifest ) )
        {
            throw new InvalidOperationException( "Invalid script command manifest." );
        }

        HashSet<string> names = new( StringComparer.Ordinal );

        foreach ( var input in manifest.Inputs )
        {
            if ( !TryValidateAndAddInput( names, input ) )
            {
                throw new InvalidOperationException( "Invalid script input names." );
            }
        }
    }

    private static bool DoesNotMatchQuery( ParsedQuery query, ScriptCommandManifest manifest )
    {
        return !manifest.Title.Contains( query.Text, StringComparison.OrdinalIgnoreCase ) &&
               !manifest.Description.Contains( query.Text, StringComparison.OrdinalIgnoreCase );
    }

    private static SearchResult CreateScriptResult( string file, ScriptCommandManifest manifest, string digest )
    {
        return new SearchResult
        {
            Id = StableId.Create( ResultIdNamespace, file ),
            Title = manifest.Title,
            Subtitle = manifest.Description,
            DetailMarkdown = manifest.Description + "\n\n" + file,
            Kind = ResultKinds.CustomCommand,
            SourceId = SourceIds.Scripts,
            ProviderId = ProviderIds.Scripts,
            Icon = "utilities-terminal",
            Score = ResultScore,
            Section = ResultSections.Scripts,
            Actions =
            [
                new CommandAction
                {
                    Id = RunActionId,
                    Title = "Run script",
                    Kind = ActionKinds.RunScript,
                    Inputs = manifest.Inputs,
                    RequiresConfirmation = true,
                    Arguments = new( StringComparer.Ordinal ) { [ManifestArgument] = file, [DigestArgument] = digest }
                }
            ]
        };
    }

    private static string ResolveExecutable( ScriptCommandManifest manifest, string directory )
    {
        if ( Path.IsPathFullyQualified( manifest.Executable ) )
        {
            return manifest.Executable;
        }

        return Path.GetFullPath( manifest.Executable, directory );
    }

    private static ProcessExecutionLimits CreateExecutionLimits( )
    {
        return new ProcessExecutionLimits
        {
            MaximumStandardOutputBytes = MaximumOutputBytes,
            MaximumStandardErrorBytes = MaximumErrorBytes,
            MaximumMemoryBytes = long.MaxValue,
            ClearEnvironment = false
        };
    }

    private static string DescribeExecution( ProcessResult result, bool exceeded )
    {
        if ( result.TimedOut )
        {
            return "Script timed out";
        }

        if ( exceeded )
        {
            return "Script exceeded its output limit";
        }

        return "Script exited with code " + result.ExitCode;
    }

    private static ActionExecutionResult CreateExecutionResult( ProcessResult result, string output, bool exceeded, string message )
    {
        if ( result is { ExitCode: 0, TimedOut: false } && !exceeded )
        {
            return ActionExecutionResult.Success( message, output );
        }

        return ActionExecutionResult.Failure( message, output );
    }

    private static string ResolveScriptDirectory( string configured, string manifestDirectory )
    {
        if ( IsExpandedDirectory( configured ) )
        {
            return PathExpansion.Expand( configured );
        }

        return Path.GetFullPath( configured, manifestDirectory );
    }

    private static bool IsInvalidManifest( ScriptCommandManifest manifest )
    {
        return manifest.Description is null ||
               string.IsNullOrWhiteSpace( manifest.Title ) ||
               string.IsNullOrWhiteSpace( manifest.Executable ) ||
               manifest.Executable.Contains( '\0' ) ||
               manifest.TimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
               manifest.Arguments is null ||
               manifest.Arguments.Count > MaximumArguments ||
               manifest.Arguments.Any( static argument => argument is null || argument.Contains( '\0' ) ) ||
               manifest.Inputs is null ||
               manifest.Inputs.Count > MaximumInputs;
    }

    private static bool TryValidateAndAddInput( HashSet<string> names, ActionInput? input )
    {
        return input is not null &&
               !string.IsNullOrWhiteSpace( input.Name ) &&
               input.Name.Length <= MaximumInputNameCharacters &&
               input.Name.All( static value => char.IsAsciiLetterOrDigit( value ) || value is '-' or '_' ) &&
               names.Add( input.Name );
    }

    private static bool IsExpandedDirectory( string configured )
    {
        return Path.IsPathFullyQualified( configured ) || configured.StartsWith( '~' ) || configured.StartsWith( '$' );
    }
}
