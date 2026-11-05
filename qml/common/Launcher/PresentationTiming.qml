import QtQuick

// Times the latest open for scripts/measure-toggle-latency.cs: when the open
// request arrived, the first frame, the first complete frame, and keyboard
// focus. Times are Date.now() milliseconds, 0 until seen. It keeps no query
// text and no results.
QtObject {
  id: timing

  // Counts opens since the launcher loaded. A protocol reconnect does not reset it.
  property double generation: 0
  property double openedAt: 0
  property double firstFrameAt: 0
  property double completeFrameAt: 0
  property double keyboardReadyAt: 0
  property double closedAt: 0
  // Frames swapped before the complete one.
  property int framesBeforeComplete: 0
  property real firstFrameRatio: 0
  property real completeFrameRatio: 0
  property string screenName: ""
  property string renderer: ""
  // Frame probes listen only while the open still waits for something.
  readonly property bool waiting: timing.openedAt > 0 && timing.closedAt === 0
    && (timing.completeFrameAt === 0 || timing.keyboardReadyAt === 0)

  function begin() {
    // Stop waiting first, so probes re-arm: their frame queues reset, and focus
    // already held is reported again.
    timing.openedAt = 0
    timing.generation++
    timing.firstFrameAt = 0
    timing.completeFrameAt = 0
    timing.keyboardReadyAt = 0
    timing.closedAt = 0
    timing.framesBeforeComplete = 0
    timing.firstFrameRatio = 0
    timing.completeFrameRatio = 0
    timing.screenName = ""
    timing.renderer = ""
    timing.openedAt = Date.now()
  }

  function end() {
    if (timing.openedAt > 0 && timing.closedAt === 0) timing.closedAt = Date.now()
  }

  // One swapped frame of the presenting window, with the state it rendered.
  function frame(screenName, ratio, complete, renderer) {
    if (!timing.waiting || timing.completeFrameAt > 0) return

    const now = Date.now()
    if (timing.firstFrameAt === 0) {
      timing.firstFrameAt = now
      timing.firstFrameRatio = ratio
      timing.screenName = screenName
      timing.renderer = renderer
    }

    if (!complete) {
      timing.framesBeforeComplete++
      return
    }

    timing.completeFrameAt = now
    timing.completeFrameRatio = ratio
  }

  function keyboardReady() {
    if (timing.waiting && timing.keyboardReadyAt === 0) timing.keyboardReadyAt = Date.now()
  }

  function describe(opened) {
    return JSON.stringify({
      generation: timing.generation,
      opened: opened === true,
      openedAt: timing.openedAt,
      firstFrameAt: timing.firstFrameAt,
      completeFrameAt: timing.completeFrameAt,
      keyboardReadyAt: timing.keyboardReadyAt,
      closedAt: timing.closedAt,
      framesBeforeComplete: timing.framesBeforeComplete,
      firstFrameRatio: timing.firstFrameRatio,
      completeFrameRatio: timing.completeFrameRatio,
      screen: timing.screenName,
      renderer: timing.renderer
    })
  }
}
