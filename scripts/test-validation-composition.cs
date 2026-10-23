#!/usr/bin/env dotnet
#:include ./ScriptExecutables.cs
#:include ../src/CommandDeck.Core/Utilities/EnvironmentVariableNames.cs
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

if ( !OperatingSystem.IsLinux( ) || RuntimeInformation.OSArchitecture != Architecture.X64 )
{
    return SkipUnsupportedPlatform( );
}

var directory = AppContext.GetData( "EntryPointFileDirectoryPath" ) as string;

if ( directory is null )
{
    throw new InvalidOperationException( "Script directory unavailable." );
}

var host = Path.GetFullPath( Path.Combine( RuntimeEnvironment.GetRuntimeDirectory( ), "../../..", ScriptExecutables.Dotnet ) );

var fixture = Directory.CreateTempSubdirectory( "commanddeck-validation-composition-" ).FullName;
var scripts = Directory.CreateDirectory( Path.Combine( fixture, "scripts" ) ).FullName;
var tools = Directory.CreateDirectory( Path.Combine( fixture, "tools" ) ).FullName;
var trace = Path.Combine( fixture, "commands.log" );
var report = Path.Combine( fixture, "build/validation-profile.json" );
var script = Path.Combine( scripts, "validate.cs" );
File.Copy( Path.Combine( directory, "validate.cs" ), script );
File.Copy( Path.Combine( directory, "ScriptExecutables.cs" ), Path.Combine( scripts, "ScriptExecutables.cs" ) );
var utilities = Directory.CreateDirectory( Path.Combine( fixture, "src/CommandDeck.Core/Utilities" ) ).FullName;
File.Copy( Path.Combine( directory, "../src/CommandDeck.Core/Utilities/EnvironmentVariableNames.cs" ), Path.Combine( utilities, "EnvironmentVariableNames.cs" ) );

try
{
    // Replace child executables, not validation itself. The real public profile
    // dispatches its checks and produces the report in an isolated repository.
    foreach ( var tool in new[] { ScriptExecutables.Dotnet, "bash", "bwrap" } )
    {
        var path = Path.Combine( tools, tool );

        await File.WriteAllTextAsync( path, """
            #!/bin/sh
            printf '%s\n' "$*" >>"$VALIDATION_FIXTURE_TRACE"
            if [ "$1" = scripts/validate-repository.sh ] && [ "$VALIDATION_FIXTURE_FAIL_REPOSITORY" = 1 ]; then
              exit 1
            fi
            exit 0
            """ + "\n" );

        File.SetUnixFileMode( path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
    }

    await RunAsync( failRepository: false );
    var commands = await File.ReadAllLinesAsync( trace );

    string[] requiredCommands =
    [
        "scripts/test-validation.cs",
        "scripts/validate-repository.sh",
        "scripts/test-extension-testing.cs",
        "scripts/test-extension-sdk.cs",
        "scripts/test-extension-sandbox.cs",
        "scripts/publish-aot.sh linux-x64",
        "run --project tests/CommandDeck.Specs --configuration Release --no-build -- --native-index-aot artifacts/publish/linux-x64/commanddeck",
        "run --project tests/CommandDeck.Specs --configuration Release --no-build -- --clipboard-native-aot artifacts/publish/linux-x64/commanddeck"
    ];
    if ( !commands.SequenceEqual( requiredCommands ) )
    {
        throw new InvalidOperationException( "Linux validation must run the repository gate once and retain every extension and Native AOT gate: " +
            string.Join( "; ", commands ) );
    }

    await RequireChecksAsync( "passed", "repository", "author-test-kit", "independent-sdk-consumer", "host-child-isolation",
        "native-aot-publish-and-smoke", "native-aot-index-watchers", "native-aot-clipboard-idle-and-lifecycle" );

    File.Delete( trace );
    await RunAsync( failRepository: true );
    commands = await File.ReadAllLinesAsync( trace );

    if ( !commands.SequenceEqual( requiredCommands.Take( 2 ) ) )
    {
        throw new InvalidOperationException( "A failed repository gate must stop extension and Native AOT execution." );
    }

    await RequireChecksAsync( "failed", "repository" );

    Console.WriteLine( "PASS Linux validation runs common checks once, preserves extension/AOT coverage, and stops after a common failure" );

    return 0;
}
finally
{
    Directory.Delete( fixture, recursive: true );
}

async Task RunAsync( bool failRepository )
{
    ProcessStartInfo start = CreateValidationStartInfo( host, fixture );

    foreach ( var argument in new[] { "run", "--file", script, "--", "linux" } )
    {
        start.ArgumentList.Add( argument );
    }

    start.Environment[CommandDeck.EnvironmentVariableNames.ExecutablePath] = tools;
    start.Environment["OMARCHY_PATH"] = Path.Combine( fixture, "missing-omarchy" );
    start.Environment["VALIDATION_FIXTURE_TRACE"] = trace;
    start.Environment["VALIDATION_FIXTURE_FAIL_REPOSITORY"] = failRepository ? "1" : "0";
    using var process = Process.Start( start );
    if ( process is null )
    {
        throw new InvalidOperationException( "Could not start validation composition fixture." );
    }

    var stdout = process.StandardOutput.ReadToEndAsync( );
    var stderr = process.StandardError.ReadToEndAsync( );
    await process.WaitForExitAsync( );
    var expected = failRepository ? 1 : 0;
    if ( process.ExitCode != expected )
    {
        throw new InvalidOperationException( $"Expected Linux validation to exit {expected}; got {process.ExitCode}: {await stdout}{await stderr}" );
    }
}

async Task RequireChecksAsync( string status, params string[] names )
{
    var result = JsonNode.Parse( await File.ReadAllTextAsync( report ) );
    if ( result is null )
    {
        throw new InvalidDataException( "The validation report is empty." );
    }

    foreach ( var name in names )
    {
        if ( IsExpectedCheckMissing( status, result, name ) )
        {
            throw new InvalidOperationException( $"Linux validation did not record {name} as {status}." );
        }
    }
}

static ProcessStartInfo CreateValidationStartInfo( string host, string fixture )
{
    return new( host )
    {
        WorkingDirectory = fixture,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
}

static int SkipUnsupportedPlatform( )
{
    Console.WriteLine( "SKIP Linux validation composition requires a native x64 Linux host" );

    return 0;
}

static bool IsExpectedCheckMissing( string status, JsonNode result, string name )
{
    return !result["checks"]!
        .AsArray( )
        .Any( check => ( string? ) check!["name"] == name && ( string? ) check["status"] == status );
}
