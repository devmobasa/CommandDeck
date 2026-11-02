import QtQuick

// The event files retain their helper tests under qmltestrunner. Their full
// launcher journeys run here, where Quickshell registers the production windows.
QtObject {
  id: journeys
  required property var host

  function run(file) {
    const component = Qt.createComponent(Qt.resolvedUrl("../../Search/" + file))
    if (component.status !== Component.Ready) {
      host.expect(false, component.errorString())
      return
    }
    const fixture = component.createObject(null, {helperTestsEnabled: false, visible: false})
    host.expect(fixture !== null, "the launcher event fixture loads")
    if (fixture === null) return
    try {
      const deck = host.part_integratedDeck
      deck.testSurfaceEnabled = true
      fixture.productionJourney(deck)
      host.expect(true, file + " production journey passed")
    } finally {
      fixture.destroy()
    }
  }

  function steps() {
    return [
      () => journeys.run("tst_result_pointer_events.qml"),
      () => journeys.run("tst_large_type_events.qml"),
      () => { host.part_integratedDeck.testSurfaceEnabled = false }
    ]
  }
}
