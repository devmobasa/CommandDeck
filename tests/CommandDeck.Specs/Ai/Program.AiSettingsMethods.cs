using System.Text.Json;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void AiSettingsContractsReserveBackendIdsAndValidateRequests( )
    {
        foreach ( var reserved in AgentBackendIds.All )
        {
            True( AgentBackendIds.IsReserved( reserved ) );
            True( AgentBackendIds.IsReserved( reserved.ToUpperInvariant( ) ) );

            Throws<InvalidOperationException>( ( ) =>
                ConfigurationValidator.ValidateAiProvider( LocalProviderDefinition( reserved, "http://127.0.0.1:11434" ) ) );
        }

        Equal( 4, AgentBackendIds.All.Count );

        ConfigurationValidator.ValidateAiProvider( LocalProviderDefinition( "ollama", "http://127.0.0.1:11434" ) );

        Throws<InvalidOperationException>( ( ) =>
            ConfigurationValidator.ValidateAiProvider( LocalProviderDefinition( "local-host", "http://localhost:11434" ) ) );

        // Presets are data: names, endpoints, and credential references, never values.
        Equal( 3, AiProviderPresets.All.Count );

        True( AiProviderPresets.All.Any( static preset => preset is { Id: "xai-grok", BaseUrl: "https://api.x.ai/v1" } ) );
        True( AiProviderPresets.All.All( static preset => preset.CredentialKind != "none" || preset.CredentialName is null ) );

        var presetJson = JsonSerializer.Serialize( AiProviderPresets.All.ToList( ), CommandDeckJsonContext.Default.ListAiProviderPreset );

        True( !presetJson.Contains( "apiKey", StringComparison.OrdinalIgnoreCase ) );
        True( !presetJson.Contains( "sk-", StringComparison.Ordinal ) );

        Throws<InvalidOperationException>( ( ) =>
            ProtocolRequestValidator.Validate( new ProtocolRequest { RequestId = 1, Operation = ProtocolOperations.AiProvidersSave } ) );

        Throws<InvalidOperationException>( ( ) => ProtocolRequestValidator.Validate( CreateReservedProviderSaveRequest( ) ) );

        Throws<InvalidOperationException>( ( ) => ProtocolRequestValidator.Validate( new ProtocolRequest
        {
            RequestId = 1,
            Operation = ProtocolOperations.AiProvidersDelete,
            ProviderId = "ollama"
        } ) );

        Throws<InvalidOperationException>( ( ) =>
            ProtocolRequestValidator.Validate( new ProtocolRequest { RequestId = 1, Operation = ProtocolOperations.AiSettingsUpdate } ) );

        Throws<InvalidOperationException>( ( ) => ProtocolRequestValidator.Validate( CreateAiProvidersTestRequest( ) ) );

        Throws<InvalidOperationException>( ( ) =>
            ProtocolRequestValidator.Validate( new ProtocolRequest { RequestId = 1, Operation = ProtocolOperations.AgentLoginComplete } ) );

        Throws<InvalidOperationException>( ( ) => ProtocolRequestValidator.Validate( new ProtocolRequest
        {
            RequestId = 1,
            Operation = ProtocolOperations.AgentLoginComplete,
            LoginCode = "abc\tdef"
        } ) );

        ProtocolRequestValidator.Validate( new ProtocolRequest
        {
            RequestId = 1,
            Operation = ProtocolOperations.AgentLoginComplete,
            LoginCode = "paste-code-123"
        } );

        ProtocolRequestValidator.Validate( new ProtocolRequest
        {
            RequestId = 1,
            Operation = ProtocolOperations.AgentLoginStart,
            LoginMethod = AgentLoginMethods.Browser
        } );

        ProtocolRequestValidator.Validate( CreateSelectedProviderSaveRequest( ) );
        ProtocolRequestValidator.Validate( CreateConfirmedProviderDeleteRequest( ) );

        static ProtocolRequest CreateReservedProviderSaveRequest( )
        {
            return new ProtocolRequest
            {
                RequestId = 1,
                Operation = ProtocolOperations.AiProvidersSave,
                AiProvider = LocalProviderDefinition( AgentBackendIds.ClaudeCode, "http://127.0.0.1:11434" )
            };
        }

        static ProtocolRequest CreateAiProvidersTestRequest( )
        {
            return new ProtocolRequest
            {
                RequestId = 1,
                Operation = ProtocolOperations.AiProvidersTest,
                AiProvider = LocalProviderDefinition( "draft", "http://127.0.0.1:11434" ),
                DraftRevision = -1
            };
        }

        static ProtocolRequest CreateSelectedProviderSaveRequest( )
        {
            return new ProtocolRequest
            {
                RequestId = 1,
                Operation = ProtocolOperations.AiProvidersSave,
                AiProvider = LocalProviderDefinition( "ollama", "http://127.0.0.1:11434" ),
                ExpectedRevision = 3,
                Create = true,
                SelectAfterSave = true
            };
        }

        static ProtocolRequest CreateConfirmedProviderDeleteRequest( )
        {
            return new ProtocolRequest
            {
                RequestId = 1,
                Operation = ProtocolOperations.AiProvidersDelete,
                ProviderId = "ollama",
                Confirmed = true
            };
        }
    }

    private static async Task ExplicitBackendPrefixesRouteToAssistantBackendsAsync( )
    {
        Equal( "claude.local", QueryParser.Parse( "claude: fix this" ).ExplicitBackendId );
        Equal( "cursor.local", QueryParser.Parse( "cursor: fix this" ).ExplicitBackendId );
        Equal( "grok.local", QueryParser.Parse( "grok: fix this" ).ExplicitBackendId );
        Equal( "codex.local", QueryParser.Parse( "codex: fix this" ).ExplicitBackendId );
        Equal( "codex.local", QueryParser.Parse( "codex fix this" ).ExplicitBackendId );
        Equal( null, QueryParser.Parse( "ai: fix this" ).ExplicitBackendId );
        Equal( null, QueryParser.Parse( "claude fix this" ).ExplicitBackendId );
        var claude = QueryParser.Parse( "claude: fix this" );
        True( claude is { IsAiMode: true, Text: "fix this", ProviderFilter: "assistant" } );

        AiOptions options = new( ) { Enabled = true, Providers = [LocalProviderDefinition( "local-one", "http://127.0.0.1:11434" )] };

        var effective = options;
        AiCommandProvider provider = new( ( ) => effective, ( ) => "local-one" );
        var request = new SearchRequest( ) { Query = "ai: hello", Limit = 8 };

        Equal( 1, (await provider.SearchAsync( QueryParser.Parse( "ai: hello" ), request, default )).Count );
        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "claude: hello" ), request, default )).Count );
        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "grok: hello" ), request, default )).Count );

        // The provider reads the effective options on every search.
        effective = new AiOptions { Enabled = false, Providers = options.Providers };

        Equal( 0, (await provider.SearchAsync( QueryParser.Parse( "ai: hello" ), request, default )).Count );
    }

    private static async Task AiSettingsStoreWritesPrivatelyDetectsConflictsAndFailsClosedAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-settings-store-{Guid.NewGuid( ):N}" );

        try
        {
            var configDirectory = Path.Combine( root, "config" );
            var path = AiProviderSettingsStore.ResolvePath( Path.Combine( configDirectory, "commanddeck.jsonc" ) );

            Equal( Path.Combine( configDirectory, "ai-providers.json" ), path );
            AiProviderSettingsStore store = new( path );
            var absent = store.Load( );
            True( absent is { Exists: false, Document: null, Error: null } );
            Equal( AiProviderSettingsIdentity.Absent, absent.Identity );

            AiProviderSettingsDocument first = new( )
            {
                Revision = 1,
                Enabled = true,
                Providers = [LocalProviderDefinition( "ollama", "http://127.0.0.1:11434" )]
            };

            var identity = store.Write( first, AiProviderSettingsIdentity.Absent );
            True( identity.Exists );
            Equal( UnixFileMode.UserRead | UnixFileMode.UserWrite, UnixModeOf( path ) );
            Equal( UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, UnixModeOf( configDirectory ) );

            True( !Directory
                .EnumerateFiles( configDirectory, ".ai-providers-*.tmp" )
                .Any( ) );

            var loaded = store.Load( );
            True( loaded is { Document: { Revision: 1, Enabled: true }, Document.Providers.Count: 1 } );
            Equal( identity, loaded.Identity );

            // Stale identity (an earlier load) cannot overwrite the newer document.
            var stale = new AiProviderSettingsDocument { Revision = 2, Enabled = true, Providers = first.Providers };

            var conflict = await ThrowsAiAsync( ( ) => Task.FromResult( store.Write( stale, AiProviderSettingsIdentity.Absent ) ) );

            Equal( "conflict", conflict.Category );
            Equal( 1, store.Load( ).Document!.Revision );

            // An external edit since load is detected by identity and digest.
            await File.WriteAllTextAsync( path, await File.ReadAllTextAsync( path ) + "\n" );
            var external = await ThrowsAiAsync( ( ) => Task.FromResult( store.Write( stale, identity ) ) );
            Equal( "conflict", external.Category );
            var reloaded = store.Load( );
            var second = store.Write( stale, reloaded.Identity );
            Equal( 2, store.Load( ).Document!.Revision );
            True( second != reloaded.Identity );

            // Invalid documents fail closed rather than falling back to legacy endpoints.
            await File.WriteAllTextAsync( path, "{ not json" );

            Equal( "invalid-config", store.Load( )
                .Error?.Category );

            await File.WriteAllTextAsync( path, "{\"schemaVersion\":99,\"revision\":1,\"enabled\":true,\"providers\":[]}" );

            Equal( "invalid-config", store.Load( )
                .Error?.Category );

            await File.WriteAllTextAsync( path,
                "{\"schemaVersion\":1,\"revision\":1,\"enabled\":true,\"providers\":[{\"id\":\"bad\",\"kind\":\"ollama\",\"baseUrl\":\"http://localhost:1\",\"model\":\"m\"}]}" );

            var invalidProvider = store.Load( );
            Equal( "invalid-config", invalidProvider.Error?.Category );
            True( invalidProvider.Document is null );
            await File.WriteAllBytesAsync( path, new byte[AiProviderSettingsLimits.MaximumDocumentBytes + 1] );

            Equal( "invalid-config", store.Load( )
                .Error?.Category );

            File.Delete( path );
            var target = Path.Combine( root, "elsewhere.json" );

            await File.WriteAllTextAsync( target, "{\"schemaVersion\":1,\"revision\":1,\"enabled\":true,\"providers\":[]}" );

            File.CreateSymbolicLink( path, target );
            var symlinked = store.Load( );
            Equal( "invalid-config", symlinked.Error?.Category );
            True( symlinked.Exists );

            var symlinkWrite = await ThrowsAiAsync( ( ) => Task.FromResult( store.Write( first, AiProviderSettingsIdentity.Absent ) ) );

            Equal( "conflict", symlinkWrite.Category );
            Equal( "{\"schemaVersion\":1,\"revision\":1,\"enabled\":true,\"providers\":[]}", await File.ReadAllTextAsync( target ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task AiSettingsServiceResolvesPrecedenceAndRetiresRegistriesAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-settings-service-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );
        await using ManagedAiFixtureServer fixture = await ManagedAiFixtureServer.StartAsync( );

        try
        {
            AiOptions legacy = new( ) { Enabled = true, Providers = [LocalProviderDefinition( "legacy", fixture.BaseUrl )] };

            var path = Path.Combine( root, AiProviderSettingsStore.FileName );
            await using ( AiProviderSettingsService service = new( legacy, new AiProviderSettingsStore( path ) ) )
            {
                var current = service.Current;
                Equal( "legacy-configuration", current.Source );
                Equal( 0L, current.Revision );
                True( current is { Enabled: true, LegacyIgnored: false, Error: null } );
                True( current.Registry.TryGet( "legacy", out _ ) );
                Equal( 0, fixture.TagRequests );
                var described = service.Describe( null );
                Equal( 3, described.Presets.Count );
                Equal( 4, described.ReservedIds.Count );
                Equal( 1, described.Providers.Count );

                // Seeding: the first mutation carries the whole legacy set forward.
                var seed = service.CurrentDocument( );
                Equal( 1, seed.Providers.Count );
                var candidate = CreateCandidate( fixture, seed );
                var prepared = service.Prepare( candidate );
                Equal( 0, fixture.TagRequests );
                var oldRegistry = current.Registry;
                var lease = oldRegistry.AcquireLease( );
                var identity = service.Store.Write( candidate, current.Identity );
                var published = service.Publish( prepared, identity );
                Equal( "managed-document", published.Source );
                Equal( 1L, published.Revision );
                True( published.LegacyIgnored );
                True( published.Registry.TryGet( "added", out _ ) );
                True( oldRegistry.IsRetired );

                // A lease keeps the retired registry's transport usable until released.
                var stillUsable = await oldRegistry.DiscoverModelsAsync( "legacy", default );
                True( stillUsable is { Error: null, Models.Count: 2 } );
                lease.Dispose( );
                var disposed = await oldRegistry.DiscoverModelsAsync( "legacy", default );
                True( disposed.Error is not null );

                // Draft tests never touch the live set and share discovery capacity.
                var draftResult = await service.TestDraftAsync( LocalProviderDefinition( "draft", fixture.BaseUrl ), 7, default );

                Equal( "models", draftResult.Outcome );
                Equal( 7L, draftResult.DraftRevision );
                Equal( 2, draftResult.Models.Count );

                True( draftResult.Destination.StartsWith( fixture.BaseUrl.TrimEnd( '/' ), StringComparison.Ordinal ) );
                True( !service.Current.Registry.TryGet( "draft", out _ ) );
                Equal( 1L, service.Current.Revision );
                fixture.TagStatus = 500;

                var failed = await service.TestDraftAsync( LocalProviderDefinition( "draft", fixture.BaseUrl ), 8, default );

                Equal( "failed", failed.Outcome );
                True( failed.Error is not null );
                fixture.TagStatus = 200;
                fixture.EmptyTags = true;

                var empty = await service.TestDraftAsync( LocalProviderDefinition( "draft", fixture.BaseUrl ), 9, default );

                Equal( "empty", empty.Outcome );
                fixture.EmptyTags = false;
            }

            // A restart reads the managed document and ignores the legacy section.
            await using ( AiProviderSettingsService reopened = new( legacy, new AiProviderSettingsStore( path ) ) )
            {
                Equal( "managed-document", reopened.Current.Source );
                Equal( 2, reopened.Current.Options.Providers.Count );
            }

            // A damaged document disables direct inference instead of resurrecting legacy providers.
            await File.WriteAllTextAsync( path, "{ broken" );

            await using ( AiProviderSettingsService broken = new( legacy, new AiProviderSettingsStore( path ) ) )
            {
                True( !broken.Current.Enabled );
                Equal( "managed-document", broken.Current.Source );
                Equal( "invalid-config", broken.Current.Error?.Category );
                Equal( 0, broken.Current.Options.Providers.Count );

                True( broken.Describe( null )
                    .Error is not null );
            }

            True( File.Exists( path ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static AiProviderSettingsDocument CreateCandidate( ManagedAiFixtureServer fixture, AiProviderSettingsDocument seed )
        {
            return new AiProviderSettingsDocument
            {
                Revision = 1,
                Enabled = true,
                Providers = [.. seed.Providers, LocalProviderDefinition( "added", fixture.BaseUrl )]
            };
        }
    }
}
