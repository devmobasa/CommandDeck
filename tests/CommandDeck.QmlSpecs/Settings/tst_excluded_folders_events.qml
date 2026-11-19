import ".."
import QtQuick
import QtTest
import "../../../qml/common/Protocol.js" as Protocol

EventPathFixture {
  id: scene
  LiteralTextProbe { id: literal }
  EventPathTestCase {
    fixture: scene
    name: "CommandDeck_excluded_folders"

    function builtIns() {
      return [
        { id: "xdg-cache", path: "/home/demo/.cache", source: "builtin", category: "caches", description: "Application caches",
          enabled: true, enabledByDefault: true, exists: true },
        { id: "podman-layers", path: "/home/demo/.local/share/containers/storage/overlay", source: "builtin", category: "containers",
          description: "Podman image layers", enabled: true, enabledByDefault: true, exists: true },
        { id: "steam", path: "/home/demo/.local/share/Steam", source: "builtin", category: "applications", description: "Steam library",
          enabled: false, enabledByDefault: false, exists: false }
      ]
    }
    function user(id, path) {
      return { id: id, path: path, source: "user", enabled: true, enabledByDefault: false, exists: true }
    }
    function reply(exclusions) {
      const panel = fixture.part_settingsPanel
      panel.exclusionsReply = exclusions
      panel.respondRoots(panel.part_indexRootsController.activeRequestId, [])
    }
    function lastChange() {
      const changes = fixture.part_settingsPanel.rootChanges
      return changes[changes.length - 1]
    }
    function focusByTab(control) {
      for (let index = 0; index < 120 && !control.activeFocus; index++) keyClick(Qt.Key_Tab)
      verify(control.activeFocus, "excluded folder controls must be reachable by Tab")
    }
    function openCard(exclusions) {
      const panel = fixture.part_settingsPanel
      panel.exclusionsReply = exclusions
      panel.part_indexRootsController.error = ""
      panel.part_indexRootsController.errorOperation = ""
      panel.part_indexRootsController.available = false
      panel.part_indexRootsController.available = true
      panel.delayRootReads = false
      panel.rootChanges = []
      panel.visible = true
      nextTurn()
      waitForValue(panel.searchControl, "activeFocus", true)
      typeText("excluded folders")
      compare(panel.currentPage, "search")
      const card = panel.part_excludedFoldersCard
      tryVerify(() => card.builtIns.length === 3, 30000, "the card shows the daemon's exclusions")
      focusByTab(card.pathControl)
      return card
    }

    function cleanup() {
      const panel = fixture.part_settingsPanel
      panel.part_settingsSearch.text = ""
      panel.part_excludedFoldersCard.editingId = ""
      panel.part_excludedFoldersCard.lastOperation = ""
      panel.part_excludedFoldersCard.expanded = ({})
      panel.exclusionsReply = []
    }

    function test_add_shows_errors_on_this_card_only_and_clears_on_success() {
      const panel = fixture.part_settingsPanel
      const card = openCard(builtIns())
      compare(card.pathControl.Accessible.name, "Folder to exclude")
      typeText("~/Videos/raw")
      keyClick(Qt.Key_Return)
      compare(lastChange().operation, Protocol.Operations.IndexExclusionAdd)
      compare(lastChange().path, "~/Videos/raw")

      panel.part_indexRootsController.handleMessage({ requestId: panel.part_indexRootsController.activeRequestId,
        type: Protocol.Messages.Error, error: "That folder is already excluded." })
      waitForValue(card.errorControl, "visible", true)
      compare(card.errorControl.text, "That folder is already excluded.")
      verify(!panel.part_indexRootsCard.errorControl.visible, "the indexed folders card keeps its own errors")
      verify(card.pathControl.activeFocus)

      keyClick(Qt.Key_Return)
      reply(builtIns().concat([user("user-1", "/home/demo/Videos/raw")]))
      tryCompare(card, "renderedUserCount", 1)
      compare(card.pathControl.text, "")
      compare(card.statusControl.text, "Saved. The folder is out of search.")
      verify(!card.errorControl.visible)
      verify(literal.labelShowing(card, "~/Videos/raw") !== null, "a home path shows as ~/…")
    }

    function test_edit_keeps_text_on_error_and_escape_cancels() {
      const panel = fixture.part_settingsPanel
      const card = openCard(builtIns().concat([user("user-1", "/home/demo/old")]))
      tryCompare(card, "renderedUserCount", 1)
      const row = card.userRowAt(0)
      focusByTab(row.editControl)
      keyClick(Qt.Key_Space)
      waitForValue(row.editFieldControl, "activeFocus", true)
      compare(row.editFieldControl.text, "~/old")

      keyClick(Qt.Key_A, Qt.ControlModifier)
      typeText("relative")
      keyClick(Qt.Key_Return)
      compare(lastChange().operation, Protocol.Operations.IndexExclusionUpdate)
      compare(lastChange().exclusionId, "user-1")
      panel.part_indexRootsController.handleMessage({ requestId: panel.part_indexRootsController.activeRequestId,
        type: Protocol.Messages.Error, error: "Use an absolute path or one that starts with ~/." })
      waitForValue(card.errorControl, "visible", true)
      verify(row.editing, "an invalid edit stays open")
      compare(row.editFieldControl.text, "relative")

      keyClick(Qt.Key_Escape)
      verify(!row.editing)
      waitForValue(row.editControl, "activeFocus", true)
      verify(panel.visible, "Escape in the editor does not close Settings")
    }

    function test_remove_moves_focus_and_builtins_toggle_by_category() {
      const panel = fixture.part_settingsPanel
      const card = openCard(builtIns().concat([user("user-1", "/home/demo/a"), user("user-2", "/home/demo/b")]))
      tryCompare(card, "renderedUserCount", 2)
      const first = card.userRowAt(0)
      focusByTab(first.removeControl)
      keyClick(Qt.Key_Space)
      compare(lastChange().operation, Protocol.Operations.IndexExclusionRemove)
      compare(lastChange().exclusionId, "user-1")
      reply(builtIns().concat([user("user-2", "/home/demo/b")]))
      tryCompare(card, "renderedUserCount", 1)
      tryVerify(() => card.userRowAt(0).editControl.activeFocus, 30000, "focus moves to the next row")

      const header = findChild(card, "exclusionCategory-caches")
      compare(header.text, "▸  Caches   1 of 1 excluded", "categories start collapsed with a count")
      verify(findChild(card, "builtInExclusion-xdg-cache") === null)
      focusByTab(header)
      keyClick(Qt.Key_Space)
      const toggle = findChild(card, "builtInExclusion-xdg-cache")
      verify(toggle !== null && toggle.checked, "an expanded category lists its folders, switched on")
      compare(toggle.text, "~/.cache")
      focusByTab(toggle)
      keyClick(Qt.Key_Space)
      compare(lastChange().operation, Protocol.Operations.IndexExclusionDisable)
      compare(lastChange().exclusionId, "xdg-cache")
    }
  }
}
