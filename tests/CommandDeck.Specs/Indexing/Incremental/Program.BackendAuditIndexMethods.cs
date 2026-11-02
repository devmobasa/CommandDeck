namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditIncrementalDescendantsAsync( )
    {
        var sandbox = CreateIncrementalSpecRoot( );
        try
        {
            var documents = Directory.CreateDirectory( Path.Combine( sandbox, "documents" ) ).FullName;
            var options = CreateIncrementalIndexOptions( Path.Combine( sandbox, "catalog.db" ), documents );
            await using SqliteCatalog catalog = new( options.Storage );
            await catalog.InitializeAsync( default );
            FileIndexSource files = new( options.Indexing, options.Privacy, options.Storage.DatabasePath );
            IncrementalEventJournal journal = new( options.Storage.DatabasePath );
            True( journal.Initialize( ) );
            await using IncrementalIndexEngine engine = new( options, files.Exclusions, ApplyAsync,
                static ( _, _ ) => ValueTask.FromResult( true ), journal );

            foreach ( var kind in new[] { IncrementalEventKind.Upsert, IncrementalEventKind.Rename } )
            {
                var subtree = Directory.CreateDirectory( Path.Combine( documents, "subtree.txt" ) ).FullName;
                await File.WriteAllTextAsync( Path.Combine( subtree, "oldchild.txt" ), "obsoletechildmarker" );
                await catalog.ReplaceAllAsync( files.ScanDirectoryAsync( documents, default ), null, default );
                True( (await SearchAsync( catalog, "obsoletechildmarker" )).Count > 0 );
                var outside = Path.Combine( sandbox, "outside-" + kind );
                Directory.Move( subtree, outside );
                await File.WriteAllTextAsync( subtree, "replacementfilemarker" );

                // Replay one durable window: the real watcher may additionally
                // request reconciliation, which would mask the coalescing error.
                journal.Append( IncrementalEventKind.Delete, subtree, null );
                journal.Append( kind, subtree, kind == IncrementalEventKind.Rename ? Path.Combine( documents, "before.txt" ) : null );
                var batch = engine.CapturePendingBatch( );
                if ( batch is null )
                {
                    throw new InvalidOperationException( "The queued events must produce a batch." );
                }

                await engine.ProcessPendingAsync( batch, default );
                True( (await SearchAsync( catalog, "replacementfilemarker" )).Count > 0 );
                Equal( 0, (await SearchAsync( catalog, "oldchild" )).Count );
                Equal( 0, (await SearchAsync( catalog, "obsoletechildmarker" )).Count );
                Equal( 1L, (await catalog.GetStatsAsync( default )).ItemCount );
                File.Delete( subtree );
            }

            async ValueTask<bool> ApplyAsync( IReadOnlyList<IncrementalPathRequest> paths, CancellationToken token )
            {
                List<IndexedPathChange> changes = [];
                foreach ( var request in paths )
                {
                    var items = await files.IndexPathAsync( request.Path, token );
                    changes.Add( new( ) { Path = request.Path, RemoveDescendants = request.RemoveDescendants, Items = items } );
                }

                await catalog.ApplyPathChangesAsync( changes, token );

                return true;
            }
        }
        finally
        {
            DeleteIncrementalSpecRoot( sandbox );
        }
    }
}
