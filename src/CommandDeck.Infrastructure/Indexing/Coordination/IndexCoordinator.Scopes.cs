using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public sealed partial class IndexCoordinator : IIndexCoordinator
{
    private const string ScopeBeginStage = "scope-begin";
    private const string ScopeCompleteStage = "scope-complete";

    private async ValueTask<ReindexReport> ReindexScopedCoreAsync( IndexReindexScope scope, IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken )
    {
        var started = DateTimeOffset.UtcNow;
        var path = ResolveScopePath( scope );
        var directory = scope.Kind is IndexReindexScopeKinds.Directory or IndexReindexScopeKinds.Root;

        if ( !_files.TryResolveChangePath( path, out var canonicalPath ) )
        {
            throw new InvalidOperationException( "The reindex target must be inside one configured index root." );
        }

        CancelFullScan( );

        progress?.Report( new IndexProgress
        {
            Stage = ScopeBeginStage,
            RootPath = scope.Kind == IndexReindexScopeKinds.Root ? canonicalPath : null,
            CurrentPath = canonicalPath
        } );

        long indexed;

        if ( directory )
        {
            indexed = await ReplaceDirectoryAsync( path, canonicalPath, cancellationToken )
                .ConfigureAwait( false );
        }
        else
        {
            var items = await _files
                .IndexPathAsync( path, cancellationToken )
                .ConfigureAwait( false );

            await _catalog
                .ApplyPathChangesAsync( [
                    new IndexedPathChange { Path = canonicalPath, Items = items }
                ], cancellationToken )
                .ConfigureAwait( false );

            indexed = items.Count;
        }

        await _catalog
            .ReplaceExclusionDiagnosticsAsync( _files.SnapshotDiagnostics( ), cancellationToken )
            .ConfigureAwait( false );

        if ( scope.Kind == IndexReindexScopeKinds.Root )
        {
            _incremental.RecordRootScan( canonicalPath, indexed );
        }

        var completed = DateTimeOffset.UtcNow;

        progress?.Report( new IndexProgress { Stage = ScopeCompleteStage, Indexed = indexed, CurrentPath = canonicalPath } );

        return CreateScopeReport( scope, started, canonicalPath, indexed, completed );
    }

    private string ResolveScopePath( IndexReindexScope scope )
    {
        if ( scope.Kind == IndexReindexScopeKinds.Root )
        {
            if ( string.IsNullOrWhiteSpace( scope.RootId ) )
            {
                throw new InvalidOperationException( "A root reindex requires rootId." );
            }

            var result2 = FindScopeRootPath( scope );
            if ( result2 is null )
            {
                throw new InvalidOperationException( $"Unknown index root '{scope.RootId}'." );
            }

            return result2;
        }

        if ( IsInvalidPathScope( scope.Kind, scope.Path ) )
        {
            throw new InvalidOperationException( "Reindex scope must be full, root, directory, or file with its required target." );
        }

        return Path.GetFullPath( PathExpansion.Expand( scope.Path ) );
    }

    private static bool IsInvalidPathScope( string kind, [NotNullWhen( false )] string? path )
    {
        return kind is not (IndexReindexScopeKinds.File or IndexReindexScopeKinds.Directory) || string.IsNullOrWhiteSpace( path );
    }

    private string? FindScopeRootPath( IndexReindexScope scope )
    {
        return _incremental
            .GetStatus( )
            .Roots.SingleOrDefault( root => root.Id == scope.RootId )
            ?.Path;
    }

    private static ReindexReport CreateScopeReport( IndexReindexScope scope, DateTimeOffset started, string canonicalPath, long indexed,
        DateTimeOffset completed )
    {
        return new ReindexReport
        {
            StartedAt = started,
            CompletedAt = completed,
            Discovered = indexed,
            Indexed = indexed,
            Scope = scope.Kind,
            Target = canonicalPath
        };
    }
}
