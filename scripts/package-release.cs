#!/usr/bin/env dotnet
#:include ../src/CommandDeck.Core/Utilities/ByteUnits.cs
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

[assembly: SupportedOSPlatform( "linux" )]

// Packages a Native AOT publish output as a release archive that installs
// without .NET. The release workflow runs it after ./scripts/publish-aot.sh.
return await ReleasePackage.RunAsync( args );

static class ReleasePackage
{
    private const string PublishDirectoryOption = "--publish-dir";
    private const string OutputDirectoryOption = "--output";
    private const string Usage =
        "Usage: dotnet scripts/package-release.cs linux-x64|linux-arm64 VERSION [--publish-dir DIR] [--output DIR]";
    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode Readable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    // install.sh calls these by their repository-relative path.
    private static readonly string[] InstallerScripts = ["install-local.sh", "install-omarchy-plugin.sh", "reset-local-config.sh"];

    public static async Task<int> RunAsync( string[] args )
    {
        var options = ReadOptions( args );
        if ( options is null )
        {
            Console.Error.WriteLine( Usage );

            return 2;
        }

        var scripts = AppContext.GetData( "EntryPointFileDirectoryPath" ) as string;
        if ( scripts is null )
        {
            throw new InvalidOperationException( "The script directory is unavailable." );
        }

        var repository = Path.GetFullPath( Path.Combine( scripts, ".." ) );
        var (rid, version) = (args[0], args[1]);

        var publish = Path.GetFullPath( options.GetValueOrDefault( PublishDirectoryOption ) ?? Path.Combine( repository, "artifacts", "publish", rid ) );
        var output = Path.GetFullPath( options.GetValueOrDefault( OutputDirectoryOption ) ?? Path.Combine( repository, "artifacts", "release" ) );

        if ( !File.Exists( Path.Combine( publish, "commanddeck" ) ) || !File.Exists( Path.Combine( publish, "picker", "shell.qml" ) ) )
        {
            Console.Error.WriteLine( $"{publish} has no commanddeck binary and picker. Publish first: ./scripts/publish-aot.sh {rid}" );

            return 2;
        }

        var name = $"commanddeck-{version}-{rid}";
        var stage = Path.Combine( output, name );
        if ( Directory.Exists( stage ) )
        {
            Directory.Delete( stage, recursive: true );
        }

        StageRelease( repository, publish, stage );
        var archive = Path.Combine( output, name + ".tar.gz" );
        File.Delete( archive );
        // Fixed order, owners, and times make the archive reproducible.
        await RunAsync( "tar", "--sort=name", "--owner=0", "--group=0", "--numeric-owner",
            $"--mtime=@{Environment.GetEnvironmentVariable( "SOURCE_DATE_EPOCH" ) ?? "0"}", "-czf", archive, "-C", output, name );

        Directory.Delete( stage, recursive: true );
        await using var stream = File.OpenRead( archive );
        var hash = Convert.ToHexStringLower( await SHA256.HashDataAsync( stream ) );
        await File.WriteAllTextAsync( archive + ".sha256", $"{hash}  {name}.tar.gz\n" );
        Console.WriteLine( $"{archive} ({stream.Length / (double) CommandDeck.ByteUnits.BytesPerMebibyte:F1} MiB)" );
        Console.WriteLine( $"{archive}.sha256" );

        return 0;
    }

    private static void StageRelease( string repository, string publish, string stage )
    {
        // The release keeps the repository layout that install.sh and its
        // helpers resolve: qml/, packaging/systemd/, and scripts/.
        Copy( Path.Combine( publish, "commanddeck" ), Path.Combine( stage, "bin", "commanddeck" ), Executable );

        var schema = Path.Combine( publish, "commanddeck.schema.json" );
        if ( File.Exists( schema ) )
        {
            Copy( schema, Path.Combine( stage, "bin", "commanddeck.schema.json" ), Readable );
        }

        foreach ( var library in Directory.EnumerateFiles( publish, "*.so" ) )
        {
            Copy( library, Path.Combine( stage, "bin", Path.GetFileName( library ) ), Executable );
        }

        CopyTree( Path.Combine( publish, "picker" ), Path.Combine( stage, "bin", "picker" ) );
        CopyTree( Path.Combine( repository, "qml" ), Path.Combine( stage, "qml" ) );

        Copy( Path.Combine( repository, "packaging", "systemd", "commanddeck.service" ),
            Path.Combine( stage, "packaging", "systemd", "commanddeck.service" ), Readable );

        Copy( Path.Combine( repository, "scripts", "install.sh" ), Path.Combine( stage, "install.sh" ), Executable );

        foreach ( var script in InstallerScripts )
        {
            Copy( Path.Combine( repository, "scripts", script ), Path.Combine( stage, "scripts", script ), Executable );
        }

        Copy( Path.Combine( repository, "LICENSE" ), Path.Combine( stage, "LICENSE" ), Readable );
        Copy( Path.Combine( repository, "packaging", "release", "INSTALL.txt" ), Path.Combine( stage, "INSTALL.txt" ), Readable );
    }

    private static Dictionary<string, string>? ReadOptions( string[] args )
    {
        if ( HasInvalidArguments( args ) )
        {
            return null;
        }

        Dictionary<string, string> options = [];

        for ( var index = 2; index < args.Length; index += 2 )
        {
            if ( args[index] is not (PublishDirectoryOption or OutputDirectoryOption) || !options.TryAdd( args[index], args[index + 1] ) )
            {
                return null;
            }
        }

        return options;
    }

    private static void CopyTree( string source, string destination )
    {
        foreach ( var file in Directory.EnumerateFiles( source, "*", SearchOption.AllDirectories ) )
        {
            Copy( file, Path.Combine( destination, Path.GetRelativePath( source, file ) ), Readable );
        }
    }

    private static void Copy( string source, string destination, UnixFileMode mode )
    {
        var directory = Path.GetDirectoryName( destination );
        if ( directory is null )
        {
            throw new InvalidOperationException( $"{destination} has no parent directory." );
        }

        Directory.CreateDirectory( directory );
        File.Copy( source, destination );
        File.SetUnixFileMode( destination, mode );
    }

    private static async Task RunAsync( string fileName, params string[] arguments )
    {
        using var process = Process.Start( fileName, arguments );
        await process.WaitForExitAsync( );

        if ( process.ExitCode != 0 )
        {
            throw new InvalidOperationException( $"{fileName} exited with {process.ExitCode}." );
        }
    }

    private static bool HasInvalidArguments( string[] args )
    {
        return args.Length < 2 || args.Length % 2 != 0 || args[0] is not ("linux-x64" or "linux-arm64") ||
                    args[1].Length == 0 || !args[1].All( character => char.IsAsciiLetterOrDigit( character ) || character is '.' or '-' or '_' );
    }
}
