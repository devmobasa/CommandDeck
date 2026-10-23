#!/usr/bin/env dotnet
#:include ./ScriptExecutables.cs
#:include ../src/CommandDeck.Core/Utilities/NativeLibraryNames.cs
#:include ../src/CommandDeck.Core/Extensions/ExtensionProbeFields.cs
#:include ../src/CommandDeck.Core/Configuration/LinuxCgroupPaths.cs
#:include ../src/CommandDeck.Core/Extensions/ExtensionProbeContract.cs
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

return await SandboxJourney.RunAsync( args );

static class SandboxJourney
{
    public static async Task<int> RunAsync( string[] args )
    {
        var entryPoint = AppContext.GetData( "EntryPointFilePath" ) as string;
        if ( entryPoint is null )
        {
            throw new InvalidOperationException( "The script path is unavailable." );
        }

        var scriptDirectory = Path.GetDirectoryName( entryPoint );
        if ( scriptDirectory is null )
        {
            throw new InvalidOperationException( "The script directory is unavailable." );
        }

        var repository = Path.GetFullPath( Path.Combine( scriptDirectory, ".." ) );
        var artifacts = Path.Combine( repository, "artifacts/extensions" );
        var isolatedLock = $"-p:NuGetLockFilePath={Path.Combine( artifacts, ".sandbox-" + Guid
            .NewGuid( )
            .ToString( "N" ) + ".lock.json" )}";

        var (reuseArtifacts, samples) = ReadArguments( args );

        Check( RuntimeInformation.OSArchitecture == Architecture.X64, "The initial tested runtime profile is linux-x64" );

        if ( !reuseArtifacts )
        {
            await RunCheckedAsync(
                ScriptExecutables.Dotnet,
                [
                    "publish", "tests/CommandDeck.ExtensionProbe", "-c", "Release", "-r", "linux-x64",
                    "--self-contained", "true", "-p:PublishAot=false", "-p:RestorePackagesWithLockFile=false",
                    isolatedLock, "-o", Path.Combine( artifacts, "probe-managed" )
                ],
                repository );

            await RunCheckedAsync(
                ScriptExecutables.Dotnet,
                [
                    "publish", "tests/CommandDeck.ExtensionProbe", "-c", "Release", "-r", "linux-x64",
                    "--self-contained", "true", "-p:RestorePackagesWithLockFile=false", isolatedLock,
                    "-o", Path.Combine( artifacts, "probe-aot" )
                ],
                repository );

            await RunCheckedAsync(
                ScriptExecutables.Dotnet,
                [
                    "publish", "src/CommandDeck.App", "-c", "Release", "-r", "linux-x64",
                    "--self-contained", "true", "-p:RestorePackagesWithLockFile=false", isolatedLock,
                    "-o", Path.Combine( artifacts, "host-aot" )
                ],
                repository );

            await RunCheckedAsync(
                ScriptExecutables.Dotnet,
                ["restore", "CommandDeck.slnx", "-p:RestorePackagesWithLockFile=false", isolatedLock],
                repository );
        }

        var binary = Path.Combine( artifacts, "host-aot/commanddeck" );
        // A native publish has no adjacent managed entry assembly. Do not infer AOT from a feature switch alone.
        Check( !File.Exists( Path.ChangeExtension( binary, ".dll" ) ), "The host is not a Native AOT publish." );

        JsonArray measurements = [];

        foreach ( var deployment in new[] { "managed", "aot" } )
        {
            var package = Path.Combine( artifacts, "probe-" + deployment );

            Check(
                File.Exists( Path.Combine( package, "CommandDeck.ExtensionProbe.dll" ) ) == (deployment == "managed"),
                "The child deployment profile is invalid." );

            for ( var index = 0; index < samples; index++ )
            {
                measurements.Add( await ProbeAsync( binary, package, repository, deployment, "warm" ) );
            }

            var coldRoot = Directory.CreateTempSubdirectory( "commanddeck-extension-cold-" ).FullName;

            try
            {
                // File-page eviction is advisory. This does not claim cold system libraries or kernel caches.
                var cold = Path.Combine( coldRoot, "package" );
                CopyDirectory( package, cold );

                foreach ( var file in Directory.EnumerateFiles( cold, "*", SearchOption.AllDirectories ) )
                {
                    using FileStream stream = new( file, FileMode.Open, FileAccess.Read, FileShare.Read );
                    var fileDescriptor = stream.SafeFileHandle
                        .DangerousGetHandle( )
                        .ToInt32( );

                    Check( NativeMethods.Fsync( fileDescriptor ) == 0, "Could not synchronize the cold-package fixture." );
                    Check( NativeMethods.PosixFadvise( fileDescriptor, 0, 0, 4 ) == 0, "Could not evict the cold-package fixture pages." );
                }

                measurements.Add( await ProbeAsync( binary, cold, repository, deployment, "advised-cold-package-pages" ) );
            }
            finally
            {
                Directory.Delete( coldRoot, recursive: true );
            }
        }

        var lifecycle = await OwnerLossAndSiblingAsync( binary, Path.Combine( artifacts, "probe-aot" ), repository );

        var kernel = await RunAsync( "/usr/bin/uname", ["-r"], repository, TimeSpan.FromSeconds( 5 ) );
        Check( kernel.ExitCode == 0, kernel.StandardError );
        await using var binaryInput = File.OpenRead( binary );
        var hostDigest = Convert.ToHexStringLower( await SHA256.HashDataAsync( binaryInput ) );
        var report = CreateReport( measurements, lifecycle, kernel, hostDigest );
        var output = Path.Combine( artifacts, "sandbox-evidence.json" );

        await File.WriteAllTextAsync( output, report.ToJsonString( new( ) { WriteIndented = true } ) + "\n" );

        foreach ( var deployment in new[] { "managed", "aot" } )
        {
            var timings = measurements
                .Select( value => value!.AsObject( ) )
                .Where( value => value["deployment"]!.GetValue<string>( ) == deployment && value["cache"]!.GetValue<string>( ) == "warm" )
                .Select( value => value["initialViewMilliseconds"]!.GetValue<double>( ) )
                .Order( )
                .ToArray( );

            Console.WriteLine(
                $"PASS AOT host + {deployment} child: {timings.Length} warm samples, " +
                $"median {timings[timings.Length / 2]:F1} ms; direct access denied, nested RPC and cleanup passed" );
        }

        Console.WriteLine( "PASS SIGKILL owner loss cleans descendants and preserves sibling session" );
        Console.WriteLine( $"Evidence: {output}" );

        return 0;
    }

    static (bool ReuseArtifacts, int Samples) ReadArguments( string[] arguments )
    {
        var reuseArtifacts = false;
        var samples = 5;

        for ( var index = 0; index < arguments.Length; index++ )
        {
            var argument = arguments[index];
            if ( argument == "--reuse-artifacts" )
            {
                reuseArtifacts = true;

                continue;
            }

            if ( argument.StartsWith( "--samples=", StringComparison.Ordinal ) )
            {
                samples = ParseSamples( argument["--samples=".Length..] );

                continue;
            }

            if ( HasSampleArgument( arguments, index, argument ) )
            {
                samples = ParseSamples( arguments[++index] );

                continue;
            }

            throw new ArgumentException( $"Unknown or incomplete argument: {argument}" );
        }

        return (reuseArtifacts, samples);
    }

    static int ParseSamples( string value )
    {
        if ( !TryReadSampleCount( value, out var result ) )
        {
            throw new ArgumentException( "--samples must be between 1 and 50." );
        }

        return result;
    }

    static async Task<JsonObject> ProbeAsync( string binary, string package, string repository, string deployment, string cache )
    {
        var environment = new Dictionary<string, string?>( StringComparer.Ordinal ) { [CommandDeck.ExtensionProbeContract.SecretEnvironmentVariable] = "synthetic-do-not-inherit" };

        var result = await RunAsync( binary, ["extension-probe", "--artifact", package], repository, TimeSpan.FromMinutes( 5 ), environment );

        Check( result.ExitCode == 0, result.StandardError );
        var report = JsonNode.Parse( result.StandardOutput ) as JsonObject;
        if ( report is null )
        {
            throw new InvalidDataException( "The extension probe returned invalid JSON." );
        }

        Check( report["hostAot"]!.GetValue<bool>( ) && report["descendantsStopped"]!.GetValue<bool>( ), result.StandardOutput );
        Check( report["cgroupTasks"]!.GetValue<int>( ) > 10, result.StandardOutput );
        report["deployment"] = deployment;
        report["cache"] = cache;

        return report;
    }

    static async Task<(Process Process, string ControlGroup)> StartHeldProbeAsync( string binary, string package, string repository )
    {
        ProcessStartInfo start = CreateProcessStartInfo( binary, repository );

        foreach ( var argument in new[] { "extension-probe", "--artifact", package, "--hold" } )
        {
            start.ArgumentList.Add( argument );
        }

        var child = Process.Start( start );
        if ( child is null )
        {
            throw new InvalidOperationException( "The held extension probe did not start." );
        }

        try
        {
            using CancellationTokenSource deadline = new( TimeSpan.FromSeconds( 10 ) );
            var line = await child.StandardOutput.ReadLineAsync( deadline.Token );
            if ( IsInvalidReadyLine( line ) )
            {
                throw new InvalidOperationException( line ?? await child.StandardError.ReadToEndAsync( ) );
            }

            return (child, CommandDeck.LinuxCgroupPaths.RootDirectory + line[CommandDeck.ExtensionProbeContract.ReadyPrefix.Length..]);
        }
        catch
        {
            child.Kill( entireProcessTree: true );
            await child.WaitForExitAsync( );
            child.Dispose( );

            throw;
        }
    }

    static async Task<JsonObject> OwnerLossAndSiblingAsync( string binary, string package, string repository )
    {
        Process? first = null;
        Process? second = null;

        try
        {
            (first, var firstGroup) = await StartHeldProbeAsync( binary, package, repository );
            (second, var secondGroup) = await StartHeldProbeAsync( binary, package, repository );
            var firstParent = Directory.GetParent( firstGroup );
            var secondParent = Directory.GetParent( secondGroup );
            if ( IsParentMissing( firstParent, secondParent ) )
            {
                throw new InvalidDataException( "The package resource group was invalid." );
            }

            Check( firstParent.FullName == secondParent.FullName, "Sibling test did not share the package limit" );

            Check(
                (await File.ReadAllTextAsync( Path.Combine( firstParent.FullName, "memory.max" ) )).Trim( ) == "536870912",
                "The package memory limit was not enforced." );

            first.Kill( );

            await first
                .WaitForExitAsync( )
                .WaitAsync( TimeSpan.FromSeconds( 5 ) );

            var timer = Stopwatch.StartNew( );
            while ( Directory.Exists( firstGroup ) && timer.Elapsed < TimeSpan.FromSeconds( 2 ) )
            {
                await Task.Delay( 20 );
            }

            Check( !Directory.Exists( firstGroup ), "Owner loss left descendants alive" );
            Check( !second.HasExited && Directory.Exists( secondGroup ), "Stopping one session killed its sibling" );

            _ = await RunAsync( "/usr/bin/kill", ["-TERM", second.Id.ToString( )], repository, TimeSpan.FromSeconds( 5 ) );

            await second
                .WaitForExitAsync( )
                .WaitAsync( TimeSpan.FromSeconds( 5 ) );

            Check( !Directory.Exists( secondGroup ), "Cooperative owner exit left descendants alive" );

            return new JsonObject { ["ownerLossCleanupMilliseconds"] = timer.Elapsed.TotalMilliseconds, ["siblingSurvived"] = true };
        }
        finally
        {
            foreach ( var child in new[] { first, second } )
            {
                if ( child is null )
                {
                    continue;
                }

                if ( !child.HasExited )
                {
                    child.Kill( entireProcessTree: true );
                    await child.WaitForExitAsync( );
                }

                child.Dispose( );
            }
        }
    }

    static async Task RunCheckedAsync( string executable, IEnumerable<string> arguments, string workingDirectory )
    {
        var result = await RunAsync( executable, arguments, workingDirectory, TimeSpan.FromMinutes( 5 ) );
        Check( result.ExitCode == 0, result.StandardError + result.StandardOutput );
    }

    static async Task<CommandResult> RunAsync( string executable, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null )
    {
        ProcessStartInfo start = CreateProcessStartInfo( executable, workingDirectory );

        foreach ( var argument in arguments )
        {
            start.ArgumentList.Add( argument );
        }

        if ( environment is not null )
        {
            foreach ( var (name, value) in environment )
            {
                start.Environment[name] = value;
            }
        }

        using var process = Process.Start( start );
        if ( process is null )
        {
            throw new InvalidOperationException( $"Could not start {executable}." );
        }

        var stdout = process.StandardOutput.ReadToEndAsync( );
        var stderr = process.StandardError.ReadToEndAsync( );

        try
        {
            await process
                .WaitForExitAsync( )
                .WaitAsync( timeout );
        }
        catch ( TimeoutException )
        {
            process.Kill( entireProcessTree: true );
            await process.WaitForExitAsync( );

            throw;
        }

        return new( process.ExitCode, await stdout, await stderr );
    }

    static void CopyDirectory( string source, string destination )
    {
        Directory.CreateDirectory( destination );

        foreach ( var file in Directory.EnumerateFiles( source ) )
        {
            File.Copy( file, Path.Combine( destination, Path.GetFileName( file ) ) );
        }

        foreach ( var directory in Directory.EnumerateDirectories( source ) )
        {
            CopyDirectory( directory, Path.Combine( destination, Path.GetFileName( directory ) ) );
        }
    }

    static void Check( bool condition, string message )
    {
        if ( !condition )
        {
            throw new InvalidOperationException( message );
        }
    }

    private static JsonObject CreateReport( JsonArray measurements, JsonObject lifecycle, CommandResult kernel, string hostDigest )
    {
        return new JsonObject
        {
            ["kernel"] = kernel.StandardOutput.Trim( ),
            ["machine"] = "x86_64",
            ["hostSha256"] = hostDigest,
            ["samples"] = measurements,
            ["lifecycle"] = lifecycle
        };
    }

    private static ProcessStartInfo CreateProcessStartInfo( string executable, string workingDirectory )
    {
        return new( executable )
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
    }

    private static bool HasSampleArgument( string[] arguments, int index, string argument )
    {
        return argument == "--samples" && index + 1 < arguments.Length;
    }

    private static bool IsInvalidReadyLine( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? line )
    {
        return line is null || !line.StartsWith( CommandDeck.ExtensionProbeContract.ReadyPrefix, StringComparison.Ordinal ) ||
               !line[CommandDeck.ExtensionProbeContract.ReadyPrefix.Length..].StartsWith( "/user.slice/", StringComparison.Ordinal );
    }

    private static bool IsParentMissing( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] DirectoryInfo? firstParent, [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] DirectoryInfo? secondParent )
    {
        return firstParent is null || secondParent is null;
    }
    private static bool TryReadSampleCount( string value, out int result )
    {
        return int.TryParse( value, out result ) && result is >= 1 and <= 50;
    }
}

sealed record CommandResult( int ExitCode, string StandardOutput, string StandardError );

static class NativeMethods
{
    [DllImport( CommandDeck.NativeLibraryNames.LibC, EntryPoint = "fsync" )]
    public static extern int Fsync( int fileDescriptor );

    [DllImport( CommandDeck.NativeLibraryNames.LibC, EntryPoint = "posix_fadvise" )]
    public static extern int PosixFadvise( int fileDescriptor, long offset, long length, int advice );
}
