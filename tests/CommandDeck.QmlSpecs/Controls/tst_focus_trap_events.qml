import ".."
import QtQuick
import QtTest
import "../../../qml/common" as CommandDeck

// Modal surfaces keep Tab and Shift+Tab on their own usable controls: focus
// wraps at the ends, skips hidden and disabled controls, and never rests on
// the surface itself. Real keys from the surface's focused control.
Item {
  id: scene
  width: 900
  height: 700

  CommandDeck.Theme { id: appTheme; loadShellTokens: false }
  LiteralTextProbe { id: literal }

  function workItems(count) {
    const values = []
    for (let index = 0; index < count; index++) {
      values.push({ id: "index-work:" + index, root: "/tmp", relativePath: "item-" + index, state: "failed", reason: "unreadable" })
    }
    return values
  }

  function inspection(count) {
    return { workItems: scene.workItems(count), failedItemCount: count, staleItemCount: 0,
      pendingItemCount: 0, totalExclusionDiagnosticCount: 0, stats: { itemCount: 10 } }
  }

  CommandDeck.IndexInspector {
    id: inspector
    anchors.fill: parent
    theme: appTheme
    onDismissed: opened = false
  }

  SignalSpy { id: stopIndexing; target: inspector; signalName: "stopIndexingRequested" }
  SignalSpy { id: excludes; target: inspector; signalName: "excludeRequested" }
  SignalSpy { id: reveals; target: inspector; signalName: "revealRequested" }

  CommandDeck.RuntimeStatusSurface {
    id: statusSurface
    visible: false
    theme: appTheme
  }

  DeadlineTestCase {
    name: "FocusTrap"
    when: windowShown

    function focused() { return inspector.Window.window.activeFocusItem }

    function cleanup() {
      inspector.opened = false
      inspector.healthDetails = ""
      inspector.missingRoots = []
      inspector.foldersRelevant = true
      statusSurface.status = null
      stopIndexing.clear()
      excludes.clear()
      reveals.clear()
    }

    // Named to run before the Tab tests below: a missing indexed folder gets a
    // Stop indexing button in the Tab cycle, and the status text drops the
    // watch-limit advice for it.
    function test_inspector_offers_stop_indexing_for_a_missing_folder() {
      statusSurface.status = { indexing: { roots: [
        { id: "missing-root", path: "/home/demo/Projects", watched: false, paused: false, missing: true,
          lastError: "Folder does not exist.", remediation: "Stop indexing this folder, or create it." },
        { id: "broken-root", path: "/home/demo/Notes", watched: false, paused: false,
          lastError: "Watch failed.", remediation: "Check the root path and inotify limits." }] } }
      compare(statusSurface.missingRoots.length, 1, "only the missing folder offers Stop indexing")
      compare(statusSurface.missingRoots[0].id, "missing-root")
      verify(statusSurface.details.indexOf("Indexed folder missing: /home/demo/Projects does not exist. "
        + "Stop indexing this folder, or create it.") >= 0)
      verify(statusSurface.details.indexOf("inotify") > statusSurface.details.indexOf("/home/demo/Notes"),
        "the watch-limit advice stays on the other folder")

      inspector.healthDetails = statusSurface.details
      inspector.missingRoots = statusSurface.missingRoots
      inspector.inspection = scene.inspection(2)
      inspector.opened = true
      waitForValue(inspector.listControl, "activeFocus", true)
      // Let the open finish placing focus before the walk starts from Close.
      wait(0)
      inspector.closeControl.forceActiveFocus()
      const stop = inspector.stopIndexingControl
      verify(stop.visible)
      compare(stop.text, "Stop indexing ~/Projects")

      keyClick(Qt.Key_Tab)
      compare(focused(), inspector.healthControl)
      keyClick(Qt.Key_Tab)
      compare(focused(), stop, "Tab reaches Stop indexing before folder management")
      keyClick(Qt.Key_Tab)
      compare(focused(), inspector.foldersControl)

      keyClick(Qt.Key_Tab, Qt.ShiftModifier)
      compare(focused(), stop)
      keyClick(Qt.Key_Space)
      compare(stopIndexing.count, 1)
      compare(stopIndexing.signalArguments[0][0], "missing-root")
      keyClick(Qt.Key_Escape)
      verify(!inspector.opened)
    }

    function test_inspector_without_issues_opens_on_close_and_shows_counts() {
      inspector.inspection = scene.inspection(0)
      inspector.opened = true
      waitForValue(inspector.closeControl, "activeFocus", true)
      verify(!inspector.listControl.visible)

      keyClick(Qt.Key_Tab)
      compare(focused(), inspector.excludedFoldersControl, "Tab reaches Manage excluded folders")
      keyClick(Qt.Key_Tab)
      compare(focused(), inspector.closeControl, "Tab wraps to Close")
      keyClick(Qt.Key_Tab, Qt.ShiftModifier)
      compare(focused(), inspector.excludedFoldersControl, "Shift+Tab wraps to the last control")
      verify(literal.labelShowing(inspector, "Indexed  10") !== null)
      verify(literal.labelShowing(inspector, "No failed, stale, or pending index items.") !== null)
      keyClick(Qt.Key_Escape)
      verify(!inspector.opened)
    }

    // One cause is one row: six unreadable files in /tmp group together, the
    // list expands with the arrow keys, and the exclude sheet edits the folder.
    function test_inspector_groups_issues_and_excludes_with_keys() {
      inspector.healthDetails = "The clipboard service is not running."
      inspector.inspection = scene.inspection(6)
      inspector.opened = true
      waitForValue(inspector.listControl, "activeFocus", true)
      compare(inspector.groups.length, 1)
      compare(inspector.listControl.rows.length, 1)
      compare(inspector.detailControl.targetFolder, "/tmp")

      keyClick(Qt.Key_Right)
      compare(inspector.listControl.rows.length, 7, "Right expands the group")
      keyClick(Qt.Key_Down)
      compare(inspector.detailControl.item.relativePath, "item-0")
      keyClick(Qt.Key_Left)
      compare(inspector.listControl.selectedRow.kind, "group", "Left returns to the group")
      keyClick(Qt.Key_Left)
      compare(inspector.listControl.rows.length, 1, "Left collapses it")

      keyClick(Qt.Key_Tab)
      compare(focused(), inspector.detailControl.excludeControl)
      keyClick(Qt.Key_Space)
      waitForValue(inspector.detailControl.sheetFieldControl, "activeFocus", true)
      compare(inspector.detailControl.sheetFieldControl.text, "/tmp")
      keyClick(Qt.Key_Escape)
      verify(inspector.opened, "Escape closes the sheet first")
      waitForValue(inspector.detailControl.excludeControl, "activeFocus", true)

      keyClick(Qt.Key_Space)
      waitForValue(inspector.detailControl.sheetFieldControl, "activeFocus", true)
      inspector.inspection = scene.inspection(6)
      verify(inspector.detailControl.sheetOpen, "a refresh of the same issues keeps the sheet open")
      verify(inspector.detailControl.sheetFieldControl.activeFocus)
      keyClick(Qt.Key_Return)
      compare(excludes.count, 1)
      compare(excludes.signalArguments[0][0], "/tmp")
      waitForValue(inspector.detailControl.excludeControl, "activeFocus", true)

      keyClick(Qt.Key_Tab)
      compare(focused(), inspector.detailControl.revealControl)
      keyClick(Qt.Key_Space)
      compare(reveals.count, 1)
      compare(reveals.signalArguments[0][0], "index-work:0")

      inspector.listControl.forceActiveFocus()
      keyClick(Qt.Key_C, Qt.ControlModifier)
      compare(inspector.statusText, "Copied the path.")
      keyClick(Qt.Key_Escape)
      verify(!inspector.opened, "Escape dismisses the inspector")
    }

    // Folders cannot help a service issue, so its inspector leaves out Manage
    // indexed folders; an index issue beside it brings the button back.
    function test_service_issue_hides_folder_management() {
      statusSurface.status = { providers: [{ id: "herdr", label: "Herdr", state: "degraded",
        detail: "Herdr status exited with code 139." }] }
      verify(!statusSurface.hasIndexIssue)
      inspector.foldersRelevant = statusSurface.hasIndexIssue
      inspector.healthDetails = statusSurface.details
      inspector.inspection = scene.inspection(2)
      inspector.opened = true
      waitForValue(inspector.listControl, "activeFocus", true)
      verify(inspector.healthControl.visible)
      verify(!inspector.foldersControl.visible)

      statusSurface.status = { providers: statusSurface.status.providers, indexing: { roots: [
        { id: "missing-root", path: "/home/demo/Projects", watched: false, paused: false, missing: true,
          lastError: "Folder does not exist." }] } }
      verify(statusSurface.hasIndexIssue)
      inspector.foldersRelevant = statusSurface.hasIndexIssue
      verify(inspector.foldersControl.visible)
    }
  }
}
