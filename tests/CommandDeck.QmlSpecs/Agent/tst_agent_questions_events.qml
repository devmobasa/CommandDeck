import ".."
import QtQuick
import QtTest

EventPathFixture {
  id: scene
  EventPathTestCase {
    fixture: scene
    name: "AssistantQuestions"

    function test_all_answers_are_submitted_together() {
      const controller = fixture.part_agentEventController
      const surface = fixture.part_agentEventSurface
      controller.pending = { id: "build-questions", kind: "question", questions: [
        { id: "target", prompt: "Build target?", options: ["debug", "release"], allowFreeText: false },
        { id: "reason", prompt: "Why?", options: [], allowFreeText: true }
      ] }
      controller.state = "waiting"
      surface.opened = true
      nextTurn()
      // The conversation opens on its composer; Tab reaches the pending card.
      keyClick(Qt.Key_Tab)
      verify(findChild(surface, "agentQuestionOption0").activeFocus)
      keyClick(Qt.Key_Space)
      compare(controller.sentRequests.length, 0, "the first answer does not resolve the whole request")
      nextTurn()
      verify(findChild(surface, "agentQuestionText").activeFocus,
        "the next question owns typing without a focus correction")
      typeText("checking a fix")
      keyClick(Qt.Key_Return)
      compare(controller.sentRequests.length, 1)
      compare(controller.sentRequests[0].operation, "agent.turn.input")
      compare(controller.sentRequests[0].pendingRequestId, "build-questions")
      compare(controller.sentRequests[0].answers, { target: "debug", reason: "checking a fix" })
      nextTurn()
      verify(findChild(surface, "agentComposer").activeFocus)
    }
  }
}
