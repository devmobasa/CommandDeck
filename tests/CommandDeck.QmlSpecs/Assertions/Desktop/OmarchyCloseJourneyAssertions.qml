import "../../common"
import QtQuick
import QtTest
import "../../common" as Common
import "../../omarchy" as Omarchy
import "../../common/Protocol.js" as Protocol

Item {
  id: journey
  required property var host
  property int hideCount: 0
  property int suspendedSearchId: 0
  property var keptSurface: null

  Common.DesktopIntegration {
    id: injectedDesktop
    screens: journey.host.part_integratedDeck.desktopIntegration.screens
    focusedMonitor: screens.length > 0 ? screens[0] : null
  }

  Omarchy.CommandDeck {
    id: deck
    visible: false
    testMode: true
    desktopIntegration: injectedDesktop
    themeTokenSource: journey.host.part_integratedDeck.themeTokenSource
    property var sentRequests: []
    onProtocolRequestSent: request => sentRequests = sentRequests.concat([JSON.parse(JSON.stringify(request))])
    shell: QtObject {
      function hide(id) {
        journey.host.expect(id === deck.integrationIds.Plugin, "deferred hide targets the Omarchy plugin")
        journey.host.expect(!deck.opened, "the Omarchy shell calls close after Escape already closed the plugin")
        deck.close()
        journey.hideCount++
      }
    }
  }

  Connections {
    target: deck
    function onTestSurfaceWindowChanged() {
      if (deck.testSurfaceWindow) deck.testSurfaceWindow.mask = journey.host.pointerIsolation
    }
  }

  TestCase { id: events; parent: deck.testSearchInput; when: false }

  function focused() {
    const input = deck.testSearchInput
    const window = input ? input.Window.window : null
    return window !== null && window.active && input.activeFocus
  }

  function latestSearch(query) {
    const requests = deck.sentRequests.filter(request => request.operation === Protocol.Operations.Search)
    const request = requests[requests.length - 1]
    return request && request.query === query ? request : null
  }

  function deliver(query) {
    const request = latestSearch(query)
    deck.handleMessage({ type: Protocol.Messages.Result, requestId: request.requestId, result: {
      id: "file:kept", kind: Protocol.ResultKinds.File, sourceId: Protocol.SourceIds.Files,
      title: "kept.txt", invocationReference: "kept",
      actions: [{ id: "open", title: "Open", kind: Protocol.ActionKinds.OpenPath }] } })
    deck.handleMessage({ type: Protocol.Messages.Done, requestId: request.requestId,
      searchCompletion: { containsPrivateQuery: false } })
  }

  // Two results, so a restored selection is visible.
  function deliverTwo(query) {
    const request = latestSearch(query)
    for (const name of ["first", "second"]) {
      deck.handleMessage({ type: Protocol.Messages.Result, requestId: request.requestId, result: {
        id: "file:" + name, kind: Protocol.ResultKinds.File, sourceId: Protocol.SourceIds.Files,
        title: name + ".txt", invocationReference: name,
        actions: [{ id: "open", title: "Open", kind: Protocol.ActionKinds.OpenPath }] } })
    }
    deck.handleMessage({ type: Protocol.Messages.Done, requestId: request.requestId,
      searchCompletion: { containsPrivateQuery: false } })
  }

  function type(query) {
    events.keyClick(Qt.Key_A, Qt.ControlModifier)
    for (const character of query) events.keyClick(character)
  }

  function restore() {
    deck.close()
    deck.testSurfaceEnabled = false
  }

  function steps() {
    const steps = [{
      ready: () => true,
      message: "open the production Omarchy plugin for deferred-close retention",
      action: () => {
        host.expect(deck.desktopIntegration === injectedDesktop,
          "the Omarchy plugin uses its injected desktop fixture")
        host.part_integratedDeck.close()
        deck.testSurfaceEnabled = true
        host.expect(!focused(), "the unopened Omarchy input waits safely for its window")
        deck.coachSeen = true
        deck.daemonAvailable = true
        deck.reconciliationPending = false
        deck.applyLauncherBehavior(false, 30)
        deck.part_personalization.resetPreferences({ hideAfterAction: false })
        deck.open("{}")
      }
    }, {
      ready: () => focused() && latestSearch("") !== null,
      message: "type alpha in the Omarchy plugin with real keys",
      action: () => type("alpha")
    }, {
      ready: () => latestSearch("alpha") !== null,
      message: "deliver alpha in the Omarchy plugin",
      action: () => deliver("alpha")
    }, {
      ready: () => focused() && deck.currentResult !== null,
      message: "accept alpha and edit to beta while the Omarchy plugin stays open",
      action: () => {
        const start = deck.sentRequests.length
        events.keyClick(Qt.Key_Return)
        const invoke = deck.sentRequests.slice(start).find(request => request.operation === Protocol.Operations.Invoke)
        host.expect(invoke !== undefined, "Enter invokes alpha through the Omarchy plugin")
        if (invoke === undefined) return

        deck.handleMessage({ type: Protocol.Messages.ActionResult, requestId: invoke.requestId,
          actionResult: { succeeded: true } })
        host.expect(deck.opened && focused(), "accepted action keeps the Omarchy search ready")
        type("beta")
      }
    }]
    for (const query of ["beta", "gamma"]) {
      const callbackCount = query === "beta" ? 1 : 2
      steps.push({
        ready: () => latestSearch(query) !== null,
        message: "complete edited Omarchy search " + query,
        action: () => deliver(query)
      }, {
        ready: () => focused() && deck.currentResult !== null,
        message: "Escape closes the Omarchy session for " + query,
        action: () => {
          keptSurface = deck.testSurfaceWindow._backingWindow
          events.keyClick(Qt.Key_Escape)
          host.expect(!deck.opened, "Escape closes the plugin before deferred shell.hide")
        }
      }, {
        ready: () => hideCount === callbackCount && !deck.testSurfaceWindow.shown,
        message: "reopen after deferred shell.hide calls close again for " + query,
        action: () => {
          host.expect(deck.testSurfaceWindow.backingWindowVisible && deck.testSurfaceWindow._backingWindow === keptSurface,
            "a closed launcher keeps its surface mapped for " + query)
          deck.open("{}")
          host.expect(deck.testSurfaceWindow._backingWindow === keptSurface, "reopening reuses the kept surface for " + query)
          host.expect(deck.queryText === query,
            "the Omarchy callback preserves " + query + "; got " + JSON.stringify(deck.queryText))
        }
      }, {
        ready: () => focused() && latestSearch(query) !== null,
        message: "the reopened Omarchy search is keyboard ready for " + query,
        action: () => {
          host.expect(deck.testSearchInput.activeFocus, "reopening restores focus without a preparatory click")
          if (query === "beta") type("gamma")
        }
      })
    }
    return steps.concat(outputLossSteps())
  }

  // Omarchy's hide calls close(). After an output loss that close must keep the
  // suspended draft, and the suspension must not hold the results.
  function outputLossSteps() {
    return [{
      ready: () => focused() && latestSearch("gamma") !== null,
      message: "deliver the reopened Omarchy search before the output loss",
      action: () => deliver("gamma")
    }, {
      ready: () => focused() && deck.currentResult !== null,
      message: "type a draft over delivered results in the Omarchy plugin",
      action: () => type("draft")
    }, {
      ready: () => latestSearch("draft") !== null,
      message: "select the second draft result before the output loss",
      action: () => {
        deliverTwo("draft")
        events.keyClick(Qt.Key_Down)
      }
    }, {
      ready: () => deck.currentResult !== null && deck.currentResult.id === "file:second",
      message: "the Omarchy output disappears with a draft result selected",
      action: () => {
        suspendedSearchId = latestSearch("draft").requestId
        const hides = hideCount
        injectedDesktop.screens = []
        host.expect(hideCount === hides + 1, "an output loss hides the Omarchy plugin through the shell")
        host.expect(deck.suspendedForScreen && deck.queryText === "draft",
          "Omarchy's close after an output loss keeps the suspended draft; got " + JSON.stringify(deck.queryText))
        host.expect(deck.part_rows.count === 0 && deck.currentResult === null, "a suspended session holds no results")
      }
    }, {
      ready: () => deck.testSurfaceWindow === null,
      message: "the lost output's launcher window is gone",
      action: () => {
        injectedDesktop.screens = Qt.binding(() => journey.host.part_integratedDeck.desktopIntegration.screens)
        deck.open("{}")
      }
    }, {
      ready: () => focused() && deck.testSearchInput.text === "draft" && latestSearch("draft") !== null
        && latestSearch("draft").requestId > suspendedSearchId,
      message: "reopening after the output returns restores the draft and searches again",
      action: () => {
        host.expect(!deck.suspendedForScreen && deck.opened, "the reopened session is no longer suspended")
        deliverTwo("draft")
      }
    }, {
      ready: () => deck.currentResult !== null,
      message: "the fresh results restore the selection from before the output loss",
      action: () => {
        host.expect(deck.currentResult.id === "file:second",
          "the resumed draft keeps its selected result; got " + deck.currentResult.id)
        injectedDesktop.screens = []
        host.expect(deck.suspendedForScreen, "a second output loss suspends the session again")
        // A completed extension handoff dismisses the launcher this way.
        deck.dismiss()
        host.expect(!deck.suspendedForScreen && deck.queryText === "",
          "a dismissal ends a suspended session; got " + JSON.stringify(deck.queryText))
        injectedDesktop.screens = Qt.binding(() => journey.host.part_integratedDeck.desktopIntegration.screens)
      }
    }]
  }
}
