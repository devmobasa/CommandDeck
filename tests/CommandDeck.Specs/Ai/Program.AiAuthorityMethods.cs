namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AiPreflightCapacityIsPerClientAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-capacity-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            await using var catalog = new SqliteCatalog( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( default );
            var options = OneLocalAiProvider( );
            await using AiProviderRegistry providers = new( options, new TestAiTransportFactory( ) );

            AiPreflightService service = new( providers, new FilePreviewService( new IndexingOptions { Roots = [] }, new PrivacyOptions { NeverIndex = [] } ),
                catalog.AiPreferences, ( _, _ ) => null );

            List<AiPreflight> firstClient = [];
            for ( var index = 0; index < 16; index++ )
            {
                firstClient.Add( await service.CreateAsync( "client-a", Draft( $"client a {index}" ), default ) );
            }

            var otherClient = await service.CreateAsync( "client-b", Draft( "client b retained" ), default );
            _ = await service.CreateAsync( "client-a", Draft( "client a overflow" ), default );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ConsumeAsync(
                "client-a", firstClient[0].Id, firstClient[0].PayloadDigest, firstClient[0].Generation, default ) );

            _ = await service.ConsumeAsync( "client-b", otherClient.Id, otherClient.PayloadDigest, otherClient.Generation, default );

            var invalidated = await service.CreateAsync( "client-c", Draft( "selection revision" ), default );
            await service.InvalidateAuthorityAsync( default );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ConsumeAsync(
                "client-c", invalidated.Id, invalidated.PayloadDigest, invalidated.Generation, default ) );

            var leased = await service.CreateAsync( "client-d", Draft( "transport authority lease" ), default );

            var prepared = await service.ConsumeAsync( "client-d", leased.Id, leased.PayloadDigest, leased.Generation, default );

            var transportLease = await service.AcquireTransportLeaseAsync( prepared.AuthorityRevision, default );

            var mutationPersisted = false;

            var mutation = service
                .MutateAuthorityAsync( _ =>
                {
                    mutationPersisted = true;

                    return ValueTask.FromResult( true );
                }, default )
                .AsTask( );

            await Task.Yield( );
            True( !mutation.IsCompleted );
            True( !mutationPersisted );
            transportLease.Dispose( );
            True( await mutation );
            True( mutationPersisted );

            var cancellationPreflight = await service.CreateAsync( "client-e", Draft( "committed mutation cancellation" ), default );

            using CancellationTokenSource mutationCancellation = new( );

            _ = await service.MutateAuthorityAsync( _ =>
            {
                mutationCancellation.Cancel( );

                return ValueTask.FromResult( true );
            }, mutationCancellation.Token );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await service.ConsumeAsync( "client-e", cancellationPreflight.Id,
                cancellationPreflight.PayloadDigest, cancellationPreflight.Generation, default ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task AiOperationsEnforceFixedDeadlinesAsync( )
    {
        AiOptions options = CreateOptions( );
        AiOperationDeadlines deadlines = CreateDeadlines( );
        await using ( AiProviderRegistry registry = new( options, new FixedTransportFactory( new NeverCompletingAiHandler( ) ), deadlines: deadlines ) )
        {
            var localElapsed = System.Diagnostics.Stopwatch.StartNew( );
            var local = await registry.DiscoverModelsAsync( "ollama", default );
            localElapsed.Stop( );
            var remoteElapsed = System.Diagnostics.Stopwatch.StartNew( );
            var remote = await registry.DiscoverModelsAsync( "remote", default );
            remoteElapsed.Stop( );
            Equal( "timeout", local.Error!.Category );
            Equal( "timeout", remote.Error!.Category );
            True( remoteElapsed.Elapsed > localElapsed.Elapsed + TimeSpan.FromMilliseconds( 35 ) );
            True( remoteElapsed.Elapsed < TimeSpan.FromMilliseconds( 500 ) );
        }

        AiOperationDeadlines queuedDeadlines = CreateQueuedDeadlines( );
        CountingNeverCompletingAiHandler queuedHandler = new( );
        await using ( AiProviderRegistry queuedRegistry = new( OneLocalAiProvider( ), new FixedTransportFactory( queuedHandler ), deadlines: queuedDeadlines ) )
        {
            var first = queuedRegistry
                .DiscoverModelsAsync( "ollama", default )
                .AsTask( );

            var second = queuedRegistry
                .DiscoverModelsAsync( "ollama", default )
                .AsTask( );

            await queuedHandler.TwoRequestsStarted.Task.WaitAsync( TimeSpan.FromSeconds( 2 ) );
            await Task.Delay( 100 );
            var queuedElapsed = System.Diagnostics.Stopwatch.StartNew( );
            var queued = await queuedRegistry.DiscoverModelsAsync( "ollama", default );
            queuedElapsed.Stop( );
            await Task.WhenAll( first, second );
            Equal( "timeout", queued.Error!.Category );

            if ( queuedElapsed.Elapsed >= TimeSpan.FromMilliseconds( 400 ) )
            {
                throw new InvalidOperationException( $"Queued discovery exceeded its operation budget: {queuedElapsed.Elapsed}." );
            }

            True( queuedHandler.Started is 2 or 3 );
        }

        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-overall-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            var firstLine = System.Text.Encoding.UTF8.GetBytes( "{\"model\":\"qwen\",\"message\":{\"content\":\"first\"},\"done\":false}\n" );

            RecordingAiHandler handler = new( _ => new HttpResponseMessage( System.Net.HttpStatusCode.OK )
            {
                Content = new StreamContent( new CancellableAiStream( firstLine ) )
            } );

            await using var catalog = new SqliteCatalog( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( default );

            await using AiProviderRegistry providers = new( OneLocalAiProvider( ), new FixedTransportFactory( handler ) );

            AiPreflightService preflights = new( providers,
                new FilePreviewService( new IndexingOptions { Roots = [] }, new PrivacyOptions { NeverIndex = [] } ), catalog.AiPreferences, ( _, _ ) => null );

            using AiGenerationCoordinator generations = new( preflights, deadlines );

            var preflight = await preflights.CreateAsync( "deadline-client", Draft( "overall deadline" ), default );

            List<AiDone> terminals = [];

            await generations.RunAsync( "deadline-client", preflight.Id, preflight.PayloadDigest, preflight.Generation, _ => ValueTask.CompletedTask, done =>
            {
                terminals.Add( done );

                return ValueTask.CompletedTask;
            }, default );

            Equal( 1, terminals.Count );
            Equal( "failed", terminals[0].Outcome );
            Equal( "timeout", terminals[0].Error!.Category );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static AiOptions CreateOptions( )
        {
            return new( )
            {
                Enabled = true,
                Providers =
                [
                    OneLocalAiProvider( )
                        .Providers.Single( ),
                    new AiProviderOptions
                    {
                        Id = "remote",
                        DisplayName = "Remote",
                        Kind = AiAdapterKinds.Ollama,
                        BaseUrl = "https://provider.example.test",
                        Model = "remote-model",
                        AllowRemoteEndpoint = true
                    }
                ]
            };
        }

        static AiOperationDeadlines CreateDeadlines( )
        {
            return new( )
            {
                LocalDiscovery = TimeSpan.FromMilliseconds( 25 ),
                RemoteDiscovery = TimeSpan.FromMilliseconds( 100 ),
                OverallGeneration = TimeSpan.FromMilliseconds( 35 )
            };
        }

        static AiOperationDeadlines CreateQueuedDeadlines( )
        {
            return new( )
            {
                LocalDiscovery = TimeSpan.FromMilliseconds( 300 ),
                RemoteDiscovery = TimeSpan.FromMilliseconds( 300 ),
                OverallGeneration = TimeSpan.FromSeconds( 1 )
            };
        }
    }

    private static async Task AiAttachmentsAreReauthorizedBeforeTransmissionAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-ai-reauthorize-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );
        var file = Path.Combine( root, "attachment.txt" );
        await File.WriteAllTextAsync( file, "reviewed bytes" );

        try
        {
            RecordingAiHandler handler = CreateHandler( );
            AiOptions options = OneLocalAiProvider( );

            await using var catalog = new SqliteCatalog( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( default );
            await using AiProviderRegistry providers = new( options, new FixedTransportFactory( handler ) );
            SearchResult cached = CreateAttachmentSearchResult( file );
            AiPreflightService preflights = CreatePreflights( root, catalog, providers, cached );
            using AiGenerationCoordinator generations = new( preflights );
            const string owner = "attachment-owner";

            var preflight = await preflights.CreateAsync( owner, new AiDraft
            {
                ProviderId = "ollama",
                ModelId = "qwen",
                UserText = "Review",
                Attachments =
                [
                    new AiAttachmentDraft { Kind = AiAttachmentKinds.File, ResultId = cached.Id }
                ]
            }, default );

            await File.WriteAllTextAsync( file, "changed after review" );
            List<AiDone> terminals = [];

            await generations.RunAsync( owner, preflight.Id, preflight.PayloadDigest, preflight.Generation, _ => ValueTask.CompletedTask, done =>
            {
                terminals.Add( done );

                return ValueTask.CompletedTask;
            }, default );

            Equal( 0, handler.Requests.Count );
            Equal( 1, terminals.Count );
            Equal( "failed", terminals[0].Outcome );
            Equal( "stale-preflight", terminals[0].Error!.Category );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }

        static RecordingAiHandler CreateHandler( )
        {
            return new( _ => Json( System.Net.HttpStatusCode.OK,
                "{\"model\":\"qwen\",\"message\":{\"content\":\"sent\"},\"done\":false}\n" +
                "{\"model\":\"qwen\",\"message\":{\"content\":\"\"},\"done\":true}\n" ) );
        }

        static AiPreflightService CreatePreflights( string root, SqliteCatalog catalog, AiProviderRegistry providers, SearchResult cached )
        {
            return new( providers,
                new FilePreviewService( new IndexingOptions { Roots = [new IndexRootOptions { Path = root, IncludeContentInSearch = true }] },
                    new PrivacyOptions { NeverIndex = [] } ), catalog.AiPreferences, ( _, id ) => id == cached.Id ? cached : null );
        }
    }

    private sealed class FailRollbackAtomicExchange : IAtomicFileExchange
    {
        private int _calls;

        public void Exchange( string firstPath, string secondPath )
        {
            var call = Interlocked.Increment( ref _calls );
            if ( call >= 2 )
            {
                throw new InvalidOperationException( "injected rollback failure" );
            }

            var preserved = secondPath + ".injected-original";
            File.Move( secondPath, preserved );
            File.Copy( preserved, secondPath );
            SafeRegularFile.ExchangePaths( firstPath, secondPath );
        }
    }

    private static async Task AiStreamsEnforceAnIdleDeadlineAsync( )
    {
        await using AiIdleTimeoutStream stream = new( new NeverCompletingReadStream( ), TimeSpan.FromMilliseconds( 25 ) );
        var elapsed = System.Diagnostics.Stopwatch.StartNew( );

        await ThrowsAsync<TimeoutException>( async ( ) => await stream.ReadExactlyAsync( new byte[1], CancellationToken.None ) );

        elapsed.Stop( );
        True( elapsed.Elapsed < TimeSpan.FromMilliseconds( 500 ) );
    }
}
