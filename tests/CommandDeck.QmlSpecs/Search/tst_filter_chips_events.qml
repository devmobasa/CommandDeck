import ".."
import QtQuick
import QtTest

EventPathFixture {
  id: scene
  EventPathTestCase {
    fixture: scene
    name: "FilterChips"

    function prepare() {
      fixture.part_controller.activeFilters = [
        {id: "applications", excluded: false}, {id: "files", excluded: false},
        {id: "windows", excluded: false}
      ]
      waitForFrame(fixture.part_searchField)
      mouseClick(fixture.part_searchField.inputControl)
      verify(fixture.part_searchField.inputControl.activeFocus)
    }

    function test_every_chip_is_reachable_both_ways_with_visible_focus() {
      prepare()
      const search = fixture.part_searchField
      for (let round = 0; round < 3; round++) {
        for (let index = 2; index >= 0; index--) {
          keyClick(Qt.Key_Backtab, Qt.ShiftModifier)
          const chip = search.filterChipAt(index)
          verify(chip.activeFocus, "Shift+Tab reaches chip " + index)
          compare(chip.border.color, fixture.part_theme.focusRing)
          compare(chip.border.width, 2)
        }
        for (let index = 1; index < 3; index++) {
          keyClick(Qt.Key_Tab)
          verify(search.filterChipAt(index).activeFocus, "Tab reaches chip " + index)
        }
        keyClick(Qt.Key_Tab)
        verify(search.inputControl.activeFocus)
      }
    }

    function test_focused_chip_draws_the_theme_ring() {
      prepare()
      keyClick(Qt.Key_Backtab, Qt.ShiftModifier)
      const focused = fixture.part_searchField.Window.activeFocusItem
      verify(focused && focused.label !== undefined)
      compare(focused.border.color, fixture.part_theme.focusRing)
      compare(focused.border.width, 2)
    }

    function test_enter_and_space_remove_each_chip_data() {
      const rows = []
      for (let index = 0; index < 3; index++) {
        for (const key of [Qt.Key_Return, Qt.Key_Space]) rows.push({tag: index + ":" + key, index: index, key: key})
      }
      return rows
    }

    function test_enter_and_space_remove_each_chip(data) {
      prepare()
      const ids = ["applications", "files", "windows"]
      for (let index = 2; index >= data.index; index--) keyClick(Qt.Key_Backtab, Qt.ShiftModifier)
      verify(fixture.part_searchField.filterChipAt(data.index).activeFocus)
      keyClick(data.key)
      compare(fixture.part_controller.activeFilters.map(filter => filter.id), ids.filter((id, index) => index !== data.index))
    }
  }
}
