import ".."
import QtQuick
import QtTest
import "../../../qml/common" as Deck
import "../../../qml/common/Protocol.js" as Protocol

EventPathFixture {
  id: scene
  SignalSpy { id: fontChanges; target: scene.part_settingsPanel; signalName: "interfaceFontSelected" }
  Deck.SettingsChoice {
    id: popupChoice
    theme: scene.part_theme
    x: 50; y: 50; width: 180
    visible: false
    options: [
      { id: "first", title: "First", description: "First explanation" },
      { id: "second", title: "Second", description: "Second explanation" },
      { id: "third", title: "Third" },
      { id: "fourth", title: "Fourth" }
    ]
  }
  Deck.SettingsSegmented {
    id: threeSegments
    theme: scene.part_theme
    x: 50; y: 100
    visible: false
    options: [{ id: "snappy", title: "Snappy" }, { id: "natural", title: "Natural" }, { id: "off", title: "Off" }]
    Accessible.name: "Animation style"
  }
  SignalSpy { id: segmentChoices; target: threeSegments; signalName: "activated" }
  SignalSpy { id: choices; target: popupChoice; signalName: "activated" }
  EventPathTestCase {
    fixture: scene
    name: "CommandDeck_settings_restyle"

    function test_group_titles_are_distinct_and_shortcuts_keep_both_targets() {
      const panel = fixture.part_settingsPanel
      panel.visible = true
      waitForValue(panel.searchControl, "activeFocus", true)
      for (const page of panel.pages) {
        for (const card of panel.pageCards[page.id]) {
          verify(card.title !== page.title, page.title + " must not repeat as a group title")
          const rows = []
          for (let index = 0; index < card.body.length; index++) {
            const item = card.body[index]
            if (item.settingsRow && (item.label || item.text)) rows.push(item)
          }
          if (rows.length === 1) verify(card.title !== (rows[0].label || rows[0].text), card.title + " must not repeat its only row")
        }
      }
      verify(panel.revealSetting(Protocol.SettingIds.Shortcuts))
      compare(panel.settingCard(Protocol.SettingIds.Shortcuts).title, "CommandDeck")
      verify(panel.revealSetting(Protocol.SettingIds.DesktopShortcuts))
      compare(panel.currentPage, "shortcuts")
      compare(panel.settingCard(Protocol.SettingIds.DesktopShortcuts).title, "Desktop")
      const filter = panel.parts.shortcutsScroll.part_keybindingSearch
      verify(filter.visible && filter.parent !== panel.settingCard(Protocol.SettingIds.Shortcuts))
      filter.forceActiveFocus()
      compare(filter.background.border.width, 1)
      compare(filter.background.border.color.a, 0.6)
      panel.closeControl.forceActiveFocus()
      compare(filter.background.border.width, 0)
      panel.searchControl.forceActiveFocus()
      compare(panel.searchControl.background.border.width, 1)
    }

    function test_sidebar_vectors_use_the_theme_and_have_visible_shapes() {
      const panel = fixture.part_settingsPanel
      panel.visible = true
      panel.searchControl.text = ""
      waitForValue(panel.searchControl, "activeFocus", true)
      waitForFrame(panel)
      panel.part_spotlight.dismiss()
      waitForFrame(panel)
      const entries = panel.navigationControl.children[0].children.filter(item => item.modelData !== undefined)
      compare(entries.length, 7)
      for (const entry of entries) {
        const glyph = findChild(entry, "settingsNavGlyph")
        verify(glyph !== null)
        compare(glyph.ink, entry.current ? panel.theme.accent : panel.theme.textMuted)
        glyph.requestPaint()
        waitForFrame(glyph)
        const pixels = grabImage(scene)
        const origin = glyph.mapToItem(scene, 0, 0)
        const background = pixels.pixel(Math.floor(origin.x), Math.floor(origin.y))
        let left = glyph.width, right = -1, top = glyph.height, bottom = -1
        for (let y = 0; y < glyph.height; y++) {
          for (let x = 0; x < glyph.width; x++) {
            const pixel = pixels.pixel(Math.floor(origin.x + x), Math.floor(origin.y + y))
            const contrast = Math.max(Math.abs(pixel.r - background.r), Math.abs(pixel.g - background.g), Math.abs(pixel.b - background.b))
            if (contrast < 0.2 || pixel.a < 0.5) continue
            left = Math.min(left, x); right = Math.max(right, x)
            top = Math.min(top, y); bottom = Math.max(bottom, y)
          }
        }
        verify(right - left >= glyph.width * 0.45 && bottom - top >= glyph.height * 0.45,
          entry.modelData.title + " has a legible glyph of comparable visual size")
      }
    }

    function test_font_search_segment_keyboard_pointer_and_tab_order() {
      const panel = fixture.part_settingsPanel
      panel.visible = true
      waitForValue(panel.searchControl, "activeFocus", true)
      typeText("interface font")
      compare(panel.currentPage, "customization")
      compare(panel.Accessible.name, "Settings")
      const card = panel.settingCard(Protocol.SettingIds.InterfaceFont)
      verify(card && card.visible)
      const choice = panel.part_interfaceFont
      compare(panel.searchTarget.label, "Interface font")
      keyClick(Qt.Key_Return)
      waitForValue(choice, "activeFocus", true)
      fontChanges.clear()
      keyClick(Qt.Key_Right)
      compare(fontChanges.count, 1)
      compare(fontChanges.signalArguments[0][0], "shell")
      compare(choice.currentIndex, 1)
      keyClick(Qt.Key_Left)
      compare(fontChanges.count, 2)
      compare(fontChanges.signalArguments[1][0], "sans")
      keyClick(Qt.Key_Tab)
      verify(panel.closeControl.activeFocus, "Close follows the last visible page control")
      keyClick(Qt.Key_Backtab)
      waitForValue(choice, "activeFocus", true)
      mouseClick(choice, choice.width * 0.8, choice.height / 2)
      compare(fontChanges.count, 3)
      compare(fontChanges.signalArguments[2][0], "shell")
      keyClick(Qt.Key_F, Qt.ControlModifier)
      panel.searchControl.text = ""
      waitForFrame(panel)
      keyClick(Qt.Key_Tab)
      verify(panel.navigationControl.activeFocus)
      keyClick(Qt.Key_Tab)
      waitForValue(choice, "activeFocus", true)
      keyClick(Qt.Key_Backtab)
      verify(panel.navigationControl.activeFocus)
      keyClick(Qt.Key_Backtab)
      verify(panel.searchControl.activeFocus)
      keyClick(Qt.Key_Backtab)
      verify(panel.closeControl.activeFocus)
      keyClick(Qt.Key_Tab)
      verify(panel.searchControl.activeFocus)
      // The indexed keywords retain the same card when searching by typeface.
      panel.searchControl.text = "monospace"
      tryCompare(panel, "currentPage", "customization")
      verify(card.matchesSearch)
    }

    function test_rail_groups_and_collapsed_search_keep_selection_reachable() {
      const panel = fixture.part_settingsPanel
      panel.visible = true
      waitForValue(panel.searchControl, "activeFocus", true)
      panel.searchControl.text = ""
      panel.showPage("search")
      waitForFrame(panel)
      const rail = panel.navigationControl
      const entries = rail.children[0].children.filter(item => item.modelData !== undefined)
      compare(entries.length, 7)
      verify(entries[2].y - (entries[1].y + entries[1].height) >= Math.round(fixture.part_theme.fontBody * 0.9))
      verify(entries[5].y - (entries[4].y + entries[4].height) >= Math.round(fixture.part_theme.fontBody * 0.9))
      verify(entries[6].y - (entries[5].y + entries[5].height) >= Math.round(fixture.part_theme.fontBody * 0.9))
      rail.forceActiveFocus()
      keyClick(Qt.Key_Down)
      compare(panel.currentPage, "ai")
      fixture.width = 600
      waitForCondition(() => panel.railCollapsed)
      keyClick(Qt.Key_F, Qt.ControlModifier)
      typeText("interface font")
      compare(panel.currentPage, "customization")
      verify(panel.pagePickerControl.visible)
      verify(panel.searchControl.width > 300)
      keyClick(Qt.Key_Down)
      waitForValue(panel.pagePickerControl, "activeFocus", true)
      fixture.width = 1100
    }

    function test_three_segments_expose_radios_and_bound_arrow_selection() {
      fixture.part_settingsPanel.visible = false
      threeSegments.visible = true
      threeSegments.currentIndex = 0
      segmentChoices.clear()
      threeSegments.forceActiveFocus()
      keyClick(Qt.Key_Right)
      keyClick(Qt.Key_Right)
      compare(threeSegments.currentIndex, 2)
      compare(segmentChoices.count, 2)
      const radios = threeSegments.contentItem.children.filter(item => item.text !== undefined)
      compare(radios.length, 3)
      for (let index = 0; index < radios.length; index++) {
        compare(radios[index].Accessible.role, Accessible.RadioButton)
        compare(radios[index].checked, index === 2)
      }
      keyClick(Qt.Key_Right)
      compare(threeSegments.currentIndex, 2)
      mouseClick(radios[0])
      compare(threeSegments.currentIndex, 0)
      threeSegments.enabled = false
      const before = segmentChoices.count
      keyClick(Qt.Key_Right)
      compare(segmentChoices.count, before)
      threeSegments.enabled = true
      threeSegments.visible = false
    }

    function test_popup_choice_keyboard_and_pointer_commit_one_option() {
      const panel = fixture.part_settingsPanel
      panel.visible = false
      popupChoice.visible = true
      popupChoice.x = fixture.width - popupChoice.width - 8
      popupChoice.currentIndex = 0
      choices.clear()
      popupChoice.forceActiveFocus()
      keyClick(Qt.Key_Space)
      tryCompare(popupChoice.popup, "visible", true)
      keyClick(Qt.Key_Down)
      waitForCondition(() => popupChoice.popup.contentItem.itemAtIndex(popupChoice.highlightedIndex) !== null)
      const origin = popupChoice.mapToItem(scene, popupChoice.popup.x, popupChoice.popup.y)
      verify(origin.x >= 0 && origin.x + popupChoice.popup.width <= fixture.width,
        "the popup stays inside the window at the right edge: " + JSON.stringify({x: origin.x, width: popupChoice.popup.width, panel: fixture.width, choiceX: popupChoice.x, popupX: popupChoice.popup.x, windowWidth: popupChoice.popup.windowWidth, anchorX: popupChoice.popup.anchorX}))
      const highlighted = popupChoice.popup.contentItem.itemAtIndex(popupChoice.highlightedIndex)
      compare(highlighted.background.color, fixture.part_theme.accent)
      keyClick(Qt.Key_Return)
      tryCompare(popupChoice.popup, "visible", false)
      compare(popupChoice.currentIndex, 1)
      compare(choices.count, 1)
      compare(popupChoice.currentDescription, "Second explanation")
      compare(popupChoice.contentItem.text, "Second", "the closed button shows only its value")
      mouseClick(popupChoice)
      tryCompare(popupChoice.popup, "visible", true)
      const list = popupChoice.popup.contentItem
      waitForCondition(() => list.itemAtIndex(2) !== null)
      mouseClick(list.itemAtIndex(2))
      compare(popupChoice.currentIndex, 2)
      compare(choices.count, 2)
      popupChoice.visible = false
    }
  }
}
