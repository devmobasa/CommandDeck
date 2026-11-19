import ".."
import QtQuick
import QtTest
import "../../../qml/common" as Deck
import "../../../qml/common/Protocol.js" as Protocol
import "../../../qml/common/UiConstants.js" as Ui
import "../../../qml/common/Launcher/Actions/DeckInvocationActions.js" as Invocation

EventPathFixture {
  id: scene

  Deck.QuicklinksController {
    id: quicklinks
    ready: true
    nextRequestId: function() { return 1 }
  }

  // The launcher state a Settings row's action touches.
  QtObject {
    id: launcher
    property var part_invocationController: ({ busy: false, collecting: false })
    property bool daemonAvailable: true
    property bool reconciliationPending: false
    property bool actionPanelOpen: true
    property string pendingSettingId: ""
    property string pendingSettingUrl: ""
    property int settingsOpened: 0
    function openSettings() { settingsOpened++ }
  }

  EventPathTestCase {
    fixture: scene
    name: "CommandDeck_settings_reveal"

    function openPanel() {
      const panel = fixture.part_settingsPanel
      panel.part_settingsSearch.text = ""
      panel.showPage(Ui.SettingsPage.Search)
      panel.visible = true
      waitForValue(panel.searchControl, "activeFocus", true)
      return panel
    }

    function cleanup() {
      const panel = fixture.part_settingsPanel
      panel.part_settingsSearch.text = ""
      panel.showPage(Ui.SettingsPage.Search)
    }

    function test_reveal_opens_the_page_scrolls_and_spotlights_the_card() {
      const panel = openPanel()
      typeText("editors")
      compare(panel.currentPage, Ui.SettingsPage.Editors)
      verify(!panel.part_transparencyCard.visible, "the search hides the card first")

      verify(panel.revealSetting(Protocol.SettingIds.Transparency))
      compare(panel.part_settingsSearch.text, "")
      compare(panel.currentPage, Ui.SettingsPage.Customization)

      const spotlight = panel.part_spotlight
      tryVerify(() => spotlight.active, 2000, "the spotlight appears")
      compare(spotlight.target, panel.part_transparencyCard)
      const top = panel.part_transparencyCard.mapToItem(panel, 0, 0).y
      verify(top >= 0 && top < panel.height, "the card is scrolled into view")
      compare(spotlight.area.y, top)
      tryVerify(() => !spotlight.active, 5000, "the spotlight fades away")
    }

    function test_reduced_motion_shows_a_still_outline() {
      const panel = openPanel()
      fixture.part_theme.reduceMotion = true
      verify(panel.revealSetting(Protocol.SettingIds.Data))
      const spotlight = panel.part_spotlight
      tryCompare(spotlight, "opacity", 1, 2000)
      tryCompare(spotlight, "opacity", 0, 5000)
    }

    function test_reveal_selects_the_ai_section() {
      const panel = openPanel()
      verify(panel.revealSetting(Protocol.SettingIds.PromptTemplates))
      compare(panel.currentPage, Ui.SettingsPage.Ai)
      compare(panel.part_aiHub.section, Ui.AiHubSection.QuickAnswers)
      verify(panel.revealSetting(Protocol.SettingIds.AssistantTrust))
      compare(panel.part_aiHub.section, Ui.AiHubSection.Assistant)
    }

    function test_save_as_quicklink_opens_the_editor_filled_in() {
      const panel = openPanel()
      const original = panel.quicklinksController
      panel.quicklinksController = quicklinks
      verify(panel.revealSetting(Protocol.SettingIds.Quicklinks, "https://github.com/devmobasa"))
      const card = panel.part_quicklinksCard
      compare(panel.currentPage, Ui.SettingsPage.Search)
      verify(card.editing)
      compare(findChild(card, "quicklinkUrl").text, "https://github.com/devmobasa")
      compare(findChild(card, "quicklinkTitle").text, "github.com")
      tryVerify(() => findChild(card, "quicklinkTitle").activeFocus, 2000, "the title is ready to edit")
      tryVerify(() => panel.part_spotlight.active, 2000)
      card.editing = false
      panel.quicklinksController = original
    }

    function test_settings_search_points_at_the_matching_control() {
      const panel = openPanel()
      typeText("preview pa")
      compare(panel.currentPage, Ui.SettingsPage.Customization)
      const toggle = panel.part_previewPaneCheck
      tryCompare(panel, "searchTarget", toggle)
      const outline = findChild(panel, "settingsSearchOutline")
      verify(outline.visible, "the matching control is outlined while typing")
      compare(outline.parent, toggle)

      // A pause spotlights it while the search keeps the keyboard.
      const spotlight = panel.part_spotlight
      tryVerify(() => spotlight.active, 2000, "a pause in typing spotlights the control")
      compare(spotlight.target, toggle)
      verify(panel.searchControl.activeFocus)

      // Typing again lifts the dim at once, and the same control is not dimmed twice.
      keyClick(Qt.Key_N)
      verify(!spotlight.active)
      wait(900)
      verify(!spotlight.active)
      compare(panel.searchTarget, toggle)

      keyClick(Qt.Key_Return)
      waitForValue(toggle, "activeFocus", true)
      tryVerify(() => panel.part_spotlight.active, 2000, "Enter spotlights the control")
      compare(panel.part_spotlight.target, toggle)

      // A word only its help holds still finds it; nothing matching keeps Enter on the pages.
      panel.part_settingsSearch.text = "loads no previews"
      tryCompare(panel, "searchTarget", toggle)
      panel.part_settingsSearch.text = "zzzz"
      tryCompare(panel, "searchTarget", null)
      verify(!outline.visible)
    }

    function test_a_card_title_points_at_the_cards_first_control() {
      const panel = openPanel()
      // "Transparency" is the card's title; its switch reads "Transparent background".
      typeText("transparency")
      compare(panel.currentPage, Ui.SettingsPage.Customization)
      tryVerify(() => panel.searchTarget !== null, 2000, "the matching card still gets a target")
      compare(panel.searchTarget.text, "Transparent background", "the card's first control")
      verify(findChild(panel, "settingsSearchOutline").visible)

      // A real control match still beats a card that matches only by title.
      panel.part_settingsSearch.text = "preview pa"
      tryVerify(() => panel.searchTarget === panel.part_previewPaneCheck, 2000)
    }

    function test_matching_group_jumps_to_its_page_and_keeps_the_query() {
      const panel = openPanel()
      typeText("layout")
      const choice = findChild(panel, "settingsMatchChoice")
      const index = choice.optionIndex(Protocol.SettingIds.Layout)
      verify(index >= 0)
      keyClick(Qt.Key_Tab)
      waitForValue(choice, "activeFocus", true)
      keyClick(Qt.Key_Backtab)
      waitForValue(panel.searchControl, "activeFocus", true)
      panel.showPage(Ui.SettingsPage.Search)
      compare(panel.currentPage, Ui.SettingsPage.Search)

      mouseClick(choice)
      waitForValue(choice.popup, "opened", true)
      const list = choice.popup.contentItem
      list.positionViewAtIndex(index, ListView.Contain)
      tryVerify(() => list.itemAtIndex(index) !== null)
      mouseClick(list.itemAtIndex(index))
      waitForValue(panel, "currentPage", Ui.SettingsPage.Customization)
      tryCompare(panel.part_spotlight, "target", panel.compactLayoutControl)
      waitForValue(panel.compactLayoutControl, "activeFocus", true)
      compare(panel.searchControl.text, "layout")
    }

    function test_home_shortcuts_change_the_layout_preview() {
      const panel = openPanel()
      typeText("home shortcuts")
      const toggle = findChild(panel, "homeShortcutsToggle")
      const preview = findChild(panel, "settingsLayoutPreview")
      tryCompare(panel, "searchTarget", toggle)
      verify(preview.showShortcuts)
      const handler = visible => panel.homeShortcutsVisible = visible
      panel.homeShortcutsSelected.connect(handler)
      try {
        keyClick(Qt.Key_Return)
        waitForValue(toggle, "activeFocus", true)
        keyClick(Qt.Key_Space)
        tryCompare(preview, "showShortcuts", false)
        keyClick(Qt.Key_Space)
        tryCompare(preview, "showShortcuts", true)
      } finally {
        panel.homeShortcutsSelected.disconnect(handler)
        panel.homeShortcutsVisible = true
      }
    }

    function test_unknown_setting_changes_nothing() {
      const panel = openPanel()
      typeText("editors")
      verify(!panel.revealSetting("no.such.setting"))
      compare(panel.part_settingsSearch.text, "editors")
      compare(panel.currentPage, Ui.SettingsPage.Editors)
    }

    function test_every_index_entry_has_one_card() {
      const panel = openPanel()
      for (const name in Protocol.SettingsEntries) {
        const card = panel.settingCard(Protocol.SettingsEntries[name].id)
        verify(card !== null, name)
        compare(card.searchTerms, Protocol.SettingsEntries[name].keywords)
      }
    }

    function test_settings_row_action_opens_settings_at_its_card() {
      launcher.pendingSettingId = ""
      launcher.settingsOpened = 0
      Invocation.invokeAction(launcher, { kind: Protocol.ResultKinds.Setting }, {
        kind: Protocol.ActionKinds.OpenSettings,
        arguments: { [Protocol.ActionArgumentNames.SettingId]: Protocol.SettingIds.Layout }
      })
      compare(launcher.pendingSettingId, Protocol.SettingIds.Layout)
      compare(launcher.pendingSettingUrl, "")
      compare(launcher.settingsOpened, 1)
      verify(!launcher.actionPanelOpen)
    }
  }
}
