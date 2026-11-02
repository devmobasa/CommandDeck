import ".."
import QtQuick
import QtQuick.Window
import QtTest
import "../TabCycle.js" as TabCycle

EventPathFixture {
  id: scene
  EventPathTestCase {
    id: test
    fixture: scene
    name: "SettingsNestedModals"

    function cleanup() {
      fixture.part_settingsPanel.aiProviderEditorControl.close()
      fixture.part_settingsPanel.aiRemoveConfirmationControl.opened = false
      fixture.part_settingsPanel.visible = false
      nextTurn()
    }

    function test_tab_stays_in_top_modal_data() {
      return [{ tag: "removal" }, { tag: "discard" }, { tag: "models" }]
    }

    function openModal(kind) {
      const panel = fixture.part_settingsPanel
      panel.aiSettings = aiSpecSettings(true, 5, [aiSpecProvider("one", true)])
      panel.visible = true
      panel.showPage("ai")
      mouseClick(findChild(panel, "aiQuickSection"))
      verify(waitForFrame(panel))
      const row = panel.renderedAiProviderAt(0)
      if (kind === "removal") {
        mouseClick(row.removeControl)
        return { surface: panel.aiRemoveConfirmationControl,
          first: panel.aiRemoveConfirmationControl.cancelControl }
      }
      mouseClick(row.editControl)
      const editor = panel.aiProviderEditorControl
      waitForValue(editor.nameControl, "activeFocus", true)
      if (kind === "editor") return { surface: editor, first: editor.nameControl }
      if (kind === "discard") {
        typeText(" changed")
        keyClick(Qt.Key_Escape)
        return { surface: editor.discardConfirmation, first: editor.discardConfirmation.cancelControl }
      }
      panel.aiTestResult = { models: [{ id: "one" }, { id: "two" }] }
      keyClick(Qt.Key_F, Qt.ControlModifier)
      return { surface: editor.modelPicker, first: editor.modelPicker.searchControl }
    }

    function test_tab_stays_in_top_modal(data) {
      const modal = openModal(data.tag)
      waitForValue(modal.first, "activeFocus", true)
      for (let round = 0; round < 3; round++) {
        TabCycle.verifyCycle(test, modal.surface.Window.window, modal.surface, modal.first, 8)
      }
    }

    function test_reopening_restores_top_modal_data() {
      return [{ tag: "removal" }, { tag: "editor" }, { tag: "discard" }, { tag: "models" }]
    }

    function test_reopening_restores_top_modal(data) {
      const modal = openModal(data.tag)
      waitForValue(modal.first, "activeFocus", true)
      const panel = fixture.part_settingsPanel
      const editor = panel.aiProviderEditorControl
      const draft = editor.nameControl.text
      panel.visible = false
      nextTurn()
      panel.visible = true
      nextTurn()
      verify(TabCycle.inside(modal.surface, modal.surface.Window.window.activeFocusItem),
        "reopening focuses the topmost retained modal")
      typeText("x")
      compare(panel.searchControl.text, "")
      if (data.tag !== "editor") compare(editor.nameControl.text, draft)
    }
  }
}
