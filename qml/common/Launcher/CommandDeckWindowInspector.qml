import ".."
pragma ComponentBehavior: Bound
import QtQuick
import "../Protocol.js" as Protocol

// The index inspector as the launcher window shows it: its own page of work
// items, the excluded folders, and the actions that change them.
IndexInspector {
  id: inspector
  required property var host
  // False until the window's on-demand loader has built this surface.
  required property bool presented
  readonly property var deck: inspector.host.host
  readonly property var roots: inspector.deck.part_indexRootsController
  readonly property var details: inspector.deck.part_indexInspectionController
  readonly property var status: inspector.host.part_runtimeStatusSurface
  readonly property bool exclusionFailed: inspector.roots.errorOperation === Protocol.Operations.IndexExclusionAdd
    || inspector.roots.errorOperation === Protocol.Operations.IndexExclusionEnable
  property string pendingExclusion: ""

  theme: inspector.host.part_settingsTheme
  opened: inspector.presented && inspector.deck.indexInspectorOpen
  // Its own page of work items once loaded; runtime status until then.
  inspection: inspector.details.inspection || (inspector.deck.runtimeStatus ? inspector.deck.runtimeStatus.inspection : null)
  exclusions: inspector.roots.exclusions
  homeDirectory: inspector.deck.homeDirectory
  busy: inspector.roots.busy
  loading: inspector.details.loading && !inspector.details.inspection
  errorText: inspector.details.actionError || inspector.details.error || (inspector.exclusionFailed ? inspector.roots.error : "")
  healthDetails: inspector.status.details
  healthSummary: inspector.status.issueCount > 0 ? inspector.status.summary : ""
  missingRoots: inspector.status.missingRoots
  foldersRelevant: inspector.status.hasIndexIssue

  onOpenedChanged: {
    if (!opened) return
    inspector.details.refresh()
    inspector.roots.refresh()
  }
  onExcludeRequested: path => {
    inspector.pendingExclusion = path
    inspector.roots.change(Protocol.Operations.IndexExclusionAdd, path, "", "")
  }
  onBuiltInExclusionRequested: exclusionId => {
    inspector.pendingExclusion = exclusionId
    inspector.roots.change(Protocol.Operations.IndexExclusionEnable, "", "", exclusionId)
  }
  onRevealRequested: workItemId => inspector.details.reveal(workItemId)
  onStopIndexingRequested: rootId => inspector.roots.change(Protocol.Operations.IndexDisable, "", rootId)
  onIndexedFoldersRequested: inspector.openSettingsAt(settings => settings.showIndexedFolders())
  onExcludedFoldersRequested: inspector.openSettingsAt(settings => settings.showExcludedFolders())
  onDismissed: {
    inspector.deck.indexInspectorOpen = false
    inspector.deck.focusRevision++
  }

  function openSettingsAt(show) {
    inspector.deck.indexInspectorOpen = false
    inspector.deck.openSettings()
    Qt.callLater(function() {
      const settings = inspector.host.settingsPanel
      if (settings) show(settings)
    })
  }

  // An exclusion leaves search at once; its issues clear when the full scan
  // publishes, so the list reloads then as well.
  Connections {
    target: inspector.roots
    function onChanged() {
      if (!inspector.opened) return
      if (inspector.pendingExclusion.length > 0 && inspector.roots.error.length === 0)
        inspector.statusText = "Excluded. Its issues clear when indexing finishes."
      inspector.pendingExclusion = ""
      inspector.details.refresh()
    }
    function onReindexPendingChanged() {
      if (inspector.opened && !inspector.roots.reindexPending) inspector.details.refresh()
    }
  }

  Timer {
    interval: 2000
    repeat: true
    running: inspector.opened && inspector.roots.reindexPending && !inspector.roots.busy
    onTriggered: inspector.roots.refresh()
  }
}
