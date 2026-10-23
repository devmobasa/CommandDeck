namespace CommandDeck;

public sealed partial class IndexCoordinator
{
    private const int DefaultInspectionWorkItems = 50;

    private const int MaximumInspectionWorkItems = 256;

    public ValueTask<CatalogInspection> InspectAsync( CancellationToken cancellationToken ) =>
        InspectAsync( 0, DefaultInspectionWorkItems, cancellationToken );

    public async ValueTask<CatalogInspection> InspectAsync( int workItemOffset, int workItemLimit, CancellationToken cancellationToken )
    {
        var boundedOffset = Math.Max( 0, workItemOffset );
        var boundedLimit = Math.Clamp( workItemLimit, 1, MaximumInspectionWorkItems );
        var live = _incremental.GetWorkItemsSnapshot( );
        if ( IsEmptyLiveInspection( live ) )
        {
            return await RunExclusiveAsync( token => _catalogOperations.InspectAsync( boundedOffset, boundedLimit, token ), cancellationToken )
                .ConfigureAwait( false );
        }

        var stored = await RunExclusiveAsync( token => _catalogOperations.InspectAsync( 0, MaximumInspectionWorkItems, token ), cancellationToken )
            .ConfigureAwait( false );

        List<IndexWorkItem> merged = [.. stored.WorkItems];

        foreach ( var work in live.Items )
        {
            var described = _files.DescribeWorkItem( work.Path, work.State, work.Reason );
            var existing = merged.FindIndex( value => string.Equals( value.Id, described.Id, StringComparison.Ordinal ) );
            if ( existing >= 0 )
            {
                merged[existing] = described;
            }
            else
            {
                merged.Add( described );
            }
        }

        merged.Sort( static ( left, right ) =>
        {
            var state = WorkStateOrder( left.State )
                .CompareTo( WorkStateOrder( right.State ) );

            if ( state != 0 )
            {
                return state;
            }

            return StringComparer.OrdinalIgnoreCase.Compare( left.RelativePath, right.RelativePath );
        } );

        var page = GetInspectionPage( boundedOffset, boundedLimit, merged );
        var exact = live.Complete && !stored.WorkItemsTruncated;
        var total = exact ? merged.Count : Math.Max( stored.TotalWorkItemCount, merged.Count );

        return CreateInspection( boundedOffset, stored, merged, page, exact, total );
    }

    private static int WorkStateOrder( string state ) => state switch
    {
        IndexWorkStates.Failed => 0,
        IndexWorkStates.Stale => 1,
        _ => 2
    };

    private static bool IsEmptyLiveInspection( (IReadOnlyList<IncrementalWorkItem> Items, bool Complete) live )
    {
        return live.Complete && live.Items.Count == 0;
    }

    private static List<IndexWorkItem> GetInspectionPage( int boundedOffset, int boundedLimit, List<IndexWorkItem> merged )
    {
        return merged
            .Skip( boundedOffset )
            .Take( boundedLimit )
            .ToList( );
    }

    private static CatalogInspection CreateInspection( int boundedOffset, CatalogInspection stored, List<IndexWorkItem> merged, List<IndexWorkItem> page,
        bool exact, long total )
    {
        return new CatalogInspection
        {
            Stats = stored.Stats,
            SourceCounts = stored.SourceCounts,
            KindCounts = stored.KindCounts,
            SourceCountsTruncated = stored.SourceCountsTruncated,
            KindCountsTruncated = stored.KindCountsTruncated,
            RebuildRequired = stored.RebuildRequired,
            FailedItemCount = CountInspectionState( merged, IndexWorkStates.Failed, exact, stored.FailedItemCount ),
            StaleItemCount = CountInspectionState( merged, IndexWorkStates.Stale, exact, stored.StaleItemCount ),
            PendingItemCount = CountInspectionState( merged, IndexWorkStates.Pending, exact, stored.PendingItemCount ),
            DetailedWorkStateAvailable = true,
            WorkItems = page,
            TotalWorkItemCount = total,
            WorkItemCountsExact = exact,
            WorkItemOffset = boundedOffset,
            WorkItemsTruncated = !exact || boundedOffset + page.Count < total,
            ExclusionDiagnostics = stored.ExclusionDiagnostics,
            TotalExclusionDiagnosticCount = stored.TotalExclusionDiagnosticCount,
            ExclusionDiagnosticsTruncated = stored.ExclusionDiagnosticsTruncated
        };
    }

    private static long CountInspectionState( List<IndexWorkItem> items, string state, bool exact, long storedCount )
    {
        var count = items.LongCount( value => value.State == state );

        return exact ? count : Math.Max( storedCount, count );
    }
}
