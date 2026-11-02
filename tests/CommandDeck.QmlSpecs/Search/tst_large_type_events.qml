import ".."
import QtQuick
import QtTest
import "../../../qml/common" as CommandDeck
import "../../../qml/common/Launcher/LargeType.js" as LargeType

EventPathFixture {
  id: scene
  property bool helperTestsEnabled: true
  function productionJourney(deck) { events.productionJourney(deck) }

  SignalSpy { id: requested; target: scene.part_searchField; signalName: "largeTypeRequested" }

  CommandDeck.LargeTypeSurface {
    id: surface
    anchors.fill: parent
    z: 1000
    theme: scene.part_theme
    onDismissed: text = ""
  }
  SignalSpy { id: dismissals; target: surface; signalName: "dismissed" }

  EventPathTestCase {
    id: events
    when: scene.helperTestsEnabled && windowShown

    function productionJourney(deck) {
      events.parent = deck.testSearchInput
      deck.close()
      deck.coachSeen = true
      deck.daemonAvailable = true
      deck.reconciliationPending = false
      deck.open("{}")
      const input = deck.testSearchInput
      waitForValue(input, "activeFocus", true)
      keyClick(Qt.Key_A, Qt.ControlModifier)
      typeText("original query")
      compare(input.text, "original query")
      for (const route of ["Escape", "Ctrl+L", "click"]) {
        keyClick(Qt.Key_L, Qt.ControlModifier)
        compare(deck.largeTypeText, "original query")
        const overlay = largeTypeSurface(deck.testSurfaceWindow.contentItem)
        verify(overlay !== null)
        waitForValue(overlay, "activeFocus", true)
        compare(overlay.label.text, "original query")
        if (route === "Escape") keyClick(Qt.Key_Escape)
        else if (route === "Ctrl+L") keyClick(Qt.Key_L, Qt.ControlModifier)
        else mouseClick(overlay)
        // Deliver typing immediately: queued focus repair must not swallow it.
        keyClick(Qt.Key_X)
        compare(deck.largeTypeText, "")
        verify(input.activeFocus, route + " restores search focus")
        compare(input.text, "original queryx", route + " preserves the query and accepts immediate typing")
        compare(deck.queryText, "original queryx")
        keyClick(Qt.Key_Backspace)
        keyClick(Qt.Key_L, Qt.ControlModifier)
        compare(deck.largeTypeText, "original query", "Large Type reopens after " + route)
        waitForValue(overlay, "activeFocus", true)
        keyClick(Qt.Key_Escape)
        waitForValue(input, "activeFocus", true)
      }
      deck.close()
    }

    function largeTypeSurface(parent) {
      if (parent.Accessible.name === "Large Type") return parent
      for (const child of parent.children || []) {
        const found = largeTypeSurface(child)
        if (found) return found
      }
      return null
    }
    fixture: scene
    name: "CommandDeck_large_type"

    function cleanup() {
      surface.text = ""
      dismissals.clear()
      requested.clear()
    }

    function test_it_shows_an_answer_then_the_text_then_the_selected_title() {
      compare(LargeType.textFor("100 * 5", { kind: "calculator", title: "500" }), "500")
      compare(LargeType.textFor("  call me  ", { kind: "application", title: "Firefox" }), "call me")
      compare(LargeType.textFor("", { kind: "application", title: "Firefox" }), "Firefox")
      compare(LargeType.textFor("   ", null), "")
    }

    function test_ctrl_l_asks_with_the_typed_text() {
      mouseClick(fixture.part_searchField.inputControl)
      fixture.part_searchField.setInputText("call me")
      keyClick(Qt.Key_L, Qt.ControlModifier)
      compare(requested.count, 1)
      compare(requested.signalArguments[0][0], "call me")
      compare(fixture.part_searchField.inputControl.text, "call me", "the search stays as typed")
      fixture.part_searchField.setInputText("")
    }

    function test_every_way_out_returns_and_other_keys_do_nothing() {
      surface.text = "call me"
      waitForValue(surface, "activeFocus", true)
      verify(surface.visible)
      compare(surface.label.text, "call me")
      compare(surface.label.textFormat, Text.PlainText, "typed text is never markup")

      keyClick(Qt.Key_A)
      keyClick(Qt.Key_Tab)
      compare(dismissals.count, 0)
      verify(surface.activeFocus, "keys stay on the surface")

      keyClick(Qt.Key_Escape)
      compare(dismissals.count, 1)
      verify(!surface.visible)

      surface.text = "again"
      waitForValue(surface, "activeFocus", true)
      keyClick(Qt.Key_L, Qt.ControlModifier)
      compare(dismissals.count, 2)

      surface.text = "and again"
      waitForValue(surface, "activeFocus", true)
      mouseClick(surface)
      compare(dismissals.count, 3)
    }

    function test_a_long_text_fits_the_screen() {
      surface.text = "a very long sentence ".repeat(30)
      waitForValue(surface, "activeFocus", true)
      verify(waitForFrame(surface.label))
      verify(surface.label.fontInfo.pixelSize < surface.label.font.pixelSize, "the text shrinks to fit")
    }
  }
}
