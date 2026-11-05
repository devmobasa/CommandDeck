import QtQuick

// Reports the presenting window's frames and keyboard focus to the open's
// PresentationTiming. It connects to the window only while that open waits.
Item {
  id: probe

  required property var timing
  required property bool presenting
  // The card shows its final state: opaque, in place, with settled results.
  required property bool complete
  required property bool inputFocused
  required property string screenName
  required property real ratio

  readonly property bool listening: probe.presenting && probe.timing.waiting
  readonly property bool keyboardReady: probe.listening && probe.Window.active && probe.inputFocused
  // The state each synchronized frame rendered, oldest first, tagged with its
  // open. A threaded renderer reports a swap after the next frame may have
  // synchronized, so a swap takes the oldest entry rather than the current state.
  property var renderedStates: []
  // The software renderer swaps on this thread right after it synchronizes, and
  // can synchronize without swapping, so only its latest state belongs to the
  // next swap.
  property bool swapsAtOnce: probe.GraphicsInfo.api === GraphicsInfo.Software

  width: 0
  height: 0
  visible: false

  onListeningChanged: probe.renderedStates = []
  onKeyboardReadyChanged: {
    if (probe.keyboardReady) probe.timing.keyboardReady()
  }

  function rendererName() {
    switch (probe.GraphicsInfo.api) {
      case GraphicsInfo.Software: return "software"
      case GraphicsInfo.OpenGL: return "opengl"
      case GraphicsInfo.Vulkan: return "vulkan"
      default: return "other"
    }
  }

  // A frame synchronized the current state for rendering.
  function synchronized() {
    const states = probe.renderedStates
    if (probe.swapsAtOnce) states.length = 0
    states.push({ generation: probe.timing.generation, complete: probe.complete })
    // A synchronized frame with no changes is not swapped; drop stale entries.
    if (states.length > 3) states.shift()
  }

  // A frame was swapped. Only a frame synchronized for this open counts.
  function swapped() {
    const state = probe.renderedStates.shift()
    if (state === undefined || state.generation !== probe.timing.generation) return

    probe.timing.frame(probe.screenName, probe.ratio, state.complete, probe.rendererName())
  }

  Connections {
    target: probe.listening ? probe.Window.window : null

    function onAfterAnimating() { probe.synchronized() }
    function onFrameSwapped() { probe.swapped() }
  }
}
