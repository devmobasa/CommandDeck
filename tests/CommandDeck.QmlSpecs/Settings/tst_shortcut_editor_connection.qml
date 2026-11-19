import QtQuick
import QtTest
import "../../../qml/common" as Deck
import "../../../qml/common/Protocol.js" as Protocol

TestCase {
  id: testCase
  name: "ShortcutEditorConnection"
  property bool linked: false
  property int sequence: 0
  property var sent: []

  // Like the launcher, the controller starts before its connection is ready.
  Component {
    id: editor
    Deck.ShortcutEditorController {
      ready: testCase.linked
      nextRequestId: () => ++testCase.sequence
      onRequest: value => testCase.sent = testCase.sent.concat([value])
    }
  }
  // A recording pill stops recording through the controller when cancelled.
  Component {
    id: pill
    QtObject {
      required property var controller
      property int captureGeneration: 0
      property int cancels: 0
      function captureReady(generation) {}
      function cancelRecording() {
        cancels++
        controller.stop()
      }
    }
  }

  function init() {
    linked = false
    sent = []
  }
  function connected() {
    const controller = createTemporaryObject(editor, testCase)
    linked = true
    return controller
  }

  function test_connecting_at_startup_shows_no_warning() {
    const controller = createTemporaryObject(editor, testCase)
    verify(!controller.ready)
    linked = true
    compare(controller.error, "")
  }

  function test_a_drop_with_nothing_in_flight_shows_no_warning() {
    const controller = connected()
    linked = false
    linked = true
    compare(controller.error, "")
  }

  function test_a_drop_during_a_change_warns_and_the_warning_stays() {
    const controller = connected()
    controller.change(Protocol.Operations.ShortcutSet, { id: "terminal", chord: "Super+T" }, false)
    verify(controller.active !== null)
    linked = false
    compare(controller.error, controller.interruptedError)
    verify(controller.active === null && controller.queue.length === 0)
    linked = true
    compare(controller.error, controller.interruptedError)
  }

  function test_a_drop_during_recording_cancels_it_and_warns() {
    const controller = connected()
    const recording = createTemporaryObject(pill, testCase, { controller: controller })
    controller.start(recording, "terminal")
    compare(sent[0].operation, Protocol.Operations.ShortcutRecordStart)
    verify(controller.handleMessage({ requestId: sent[0].requestId, type: Protocol.Messages.Done }))
    verify(controller.active === null)
    linked = false
    compare(recording.cancels, 1)
    verify(controller.recordingPill === null)
    compare(controller.error, controller.interruptedError)
    linked = true
    compare(controller.error, controller.interruptedError)
  }

  function test_reconnecting_clears_the_wait_message() {
    const controller = connected()
    linked = false
    controller.change(Protocol.Operations.ShortcutSet, { id: "terminal", chord: "Super+T" }, false)
    compare(controller.error, controller.waitError)
    linked = true
    compare(controller.error, "")
    compare(sent.length, 0)
  }
}
