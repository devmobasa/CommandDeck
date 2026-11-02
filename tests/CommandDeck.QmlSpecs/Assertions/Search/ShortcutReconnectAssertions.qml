import "../../common"
import QtQuick
import "../../common" as CommandDeck
import "../../common/Protocol.js" as Protocol

QtObject {
  id: spec
  required property var host
  property double deadline: 0
  property CommandDeck.CommandDeck deck: CommandDeck.CommandDeck {
    testMode: true
    visible: false
    property var sent: []
    onProtocolRequestSent: request => sent = sent.concat([request])
  }
  property Timer poll: Timer { interval: 10; repeat: true; onTriggered: spec.advance() }

  function start() {
    host.pendingAsyncSpecs++
    deadline = Date.now() + 5000
    deck.part_protocol.bridge.command = ["/bin/cat"]
    deck.part_protocol.awaitingReconnect = true
    deck.part_protocol.enabled = true
    poll.start()
  }

  function advance() {
    if (!deck.part_protocol.bridge.running && Date.now() < deadline) return
    poll.stop()
    const protocol = deck.part_protocol
    const controller = deck.part_shortcutEditor
    host.expect(protocol.bridge.running, "shortcut reconnect uses an isolated echo bridge")
    controller.change(Protocol.Operations.ShortcutSet, {id: "test", chord: "Super+Q"}, false)
    const early = controller.active ? controller.active.requestId : -1
    protocol.handleBridgeMessage({requestId: early, type: Protocol.Messages.PreferencesResult,
      preferences: {shortcutEditor: {bindings: []}}})
    protocol.handleBridgeMessage({requestId: early, type: Protocol.Messages.Done})
    host.expect(!controller.active && !deck.sent.some(value => value.operation === Protocol.Operations.ShortcutSet),
      "shortcut edits before reconnect Pong are refused without an orphaned request")
    protocol.handleBridgeMessage({requestId: protocol.reconnectProbeRequestId, type: Protocol.Messages.Pong})
    protocol.handleBridgeMessage({requestId: protocol.reconnectProbeRequestId, type: Protocol.Messages.Done})
    deck.reconciliationPending = true
    controller.change(Protocol.Operations.ShortcutSet, {id: "test", chord: "Super+W"}, false)
    host.expect(!controller.active, "shortcut edits wait for snapshot reconciliation")
    deck.reconciliationPending = false
    controller.change(Protocol.Operations.ShortcutSet, {id: "test", chord: "Super+E"}, false)
    host.expect(!!controller.active && controller.active.shortcut.chord === "Super+E",
      "a fresh shortcut edit dispatches after readiness recovers")
    const sent = deck.sent.filter(value => value.operation === Protocol.Operations.ShortcutSet).length
    protocol.awaitingReconnect = true
    host.expect(!controller.active && controller.queue.length === 0,
      "losing readiness clears an uncertain shortcut edit")
    protocol.handleBridgeMessage({requestId: protocol.reconnectProbeRequestId, type: Protocol.Messages.Pong})
    deck.reconciliationPending = false
    host.expect(deck.sent.filter(value => value.operation === Protocol.Operations.ShortcutSet).length === sent,
      "reconnect does not replay an uncertain shortcut edit")
    protocol.enabled = false
    host.pendingAsyncSpecs--
  }
}
