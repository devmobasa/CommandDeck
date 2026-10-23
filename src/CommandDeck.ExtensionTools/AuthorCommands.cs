using System.Diagnostics;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.ExtensionTools;

public static class AuthorCommands
{
    private const string HelpCommand = "help";
    private const string HelpOption = "--help";
    private const string UpdateCommand = "update";
    private const string AttachCommand = "attach";
    private const string ReloadCommand = "reload";
    private const string DirectoryOption = "--directory";
    private const string NewCommand = "new";
    private const string IdOption = "--id";
    private const string NameOption = "--name";
    private const string FeedOption = "--feed";
    private const string CheckCommand = "check";
    private const string BuildCommand = "build";
    private const string PackCommand = "pack";
    private const string DevCommand = "dev";
    private const string OutputOption = "--output";
    private const string DotnetExecutable = "dotnet";
    private const string BuildDirectoryName = ".commanddeck";
    private const string PublishDirectoryPrefix = "publish-";
    private const string YesOption = "--yes";
    private const string ArchiveOption = "--archive";
    private const string DigestOption = "--digest";
    private const string AllowOption = "--allow";
    private const string SocketOption = "--socket";
    private const string HostOption = "--host";

    public static async Task<int> RunAsync( string[] args, CancellationToken cancellationToken )
    {
        var command = args.FirstOrDefault( ) ?? HelpCommand;
        if ( command is HelpCommand or HelpOption )
        {
            Console.WriteLine(
                "commanddeck-ext new --directory DIR --id org.example.draft --name NAME --feed DIR\ncommanddeck-ext check|build --directory DIR\ncommanddeck-ext pack|dev --directory DIR --output FILE.cdext\ncommanddeck-ext update|attach|reload --host PATH --archive FILE.cdext --digest SHA256 --yes [--allow capability,...] [--socket PATH]\ndev creates a separate .development identity with a unique build version. Inspect its archive, then attach/reload the reviewed digest. For native debugging use commanddeck extensions debug PACKAGE --command ID --digest SHA256 --seconds 300 --yes; open the command through its explicit extension filter.\nBuild and pack execute your project's MSBuild targets. Check only validates the manifest and project layout. Update installs the reviewed archive through the daemon; it does not enable or open it." );

            return 0;
        }

        var options = Parse( args
            .Skip( 1 )
            .ToArray( ) );

        if ( command is UpdateCommand or AttachCommand or ReloadCommand )
        {
            return await UpdateAsync( options, command != UpdateCommand, cancellationToken )
                .ConfigureAwait( false );
        }

        var directory = Path.GetFullPath( Required( options, DirectoryOption ) );

        switch ( command )
        {
            case NewCommand:
                AuthorProject.Create( directory, Required( options, IdOption ), Required( options, NameOption ), Required( options, FeedOption ) );

                Console.WriteLine( "Created " + directory );

                return 0;
            case CheckCommand:
                var manifest = AuthorProject.Check( directory );

                Console.WriteLine( $"Valid manifest: {manifest.Id} {manifest.Version}. Build and daemon archive inspection are separate checks." );

                return 0;
            case BuildCommand:
            case PackCommand:
            case DevCommand:
                var output = command != BuildCommand ? Path.GetFullPath( Required( options, OutputOption ) ) : null;

                if ( output is not null && File.Exists( output ) )
                {
                    throw new IOException( "The output archive already exists." );
                }

                var publish = await BuildAsync( directory, cancellationToken )
                    .ConfigureAwait( false );

                if ( output is not null )
                {
                    var archiveManifest = Path.Combine( directory, ExtensionPackageFiles.Manifest );
                    if ( command == DevCommand )
                    {
                        archiveManifest = DevelopmentArtifact.CreateManifest( directory, publish );
                    }

                    ExtensionPackageBuilder.Pack( archiveManifest, publish, output );
                    Console.WriteLine( "Packed " + output + "; inspect this archive with the daemon before updating." );
                }

                return 0;
            default:
                throw new ArgumentException( "Unknown author command: " + command );
        }
    }

    private static async Task<string> BuildAsync( string directory, CancellationToken cancellationToken )
    {
        _ = AuthorProject.Check( directory );

        var output = Path.Combine( directory, BuildDirectoryName, PublishDirectoryPrefix +
                                                                  Guid
                                                                      .NewGuid( )
                                                                      .ToString( "N" ) );

        var code = await RunProcessAsync( DotnetExecutable, directory, [
                "publish", AuthorProject.ProjectPath( directory ), "-c", "Release", "-r", ExtensionTargets.LinuxX64, "--self-contained", "true",
                "-p:PublishAot=true", "-o", output
            ], cancellationToken )
            .ConfigureAwait( false );

        if ( code != 0 )
        {
            throw new InvalidOperationException( "Extension publish failed; no package was created." );
        }

        Console.WriteLine( "Published " + output );

        return output;
    }

    private static Task<int> UpdateAsync( Dictionary<string, string> options, bool development, CancellationToken cancellationToken )
    {
        if ( !options.ContainsKey( YesOption ) )
        {
            throw new InvalidOperationException( "Inspect the archive with the daemon, review its digest and grants, then repeat with --yes." );
        }

        List<string> args =
        [
            "extensions", development ? AttachCommand : "install", ArchiveOption, Path.GetFullPath( Required( options, ArchiveOption ) ), DigestOption,
            Required( options, DigestOption ), YesOption
        ];

        foreach ( var key in new[] { AllowOption, SocketOption } )
        {
            if ( options.TryGetValue( key, out var value ) )
            {
                args.Add( key );
                args.Add( value );
            }
        }

        return RunProcessAsync( Path.GetFullPath( Required( options, HostOption ) ), Environment.CurrentDirectory, args, cancellationToken );
    }

    private static Dictionary<string, string> Parse( string[] args )
    {
        Dictionary<string, string> options = new( StringComparer.Ordinal );

        for ( var index = 0; index < args.Length; index++ )
        {
            var key = args[index];
            if ( key is not (DirectoryOption
                or IdOption
                or NameOption
                or FeedOption
                or OutputOption
                or HostOption
                or ArchiveOption
                or DigestOption
                or AllowOption
                or SocketOption
                or YesOption) )
            {
                throw new ArgumentException( "Unknown option: " + key );
            }

            var value = ReadOptionValue( args, key, ref index );
            if ( !options.TryAdd( key, value ) )
            {
                throw new ArgumentException( "Repeated option: " + key );
            }
        }

        return options;
    }

    private static string ReadOptionValue( string[] args, string key, ref int index )
    {
        if ( key == YesOption )
        {
            return "true";
        }

        if ( ++index < args.Length )
        {
            return args[index];
        }

        throw new ArgumentException( "Missing value: " + key );
    }

    private static string Required( Dictionary<string, string> options, string key ) =>
        options.TryGetValue( key, out var value ) && !string.IsNullOrWhiteSpace( value ) ? value : throw new ArgumentException( "Required option: " + key );

    private static async Task<int> RunProcessAsync( string executable, string directory, IEnumerable<string> args, CancellationToken cancellationToken )
    {
        var start = new ProcessStartInfo( executable ) { WorkingDirectory = directory, UseShellExecute = false };

        foreach ( var arg in args )
        {
            start.ArgumentList.Add( arg );
        }

        using var process = Process.Start( start );
        if ( process is null )
        {
            throw new InvalidOperationException( "Could not start " + executable );
        }

        try
        {
            await process
                .WaitForExitAsync( cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( OperationCanceledException )
        {
            if ( !process.HasExited )
            {
                process.Kill( true );
            }

            await process
                .WaitForExitAsync( CancellationToken.None )
                .ConfigureAwait( false );

            throw;
        }

        return process.ExitCode;
    }
}
