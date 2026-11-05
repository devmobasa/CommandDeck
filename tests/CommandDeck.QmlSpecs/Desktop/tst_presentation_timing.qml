import ".."
import QtQuick
import QtTest
import "../../../qml/common" as Deck

DeadlineTestCase {
  id: testCase
  name: "PresentationTiming"
  width: 200
  height: 200
  // Visible, so that a color change below renders a real frame.
  visible: true
  when: windowShown

  Component { id: timingFactory; Deck.PresentationTiming {} }
  Component {
    id: probeFactory
    Deck.PresentationFrameProbe {
      presenting: true
      complete: false
      inputFocused: false
      actionable: false
      screenName: "DP-1"
      ratio: 1.5
    }
  }
  // Changing its color makes the test window render another frame.
  Rectangle { id: paint; width: 10; height: 10; color: "black" }

  function opened() {
    const timing = createTemporaryObject(timingFactory, testCase)
    verify(timing !== null)
    timing.begin()
    return timing
  }

  function probeFor(timing, properties) {
    const probe = createTemporaryObject(probeFactory, testCase, Object.assign({ timing: timing }, properties || {}))
    verify(probe !== null)
    return probe
  }

  function test_a_complete_frame_follows_the_first_frame() {
    const timing = opened()

    timing.frame("DP-3", 2, false, "software")
    timing.frame("DP-3", 1.6666, false, "software")
    verify(timing.firstFrameAt >= timing.openedAt)
    compare(timing.completeFrameAt, 0)

    timing.frame("DP-3", 1.6666, true, "software")
    verify(timing.completeFrameAt >= timing.firstFrameAt)
    compare(timing.framesBeforeComplete, 2)
    compare(timing.firstFrameRatio, 2)
    compare(timing.completeFrameRatio, 1.6666)
    compare(timing.screenName, "DP-3")
    compare(timing.renderer, "software")
  }

  function test_the_open_waits_until_the_keyboard_is_ready_too() {
    const timing = opened()

    timing.frame("DP-1", 1, true, "opengl")
    verify(timing.waiting)

    timing.keyboardReady()
    verify(timing.keyboardReadyAt >= timing.openedAt)
    verify(timing.waiting, "the shown results cannot run yet")

    timing.resultsActionable()
    verify(timing.actionableAt >= timing.openedAt)
    verify(!timing.waiting)
  }

  function test_frames_after_close_do_not_count() {
    const timing = opened()

    timing.end()
    timing.frame("DP-1", 1, true, "software")
    timing.keyboardReady()

    compare(timing.firstFrameAt, 0)
    compare(timing.completeFrameAt, 0)
    compare(timing.keyboardReadyAt, 0)
    verify(timing.closedAt >= timing.openedAt)
  }

  function test_the_next_open_starts_a_new_record() {
    const timing = opened()
    timing.frame("DP-1", 1, true, "software")
    timing.keyboardReady()
    timing.resultsActionable()
    timing.end()

    timing.begin()

    compare(timing.generation, 2)
    compare(timing.firstFrameAt, 0)
    compare(timing.completeFrameAt, 0)
    compare(timing.keyboardReadyAt, 0)
    compare(timing.closedAt, 0)
    verify(timing.waiting)
  }

  function test_the_report_holds_times_and_counts_only() {
    const timing = opened()
    timing.frame("DP-3", 2, true, "software")

    const report = JSON.parse(timing.describe(true))

    compare(Object.keys(report).sort(), ["actionableAt", "closedAt", "completeFrameAt", "completeFrameRatio", "firstFrameAt",
      "firstFrameRatio", "framesBeforeComplete", "generation", "keyboardReadyAt", "opened", "openedAt", "renderer", "screen"])
    compare(report.generation, 1)
    compare(report.opened, true)
    compare(report.screen, "DP-3")
    compare(JSON.parse(timing.describe(undefined)).opened, false)
  }

  function test_the_probe_credits_a_frame_only_once_the_card_is_complete() {
    const timing = opened()
    const probe = probeFor(timing)

    paint.color = "red"
    waitForCondition(() => timing.firstFrameAt > 0, 0, "a rendered frame reaches the timing")
    compare(timing.completeFrameAt, 0)
    compare(timing.screenName, "DP-1")
    compare(timing.firstFrameRatio, 1.5)

    probe.complete = true
    paint.color = "blue"
    waitForCondition(() => timing.completeFrameAt > 0, 0, "the complete frame reaches the timing")
    verify(timing.framesBeforeComplete >= 1)
  }

  function test_a_probe_that_is_not_presenting_stays_silent() {
    const timing = opened()
    const probe = probeFor(timing, { presenting: false, complete: true })

    paint.color = "green"
    waitForRendering(paint)
    observeFor(100, () => timing.firstFrameAt === 0, "a window that does not present the open reports no frames")
  }

  function test_a_frame_synchronized_for_an_earlier_open_does_not_complete_the_next() {
    const timing = opened()
    const probe = probeFor(timing, { complete: true, swapsAtOnce: false })

    probe.synchronized()
    probe.complete = false
    timing.begin()
    probe.swapped()

    compare(timing.firstFrameAt, 0)
    compare(timing.framesBeforeComplete, 0)
    compare(timing.completeFrameAt, 0)
  }

  function test_a_swap_without_a_synchronized_state_does_not_count() {
    const timing = opened()
    const probe = probeFor(timing, { complete: true })

    probe.swapped()

    compare(timing.firstFrameAt, 0)
    compare(timing.completeFrameAt, 0)
  }

  function test_focus_held_across_a_reopen_is_reported_again() {
    const timing = opened()
    probeFor(timing, { inputFocused: true })
    waitForCondition(() => timing.keyboardReadyAt > 0, 0, "focus in an active window is reported")

    timing.begin()

    verify(timing.keyboardReadyAt >= timing.openedAt)
  }

  function test_the_probe_stops_listening_once_the_open_stops_waiting() {
    const timing = opened()
    const probe = probeFor(timing)

    verify(probe.listening)
    timing.end()

    verify(!probe.listening)
    compare(probe.renderedStates.length, 0)
  }

  function test_a_late_swap_takes_the_state_it_rendered() {
    const timing = opened()
    const probe = probeFor(timing, { swapsAtOnce: false })

    probe.synchronized()
    probe.complete = true
    probe.synchronized()
    probe.swapped()
    compare(timing.completeFrameAt, 0)

    probe.swapped()
    verify(timing.completeFrameAt > 0)
    compare(timing.framesBeforeComplete, 1)
  }

  function test_a_software_swap_takes_the_latest_synchronized_state() {
    const timing = opened()
    const probe = probeFor(timing, { swapsAtOnce: true })

    probe.synchronized()
    probe.synchronized()
    probe.complete = true
    probe.synchronized()
    probe.swapped()

    verify(timing.completeFrameAt > 0)
    compare(timing.framesBeforeComplete, 0)
  }

  function test_results_become_actionable_when_the_probe_says_so() {
    const timing = opened()
    const probe = probeFor(timing)

    compare(timing.actionableAt, 0)
    probe.actionable = true

    verify(timing.actionableAt >= timing.openedAt)
  }
}
