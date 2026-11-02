import ".."
import QtQuick
import QtTest
import "../../../qml/common/Search/ResultWheel.js" as ResultWheel
import "../../../qml/common/Search/FileDrag.js" as FileDrag
import "../../../qml/common/Protocol.js" as Protocol

// The rules behind the mouse on result rows: one row per wheel notch, and the
// payload a dragged file offers other apps.
DeadlineTestCase {
  id: events
  property bool helperTestsEnabled: true
  when: helperTestsEnabled && windowShown

  // The Quickshell component harness calls this with its production launcher.
  // qmltestrunner cannot load Quickshell's runtime-only window types.
  function productionJourney(deck) {
    events.parent = deck.testSearchInput
    deck.close()
    deck.coachSeen = true
    deck.daemonAvailable = true
    deck.reconciliationPending = false
    deck.open("{}")
    waitForValue(deck.testSearchInput, "activeFocus", true)
    waitForCondition(() => deck.part_searchSession.activeRequestId > 0)
    const request = deck.part_searchSession.activeRequestId
    for (let index = 0; index < 32; index++) {
      deck.handleMessage({type: Protocol.Messages.Result, requestId: request, result: {
        id: "wheel-" + index, title: "Wheel row " + index,
        kind: index < 2 ? "application" : "file",
        sourceId: index < 2 ? "applications" : "files",
        invocationReference: "wheel-lease-" + index,
        actions: [{id: "settings", title: "Open Settings", kind: "open-settings"}]
      }})
    }
    deck.handleMessage({type: Protocol.Messages.Done, requestId: request})
    const list = deck.testSurfaceWindow.part_resultList
    waitForCondition(() => list.count === 34 && list.contentHeight > list.height)
    verify(deck.part_rows.get(0).isHeader)
    verify(deck.part_rows.get(3).isHeader)
    verify(waitForFrame(list))
    // The last row that starts at or above the top edge; rows may overlap a little.
    function topRow() {
      let top = -1
      for (let index = 0; index < list.count; index++) {
        const item = list.itemAtIndex(index)
        if (item && item.y <= list.contentY + 1) top = index
      }
      return top
    }
    compare(topRow(), 0)
    for (const target of [2, 3, 5]) {
      mouseWheel(list, list.width / 2, list.height / 2, 0, -120)
      compare(topRow(), target, "each notch advances one result, carrying its header")
    }
    for (const target of [3, 2, 0]) {
      mouseWheel(list, list.width / 2, list.height / 2, 0, 120)
      compare(topRow(), target, "upward notches skip section headers too")
    }
    const row = list.itemAtIndex(2)
    verify(row !== null)
    waitForFrame(row)
    mouseClick(row, 20, row.height / 2)
    compare(deck.currentResult.id, "wheel-1")
    verify(deck.settingsOpen, "a normal row click still activates through the wheel host")
    deck.closeSettings()
    deck.close()
  }

  name: "CommandDeck_result_pointer"

  // Rows 0 and 3 are section headers.
  function isHeader(row) { return row === 0 || row === 3 }

  function test_wheels_step_by_rows_and_touchpads_keep_pixels() {
    verify(ResultWheel.isWheelTurn(true, 120, 15), "a notched wheel also reports pixels")
    verify(ResultWheel.isWheelTurn(true, -240, 30))
    verify(ResultWheel.isWheelTurn(true, 15, 0), "a hi-res wheel reports no pixels")
    verify(!ResultWheel.isWheelTurn(true, 37, 8), "a touchpad reports both")
    verify(!ResultWheel.isWheelTurn(false, 120, 15), "a gesture in progress is a touchpad")
    verify(!ResultWheel.isWheelTurn(true, 0, 0))
  }

  function test_a_part_notch_carries_to_the_next_event() {
    let turn = ResultWheel.notches(40, 0)
    compare(turn.steps, 0)
    compare(turn.remainder, 40)
    turn = ResultWheel.notches(80, turn.remainder)
    compare(turn.steps, 1)
    compare(turn.remainder, 0)
    compare(ResultWheel.notches(-240, 0).steps, -2)
  }

  function test_headers_do_not_use_up_a_notch() {
    compare(ResultWheel.targetRow(1, 1, 6, isHeader), 2)
    compare(ResultWheel.targetRow(2, 1, 6, isHeader), 4, "the step skips the header")
    compare(ResultWheel.targetRow(4, -1, 6, isHeader), 2)
    compare(ResultWheel.targetRow(0, 1, 6, isHeader), 2, "a header at the top counts as its first row")
  }

  function test_the_ends_of_the_list_stop_the_wheel() {
    compare(ResultWheel.targetRow(5, 3, 6, isHeader), 5)
    compare(ResultWheel.targetRow(1, -3, 6, isHeader), 1)
  }

  function test_only_rows_that_open_a_path_can_be_dragged() {
    compare(FileDrag.filePath({ actions: [
      { kind: "launch-desktop", arguments: {} },
      { kind: "open-path", arguments: { path: "/tmp/commanddeck-pointer/notes.md" } }] }), "/tmp/commanddeck-pointer/notes.md")
    compare(FileDrag.filePath({ actions: [{ kind: "launch-desktop", arguments: {} }] }), "")
    compare(FileDrag.filePath({}), "")
    compare(FileDrag.filePath(null), "")
  }

  function test_a_drag_offers_what_a_file_manager_sends() {
    const data = FileDrag.mimeData("/tmp/commanddeck-pointer/my notes #1.md")
    compare(data["text/uri-list"], "file:///tmp/commanddeck-pointer/my%20notes%20%231.md\r\n")
    compare(data["text/plain"], "/tmp/commanddeck-pointer/my notes #1.md")
    compare(Object.keys(FileDrag.mimeData("")).length, 0)
  }
}
