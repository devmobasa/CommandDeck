namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task RunIncrementalIndexRegistrationsAsync( )
    {
        await RunAsync( "exclusion listing omits a deleted file and preserves existing and redacted entries",
            ( ) => ExclusionListingOmitsMissingPathsAsync( directory: false, renamed: null ) );

        await RunAsync( "exclusion listing omits a deleted subtree and preserves existing and redacted entries",
            ( ) => ExclusionListingOmitsMissingPathsAsync( directory: true, renamed: null ) );

        await RunAsync( "exclusion listing omits a renamed visible file until a scan",
            ( ) => ExclusionListingOmitsMissingPathsAsync( directory: false, renamed: "visible.txt" ) );

        await RunAsync( "exclusion listing omits a renamed hidden file until a scan records it",
            ( ) => ExclusionListingOmitsMissingPathsAsync( directory: false, renamed: ".renamed.txt" ) );

        await RunAsync( "full scan exclusion diagnostics survive incremental edits and refresh with changed paths",
            FullScanExclusionDiagnosticsSurviveIncrementalEditsAsync );

        await RunAsync( "full scan exclusion diagnostics retire removed roots after incremental edits",
            ( ) => ExerciseExclusionDiagnosticsRootChangeAsync( configured: false ) );

        await RunAsync( "full scan exclusion diagnostics retire disabled roots after incremental edits",
            ( ) => ExerciseExclusionDiagnosticsRootChangeAsync( configured: true ) );

        await RunAsync( "indexed folder pending scan respects catalog purge hold", ( ) => ExercisePendingRootMaintenanceAsync( repair: false ) );
        await RunAsync( "indexed folder pending scan respects catalog repair hold", ( ) => ExercisePendingRootMaintenanceAsync( repair: true ) );
        await RunAsync( "indexed folder reset requires ownership even with an empty overlay", IndexedFolderEmptyResetRequiresOwnershipAsync );

        await RunAsync( "indexed folder purge and pending full reindex survive reboot with a lost journal window",
            ( ) => IndexedFolderPurgeSurvivesPendingReindexRestartAsync( reboot: true ) );

        await RunAsync( "indexed folder purge and pending full reindex survive restart with a torn journal tail",
            ( ) => IndexedFolderPurgeSurvivesPendingReindexRestartAsync( reboot: false ) );

        await RunAsync( "cancelled full reindex keeps subsequent incremental events live", CancelledFullReindexKeepsIncrementalEventsAsync );
        await RunAsync( "indexed folder edit supersedes startup recovery and preserves incremental events", IndexedFolderEditSupersedesStartupRecoveryAsync );
        await RunAsync( "indexed folder edits coalesce full reindexes and watch existing deep files", IndexedFolderEditsReindexAllRootsAndRebuildWatchesAsync );
        await RunAsync( "indexed folder symlink reports pending and failed work", IndexedFolderSymlinkReportsPendingAndFailedWorkAsync );
        await RunAsync( "indexed folder trailing separator restores watches after nested edits", IndexedFolderTrailingSeparatorRestoresWatchesAsync );

        await RunAsync( "indexed folder missing symlink removal retires target watches",
            ( ) => ExerciseUnavailableRootLinkAsync( configured: false, retargeted: false ) );

        await RunAsync( "indexed folder missing symlink disable retires target watches",
            ( ) => ExerciseUnavailableRootLinkAsync( configured: true, retargeted: false ) );

        await RunAsync( "indexed folder retargeted symlink removal retires target watches",
            ( ) => ExerciseUnavailableRootLinkAsync( configured: false, retargeted: true ) );

        await RunAsync( "indexed folder retargeted symlink disable retires target watches",
            ( ) => ExerciseUnavailableRootLinkAsync( configured: true, retargeted: true ) );

        await RunAsync( "indexed folder non-owner runtime cannot resurrect removed rows", IndexedFolderNonOwnerCannotResurrectRemovedRowsAsync );

        await RunAsync( "indexed folder resumed work reconciles unrelated offline policy across restarts",
            IndexedFolderResumedWorkReconcilesOfflinePolicyAsync );

        await RunAsync( "indexed folder edit preserves unrelated offline policy inputs", IndexedFolderEditPreservesUnrelatedOfflinePolicyInputsAsync );
        await RunAsync( "indexed folder filesystem root retains covered Search and Home rows", IndexedFolderFilesystemRootRetainsCoveredRowsAsync );
        await RunAsync( "indexed folder scan preserves unrelated pending incremental events", IndexedFolderScanPreservesUnrelatedPendingEventsAsync );
        await RunAsync( "indexed folder purge failure rolls back preferences and coverage", IndexedFolderPurgeFailureRollsBackPreferencesAsync );

        await RunAsync( "indexed folder purge revokes content beyond surviving root extraction limits",
            ( ) => ExerciseFailedRootCommitAsync( failPurge: false, limitContent: true ) );

        await RunAsync( "indexed folder failed publication purges Search and Home and retries", IndexedFolderFailedPublicationPurgesSearchAndHomeAsync );
        await RunAsync( "indexed folder overlap preserves parent policy and disabled roots across restart", IndexedFolderOverlapAndRestartAsync );
        await RunAsync( "cancelled initial watch admission rebuilds coverage for an existing deep file", CancelledWatchAdmissionRebuildsCoverageAsync );

        Run( "a replaced parent cannot create outside native watches", ReplacedParentCannotCreateOutsideWatches );

        await RunAsync( "overflow reconciliation restores missing native watches", OverflowReconciliationRestoresMissingWatchesAsync );
        await RunAsync( "overflow reconciliation removes stale native watches", OverflowReconciliationRemovesStaleWatchesAsync );
        await RunAsync( "overflow reconciliation covers overlapping roots once", OverflowReconciliationCoversOverlappingRootsAsync );
        await RunAsync( "overflow recovery detects a retargeted retained root", OverflowRecoveryDetectsRetargetedRetainedRootAsync );
        await RunAsync( "home search defaults cover downloads code and hidden filenames", HomeSearchDefaultsCoverUserFilesAsync );
        await RunAsync( "indexing a storage parent ignores its own database and watcher journal", IndexStorageDoesNotFeedItsOwnWatcherAsync );
        await RunAsync( "full and scoped file scans skip unreadable subdirectories", FileScansSkipUnreadableDirectoriesAsync );

        Run( "filesystem root contains ordinary descendants", ( ) =>
        {
            True( CanonicalPathBoundary.TryCreate( "/", out var boundary ) );
            True( boundary!.Contains( "/usr" ) );
            True( boundary.Contains( "/home/user/Documents/note.txt" ) );
        } );

        await RunAsync( "per-root status pause and resume converge without blocking active roots", PerRootStatusPauseAndResumeConvergeAsync );
        await RunAsync( "scoped reindex preserves unrelated rows and personalization", ScopedReindexPreservesUnrelatedRowsAndPersonalizationAsync );
        await RunAsync( "scoped and overflow rescans keep the rows of a full scan", ScopedAndOverflowRescansKeepFullScanRowsAsync );
        await RunAsync( "scoped rescans stream without holding the subtree in memory", ScopedRescansStreamWithoutHoldingTheSubtreeAsync );
        await RunAsync( "staged rescans keep the search rowid link and search inclusion", StagedRescansKeepTheSearchLinkAndInclusionAsync );
        await RunAsync( "staged path batches publish as one transaction in change order", StagedPathBatchesPublishAsOneTransactionAsync );
        await RunAsync( "event storm reconciles only the affected root within a batch budget", EventStormReconcilesOnlyAffectedRootWithinBatchBudgetAsync );
        await RunAsync( "watcher overflow survives restart and reconciles only its root", WatcherOverflowSurvivesRestartAndReconcilesOnlyItsRootAsync );
        await RunAsync( "scheduled maintenance is due once and persists its result", ScheduledMaintenanceIsDueOnceAndPersistsItsResultAsync );
        await RunAsync( "incremental index applies create update rename atomic save and delete", IncrementalIndexAppliesFileLifecycleAsync );
        await RunAsync( "incremental journal replays an unapplied event after restart", IncrementalJournalReplaysAfterRestartAsync );

        Run( "incremental journal commits an event burst with a bounded number of flushes", IncrementalJournalGroupCommitsBursts );
        Run( "incremental journal window survives a crash and reconciles after a reboot", IncrementalJournalWindowRecoversAfterCrashOrReboot );

        await RunAsync( "incremental engine flushes the journal once per debounce window", IncrementalEngineFlushesOncePerDebounceWindowAsync );
        await RunAsync( "a journal window lost across a reboot reconciles the index", LostJournalWindowReconcilesAfterRebootAsync );
        await RunAsync( "a failed offline root does not abandon later roots", OfflineRootFailureDoesNotAbandonLaterRootsAsync );
        await RunAsync( "restart applies files created modified and deleted while stopped", RestartAppliesChangesMadeWhileStoppedAsync );
        await RunAsync( "resume watches a root created after startup", ResumeWatchesRootCreatedAfterStartupAsync );
        await RunAsync( "root and full reindex watch roots created after startup", ReindexWatchesRootsCreatedAfterStartupAsync );
        await RunAsync( "watcher closes the initial scan window without rescanning ordinary edits", WatcherClosesInitialScanWindowAsync );
        await RunAsync( "superseded reconciliation preserves edits in the full scan window", SupersededReconciliationPreservesScanWindowAsync );

        await RunAsync( "superseded reconciliation clears active status when the full scan precedes dispatch",
            SupersededReconciliationClearsActiveStatusAsync );

        await RunAsync( "incremental status reports watched and unwatched roots", IncrementalStatusReportsRootCoverageAsync );
        await RunAsync( "watcher replacement reconciles interrupted unrelated roots", WatcherReplacementReconcilesOtherRootsAsync );
        await RunAsync( "retargeted root resume removes old target results", RetargetedRootResumeRemovesOldResultsAsync );
        await RunAsync( "retargeted roots release obsolete external rule watches", RetargetedRootDropsObsoleteExternalControlsAsync );
        await RunAsync( "excluded trees cost no inotify watches and nested roots are watched once", ExcludedTreesCostNoInotifyWatchesAsync );
        await RunAsync( "roots below hidden and repository ancestors share the traversal policy", RootsBelowHiddenAndRepositoryAncestorsAsync );
        await RunAsync( "the watch limit leaves a visible unwatched subtree without an overflow reconcile", WatchLimitLeavesAVisibleUnwatchedSubtreeAsync );

        Run( "inotify events keep the watched tree in step with moves and new directories", InotifyEventsKeepTheWatchedTreeInStep );

        await RunAsync( "new and moved directories are watched by the traversal policy", NewAndMovedDirectoriesAreWatchedByPolicyAsync );
        await RunAsync( "extractor version changes reconcile the durable catalog state", ExtractorVersionChangeReconcilesAsync );
        await RunAsync( "incremental changes preserve rows owned by other providers", IncrementalChangesPreserveOtherProvidersAsync );
        await RunAsync( "incremental path removal deletes search rows by rowid through the path index", PathRemovalProbesSearchRowsByRowidAsync );
        await RunAsync( "incremental path removal matches only the path and its descendants", PathRemovalMatchesOnlyThePathAndDescendantsAsync );
        await RunAsync( "stale-path pager reads the path index in order without a temporary sort", StalePathPagerReadsThePathIndexInOrderAsync );
        await RunAsync( "incremental journal has one private runtime owner", IncrementalJournalHasOnePrivateRuntimeOwnerAsync );
        await RunAsync( "catalog maintenance cancels staged indexing and pauses after purge", CatalogMaintenanceSerializesWithIndexingAsync );
        await RunAsync( "catalog repair that needs a rebuild pauses indexing until a full scan", CatalogRepairPausesIndexingUntilAFullScanAsync );
        await RunAsync( "catalog writes reject a second process while the daemon owns indexing", CatalogWritesRequireProcessOwnershipAsync );
    }
}
