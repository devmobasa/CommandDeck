namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AiSettingsRuntimeAppliesLiveMutationsAndPersistsAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-settings-runtime-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );
        await using ManagedAiFixtureServer fixture = await ManagedAiFixtureServer.StartAsync( );

        try
        {
            var config = await WriteManagedSpecConfigAsync( root,
                new AiOptions { Enabled = false, Providers = [LocalProviderDefinition( "legacy", fixture.BaseUrl )] } );

            var managedPath = Path.Combine( root, AiProviderSettingsStore.FileName );

            await using ( var runtime = await CommandDeckRuntime.CreateAsync( config, CancellationToken.None ) )
            {
                var initial = await runtime.GetAiSettingsAsync( default );
                Equal( "legacy-configuration", initial.Source );
                True( initial is { Enabled: false, Providers.Count: 1, Presets.Count: 3 } );
                Equal( 0, runtime.GetAiProviders( ).Providers.Count );
                True( !File.Exists( managedPath ) );

                // Reserved IDs and conflicts are rejected by the daemon, not just the UI.
                await ThrowsAsync<InvalidOperationException>( async ( ) => await runtime.SaveAiProviderAsync(
                    LocalProviderDefinition( AgentBackendIds.Grok, fixture.BaseUrl ), true, 0, false, default ) );

                var conflict = await ThrowsAiAsync( async ( ) => await runtime.UpdateAiEnabledAsync( true, 5, default ) );

                Equal( "conflict", conflict.Category );
                True( !File.Exists( managedPath ) );

                // Enabling seeds the managed document from the legacy set.
                var enabled = await runtime.UpdateAiEnabledAsync( true, 0, default );
                Equal( "managed-document", enabled.Source );
                Equal( 1L, enabled.Revision );
                True( enabled is { Enabled: true, LegacyIgnored: true, Providers.Count: 1 } );
                True( File.Exists( managedPath ) );
                Equal( UnixFileMode.UserRead | UnixFileMode.UserWrite, UnixModeOf( managedPath ) );
                Equal( 1, runtime.GetAiProviders( ).Providers.Count );
                Equal( 0, fixture.TagRequests );

                // Save and use: provider committed, selection acknowledged, live inventory updated.
                var saved = await runtime.SaveAiProviderAsync( LocalProviderDefinition( "added", fixture.BaseUrl ), true, 1, true, default );

                Equal( 2L, saved.Settings.Revision );
                True( saved is { SelectionRequested: true, SelectionApplied: true, SelectionError: null } );
                Equal( "added", saved.Selection?.ProviderId );
                Equal( 2, runtime.GetAiProviders( ).Providers.Count );
                var models = await runtime.DiscoverAiModelsAsync( "added", default );
                True( models is { Error: null, Models.Count: 2 } );
                Equal( 1, fixture.TagRequests );
                Equal( "ready", (await runtime.GetAiSelectionAsync( default )).State );

                // Duplicate create and missing edit are categorised.
                var duplicate = await ThrowsAiAsync( async ( ) => await runtime.SaveAiProviderAsync(
                    LocalProviderDefinition( "ADDED", fixture.BaseUrl ), true, 2, false, default ) );

                Equal( "conflict", duplicate.Category );

                var missing = await ThrowsAiAsync( async ( ) => await runtime.SaveAiProviderAsync(
                    LocalProviderDefinition( "ghost", fixture.BaseUrl ), false, 2, false, default ) );

                Equal( "not-found", missing.Category );
                Equal( 2L, (await runtime.GetAiSettingsAsync( default )).Revision );

                // A definition change invalidates an outstanding preflight.
                var preflight = await runtime.CreateAiPreflightAsync( "client-a", new AiDraft
                {
                    ProviderId = "added",
                    ModelId = "m1",
                    UserText = "hello",
                    Attachments = []
                }, default );

                var edited = LocalProviderDefinition( "added", fixture.BaseUrl, model: "m2" );
                var editResult = await runtime.SaveAiProviderAsync( edited, false, 2, false, default );
                Equal( 3L, editResult.Settings.Revision );
                AiDone? terminal = null;

                await runtime.StreamAiAsync( "client-a", preflight.Id, preflight.PayloadDigest, preflight.Generation, _ => ValueTask.CompletedTask, done =>
                {
                    terminal = done;

                    return ValueTask.CompletedTask;
                }, default );

                Equal( "failed", terminal?.Outcome );
                Equal( "stale-preflight", terminal?.Error?.Category );
                Equal( 0, fixture.ChatRequests );

                // Draft tests do not persist and honour the disabled state.
                var tested = await runtime.TestAiProviderDraftAsync( LocalProviderDefinition( "draft", fixture.BaseUrl ), 4, 3, default );

                Equal( "models", tested.Outcome );
                Equal( 3L, (await runtime.GetAiSettingsAsync( default )).Revision );

                True( !(await runtime.GetAiSettingsAsync( default )).Providers.Any( static value => value.Id == "draft" ) );

                var staleTest = await ThrowsAiAsync( async ( ) =>
                    await runtime.TestAiProviderDraftAsync( LocalProviderDefinition( "draft", fixture.BaseUrl ), 5, 1, default ) );

                Equal( "conflict", staleTest.Category );

                // Removing the legacy provider and disabling direct AI.
                var removed = await runtime.DeleteAiProviderAsync( "legacy", 3, default );
                Equal( 4L, removed.Revision );
                Equal( 1, removed.Providers.Count );

                var notFound = await ThrowsAiAsync( async ( ) => await runtime.DeleteAiProviderAsync( "legacy", 4, default ) );

                Equal( "not-found", notFound.Category );
                var disabled = await runtime.UpdateAiEnabledAsync( false, 4, default );
                True( disabled is { Enabled: false, Providers.Count: 1 } );
                Equal( 0, runtime.GetAiProviders( ).Providers.Count );

                var disabledTest = await ThrowsAiAsync( async ( ) =>
                    await runtime.TestAiProviderDraftAsync( LocalProviderDefinition( "draft", fixture.BaseUrl ), 6, 5, default ) );

                Equal( "disabled", disabledTest.Category );

                // Save and use while disabled: provider saved, selection reported as not changed.
                var partial = await runtime.SaveAiProviderAsync( LocalProviderDefinition( "offline", fixture.BaseUrl ), true, 5, true, default );

                Equal( 6L, partial.Settings.Revision );
                True( partial is { SelectionRequested: true, SelectionApplied: false } );
                Equal( "disabled", partial.SelectionError?.Category );
                True( partial.Settings.Providers.Any( static value => value.Id == "offline" ) );
                Equal( "added", (await runtime.GetAiSelectionAsync( default )).ProviderId );

                // Removing the selected provider preserves the selection as unavailable.
                _ = await runtime.UpdateAiEnabledAsync( true, 6, default );
                _ = await runtime.DeleteAiProviderAsync( "added", 7, default );
                var orphaned = await runtime.GetAiSelectionAsync( default );
                Equal( "added", orphaned.ProviderId );
                Equal( "unavailable", orphaned.State );

                // Search still works and never consults the fixture: one explicit
                // refresh and one explicit draft test are the only discovery calls.
                var results = await runtime.SearchAsync( new SearchRequest { Query = "ai: hello", Limit = 8 }, default );

                True( results.All( static value => value.Id != "ai:added" ) );
                Equal( 2, fixture.TagRequests );
            }

            // Restart: the managed document owns the set; the removed legacy provider does not return.
            await using ( var restarted = await CommandDeckRuntime.CreateAsync( config, CancellationToken.None ) )
            {
                var settings = await restarted.GetAiSettingsAsync( default );
                Equal( "managed-document", settings.Source );
                Equal( 8L, settings.Revision );
                True( settings is { Enabled: true, LegacyIgnored: true } );

                True( settings
                    .Providers.Select( static value => value.Id )
                    .SequenceEqual( ["offline"] ) );

                Equal( "unavailable", (await restarted.GetAiSelectionAsync( default )).State );
            }

            // A damaged document at startup fails closed and stays editable only after repair.
            await File.WriteAllTextAsync( managedPath, "{ broken" );

            await using ( var damaged = await CommandDeckRuntime.CreateAsync( config, CancellationToken.None ) )
            {
                var settings = await damaged.GetAiSettingsAsync( default );
                True( settings is { Enabled: false, Error.Category: "invalid-config" } );
                Equal( 0, settings.Providers.Count );
                Equal( 0, damaged.GetAiProviders( ).Providers.Count );

                var rejected = await ThrowsAiAsync( async ( ) => await damaged.UpdateAiEnabledAsync( true, null, default ) );

                Equal( "invalid-config", rejected.Category );
                var status = await damaged.GetRuntimeStatusAsync( default );
                True( status.Providers.Any( static value => (value is { Id: "ai", State: not "available" }) ) );
                _ = await damaged.SearchAsync( new SearchRequest { Query = "hello", Limit = 8 }, default );
            }

            Equal( "{ broken", await File.ReadAllTextAsync( managedPath ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}
