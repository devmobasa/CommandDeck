#!/usr/bin/env dotnet
#:include ./ScriptExecutables.cs
#:include ../src/CommandDeck.Core/Utilities/NativeLibraryNames.cs
#:include ../src/CommandDeck.Infrastructure/Utilities/SystemdCommandNames.cs
#:include ../src/CommandDeck.Core/Configuration/ToolExecutables.cs
#:include ../src/CommandDeck.ExtensionProtocol/Packaging/ExtensionPackageFiles.cs
#:include ../src/CommandDeck.ExtensionProtocol/Packaging/ExtensionTargets.cs
#:include ../src/CommandDeck.Core/Utilities/EnvironmentVariableNames.cs
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
#:property EnableNETAnalyzers=false
#:include ./test-extension-session.clients.cs
#:include ./test-extension-session.Fixtures.cs

using System.Diagnostics;
using System.Text.Json.Nodes;

if ( args.Length < 2 )
{
    throw new ArgumentException( "Usage: dotnet scripts/test-extension-session.cs HOST PACKAGE [--wayland]" );
}

await Journey.RunAsync( Path.GetFullPath( args[0] ), Path.GetFullPath( args[1] ), args.Contains( "--wayland", StringComparer.Ordinal ) );

static partial class Journey
{
    public static readonly string Repo = Path.GetFullPath( Path.Combine( AppContext.GetData( "EntryPointFileDirectoryPath" )!.ToString( )!, ".." ) );
    public static readonly string[] Features = ["commands@1", "views.list@1", "views.detail@1", "views.stack@1", "views.pagination@1", "actions.copy-text@1"];
    public static void Check( bool condition, string message )
    {
        if ( !condition )
        {
            throw new InvalidOperationException( message );
        }
    }
    public static JsonObject Input( string session, long sequence, long revision, string? handle = null, string? item = null ) => new( )
    {
        ["sessionId"] = session,
        ["extensionInput"] = new JsonObject { ["sequence"] = sequence, ["revision"] = revision, ["handle"] = handle, ["itemId"] = item }
    };
    public static async Task RunAsync( string host, string package, bool wayland )
    {
        var baseline = await Units( );
        var root = Directory.CreateTempSubdirectory( "commanddeck-session-" ).FullName;
        var effects = Path.Combine( root, "effects" );
        Directory.CreateDirectory( effects );
        var clipboard = Path.Combine( effects, "clipboard" );
        var copied = Path.Combine( effects, "copied" );
        var hold = Path.Combine( effects, "hold-copy" );
        var started = Path.Combine( effects, "copy-started" );
        var bin = Path.Combine( root, "bin" );

        await Exec( ScriptExecutables.Dotnet, "publish", Path.Combine( Repo, "scripts/extension-clipboard-sink.cs" ), "-o", bin );

        var address = Path.Combine( root, "daemon.sock" );
        var config = Path.Combine( root, "config.json" );

        await File.WriteAllTextAsync( config, new JsonObject
        {
            ["storage"] = new JsonObject { ["databasePath"] = Path.Combine( root, "catalog.db" ) },
            ["indexing"] = new JsonObject { ["roots"] = new JsonArray( ), ["indexDesktopApplications"] = false, ["indexOmarchyCommands"] = false },
            ["ai"] = new JsonObject { ["enabled"] = false },
            ["agent"] = new JsonObject { ["runtimeDirectory"] = Path.Combine( root, "runtime" ), ["stateDirectory"] = Path.Combine( root, "state" ) }
        }.ToJsonString( ) );

        var start = new ProcessStartInfo( host ) { RedirectStandardOutput = true, RedirectStandardError = true };

        foreach ( var argument in new[] { "daemon", "--config", config, "--socket", address } )
        {
            start.ArgumentList.Add( argument );
        }

        start.Environment[CommandDeck.EnvironmentVariableNames.ExecutablePath] = bin + ":" + Environment.GetEnvironmentVariable( CommandDeck.EnvironmentVariableNames.ExecutablePath );
        start.Environment["COMMANDDECK_TEST_EFFECTS"] = effects;
        using var daemon = Process.Start( start )!;
        var stdout = daemon.StandardOutput.ReadToEndAsync( );
        var stderr = daemon.StandardError.ReadToEndAsync( );
        var clients = new List<Client>( );

        Client Connect( )
        {
            var connection = new Client( address );
            clients.Add( connection );

            return connection;
        }

        async Task<JsonNode> Cli( params string[] arguments ) => JsonNode.Parse( await Exec( host, ["extensions", .. arguments, "--socket", address, "--json"] ) )!;

        async Task Clean( string label ) => await Wait( async ( ) => (await Units( )).SetEquals( baseline ), label );

        try
        {
            await Wait( ( ) => Task.FromResult( File.Exists( address ) || daemon.HasExited ), "Daemon startup" );

            Check( !daemon.HasExited, "Daemon failed to start" );
            var review = await Cli( "inspect", "--archive", package );

            var inventory = await Cli( "install", "--archive", package, "--digest", review["archiveDigest"]!.GetValue<string>( ), "--yes" );

            var packageId = inventory["packages"]![0]!["id"]!.GetValue<string>( );
            var packageKey = inventory["packages"]![0]!["key"]!.GetValue<string>( );
            await Cli( "enable", packageId );
            await Cli( "enable" );
            var client = Connect( );
            var rows = await client.Call( "search", new( ) { ["query"] = "UUID" } );

            var result = rows
                .Select( message => message["result"] )
                .First( row => row?["kind"]?.GetValue<string>( ) == "extension-command" )!;

            Check( result["actions"]![0]!["kind"]!.GetValue<string>( ) == "extension-open", "Missing launcher activation" );

            await Clean( "Discovery started a child" );

            JsonObject OpenInput( string[] features ) => CreateOpenInput( features, result );

            await client.Call( "extension.open", OpenInput( ["commands@1"] ), true );
            await Clean( "Unsupported renderer started child" );

            async Task<JsonNode> Open( Client connection )
            {
                var messages = await connection.Call( "extension.open", OpenInput( Features ) );

                var view = messages
                    .Select( message => message["extensionSession"] )
                    .First( state => state?["status"]?.GetValue<string>( ) == "view" )!;

                Check( view["content"]!["items"]!.AsArray( ).Count == 8, "UUID row count" );

                await connection.Call( "extension.present", Input( view["sessionId"]!.GetValue<string>( ), 1, view["revision"]!.GetValue<long>( ) ) );

                return view;
            }

            var view = await Open( client );
            var session = view["sessionId"]!.GetValue<string>( );
            Check( !(await Units( )).SetEquals( baseline ), "No isolated session" );

            JsonObject Copy( JsonNode snapshot, long sequence ) => Input( snapshot["sessionId"]!.GetValue<string>( ), sequence, snapshot["revision"]!.GetValue<long>( ),
                        snapshot["content"]!["items"]![0]!["actions"]![0]!["handle"]!.GetValue<string>( ), snapshot["content"]!["items"]![0]!["id"]!.GetValue<string>( ) );

            await client.Call( "extension.action", Copy( view, 2 ), true );
            Check( !File.Exists( clipboard ), "Copy without grant" );
            var foreign = Connect( );
            await foreign.Call( "extension.action", Copy( view, 2 ), true );
            await foreign.Call( "extension.close", Input( session, 0, 0 ), true );
            await Cli( "grants", packageId, "--allow", "clipboard.write", "--yes" );
            await Clean( "Grant cleanup" );
            await client.Call( "extension.action", Copy( view, 3 ), true );
            await ControllerLifecycle( host, address, root, result["id"]!.GetValue<string>( ) );
            await Clean( "Controller rapid reopen cleanup" );

            if ( wayland )
            {
                await Wayland.Run( host, address, clipboard, root );
                await Clean( "Wayland cleanup" );
                File.Delete( copied );
            }

            view = await Open( client );
            session = view["sessionId"]!.GetValue<string>( );
            var copyClock = Stopwatch.StartNew( );
            await client.Call( "extension.action", Copy( view, 2 ) );

            Check( copyClock.Elapsed < TimeSpan.FromSeconds( 2 ), "Copy waited for the background clipboard owner's inherited stderr" );
            Check( await File.ReadAllTextAsync( clipboard ) == view["content"]!["items"]![0]!["title"]!.GetValue<string>( ), "Exact displayed copy" );

            await client.Call( "extension.action", Copy( view, 2 ), true );
            Check( File.ReadAllLines( copied ).Length == 1, "Replayed effect" );

            var messages = await client.Call( "extension.action", Input( session, 3, 1, view["content"]!["actions"]![0]!["handle"]!.GetValue<string>( ) ) );

            var replacement = messages
                .Select( message => message["extensionSession"] )
                .First( state => state?["status"]?.GetValue<string>( ) == "view" )!;

            Check( !JsonNode.DeepEquals( view["content"]!["items"], replacement["content"]!["items"] ), "Callback did not regenerate" );

            await client.Call( "extension.action", Copy( view, 4 ) );

            Check( await File.ReadAllTextAsync( clipboard ) == view["content"]!["items"]![0]!["title"]!.GetValue<string>( ), "Redraw retargeted old click" );

            await client.Call( "extension.present", Input( session, 5, 2 ) );
            await client.Call( "extension.action", Copy( view, 6 ), true );
            File.WriteAllText( hold, "" );
            File.Delete( started );
            var copyRequest = await client.Send( "extension.action", Copy( replacement, 7 ) );
            await Wait( ( ) => Task.FromResult( File.Exists( started ) ), "Effect never started" );
            inventory = await Cli( "list" );
            var disableRequest = await DisablePackageAsync( inventory, packageKey, foreign );
            await Task.Delay( 100 );
            Check( !foreign.HasData, "Disable committed while started effect held lease" );
            File.Delete( hold );
            await client.Done( copyRequest );
            Client.NoErrors( await foreign.Done( disableRequest ) );
            await Clean( "Disable cleanup" );

            Check( await File.ReadAllTextAsync( clipboard ) == replacement["content"]!["items"]![0]!["title"]!.GetValue<string>( ), "Race effect payload" );

            await client.Call( "extension.open", OpenInput( Features ), true );
            await Cli( "enable", packageId );
            var reopened = await Open( client );
            Check( reopened["sessionId"]!.GetValue<string>( ) != session, "Reused authority" );
            await client.Call( "extension.action", Copy( view, 8 ), true );
            await client.Call( "extension.close", Input( reopened["sessionId"]!.GetValue<string>( ), 0, 0 ) );
            await Clean( "Close cleanup" );
            view = await Open( client );
            await CheckNavigationAsync( client, view, clipboard );
            await Clean( "Navigation cleanup" );

            Console.WriteLine( "PASS connected navigation: push Detail, copy, Back, Next/Previous pages, retired detail/page handles, callback after restored parent." );

            await Open( client );
            client.Dispose( );
            clients.Remove( client );
            await Clean( "Disconnect cleanup" );
            client = Connect( );
            await Open( client );
            await Cli( "disable" );
            await Clean( "Master disable cleanup" );
            await Cli( "enable" );
            // Updating one package must leave a different package's foreground command usable.
            var siblingArchive = CreatePackageVariant( package, Path.Combine( root, "sibling.cdext" ), "org.commanddeck.uuid-sibling", "0.1.0" );

            var siblingReview = await Cli( "inspect", "--archive", siblingArchive );

            await Cli( "install", "--archive", siblingArchive, "--digest", siblingReview["archiveDigest"]!.GetValue<string>( ), "--yes" );
            await Cli( "enable", "org.commanddeck.uuid-sibling" );
            var siblingResult = await FindForeignResultAsync( result, foreign );
            var siblingInput = OpenInput( Features );
            siblingInput["resultId"] = siblingResult["id"]!.GetValue<string>( );
            var siblingMessages = await foreign.Call( "extension.open", siblingInput );

            var siblingView = siblingMessages
                .Select( message => message["extensionSession"] )
                .First( state => state?["status"]?.GetValue<string>( ) == "view" )!;
            var siblingSession = siblingView["sessionId"]!.GetValue<string>( );
            await foreign.Call( "extension.present", Input( siblingSession, 1, 1 ) );
            var siblingUnits = await Units( );
            view = await Open( client );

            var updatedVersion = review["manifest"]!["version"]!.GetValue<string>( ) == "0.1.1" ? "0.1.2" : "0.1.1";

            var updatedArchive = CreatePackageVariant( package, Path.Combine( root, "updated.cdext" ), packageId, updatedVersion );

            var updatedReview = await Cli( "inspect", "--archive", updatedArchive );

            await Cli( "install", "--archive", updatedArchive, "--digest", updatedReview["archiveDigest"]!.GetValue<string>( ), "--yes" );

            await Wait( async ( ) => (await Units( )).SetEquals( siblingUnits ), "Update stopped sibling or leaked old session" );

            await client.Call( "extension.action", Copy( view, 2 ), true );
            await Open( client );
            inventory = await Cli( "list" );
            await client.Call( "extensions.manage", CreateRollbackRequest( review, inventory, packageKey ) );
            await Wait( async ( ) => (await Units( )).SetEquals( siblingUnits ), "Rollback cleanup" );

            await foreign.Call( "extension.action", Input( siblingSession, 2, 1, siblingView["content"]!["actions"]![0]!["handle"]!.GetValue<string>( ) ) );
            await foreign.Call( "extension.close", Input( siblingSession, 0, 0 ) );
            await Clean( "Sibling cleanup" );
            await Open( client );
            await Cli( "remove", packageId, "--yes" );
            await Clean( "Remove cleanup" );
            await client.Call( "ping", new( ) );
            var hostileOutput = Path.Combine( root, "hostile" );

            await Exec( ScriptExecutables.Dotnet, "publish", Path.Combine( Repo, "scripts/extension-hostile.cs" ), "-r", "linux-x64", "-o", hostileOutput );

            foreach ( var behavior in new[] { "crash", "hang", "malformed", "flood" } )
            {
                var hostileArchive = CreatePackageVariant( package, Path.Combine( root, behavior + ".cdext" ), "org.commanddeck.hostile-" + behavior, "0.1.0", Path.Combine( hostileOutput, "extension-hostile" ), behavior );

                var inspected = await Cli( "inspect", "--archive", hostileArchive );

                await Cli( "install", "--archive", hostileArchive, "--digest", inspected["archiveDigest"]!.GetValue<string>( ), "--yes" );
                await Cli( "enable", "org.commanddeck.hostile-" + behavior );
                var search = await client.Call( "search", new( ) { ["query"] = behavior } );

                var hostileResult = search
                    .Select( message => message["result"] )
                    .First( row => row?["kind"]?.GetValue<string>( ) == "extension-command" && row["title"]!.GetValue<string>( ) == behavior )!;

                var hostileInput = OpenInput( Features );
                hostileInput["resultId"] = hostileResult["id"]!.GetValue<string>( );
                var failed = await client.Call( "extension.open", hostileInput, true );

                Check( failed.Any( message => message["extensionSession"]?["status"]?.GetValue<string>( ) == "failed" ), behavior + " did not publish recovery state" );

                await Clean( behavior + " descendants survived" );
                await client.Call( "ping", new( ) );
            }

            if ( wayland )
            {
                await Wayland.Run( host, address, clipboard, root, true );
                await Clean( "Wayland failure recovery cleanup" );
            }

            await OpenSiblingAndStopDaemon( );

            async Task OpenSiblingAndStopDaemon( )
            {
                await foreign.Call( "extension.open", siblingInput );
                using var signal = Process.Start( "kill", $"-TERM {daemon.Id}" )!;
                await signal.WaitForExitAsync( );
                await Wait( ( ) => Task.FromResult( daemon.HasExited ), "Daemon shutdown", 10 );
                await Clean( "Daemon shutdown cleanup" );
            }

            var evidence = await CreateReportAsync( host, package, wayland );
            var evidencePath = Path.Combine( Repo, "artifacts/extensions/session-evidence.json" );
            Directory.CreateDirectory( Path.GetDirectoryName( evidencePath )! );

            await File.WriteAllTextAsync( evidencePath, evidence.ToJsonString( new( ) { WriteIndented = true } ) + "\n" );

            Console.WriteLine( "PASS real daemon/sandbox: discovery, negotiation, ownership, copy grants, replay, regeneration, presentation, revocation race, close/reopen, disconnect, master disable, update/rollback/sibling, remove, crash/hang/malformed/flood, daemon shutdown." );
        }
        finally
        {
            File.Delete( hold );

            foreach ( var client in clients )
            {
                client.Dispose( );
            }

            if ( !daemon.HasExited )
            {
                using var signal = Process.Start( "kill", $"-TERM {daemon.Id}" )!;
                await signal.WaitForExitAsync( );
            }

            using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 10 ) );

            try
            {
                await daemon.WaitForExitAsync( timeout.Token );
            }
            catch ( OperationCanceledException )
            {
                daemon.Kill( true );
                await daemon.WaitForExitAsync( );
            }

            Directory.CreateDirectory( Path.Combine( Repo, "artifacts/extensions" ) );

            await File.WriteAllTextAsync( Path.Combine( Repo, "artifacts/extensions/session-daemon.log" ), await stdout + await stderr );

            Directory.Delete( root, true );
        }
    }

    private static JsonObject CreateOpenInput( string[] features, JsonNode result )
    {
        return new( )
        {
            ["resultId"] = result["id"]!.GetValue<string>( ),
            ["extensionInput"] = new JsonObject
            {
                ["features"] = new JsonArray( features
                    .Select( value => ( JsonNode? ) JsonValue.Create( value ) )
                    .ToArray( ) )
            }
        };
    }

    private static async Task<int> DisablePackageAsync( JsonNode inventory, string packageKey, Client foreign )
    {
        return await foreign.Send( "extensions.manage", new( )
        {
            ["extension"] = new JsonObject { ["action"] = "disable", ["key"] = packageKey, ["expectedRevision"] = inventory["revision"]!.DeepClone( ) }
        } );
    }

    private static JsonNode FindPublishedView( List<JsonNode> response )
    {
        return response
            .Select( message => message["extensionSession"] )
            .First( state => state?["status"]?.GetValue<string>( ) == "view" )!;
    }

    private static async Task<JsonNode> FindForeignResultAsync( JsonNode result, Client foreign )
    {
        return (await foreign.Call( "search", new( ) { ["query"] = "UUID" } ))
            .Select( message => message["result"] )
            .First( row => row?["kind"]?.GetValue<string>( ) == "extension-command" && row["id"]!.GetValue<string>( ) != result["id"]!.GetValue<string>( ) )!;
    }

}
