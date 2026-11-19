namespace CommandDeck;

// Preferences, revoked catalog coverage and the full reindex request share one commit.
// With scope folders, roots are unchanged and only rows below them are re-evaluated.
internal interface IIndexRootChangeStore
{
    ValueTask CommitRootsAsync( IndexRootPreferences preferences, IndexExclusionPolicy previousPolicy, IndexExclusionPolicy policy, bool reset,
        CancellationToken cancellationToken, IReadOnlyList<string>? scopeFolders = null );
}
