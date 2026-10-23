using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace CommandDeck;

public sealed partial class FileIndexSource
{
    private const int ProgressIntervalItems = 50;

    private const string RootRelativePath = "[root]";
    private const string DirectoryReadFailedReason = "directory-read-failed";

    public async IAsyncEnumerable<IndexedItem> ScanAsync( IProgress<IndexProgress>? progress, [EnumeratorCancellation] CancellationToken cancellationToken )
    {
        _diagnostics.Clear( );
        _workItems.Clear( );
        ScanCounters counters = new( );
        var roots = ResolveScanRoots( counters );
        HashSet<string> emittedStableIds = new( StringComparer.Ordinal );

        foreach ( (var boundary, var rootOptions) in roots )
        {
            var indexedBeforeRoot = counters.Indexed;

            var nestedRoots = roots
                .Where( value => value.Boundary.CanonicalRoot.Length > boundary.CanonicalRoot.Length && boundary.Contains( value.Boundary.CanonicalRoot ) )
                .Select( static value => value.Boundary.CanonicalRoot )
                .ToHashSet( StringComparer.Ordinal );

            var start = Exclusions.EnterRoot( boundary, rootOptions, out var rootExclusion );
            if ( start is null )
            {
                RecordDiagnostic( boundary, boundary.CanonicalRoot, rootExclusion!.Reason, rootExclusion.Rule );
                counters.Skipped++;
            }
            else
            {
                await foreach ( var item in ScanRootAsync( start, nestedRoots, counters, progress, cancellationToken )
                                   .ConfigureAwait( false ) )
                {
                    if ( emittedStableIds.Add( item.StableId ) )
                    {
                        yield return item;
                    }
                }
            }

            progress?.Report( CreateRootCompletionProgress( counters, boundary, indexedBeforeRoot ) );
        }
    }

    private CanonicalPathBoundary? ResolveRoot( IndexRootOptions rootOptions, ScanCounters counters )
    {
        if ( !CanonicalPathBoundary.TryCreate( rootOptions.Path, out var boundary ) || boundary is null )
        {
            RecordDiagnostic( rootOptions.Path, RootRelativePath, IndexExclusionReasons.RootUnavailable, null );
            counters.Skipped++;

            return null;
        }

        if ( TryGetRootDenial( boundary, out var lexicalRule ) )
        {
            RecordDiagnostic( rootOptions.Path, IndexingConstants.RedactedDiagnosticPath, IndexExclusionReasons.PrivacyRule, lexicalRule );

            counters.Skipped++;

            return null;
        }

        return boundary;
    }

    // Streams every file below an authorized directory. Each directory's policy
    // is settled when it is entered, so an entry costs one status call and a
    // check of its own path; every emitted file is still re-resolved against the
    // root boundary after its content is read.
    private async IAsyncEnumerable<IndexedItem> ScanRootAsync( AuthorizedDirectory start, HashSet<string> nestedRoots, ScanCounters counters,
        IProgress<IndexProgress>? progress, [EnumeratorCancellation] CancellationToken cancellationToken )
    {
        var boundary = start.Boundary;
        var rootOptions = start.Options;
        var root = boundary.CanonicalRoot;
        Stack<AuthorizedDirectory> directories = new( );

        // Without followed links every directory is reached once, through its
        // parent, so only link-following scans remember where they have been.
        HashSet<string>? visitedDirectories;

        if ( rootOptions.FollowSymbolicLinks )
        {
            visitedDirectories = new( StringComparer.Ordinal );
        }
        else
        {
            visitedDirectories = null;
        }

        directories.Push( start );

        while ( directories.Count > 0 )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            var directory = directories.Pop( );
            if ( visitedDirectories is not null && !visitedDirectories.Add( directory.Path ) )
            {
                continue;
            }

            IEnumerable<string> entries;

            try
            {
                entries = Directory.EnumerateFileSystemEntries( directory.Path );
            }
            catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
            {
                counters.Failed++;
                RecordWorkItem( directory.Path, DirectoryReadFailedReason );

                continue;
            }

            foreach ( var entry in entries )
            {
                cancellationToken.ThrowIfCancellationRequested( );
                counters.Discovered++;
                var decision = AuthorizeEntry( directory, entry );
                if ( decision.Exclusion is not null )
                {
                    RecordDiagnostic( boundary, entry, decision.Exclusion.Reason, decision.Exclusion.Rule );
                    counters.Skipped++;

                    continue;
                }

                if ( decision.MetadataFailed )
                {
                    counters.Failed++;
                    RecordWorkItem( decision.Path, MetadataReadFailedReason );

                    continue;
                }

                if ( decision.Directory is not null )
                {
                    if ( nestedRoots.Contains( decision.Directory.Path ) )
                    {
                        counters.Skipped++;

                        continue;
                    }

                    directories.Push( decision.Directory );

                    continue;
                }

                var canonicalEntry = decision.Path;
                var size = decision.Status.Size;

                (var policy, var body) = await ExtractBodyAsync( boundary, rootOptions, canonicalEntry, size, counters, cancellationToken )
                    .ConfigureAwait( false );

                if ( HasEntryPathChanged( boundary, rootOptions, canonicalEntry ) )
                {
                    counters.Skipped++;

                    continue;
                }

                var name = Path.GetFileName( canonicalEntry );
                var modified = decision.Status.ModifiedUnixMilliseconds;

                yield return CreateScannedFileItem( root, canonicalEntry, size, policy, body, name,
                    modified );

                counters.Indexed++;

                if ( HasStructuredContent( canonicalEntry, body ) )
                {
                    foreach ( var config in StructuredConfigExtractor.Extract( canonicalEntry, body ) )
                    {
                        yield return CreateConfigurationItem( canonicalEntry, size, modified, config, policy );

                        counters.Indexed++;
                    }
                }

                if ( counters.Indexed % ProgressIntervalItems == 0 )
                {
                    progress?.Report( CreateScanProgress( counters, canonicalEntry ) );
                }
            }
        }
    }

    private sealed class ScanCounters
    {
        public long Discovered { get; set; }
        public long Indexed { get; set; }
        public long Skipped { get; set; }
        public long Failed { get; set; }
    }

    private bool TryGetRootDenial( CanonicalPathBoundary boundary, [NotNullWhen( true )] out string? lexicalRule )
    {
        lexicalRule = default;

        return _pathPolicy.TryGetDenial( boundary.LexicalRoot, out lexicalRule ) || _pathPolicy.TryGetDenial( boundary.CanonicalRoot, out lexicalRule );
    }

    private static IndexProgress CreateRootCompletionProgress( ScanCounters counters, CanonicalPathBoundary boundary, long indexedBeforeRoot )
    {
        return new IndexProgress
        {
            Stage = IndexingConstants.RootCompleteStage,
            RootId = StableId.Create( IndexingConstants.IndexRootIdKind, boundary.CanonicalRoot ),
            RootPath = boundary.CanonicalRoot,
            Discovered = counters.Discovered,
            Indexed = counters.Indexed - indexedBeforeRoot,
            Skipped = counters.Skipped,
            Failed = counters.Failed,
            CurrentPath = boundary.CanonicalRoot
        };
    }

    private static IndexedItem CreateScannedFileItem( string root, string canonicalEntry, long size, FileIndexPolicy policy, string? body, string name,
        long modified )
    {
        return new IndexedItem
        {
            StableId = StableId.Create( ResultKinds.File, canonicalEntry ),
            Kind = ResultKinds.File,
            Title = name,
            Subtitle = Path.GetRelativePath( root, canonicalEntry ),
            Path = canonicalEntry,
            Icon = GuessIcon( Path.GetExtension( name ) ),
            Body = body,
            SizeBytes = size,
            ModifiedUnixMilliseconds = modified,
            ProviderId = ProviderIds.Files,
            SourceId = SourceIds.Files,
            ExtractorVersion = ContentExtractor.ExtractorVersion,
            IncludeTitleInSearch = policy.IncludeFileName,
            IncludeMetadataInSearch = policy.IncludeMetadata,
            IndexPolicies = [.. policy.Provenance]
        };
    }

    private static IndexProgress CreateScanProgress( ScanCounters counters, string canonicalEntry )
    {
        return new IndexProgress
        {
            Stage = IndexProgressStages.ScanFiles,
            Discovered = counters.Discovered,
            Indexed = counters.Indexed,
            Skipped = counters.Skipped,
            Failed = counters.Failed,
            CurrentPath = canonicalEntry
        };
    }

    private List<(CanonicalPathBoundary Boundary, IndexRootOptions Options)> ResolveScanRoots( ScanCounters counters )
    {
        List<(CanonicalPathBoundary Boundary, IndexRootOptions Options)> roots = [];

        foreach ( var rootOptions in _options.Roots )
        {
            var boundary = ResolveRoot( rootOptions, counters );
            if ( boundary is null )
            {
                continue;
            }

            roots.Add( (boundary, rootOptions) );
        }

        var ambiguousRoots = roots
            .GroupBy( static value => value.Boundary.CanonicalRoot, StringComparer.Ordinal )
            .Where( static group => group.Count( ) > 1 )
            .Select( static group => group.Key )
            .ToHashSet( StringComparer.Ordinal );

        roots.RemoveAll( value => ambiguousRoots.Contains( value.Boundary.CanonicalRoot ) );
        counters.Skipped += ambiguousRoots.Count;

        roots.Sort( static ( left, right ) => right.Boundary.CanonicalRoot.Length.CompareTo( left.Boundary.CanonicalRoot.Length ) );

        return roots;
    }
}
