import "../.."
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "../../Protocol.js" as Protocol
import "../KeyChords.js" as KeyChords

ScrollView {
  id: shortcutsScroll
  required property var host
  readonly property alias part_shortcutsCard: shortcutsCard
  readonly property alias part_desktopShortcutsCard: desktopCard
  readonly property alias part_keybindingSearch: keybindingSearch
  readonly property alias part_keybindingResults: keybindingResults
  readonly property alias part_shortcutRepeater: shortcutRepeater
  readonly property alias hotkeyRow: hotkey
  readonly property var controller: host.shortcutController
  readonly property var editor: controller && controller.snapshot ? controller.snapshot
    : host.snapshot ? host.snapshot.shortcutEditor || null : null
  readonly property var keybindings: editor ? editor.bindings : host.snapshot ? host.snapshot.keybindings || [] : []
  readonly property var hotkeyBindings: {
    const own = host.snapshot ? (host.snapshot.shortcuts || []).find(row => row.id === "commanddeck") : null
    const chords = editor && editor.hotkeyManaged ? (editor.hotkey ? [editor.hotkey] : []) : own ? own.chords : []
    return chords.map(chord => ({id: "hotkey", chord: chord, description: "Open CommandDeck", launcherHotkey: true}))
  }
  readonly property bool hotkeyMatches: keybindingSearch.text.trim().length === 0
    || KeyChords.search(hotkeyBindings.length > 0 ? hotkeyBindings : [{chord: "", description: "Open CommandDeck"}], keybindingSearch.text).rows.length > 0
  readonly property bool clipboardMatches: keybindingSearch.text.trim().length === 0 || KeyChords.search(
    [{chord: clipboardSettings.savedChord, description: "Open Clipboard History"}], keybindingSearch.text).rows.length > 0
  readonly property var keybindingMatches: KeyChords.search(keybindings.filter(row => !row.launcherHotkey).concat(hotkeyBindings), keybindingSearch.text)
  readonly property var hotkeyChange: keybindings.find(row => row.launcherHotkey) || null
  property var pendingAddition: null
  property bool resetConfirmation: false
  property var savedFocus: null
  readonly property var moreMenu: desktopCard.accessoryItem ? desktopCard.accessoryItem.menu : null
  readonly property alias desktopRepeater: keybindingResults
  clip: true
  contentWidth: availableWidth

  function copy(text) { if (controller) controller.copyRequested(text) }
  function rememberFocus(id) {
    savedFocus = { id: id, control: shortcutsScroll.Window.activeFocusItem, received: false }
  }
  function restoreSavedFocus() {
    if (!savedFocus || !savedFocus.received || !visible || !host.visible
        || !controller || controller.isSaving(savedFocus.id)) return
    const id = savedFocus.id
    savedFocus = null
    const rows = [hotkey]
    for (const repeater of [keybindingResults, addedBindings]) {
      for (let index = 0; index < repeater.count; index++) rows.push(repeater.itemAt(index))
    }
    for (const row of rows) {
      if (row.binding.id !== id || !row.visible || !row.pill.enabled) continue
      row.pill.forceActiveFocus()
      return
    }
    keybindingSearch.forceActiveFocus()
  }
  Connections {
    target: shortcutsScroll.Window.window
    function onActiveFocusItemChanged() {
      const pending = shortcutsScroll.savedFocus
      const focused = shortcutsScroll.Window.activeFocusItem
      if (pending && focused !== pending.control && focused !== shortcutsScroll) shortcutsScroll.savedFocus = null
    }
  }
  Connections {
    target: shortcutsScroll.controller
    function onSnapshotChanged() {
      if (shortcutsScroll.savedFocus && shortcutsScroll.controller.snapshot) {
        shortcutsScroll.savedFocus.received = true
        Qt.callLater(shortcutsScroll.restoreSavedFocus)
      }
    }
    function onBusyIdChanged() { Qt.callLater(shortcutsScroll.restoreSavedFocus) }
  }
  function recordBinding(target) {
    keybindingSearch.text = ""
    host.showPage("shortcuts")
    host.part_settingsSearch.text = ""
    if (target.description === "CommandDeck" || target.description === "Open CommandDeck") {
      Qt.callLater(function() { hotkey.pill.startRecording(); if (!hotkey.binding.changeable) hotkey.infoPopup.open() })
      return
    }
    const existingAdded = target.id ? keybindings.find(binding => binding.added && binding.id === target.id) : null
    if (target.command && !target.chord && !existingAdded) {
      pendingAddition = { id: target.id, description: target.description, chord: "", changeable: true, added: true }
      addition.editValues = { command: target.command, description: target.description }
      Qt.callLater(function() { addition.pill.startRecording() })
      return
    }
    const description = String(target.description || "").trim().toLowerCase()
    const row = existingAdded || keybindings.find(binding => !binding.submap
      && String(binding.description || "").trim().toLowerCase() === description
      && KeyChords.display(binding.chord).toLowerCase() === KeyChords.display(target.chord).toLowerCase())
    if (!row) return
    Qt.callLater(function() {
      for (const repeater of [keybindingResults, addedBindings]) {
        for (let index = 0; index < repeater.count; index++) {
          const item = repeater.itemAt(index)
          if (item.binding.id !== row.id) continue
          item.pill.startRecording()
          if (!row.changeable) item.infoPopup.open()
          return
        }
      }
    })
  }
  onVisibleChanged: {
    if (!visible) savedFocus = null
    if (!visible && controller) controller.cancelRecording()
  }
  Connections {
    target: shortcutsScroll.host
    function onVisibleChanged() {
      if (!shortcutsScroll.host.visible) shortcutsScroll.savedFocus = null
      if (!shortcutsScroll.host.visible && shortcutsScroll.controller) shortcutsScroll.controller.cancelRecording()
    }
  }

  component MenuChoice: MenuItem {
    id: option
    required property var theme
    property bool destructive: false
    implicitHeight: Math.round(theme.fontBody * 2.4)
    contentItem: Text {
      text: option.text
      color: !option.enabled ? option.theme.textMuted : option.destructive ? option.theme.urgent : option.theme.text
      opacity: option.enabled ? 1 : 0.65
      font.family: option.theme.fontFamily
      font.pixelSize: option.theme.fontBody
      verticalAlignment: Text.AlignVCenter
      elide: Text.ElideRight
    }
    background: Rectangle {
      radius: 6
      color: option.highlighted ? option.theme.withAlpha(option.theme.text, 0.10) : "transparent"
    }
  }

  ColumnLayout {
    width: shortcutsScroll.availableWidth
    spacing: Math.round(22 * shortcutsScroll.host.theme.scale)
    SettingsField {
      id: keybindingSearch
      objectName: "keybindingSearch"
      search: true
      theme: shortcutsScroll.host.theme
      Layout.fillWidth: true
      placeholderText: "Find a key or binding, such as super+space or screenshot"
      Accessible.name: "Find a key or binding"
    }
    SettingsCard {
      id: shortcutsCard
      visible: matchesSearch && (shortcutsScroll.hotkeyMatches || shortcutsScroll.clipboardMatches)
      searchQuery: shortcutsScroll.host.part_settingsSearch.text
      settingId: Protocol.SettingIds.Shortcuts
      theme: shortcutsScroll.host.theme
      title: "CommandDeck"
      SettingsShortcutRow {
        id: hotkey
        onRecorded: shortcutsScroll.rememberFocus(binding.id)
        onRestoreRequested: shortcutsScroll.rememberFocus(binding.id)
        visible: shortcutsScroll.hotkeyMatches
        theme: shortcutsScroll.host.theme
        controller: shortcutsScroll.controller
        operation: Protocol.Operations.ShortcutHotkey
        moduleInstalled: !!shortcutsScroll.editor && shortcutsScroll.editor.moduleInstalled
        binding: ({ id: shortcutsScroll.hotkeyChange ? shortcutsScroll.hotkeyChange.id : "hotkey", description: "Open CommandDeck",
          changed: !!shortcutsScroll.hotkeyChange,
          chord: shortcutsScroll.editor && shortcutsScroll.editor.hotkeyManaged ? shortcutsScroll.editor.hotkey
            : ((shortcutsScroll.host.snapshot ? shortcutsScroll.host.snapshot.shortcuts || [] : []).find(row => row.id === "commanddeck") || {chords: []}).chords.join(", "),
          changeable: !!shortcutsScroll.editor && shortcutsScroll.editor.hotkeyManaged,
          reason: "You bound this key yourself. Change it in your Hyprland bindings, or run `commanddeck hotkey install`." })
      }
      ClipboardShortcutSettings {
        id: clipboardSettings
        visible: shortcutsScroll.clipboardMatches
        theme: shortcutsScroll.host.theme
        savedSearches: shortcutsScroll.host.savedSearches || []
        Layout.fillWidth: true
        preferences: shortcutsScroll.host.snapshot ? shortcutsScroll.host.snapshot.preferences : null
        saveError: shortcutsScroll.host.preferencesSaveError
        busy: shortcutsScroll.host.loading || shortcutsScroll.host.snapshotMutationBusy
        onSaved: preferences => shortcutsScroll.host.preferencesChanged(preferences)
      }
    }
    SettingsCard {
      id: desktopCard
      searchQuery: shortcutsScroll.host.part_settingsSearch.text
      settingId: Protocol.SettingIds.DesktopShortcuts
      theme: shortcutsScroll.host.theme
      title: "Desktop"
      description: "Recorded shortcuts take priority over your Hyprland configuration."
      headingAccessory: Component {
        DeckButton {
          theme: shortcutsScroll.host.theme
          text: "…"
          implicitWidth: implicitHeight
          Accessible.name: "Desktop shortcuts menu"
          readonly property alias menu: menuPopup
          onClicked: menuPopup.open()
          Menu {
            id: menuPopup
            y: parent.height
            x: parent.width - width
            width: Math.round(shortcutsScroll.host.theme.fontBody * 20)
            padding: Math.round(8 * shortcutsScroll.host.theme.scale)
            background: Rectangle { color: shortcutsScroll.host.theme.popupBackground; radius: 12; border.color: shortcutsScroll.host.theme.separator }
            MenuChoice { theme: shortcutsScroll.host.theme; text: shortcutsScroll.editor && shortcutsScroll.editor.changeCount > 0 ? shortcutsScroll.editor.changeCount + (shortcutsScroll.editor.changeCount === 1 ? " change" : " changes") : "No changes yet"; enabled: false }
            MenuSeparator {
              contentItem: Rectangle { implicitHeight: 1; color: shortcutsScroll.host.theme.separator }
            }
            MenuChoice { theme: shortcutsScroll.host.theme; text: "Copy configuration file path"; onTriggered: shortcutsScroll.copy(shortcutsScroll.editor ? shortcutsScroll.editor.configurationPath : "") }
            MenuChoice {
              theme: shortcutsScroll.host.theme
              text: "Copy changes as Lua"
              enabled: !!shortcutsScroll.editor && shortcutsScroll.editor.changeCount > 0
              onTriggered: { if (shortcutsScroll.controller) shortcutsScroll.controller.submit(Protocol.Operations.ShortcutExport) }
            }
            MenuSeparator {
              contentItem: Rectangle { implicitHeight: 1; color: shortcutsScroll.host.theme.separator }
            }
            MenuChoice { theme: shortcutsScroll.host.theme; text: "Reset all…"; destructive: true; enabled: !!shortcutsScroll.editor && shortcutsScroll.editor.changeCount > 0; onTriggered: shortcutsScroll.resetConfirmation = true }
          }
        }
      }
      RowLayout {
        Layout.fillWidth: true
        visible: shortcutsScroll.resetConfirmation
        Text {
          Layout.fillWidth: true
          text: "Restore all original desktop shortcuts?"
          wrapMode: Text.Wrap
          color: shortcutsScroll.host.theme.textMuted
          font.family: shortcutsScroll.host.theme.fontFamily
          font.pixelSize: shortcutsScroll.host.theme.fontSmall
        }
        DeckButton {
          theme: shortcutsScroll.host.theme
          objectName: "confirmResetShortcuts"
          text: "Reset all…"
          compact: true
          onClicked: {
            shortcutsScroll.resetConfirmation = false
            if (shortcutsScroll.controller) shortcutsScroll.controller.change(Protocol.Operations.ShortcutResetAll, {id: "all"}, true)
          }
        }
        DeckButton { theme: shortcutsScroll.host.theme; text: "Cancel"; compact: true; onClicked: shortcutsScroll.resetConfirmation = false }
      }
      Text {
        Layout.fillWidth: true
        visible: !!shortcutsScroll.controller && shortcutsScroll.controller.error.length > 0
          && (shortcutsScroll.controller.errorId === "all" || shortcutsScroll.controller.errorId.length === 0)
        text: shortcutsScroll.controller ? shortcutsScroll.controller.error : ""
        wrapMode: Text.Wrap
        color: shortcutsScroll.host.theme.urgent
        font.family: shortcutsScroll.host.theme.fontFamily
        font.pixelSize: shortcutsScroll.host.theme.fontSmall
      }
      Text {
        id: keybindingHeading
        objectName: "keybindingHeading"
        Layout.fillWidth: true
        visible: keybindingSearch.text.trim().length > 0
        text: KeyChords.heading(shortcutsScroll.keybindingMatches, keybindingSearch.text)
        textFormat: Text.PlainText
        wrapMode: Text.Wrap
        color: shortcutsScroll.host.theme.textMuted
        font.family: shortcutsScroll.host.theme.fontFamily
        font.pixelSize: shortcutsScroll.host.theme.fontSmall
        Accessible.role: Accessible.StaticText
        Accessible.name: text
      }
      Repeater {
        id: keybindingResults
        model: (keybindingHeading.visible ? shortcutsScroll.keybindingMatches.rows : shortcutsScroll.keybindings)
          .filter(row => !row.added && !row.launcherHotkey)
        SettingsShortcutRow {
          required property var modelData
          onRecorded: shortcutsScroll.rememberFocus(binding.id)
          onRestoreRequested: shortcutsScroll.rememberFocus(binding.id)
          theme: shortcutsScroll.host.theme
          binding: modelData
          controller: shortcutsScroll.controller
          moduleInstalled: !!shortcutsScroll.editor && shortcutsScroll.editor.moduleInstalled
          onCopyRequested: text => shortcutsScroll.copy(text)
        }
      }
      Text {
        visible: !!shortcutsScroll.pendingAddition || shortcutsScroll.keybindings.some(row => row.added)
        text: "Added by you"
        color: shortcutsScroll.host.theme.textMuted
        font.family: shortcutsScroll.host.theme.fontFamily
        font.pixelSize: shortcutsScroll.host.theme.fontSmall
      }
      Repeater {
        id: addedBindings
        objectName: "savedAddedShortcuts"
        model: shortcutsScroll.keybindings.filter(row => row.added && (!keybindingHeading.visible || shortcutsScroll.keybindingMatches.rows.indexOf(row) >= 0))
        SettingsShortcutRow {
          required property var modelData
          onRecorded: shortcutsScroll.rememberFocus(binding.id)
          onRestoreRequested: shortcutsScroll.rememberFocus(binding.id)
          theme: shortcutsScroll.host.theme
          binding: modelData
          controller: shortcutsScroll.controller
          moduleInstalled: !!shortcutsScroll.editor && shortcutsScroll.editor.moduleInstalled
        }
      }
      SettingsShortcutRow {
        id: addition
        objectName: "addedShortcut"
        pendingAddition: true
        onRecorded: shortcutsScroll.rememberFocus(binding.id)
        visible: !!shortcutsScroll.pendingAddition && !shortcutsScroll.keybindings.some(row => row.id === shortcutsScroll.pendingAddition.id)
        theme: shortcutsScroll.host.theme
        binding: shortcutsScroll.pendingAddition || ({id: "", description: "", chord: "", changeable: true})
        controller: shortcutsScroll.controller
        moduleInstalled: !!shortcutsScroll.editor && shortcutsScroll.editor.moduleInstalled
        onRestoreRequested: shortcutsScroll.pendingAddition = null
      }
      Repeater {
        id: shortcutRepeater
        model: shortcutsScroll.keybindings.length === 0 && !keybindingHeading.visible && shortcutsScroll.host.snapshot
          ? (shortcutsScroll.host.snapshot.shortcuts || []).filter(row => row.id !== "commanddeck") : []
        SettingsShortcutRow {
          required property var modelData
          theme: shortcutsScroll.host.theme
          binding: ({ id: modelData.id, description: modelData.label, chord: (modelData.chords || []).join(", "), changeable: false,
            reason: modelData.detail || "Change this binding in your Hyprland configuration." })
        }
      }
    }
  }
}
