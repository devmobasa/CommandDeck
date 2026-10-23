using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AiSettingsRuntimeCancelsAffectedGenerationsAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-settings-cancel-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );
        await using ManagedAiFixtureServer fixture = await ManagedAiFixtureServer.StartAsync( );

        try
        {
            var config = await WriteManagedSpecConfigAsync( root, CreateAiOptions( fixture ) );
            await using var runtime = await CommandDeckRuntime.CreateAsync( config, CancellationToken.None );

            // A model change cancels the active stream on that provider within the bound.
            (var task, var done) = StartHeldGeneration( runtime, "client-a", "one" );
            await fixture.GenerationStarted.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );
            var started = TimeProvider.System.GetTimestamp( );

            var edited = await runtime.SaveAiProviderAsync( LocalProviderDefinition( "one", fixture.BaseUrl, model: "changed" ), false, 0, false, default );

            True( TimeProvider.System.GetElapsedTime( started ) < TimeSpan.FromSeconds( 5 ) );
            await task.WaitAsync( TimeSpan.FromSeconds( 5 ) );
            Equal( "cancelled", done.Value?.Outcome );
            Equal( 1L, edited.Settings.Revision );
            True( runtime.EffectiveAiSettings.Registry.TryGet( "one", out _ ) );

            // A display-name-only edit leaves an active stream running to completion.
            fixture.ResetGeneration( );
            (var unaffected, var unaffectedDone) = StartHeldGeneration( runtime, "client-b", "two" );
            await fixture.GenerationStarted.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

            var renamed = await runtime.SaveAiProviderAsync( LocalProviderDefinition( "two", fixture.BaseUrl, displayName: "Renamed" ), false, 1, false,
                default );

            Equal( 2L, renamed.Settings.Revision );
            True( !unaffected.IsCompleted );
            fixture.ReleaseGeneration( );
            await unaffected.WaitAsync( TimeSpan.FromSeconds( 5 ) );
            Equal( "completed", unaffectedDone.Value?.Outcome );

            // Master disable cancels every direct stream.
            fixture.ResetGeneration( );
            (var all, var allDone) = StartHeldGeneration( runtime, "client-c", "two" );
            await fixture.GenerationStarted.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );
            _ = await runtime.UpdateAiEnabledAsync( false, 2, default );
            await all.WaitAsync( TimeSpan.FromSeconds( 5 ) );
            Equal( "cancelled", allDone.Value?.Outcome );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static AiOptions CreateAiOptions( ManagedAiFixtureServer fixture )
        {
            return new AiOptions
            {
                Enabled = true,
                Providers =
                [
                    LocalProviderDefinition( "one", fixture.BaseUrl ),
                    LocalProviderDefinition( "two", fixture.BaseUrl )
                ]
            };
        }
    }

    private static async Task AiSettingsSocketAndCliUseTheDaemonAsync( )
    {
        var root = CreateCliSpecRoot( );
        await using ManagedAiFixtureServer fixture = await ManagedAiFixtureServer.StartAsync( );
        using CancellationTokenSource serverCancellation = new( );

        try
        {
            var config = await WriteManagedSpecConfigAsync( root, new AiOptions { Enabled = true, Providers = [] } );

            var socket = SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" );
            await using var runtime = await CommandDeckRuntime.CreateAsync( config, CancellationToken.None );
            UnixProtocolServer server = new( runtime, socket );
            var serverTask = server.RunAsync( serverCancellation.Token );
            await using var client = await ConnectWithRetryAsync( socket );

            await client.SendAsync( new ProtocolRequest { RequestId = 1, Operation = ProtocolOperations.AiSettingsGet }, default );

            var settings = await ReadMessageAsync( client, 1, "ai-settings-result", default );
            True( settings?.AiSettings is { Enabled: true, Revision: 0 } );
            Equal( "legacy-configuration", settings!.AiSettings!.Source );
            await client.SendAsync( CreateSettingsSocketAiProvidersSaveRequest( fixture ), default );
            var conflict = await ReadMessageAsync( client, 2, "error", default );
            True( conflict?.Error?.StartsWith( "conflict" + ": ", StringComparison.Ordinal ) == true );
            await client.SendAsync( CreateSettingsSocketCurrentRevisionSaveRequest( fixture ), default );
            var saved = await ReadMessageAsync( client, 3, "ai-provider-save-result", default );

            True( saved is { AiProviderSave: { SelectionApplied: true }, AiProviderSave.Settings.Revision: 1 } );

            True( !JsonSerializer
                .Serialize( saved!.AiProviderSave!, CommandDeckJsonContext.Default.AiProviderSaveResult )
                .Contains( "apiKey", StringComparison.OrdinalIgnoreCase ) );

            await client.SendAsync( CreateSettingsSocketAiProvidersTestRequest( fixture ), default );
            var tested = await ReadMessageAsync( client, 4, "ai-provider-test-result", default );
            True( tested?.AiProviderTest is { Outcome: "models", DraftRevision: 2 } );
            await client.SendAsync( CreateSettingsSocketAiProvidersDeleteRequest( ), default );
            var unconfirmed = await ReadMessageAsync( client, 5, "error", default );
            True( unconfirmed?.Error?.Contains( "confirmed", StringComparison.Ordinal ) == true );

            // The CLI path uses the daemon socket and prints the same documents.
            var show = await RunCommandDeckProcessAsync( "ai", "settings", "show", "--json", "--socket", socket );

            Equal( 0, show.ExitCode );

            var shown = JsonSerializer.Deserialize( show.StandardOutput, CommandDeckJsonContext.Default.AiProviderSettings );

            True( shown is { Revision: 1, Providers.Count: 1 } );

            var added = await RunCommandDeckProcessAsync( "ai", "providers", "add", "cli", "--preset", "ollama",
                "--base-url", fixture.BaseUrl.TrimEnd( '/' ), "--model", "m1", "--json", "--socket",
                socket );

            Equal( 0, added.ExitCode );

            var addResult = JsonSerializer.Deserialize( added.StandardOutput, CommandDeckJsonContext.Default.AiProviderSaveResult );

            True( addResult is { ProviderId: "cli", Settings.Revision: 2 } );

            var reserved = await RunCommandDeckProcessAsync( "ai", "providers", "add", AgentBackendIds.Cursor, "--preset", "ollama",
                "--base-url", fixture.BaseUrl.TrimEnd( '/' ), "--model", "m1", "--socket", socket );

            True( reserved.ExitCode != 0 );

            var removeWithoutYes = await RunCommandDeckProcessAsync( "ai", "providers", "remove", "cli", "--socket", socket );

            Equal( 2, removeWithoutYes.ExitCode );

            var testCli = await RunCommandDeckProcessAsync( "ai", "providers", "test", "cli", "--socket", socket );

            Equal( 0, testCli.ExitCode );
            True( testCli.StandardOutput.Contains( "Outcome:     models", StringComparison.Ordinal ) );

            var disabledCli = await RunCommandDeckProcessAsync( "ai", "providers", "disable", "cli", "--socket", socket );

            Equal( 0, disabledCli.ExitCode );

            var removed = await RunCommandDeckProcessAsync( "ai", "providers", "remove", "cli", "--yes", "--json",
                "--socket", socket );

            Equal( 0, removed.ExitCode );

            var afterRemove = JsonSerializer.Deserialize( removed.StandardOutput, CommandDeckJsonContext.Default.AiProviderSettings );

            True( afterRemove is { Revision: 4 } && afterRemove.Providers.All( static value => value.Id != "cli" ) );

            var offline = await RunCommandDeckProcessAsync( "ai", "settings", "show", "--socket", SpecTemporaryFiles.SocketPath( root, "missing.sock" ) );

            Equal( 1, offline.ExitCode );
            True( offline.StandardError.Contains( "daemon", StringComparison.OrdinalIgnoreCase ) );
            serverCancellation.Cancel( );
            await serverTask;
        }
        finally
        {
            serverCancellation.Cancel( );
            Directory.Delete( root, recursive: true );
        }
    }
}
