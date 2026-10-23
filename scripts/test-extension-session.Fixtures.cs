using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

static partial class Journey
{
    private static async Task CheckNavigationAsync( Client client, JsonNode view, string clipboard )
    {
        var session = view["sessionId"]!.GetValue<string>( );

        var detail = FindPublishedView( await client.Call( "extension.action", Input( session, 2, view["revision"]!.GetValue<long>( ),
                    view["content"]!["items"]![0]!["actions"]![1]!["handle"]!.GetValue<string>( ), view["content"]!["items"]![0]!["id"]!.GetValue<string>( ) ) ) );

        Check( detail["content"]!["kind"]!.GetValue<string>( ) == "detail", "Details did not push" );
        Check( detail["content"]!["depth"]!.GetValue<int>( ) == 2, "Missing stack depth" );

        await client.Call( "extension.present", Input( session, 3, detail["revision"]!.GetValue<long>( ) ) );
        await client.Call( "extension.action", Input( session, 4, detail["revision"]!.GetValue<long>( ), detail["content"]!["actions"]![0]!["handle"]!.GetValue<string>( ) ) );

        Check( await File.ReadAllTextAsync( clipboard ) == view["content"]!["items"]![0]!["title"]!.GetValue<string>( ), "Detail copy payload" );

        var parent = FindPublishedView( await client.Call( "extension.action", Input( session, 5, detail["revision"]!.GetValue<long>( ), detail["content"]!["backHandle"]!.GetValue<string>( ) ) ) );

        Check( JsonNode.DeepEquals( parent["content"]!["items"]![0]!["id"], view["content"]!["items"]![0]!["id"] ), "Back lost parent content" );

        await client.Call( "extension.present", Input( session, 6, parent["revision"]!.GetValue<long>( ) ) );
        await client.Call( "extension.action", Input( session, 7, detail["revision"]!.GetValue<long>( ), detail["content"]!["actions"]![0]!["handle"]!.GetValue<string>( ) ), true );

        var page = FindPublishedView( await client.Call( "extension.action", Input( session, 8, parent["revision"]!.GetValue<long>( ), parent["content"]!["nextPage"]!.GetValue<string>( ) ) ) );

        Check( !JsonNode.DeepEquals( page["content"]!["items"]![0]!["id"], parent["content"]!["items"]![0]!["id"] ), "Next page did not replace items" );
        Check( page["content"]!["items"]!.AsArray( ).Count == 8, "Pages were appended" );
        await client.Call( "extension.present", Input( session, 9, page["revision"]!.GetValue<long>( ) ) );
        await client.Call( "extension.action", Input( session, 10, parent["revision"]!.GetValue<long>( ), parent["content"]!["nextPage"]!.GetValue<string>( ) ), true );

        var previous = FindPublishedView( await client.Call( "extension.action", Input( session, 11, page["revision"]!.GetValue<long>( ), page["content"]!["previousPage"]!.GetValue<string>( ) ) ) );

        Check( JsonNode.DeepEquals( previous["content"]!["items"]![0]!["id"], parent["content"]!["items"]![0]!["id"] ), "Previous page lost items" );

        await client.Call( "extension.present", Input( session, 12, previous["revision"]!.GetValue<long>( ) ) );
        await client.Call( "extension.action", Input( session, 13, previous["revision"]!.GetValue<long>( ), previous["content"]!["actions"]![0]!["handle"]!.GetValue<string>( ) ) );
        await client.Call( "extension.close", Input( session, 0, 0 ) );
    }

    public static async Task<string> Exec( string executable, params string[] arguments )
    {
        var start = new ProcessStartInfo( executable ) { RedirectStandardOutput = true, RedirectStandardError = true };

        foreach ( var argument in arguments )
        {
            start.ArgumentList.Add( argument );
        }

        using var process = Process.Start( start )!;
        var output = process.StandardOutput.ReadToEndAsync( );
        var error = process.StandardError.ReadToEndAsync( );
        using var timeout = new CancellationTokenSource( TimeSpan.FromMinutes( 3 ) );

        try
        {
            await process.WaitForExitAsync( timeout.Token );
        }
        catch
        {
            process.Kill( true );

            throw;
        }

        Check( process.ExitCode == 0, $"{executable} {string.Join( ' ', arguments )}: {await error} {await output}" );

        return await output;
    }
    public static async Task Wait( Func<Task<bool>> predicate, string label, int seconds = 8 )
    {
        var deadline = DateTime.UtcNow.AddSeconds( seconds );

        while ( !await predicate( ) )
        {
            Check( DateTime.UtcNow < deadline, label );
            await Task.Delay( 25 );
        }
    }
    static async Task<HashSet<string>> Units( ) => await FindTransientServicesAsync( );
    static async Task ControllerLifecycle( string host, string address, string root, string command )
    {
        var directory = Path.Combine( root, "controller-lifecycle" );
        Directory.CreateDirectory( directory );
        var fixture = Path.Combine( directory, "shell.qml" );
        File.Copy( Path.Combine( Repo, "tests/CommandDeck.ExtensionJourney/lifecycle.qml" ), fixture );

        await Exec( "cp", "-r", Path.Combine( Repo, "qml", "common" ), Path.Combine( directory, "plugin" ) );

        var start = new ProcessStartInfo( "qs" ) { RedirectStandardOutput = true, RedirectStandardError = true };

        foreach ( var argument in new[] { "--no-color", "-p", fixture } )
        {
            start.ArgumentList.Add( argument );
        }

        start.Environment["COMMANDDECK_TEST_HOST"] = host;
        start.Environment["COMMANDDECK_TEST_SOCKET"] = address;
        start.Environment["COMMANDDECK_TEST_COMMAND"] = command;
        using var process = Process.Start( start )!;
        var output = process.StandardOutput.ReadToEndAsync( );
        var errors = process.StandardError.ReadToEndAsync( );
        using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 70 ) );

        try
        {
            await process.WaitForExitAsync( timeout.Token );
        }
        catch
        {
            process.Kill( true );
            await process.WaitForExitAsync( );

            throw;
        }

        var log = await output + await errors;

        Check( process.ExitCode == 0 && log.Contains( "PASS connected controller", StringComparison.Ordinal ), log );

        Console.WriteLine( "PASS connected controller: 20 immediate close/reopen cycles" );
    }
    static string RendererDigest( )
    {
        using var hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
        var root = Path.Combine( Repo, "qml", "common" );
        foreach ( var file in Directory
            .EnumerateFiles( root, "*", SearchOption.AllDirectories )
            .Order( StringComparer.Ordinal ) )
        {
            hash.AppendData( Encoding.UTF8.GetBytes( Path.GetRelativePath( root, file ) + "\0" ) );
            hash.AppendData( File.ReadAllBytes( file ) );
        }

        return Convert.ToHexStringLower( hash.GetHashAndReset( ) );
    }
    static string CreatePackageVariant( string source, string destination, string id, string version, string? executable = null, string? behavior = null )
    {
        using var input = ZipFile.OpenRead( source );
        using var output = ZipFile.Open( destination, ZipArchiveMode.Create );

        foreach ( var original in input.Entries )
        {
            var entry = output.CreateEntry( original.FullName );
            entry.ExternalAttributes = original.ExternalAttributes;
            using var target = entry.Open( );
            using var content = original.Open( );

            if ( entry.FullName == CommandDeck.ExtensionProtocol.ExtensionPackageFiles.Manifest )
            {
                var manifest = JsonNode.Parse( content )!;
                manifest["id"] = id;
                manifest["version"] = version;

                if ( behavior is not null )
                {
                    manifest["commands"]![0]!["title"] = behavior;
                }

                target.Write( Encoding.UTF8.GetBytes( manifest.ToJsonString( ) ) );
            }
            else if ( ShouldReplaceExecutable( executable, entry ) )
            {
                using var binary = File.OpenRead( executable );
                binary.CopyTo( target );
            }
            else
            {
                content.CopyTo( target );
            }
        }

        if ( behavior is not null )
        {
            var entry = output.CreateEntry( "behavior" );
            entry.ExternalAttributes = 0x81a4 << 16;
            using var target = new StreamWriter( entry.Open( ) );
            target.Write( behavior );
        }

        return destination;
    }

    private static async Task<HashSet<string>> FindTransientServicesAsync( )
    {
        return (await Exec( CommandDeck.SystemdCommandNames.Control, CommandDeck.SystemdCommandNames.User, "list-units", "--all", "--plain", "--no-legend", "commanddeck-extension-*.service" ))
            .Split( '\n', StringSplitOptions.RemoveEmptyEntries )
            .Select( line => line.Split( ' ', StringSplitOptions.RemoveEmptyEntries )[0] )
            .ToHashSet( StringComparer.Ordinal );
    }

    private static async Task<JsonObject> CreateReportAsync( string host, string package, bool wayland )
    {
        return new JsonObject
        {
            ["hostSha256"] = Convert.ToHexStringLower( SHA256.HashData( File.ReadAllBytes( host ) ) ),
            ["packageSha256"] = Convert.ToHexStringLower( SHA256.HashData( File.ReadAllBytes( package ) ) ),
            ["commit"] = (await Exec( "git", "rev-parse", "HEAD" )).Trim( ),
            ["recordedUtc"] = DateTime.UtcNow.ToString( "O" ),
            ["runtimeProfile"] = "linux-glibc-invariant-v1",
            ["clipboard"] = "private sink",
            ["rendererVersion"] = (await Exec( "qs", "--version" )).Trim( ),
            ["rendererSha256"] = RendererDigest( ),
            ["repositoryWaylandJourney"] = wayland,
            ["installedAcceptance"] = "pending",
            ["environment"] = RuntimeInformation.OSDescription
        };
    }

    private static bool ShouldReplaceExecutable( [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] string? executable, ZipArchiveEntry entry )
    {
        return executable is not null && entry.FullName == "bin/linux-x64/UuidTools";
    }

    private static JsonObject CreateRollbackRequest( JsonNode review, JsonNode inventory, string packageKey )
    {
        return new( )
        {
            ["extension"] = new JsonObject
            {
                ["action"] = "rollback",
                ["key"] = packageKey,
                ["confirmed"] = true,
                ["digest"] = review["archiveDigest"]!.DeepClone( ),
                ["expectedRevision"] = inventory["revision"]!.DeepClone( )
            }
        };
    }
}
