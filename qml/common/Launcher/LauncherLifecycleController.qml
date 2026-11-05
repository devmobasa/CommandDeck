import ".."
pragma ComponentBehavior: Bound
import QtQuick
import "Actions/DeckSearchActions.js" as DeckSearchActions
import "../Protocol.js" as Protocol

QtObject {
  id: lifecycle
  required property var host
  property string frontendInstance: newInstance()
  property double openGeneration: 0
  readonly property PresentationTiming timing: PresentationTiming {}

  function newInstance() {
    return "xxxxxxxxxxxx4xxxyxxxxxxxxxxxxxxx".replace(/[xy]/g, function(value) {
      const digit = Math.floor(Math.random() * 16)
      return (value === "x" ? digit : (digit & 3) | 8).toString(16)
    })
  }

  function reconnected() {
    frontendInstance = newInstance()
    openGeneration = host.opened ? 1 : 0
    host.part_clipboardController.close(false)
  }

  function toggle(payload) { return DeckSearchActions.toggle(lifecycle.host, payload) }
  function open(payload) {
    if (openGeneration >= 9007199254740991) {
      host.part_protocol.restart()
      return
    }
    // Publish before any surface becomes visible, on every launcher-open path.
    openGeneration++
    timing.begin()
    host.part_clipboardController.close(false)
    return DeckSearchActions.open(lifecycle.host, payload)
  }
  // Ends what this open holds, such as its clipboard sessions in the daemon,
  // without clearing the search. A close and an output suspension both do it.
  function release() {
    timing.end()
    if (openGeneration > 0 && host.daemonAvailable) {
      host.part_protocol.send({version: Protocol.Version, requestId: ++host.requestSequence,
        operation: Protocol.Operations.LauncherOpenCancel,
        clipboard: {frontendInstance: frontendInstance, openGeneration: openGeneration}})
    }
    host.part_clipboardController.close(false)
  }
  function close() {
    // Omarchy's deferred shell.hide can close the same session again. After an
    // output loss it closes the suspended session, which keeps its draft.
    if (!host.opened) return

    release()
    DeckSearchActions.close(lifecycle.host)
    prewarm()
  }
  // While closed, the kept window shows Home at the next open unless a kept
  // search or a suspended draft comes back, so Home is searched ahead of time.
  function prewarm() {
    if (!host.opened && !host.suspendedForScreen && host.lastSearch === null && host.daemonAvailable) host.part_searchSession.prewarm()
  }
  // A dismissal ends a session even if an output loss suspended it; only the
  // shell's repeated close() keeps a suspended draft.
  function dismiss() {
    if (host.suspendedForScreen) DeckSearchActions.close(lifecycle.host)
    return DeckSearchActions.dismiss(lifecycle.host)
  }
  function presentationState() { return timing.describe(host.opened) }
}
