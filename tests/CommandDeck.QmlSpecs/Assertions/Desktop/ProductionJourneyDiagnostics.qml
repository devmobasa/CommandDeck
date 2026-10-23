import "../../common"
import QtQml

QtObject {
  required property var journey

  function diagnostics(step) {
    const deck = journey.deck
    const window = deck.testSurfaceWindow
    const input = deck.testSearchInput
    const session = deck.part_searchSession
    const palette = window ? window.part_palette : null
    return "predicate=" + step.ready.toString() + "; " + JSON.stringify({
      step: journey.stepIndex, opened: deck.opened, windowVisible: window ? window.visible : null,
      windowActive: window ? window.active : null, query: deck.queryText,
      inputText: input ? input.text : null, inputFocus: input ? input.activeFocus : null,
      selectedIndex: deck.selectedIndex, result: deck.currentResult,
      requestId: session.activeRequestId, inFlight: session.inFlight,
      debounceRunning: session.debounce.running, lastRequests: deck.sentRequests.slice(-3),
      workAreaHeight: journey.workArea.height, baselineWorkAreaHeight: journey.baselineWorkAreaHeight,
      palette: palette ? [palette.x, palette.y, palette.width, palette.height, palette.opacity] : null,
      undo: deck.lastUndo, helpOpen: deck.helpOpen, actionPanelOpen: deck.actionPanelOpen
    }) + (step.diagnostics ? "; " + step.diagnostics() : "")
  }
}
