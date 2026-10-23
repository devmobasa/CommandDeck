using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public sealed partial class FileIndexSource : IDisposable
{
    private const string InvalidPathReason = "invalid-path";
    private const string MetadataReadFailedReason = "metadata-read-failed";
    private const string ContentExtractionFailedReason = "content-extraction-failed";

    private const int MaximumDiagnostics = 512;
    private const int MaximumWorkItems = 512;
    private const int MaximumDiagnosticPathCharacters = 512;
    private readonly IndexingOptions _options;
    private readonly PathPolicy _pathPolicy;
    private readonly ContentExtractor _contentExtractor;
    internal IndexExclusionPolicy Exclusions { get; }
    private readonly IndexStoragePaths? _storagePaths;
    private readonly List<IndexExclusionDiagnostic> _diagnostics = [];
    private readonly List<IndexWorkItem> _workItems = [];

    public FileIndexSource( IndexingOptions options, PrivacyOptions privacy, string? databasePath = null ) : this( options, privacy, databasePath, null )
    {
    }

    private FileIndexSource( IndexingOptions options, PrivacyOptions privacy, string? databasePath, IndexExclusionPolicy? exclusions )
    {
        _options = options;
        _pathPolicy = new PathPolicy( privacy );

        Exclusions = exclusions ?? new IndexExclusionPolicy( options, privacy, databasePath, cacheRootBoundaries: true );

        _contentExtractor = new ContentExtractor( options );
        _storagePaths = databasePath is null ? null : new IndexStoragePaths( databasePath );
    }

    internal FileIndexSource CreateScanSource( IndexingOptions options, PrivacyOptions privacy, string databasePath ) =>
        new( options, privacy, databasePath, Exclusions );

    public IReadOnlyList<IndexExclusionDiagnostic> SnapshotDiagnostics( ) =>
        _diagnostics.ToArray( );

    internal void ReplaceDiagnostics( IReadOnlyList<IndexExclusionDiagnostic> diagnostics )
    {
        _diagnostics.Clear( );
        _diagnostics.AddRange( diagnostics );
    }

    public IReadOnlyList<IndexWorkItem> SnapshotWorkItems( ) =>
        _workItems.ToArray( );

    internal IndexWorkItem DescribeWorkItem( string path, string state, string reason ) =>
        CreateWorkItem( path, state, reason );

    public async ValueTask<IReadOnlyList<IndexedItem>> IndexPathAsync( string path, CancellationToken cancellationToken )
    {
        string requestedPath;

        try
        {
            requestedPath = Path.GetFullPath( path );
            ClearScopeDiagnostics( requestedPath );
        }
        catch ( Exception exception ) when ( exception is ArgumentException or NotSupportedException )
        {
            RecordWorkItem( path, InvalidPathReason );

            return [];
        }

        var owningRoot = Exclusions.FindRoot( requestedPath );
        if ( owningRoot is null )
        {
            return [];
        }

        if ( !TryResolveAuthorizedFile( owningRoot.Value, requestedPath, out var fullPath ) )
        {
            return [];
        }

        var relative = Path.GetRelativePath( owningRoot.Value.Boundary.CanonicalRoot, fullPath );
        FileAttributes attributes;
        FileInfo info;

        try
        {
            attributes = File.GetAttributes( fullPath );

            if ( HasDisallowedAttributes( owningRoot.Value, attributes ) )
            {
                return [];
            }

            info = new FileInfo( fullPath );
        }
        catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
        {
            RecordWorkItem( fullPath, MetadataReadFailedReason );

            return [];
        }

        var policy = FileIndexPolicyResolver.Resolve( owningRoot.Value.Options, fullPath );
        string? body = null;

        if ( policy.IncludeContent )
        {
            try
            {
                var extraction = await _contentExtractor
                    .ExtractAsync( fullPath, info.Length, policy, cancellationToken )
                    .ConfigureAwait( false );

                body = extraction.Text;

                if ( extraction.FailureReason is not null )
                {
                    RecordWorkItem( fullPath, extraction.FailureReason );
                }
            }
            catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
            {
                // A transient extractor/read failure must not make an ordinary
                // watcher update delete metadata that the full scan preserves.
                body = null;
                RecordWorkItem( fullPath, ContentExtractionFailedReason );
            }
        }

        if ( HasPathChanged( requestedPath, owningRoot.Value, fullPath ) )
        {
            return [];
        }

        var modified = new DateTimeOffset( info.LastWriteTimeUtc ).ToUnixTimeMilliseconds( );
        List<IndexedItem> items = CreateFileItems( fullPath, relative, info, policy, body, modified );

        if ( HasStructuredContent( fullPath, body ) )
        {
            foreach ( var config in StructuredConfigExtractor.Extract( fullPath, body ) )
            {
                items.Add( CreateConfigurationItem( fullPath, info.Length, modified, config, policy ) );
            }
        }

        return items;
    }

    public bool TryResolveChangePath( string path, out string canonicalPath )
    {
        canonicalPath = string.Empty;
        string requestedPath;

        try
        {
            requestedPath = Path.GetFullPath( path );
        }
        catch ( Exception exception ) when ( exception is ArgumentException or NotSupportedException )
        {
            return false;
        }

        var owningRoot = Exclusions.FindRoot( requestedPath );

        return owningRoot is not null && owningRoot.Value.Boundary.TryMapPath( requestedPath, owningRoot.Value.Options.FollowSymbolicLinks, out canonicalPath );
    }

    public void Dispose( ) => _contentExtractor.Dispose( );

    private static bool HasDisallowedAttributes( (CanonicalPathBoundary Boundary, IndexRootOptions Options) owningRoot, FileAttributes attributes )
    {
        return (attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0 && !owningRoot.Options.FollowSymbolicLinks;
    }

    private static bool HasPathChanged( string requestedPath, (CanonicalPathBoundary Boundary, IndexRootOptions Options) owningRoot, string fullPath )
    {
        return !owningRoot.Boundary.IsCurrent( ) ||
               !owningRoot.Boundary.TryResolve( requestedPath, owningRoot.Options.FollowSymbolicLinks, out var verifiedPath ) ||
               !string.Equals( verifiedPath, fullPath, StringComparison.Ordinal );
    }

    private static bool HasStructuredContent( string fullPath, [NotNullWhen( true )] string? body )
    {
        return body is not null && StructuredConfigExtractor.IsStructured( fullPath );
    }

    private static List<IndexedItem> CreateFileItems( string fullPath, string relative, FileInfo info, FileIndexPolicy policy, string? body, long modified )
    {
        return
        [
            new IndexedItem
            {
                StableId = StableId.Create( ResultKinds.File, fullPath ),
                Kind = ResultKinds.File,
                Title = info.Name,
                Subtitle = relative,
                Path = fullPath,
                Icon = GuessIcon( info.Extension ),
                Body = body,
                SizeBytes = info.Length,
                ModifiedUnixMilliseconds = modified,
                ProviderId = ProviderIds.Files,
                SourceId = SourceIds.Files,
                ExtractorVersion = ContentExtractor.ExtractorVersion,
                IncludeTitleInSearch = policy.IncludeFileName,
                IncludeMetadataInSearch = policy.IncludeMetadata,
                IndexPolicies = [.. policy.Provenance]
            }
        ];
    }

    private bool TryResolveAuthorizedFile( (CanonicalPathBoundary Boundary, IndexRootOptions Options) root, string requestedPath, out string fullPath )
    {
        return root.Boundary.TryResolve( requestedPath, root.Options.FollowSymbolicLinks, out fullPath ) &&
               File.Exists( fullPath ) &&
               TryAuthorizePath( root.Boundary, root.Options, requestedPath, fullPath );
    }
}
