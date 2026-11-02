import ".."
import QtQuick
import QtTest
import "../../../qml/common/UiConstants.js" as Ui

// Home and End go to the top or bottom of a Settings page from the keyboard:
// the empty search scrolls the page, a control on the page moves focus to the
// first or last control, and the rail picks the first or last page.
EventPathFixture {
  id: scene

  EventPathTestCase {
    fixture: scene
    name: "CommandDeck_settings_home_end"

    function openCustomization() {
      const panel = fixture.part_settingsPanel
      panel.part_settingsSearch.text = ""
      panel.showPage(Ui.SettingsPage.Customization)
      panel.visible = true
      waitForValue(panel.searchControl, "activeFocus", true)
      wait(0) // Settings focuses its search once more after it shows.
      const flick = panel.parts.customizationScroll.contentItem
      flick.contentY = 0
      return flick
    }

    function tabTo(control) {
      for (let step = 0; step < 100 && !control.activeFocus; step++) keyClick(Qt.Key_Tab)
      verify(control.activeFocus, "Tab reaches " + control.Accessible.name)
    }

    function namedControl(parent, name) {
      if (parent.Accessible.name === name && parent.activeFocusOnTab) return parent
      for (const child of parent.children || []) {
        const found = namedControl(child, name)
        if (found) return found
      }
      return null
    }

    function bottomOf(flick) { return flick.originY + Math.max(0, flick.contentHeight - flick.height) }

    function cleanup() {
      const panel = fixture.part_settingsPanel
      panel.part_settingsSearch.text = ""
      panel.showPage(Ui.SettingsPage.Search)
    }

    function test_empty_search_scrolls_the_page_and_keeps_typing_focus() {
      const panel = fixture.part_settingsPanel
      const flick = openCustomization()
      verify(bottomOf(flick) > flick.originY, "the page is long enough to scroll")

      keyClick(Qt.Key_End)
      compare(flick.contentY, bottomOf(flick))
      verify(panel.searchControl.activeFocus, "typing still searches")
      keyClick(Qt.Key_Home)
      compare(flick.contentY, flick.originY)
      verify(panel.searchControl.activeFocus)
    }

    function test_search_text_keeps_home_and_end_for_its_cursor() {
      const panel = fixture.part_settingsPanel
      const flick = openCustomization()
      typeText("size")
      flick.contentY = 0
      keyClick(Qt.Key_End)
      compare(flick.contentY, flick.originY, "a typed search keeps End for its cursor")
      verify(panel.searchControl.activeFocus)
    }

    function test_page_controls_jump_to_the_first_and_last_control_data() {
      return [
        {tag: "all controls", hiddenFirst: false, disabledLast: false},
        {tag: "hidden and disabled edges", hiddenFirst: true, disabledLast: true}
      ]
    }

    function test_page_controls_jump_to_the_first_and_last_control(data) {
      const panel = fixture.part_settingsPanel
      const flick = openCustomization()
      const reset = namedControl(panel.parts.customizationScroll, "Reset personalizations to defaults")
      const exportButton = namedControl(panel.parts.customizationScroll, "Export personalizations")
      verify(reset !== null && exportButton !== null)
      panel.part_interfaceFont.visible = !data.hiddenFirst
      reset.enabled = !data.disabledLast
      const first = data.hiddenFirst ? panel.displaySizeControl.sliderControl : panel.part_interfaceFont
      const last = data.disabledLast ? exportButton : reset
      try {
        tabTo(panel.compactLayoutControl)
        keyClick(Qt.Key_End)
        verify(last.activeFocus, "End reaches the explicit last enabled control")
        compare(flick.contentY, bottomOf(flick))

        keyClick(Qt.Key_Home)
        verify(first.activeFocus, "Home reaches the explicit first visible control")
        compare(flick.contentY, flick.originY)
      } finally {
        panel.part_interfaceFont.visible = true
        reset.enabled = Qt.binding(() => !panel.loading && !panel.snapshotMutationBusy)
      }
    }

    function test_rail_picks_the_first_and_last_page() {
      const panel = fixture.part_settingsPanel
      openCustomization()
      tabTo(panel.navigationControl)
      keyClick(Qt.Key_End)
      compare(panel.currentPage, "diagnostics")
      keyClick(Qt.Key_Home)
      compare(panel.currentPage, "search")
      verify(panel.navigationControl.activeFocus)
    }
  }
}
