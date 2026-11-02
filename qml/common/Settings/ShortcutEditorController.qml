import ".."
import QtQuick
import "../Protocol.js" as Protocol

QtObject {
  id: root
  required property var nextRequestId
  property bool ready: true
  property var snapshot: null
  property var queue: []
  property var active: null
  property string error: ""
  property string errorId: ""
  property var conflict: null
  property string busyId: ""
  property var recordingPill: null
  property string recordingId: ""
  property var activeRecordingPill: null
  property int activeRecordingGeneration: -1
  signal request(var value)
  signal snapshotReceived(var value)
  signal copyRequested(string text)
  onReadyChanged: { if (!ready) disconnected() }

  function submit(operation, edit, confirmed) {
    if (!ready) {
      error = "Wait for CommandDeck to reconnect and refresh shortcuts before trying again."
      return false
    }
    queue = queue.concat([{ operation: operation, shortcut: edit || ({}), confirmed: confirmed === true }])
    dispatch()
    return true
  }
  function dispatch() {
    if (!ready || active || queue.length === 0) return
    active = queue[0]
    queue = queue.slice(1)
    active.requestId = nextRequestId()
    active.version = Protocol.Version
    busyId = active.operation === Protocol.Operations.ShortcutRecordStart || active.operation === Protocol.Operations.ShortcutRecordStop
      ? "" : active.shortcut.id || ""
    activeRecordingPill = active.operation === Protocol.Operations.ShortcutRecordStart && active.shortcut.id === recordingId ? recordingPill : null
    activeRecordingGeneration = activeRecordingPill ? activeRecordingPill.captureGeneration : -1
    request(active)
  }
  function start(pill, id) {
    if (recordingPill && recordingPill !== pill) recordingPill.cancelRecording()
    recordingPill = pill
    recordingId = id || ""
    error = ""
    if (!submit(Protocol.Operations.ShortcutRecordStart, { id: id || "" })) cancelRecording()
  }
  function stop() {
    recordingPill = null
    recordingId = ""
    queue = queue.filter(value => value.operation !== Protocol.Operations.ShortcutRecordStart)
    submit(Protocol.Operations.ShortcutRecordStop)
  }
  function cancelRecording() { if (recordingPill) recordingPill.cancelRecording() }
  function isSaving(id) {
    return (active ? [active].concat(queue) : queue).some(value => value.shortcut.id === id
      && value.operation !== Protocol.Operations.ShortcutRecordStart && value.operation !== Protocol.Operations.ShortcutRecordStop)
  }
  function change(operation, edit, confirmed) {
    error = ""
    errorId = edit.id || ""
    conflict = null
    submit(operation, edit, confirmed)
  }
  function replaceConflict() {
    if (!conflict) return
    const pending = conflict
    pending.shortcut.replace = true
    change(pending.operation, pending.shortcut, true)
  }
  function handleMessage(message) {
    if (!active || message.requestId !== active.requestId) return false
    if (message.type === Protocol.Messages.PreferencesResult) {
      const editor = message.preferences ? message.preferences.shortcutEditor : null
      if (editor && editor.conflictDescription) conflict = { operation: active.operation, shortcut: active.shortcut,
        description: editor.conflictDescription }
      else if (active.operation === Protocol.Operations.ShortcutExport && editor) copyRequested(editor.lua)
      if (editor) snapshot = editor
      if (message.preferences) snapshotReceived(message.preferences)
    } else if (message.type === Protocol.Messages.Error) {
      if (active.operation === Protocol.Operations.ShortcutRecordStart && (!recordingPill || activeRecordingPill !== recordingPill || activeRecordingGeneration !== recordingPill.captureGeneration)) return true
      error = message.error || "The shortcut could not be saved."
      errorId = active.shortcut.id || ""
      if (active.operation === Protocol.Operations.ShortcutRecordStart) cancelRecording()
    } else if (message.type === Protocol.Messages.Done) {
      if (active.operation === Protocol.Operations.ShortcutRecordStart && activeRecordingPill && activeRecordingPill === recordingPill) activeRecordingPill.captureReady(activeRecordingGeneration)
      activeRecordingPill = null
      active = null
      busyId = ""
      dispatch()
    }
    return true
  }
  function disconnected() {
    cancelRecording()
    active = null
    queue = []
    busyId = ""
    snapshot = null
    error = "The connection closed. Press Escape if shortcut recording is still active."
  }
}
