import ".."
import QtQuick
import QtQuick.Controls
import QtTest
import "../../../qml/common" as Deck
import "../../../qml/common/Protocol.js" as Protocol
import "../../../qml/common/Settings/RecordingKeys.js" as RecordingKeys

Item {
  id: scene
  width: 1000
  height: 740
  property int sequence: 0
  property var sent: []
  property var recorded: []
  property int restores: 0
  property var copied: []
  Deck.Theme { id: theme; loadShellTokens: false }
  Deck.ShortcutEditorController {
    id: controller
    nextRequestId: () => ++scene.sequence
    onRequest: value => scene.sent = scene.sent.concat([JSON.parse(JSON.stringify(value))])
    onCopyRequested: value => scene.copied = scene.copied.concat([value])
  }
  Deck.SettingsShortcutRow {
    id: row
    width: 800
    theme: theme
    controller: controller
    binding: ({id: "test", description: "Terminal", chord: "Super+T", changeable: true, changed: true})
    onRecorded: value => scene.recorded = scene.recorded.concat([value])
    onRestoreRequested: scene.restores++
  }
  TextField { id: other; y: 300; width: 250; text: "Other" }
  Deck.SettingsPanel {
    id: panel
    anchors.fill: parent
    theme: theme
    visible: false
    shortcutController: controller
    snapshot: ({preferences: {sources: []}, keybindings: [], shortcutEditor: {moduleInstalled: true, hotkeyManaged: true, changeCount: 2,
      hotkey: "Ctrl+Space", configurationPath: "/fictional/hypr/commanddeck_keys.lua", bindings: [
        {id: "test", description: "Terminal", chord: "Super+T", changeable: true, changed: true},
        {id: "readonly", description: "Workspaces", chord: "Super+1", changeable: false, reason: "Built in a loop.", sourceFile: "/fictional/bindings.lua"},
        {id: "added:saved-browser", description: "Saved browser", chord: "Super+Alt+B", changeable: true, changed: true, added: true}
      ]}})
  }
  DeadlineTestCase {
    name: "ShortcutEditor"
    when: windowShown
    function init() {
      panel.visible = false
      panel.parts.shortcutsScroll.pendingAddition = null
      panel.parts.shortcutsScroll.resetConfirmation = false
      panel.parts.shortcutsScroll.moreMenu.close()
      controller.cancelRecording()
      row.visible = true
      row.pill.cancelRecording()
      row.moduleInstalled = true
      row.removing = false
      row.localError = ""
      row.binding = {id: "test", description: "Terminal", chord: "Super+T", changeable: true, changed: true}
      row.pill.chord = "Super+T"
      row.pill.recordable = true
      row.pill.saving = false
      controller.active = null
      controller.activeRecordingPill = null
      controller.recordingId = ""
      controller.snapshot = null
      controller.queue = []
      controller.error = ""
      controller.busyId = ""
      controller.conflict = null
      scene.sent = []
      scene.recorded = []
      scene.restores = 0
      scene.copied = []
      other.forceActiveFocus()
    }
    function cleanup() { row.pill.cancelRecording(); panel.visible = false }
    function done() {
      const request = controller.active
      verify(request !== null)
      controller.handleMessage({requestId: request.requestId, type: Protocol.Messages.Done})
    }
    function verifyUnderLabel(help) {
      const label = findChild(row, "shortcutRowLabel")
      const bottom = label.mapToItem(row, 0, label.height)
      const top = help.mapToItem(row, 0, 0)
      fuzzyCompare(top.x, bottom.x, 1)
      verify(top.y >= bottom.y && top.y - bottom.y <= 6 * theme.scale)
      verify(help.mapToItem(row, 0, help.height).y <= row.height - 7 * theme.scale)
    }
    function clickPill() {
      mouseClick(row.pill)
      for (let attempt = 0; row.pill.preparing && controller.active && attempt < 6; attempt++) done()
    }
    function test_preparing_ignores_keys_until_capture_is_ready_and_reports_start_failure() {
      mouseClick(row.pill)
      verify(row.pill.preparing)
      verify(!row.pill.recording)
      compare(row.pill.contentItem.text, "Preparing…")
      compare(row.pill.background.border.width, 0)
      keyClick(Qt.Key_Q, Qt.MetaModifier)
      compare(scene.recorded.length, 0)
      controller.handleMessage({requestId: controller.active.requestId, type: Protocol.Messages.Error, error: "Capture failed"})
      verify(!row.pill.preparing && !row.pill.recording)
      waitForFrame(row)
      compare(findChild(row, "shortcutRowError").text, "Capture failed")
      verifyUnderLabel(findChild(row, "shortcutRowError"))
    }
    function test_enter_space_chord_and_ordered_start_stop_save() {
      row.pill.forceActiveFocus()
      keyClick(Qt.Key_Return)
      verify(row.pill.preparing)
      compare(scene.sent[0].operation, Protocol.Operations.ShortcutRecordStart)
      keyClick(Qt.Key_Q, Qt.MetaModifier)
      compare(scene.recorded.length, 0)
      done()
      verify(row.pill.recording)
      keyClick(Qt.Key_Q, Qt.MetaModifier)
      compare(scene.recorded[0], "Super+Q")
      verify(!row.pill.recording)
      compare(scene.sent[1].operation, Protocol.Operations.ShortcutRecordStop)
      done()
      compare(scene.sent[2].operation, Protocol.Operations.ShortcutSet)
      compare(scene.sent[2].shortcut.chord, "Super+Q")
      done()
      row.pill.forceActiveFocus()
      keyClick(Qt.Key_Space)
      verify(row.pill.preparing)
      done()
      verify(row.pill.recording)
      keyClick(Qt.Key_Escape)
      verify(!row.pill.recording)
    }
    function test_no_module_records_immediately_without_a_capture_request() {
      row.moduleInstalled = false
      mouseClick(row.pill)
      verify(row.pill.recording && !row.pill.preparing)
      compare(scene.sent.length, 0)
      keyClick(Qt.Key_Q, Qt.MetaModifier)
      compare(scene.recorded[0], "Super+Q")
      compare(controller.active.operation, Protocol.Operations.ShortcutSet)
    }
    function test_cancelled_preparation_ignores_its_late_acknowledgement() {
      mouseClick(row.pill)
      verify(row.pill.preparing)
      keyClick(Qt.Key_Escape)
      mouseClick(row.pill)
      verify(row.pill.preparing)
      done()
      verify(row.pill.preparing && !row.pill.recording)
      done()
      verify(row.pill.preparing && !row.pill.recording)
      done()
      verify(row.pill.recording)
    }
    function test_pill_visual_states_and_media_key() {
      compare(row.pill.contentItem.text, "Super+T")
      row.pill.chord = ""
      compare(row.pill.contentItem.text, "Record Shortcut")
      row.pill.recordable = false
      compare(row.pill.contentItem.text, "Not set")
      row.pill.chord = "Super+T"
      compare(row.pill.contentItem.text, "Super+T")
      const sharedWidth = row.pill.implicitWidth
      // A long chord widens its pill instead of hiding the key behind an ellipsis.
      row.pill.chord = "Super+Alt+Shift+Print"
      waitForFrame(row.pill)
      verify(row.pill.implicitWidth > sharedWidth)
      verify(!row.pill.contentItem.truncated)
      row.pill.chord = "Super+T"
      compare(row.pill.implicitWidth, sharedWidth, "short chords keep the shared width")
      row.pill.recordable = true
      row.pill.saving = true
      compare(row.pill.contentItem.opacity, 0.6)
      verify(!row.pill.enabled)
      row.pill.saving = false
      clickPill()
      compare(row.pill.contentItem.text, "Type Shortcut…")
      compare(row.pill.contentItem.font.pixelSize, theme.fontBody)
      compare(row.pill.contentItem.font.family, theme.fontFamily)
      waitForFrame(row.pill)
      verify(!row.pill.contentItem.truncated)
      compare(row.pill.Accessible.description, "Press the new shortcut. Escape cancels.")
      keyClick(Qt.Key_VolumeMute)
      compare(scene.recorded[0], "XF86AudioMute")
    }
    function test_additional_media_keys_record_without_modifiers_data() {
      return [{tag: "microphone", key: Qt.Key_MicMute, chord: "XF86AudioMicMute"},
        {tag: "keyboard light", key: Qt.Key_KeyboardBrightnessUp, chord: "XF86KbdBrightnessUp"},
        {tag: "audio record", key: Qt.Key_MediaRecord, chord: "XF86AudioRecord"}]
    }
    function test_additional_media_keys_record_without_modifiers(data) {
      clickPill()
      keyClick(data.key)
      compare(scene.recorded[0], data.chord)
    }
    function test_punctuation_native_codes_and_modifiers() {
      compare(RecordingKeys.chord({key: Qt.Key_Colon, modifiers: Qt.ControlModifier | Qt.ShiftModifier, nativeScanCode: 0}), "Ctrl+Shift+colon")
      compare(RecordingKeys.chord({key: 0x00dc, modifiers: Qt.MetaModifier, nativeScanCode: 34}), "Super+code:34")
      compare(RecordingKeys.chord({key: Qt.Key_Meta, modifiers: Qt.MetaModifier, nativeScanCode: 133}), "")
      compare(RecordingKeys.chord({key: Qt.Key_Print, modifiers: Qt.MetaModifier, nativeScanCode: 0}), "Super+Print")
      compare(RecordingKeys.chord({key: Qt.Key_1, modifiers: Qt.ControlModifier | Qt.KeypadModifier, nativeScanCode: 0}), "Ctrl+KP_1")
      compare(RecordingKeys.chord({key: Qt.Key_Enter, modifiers: Qt.ControlModifier, nativeScanCode: 0}), "Ctrl+KP_Enter")
    }
    function test_plain_key_ignored_and_function_key_recorded() {
      clickPill()
      keyClick(Qt.Key_A)
      verify(row.pill.recording)
      keyClick(Qt.Key_F12)
      compare(scene.recorded[0], "F12")
    }
    function test_escape_tab_focus_loss_and_outside_click_cancel() {
      clickPill()
      keyClick(Qt.Key_Escape)
      verify(!row.pill.recording)
      clickPill()
      keyClick(Qt.Key_Tab)
      verify(!row.pill.recording)
      clickPill()
      other.forceActiveFocus()
      verify(!row.pill.recording)
      clickPill()
      mouseClick(scene, 900, 500)
      verify(!row.pill.recording)
      compare(scene.recorded.length, 0)
    }
    function test_backspace_confirmation_cancel_remove_and_restore() {
      clickPill()
      keyClick(Qt.Key_Backspace)
      verify(row.removing)
      waitForFrame(row)
      verifyUnderLabel(findChild(row, "shortcutRowMessage"))
      // The public confirmation control activates the real mutation path.
      mouseClick(row.removeControl)
      verify(!row.removing)
      done()
      compare(controller.active.operation, Protocol.Operations.ShortcutSet)
      verify(controller.active.confirmed)
      compare(controller.active.shortcut.chord, "")
      done()
      mouseClick(row.restoreControl)
      compare(scene.restores, 1)
      compare(controller.active.operation, Protocol.Operations.ShortcutRestore)
    }
    function test_removal_and_conflict_cancel_preserve_the_old_chord() {
      clickPill()
      keyClick(Qt.Key_Delete)
      verify(row.removing)
      mouseClick(findChild(row, "shortcutConfirmationCancel"))
      verify(!row.removing)
      compare(row.pill.chord, "Super+T")
      controller.conflict = {operation: Protocol.Operations.ShortcutSet, shortcut: {id: "test", chord: "Super+Q"}, description: "Menu"}
      mouseClick(findChild(row, "shortcutConfirmationCancel"))
      verify(!controller.conflict)
      verify(!scene.sent.some(request => request.operation === Protocol.Operations.ShortcutSet))
      row.localError = "The shortcut could not be saved."
      waitForFrame(row)
      verifyUnderLabel(findChild(row, "shortcutRowError"))
    }
    function test_recording_timeout_cancels_the_pill() {
      clickPill()
      verify(row.pill.recording)
      tryCompare(row.pill, "recording", false, 11500)
      verify(!controller.recordingPill)
    }
    function test_conflict_cancel_and_replace() {
      controller.conflict = {operation: Protocol.Operations.ShortcutSet, shortcut: {id: "test", chord: "Super+Q"}, description: "Menu"}
      verify(row.hasConflict)
      compare(row.pill.contentItem.text, "Super+Q")
      compare(row.pill.background.border.width, 1)
      fuzzyCompare(row.pill.background.border.color.a, 0.6, 0.01)
      compare(row.removeControl.background.color, theme.accent)
      compare(row.removeControl.contentItem.color, theme.accentText)
      waitForFrame(row)
      verifyUnderLabel(findChild(row, "shortcutRowMessage"))
      mouseClick(row.removeControl)
      verify(!controller.conflict)
      compare(controller.active.shortcut.replace, true)
      compare(controller.active.shortcut.chord, "Super+Q")
    }
    function test_pills_keep_the_right_inset_with_restore_or_info() {
      row.binding = {id: "test", description: "Terminal", chord: "Super+T", changeable: true}
      waitForFrame(row)
      const right = row.pill.mapToItem(row, row.pill.width, 0).x
      for (const binding of [
        {id: "test", description: "Terminal", chord: "Super+T", changeable: true, changed: true},
        {id: "test", description: "Terminal", chord: "Super+T", changeable: false}]) {
        row.binding = binding
        waitForFrame(row)
        fuzzyCompare(row.pill.mapToItem(row, row.pill.width, 0).x, right, 1)
      }
    }
    function test_readonly_changed_menu_and_launcher_reveal() {
      row.visible = false
      panel.visible = true
      panel.showPage("shortcuts")
      const page = panel.parts.shortcutsScroll
      const readonly = page.desktopRepeater.itemAt(1)
      const path = "/fictional/configuration/" + "long-directory/".repeat(8) + "bindings.lua"
      readonly.binding = Object.assign({}, readonly.binding, {sourceFile: path,
        reason: "This binding is built by your Hyprland configuration (for example in a loop). Change it in " + path + "."})
      readonly.infoPopup.open()
      verify(readonly.infoPopup.opened)
      const pathLine = findChild(readonly.infoPopup.contentItem, "shortcutSourcePath")
      waitForFrame(pathLine)
      compare(pathLine.text, path)
      compare(pathLine.font.family, theme.monoFontFamily)
      compare(pathLine.elide, Text.ElideMiddle)
      verify(pathLine.truncated)
      verify(readonly.infoSentence.indexOf(path) < 0)
      mouseClick(findChild(readonly.infoPopup.contentItem, "copyShortcutSource"))
      compare(scene.copied[0], path)
      readonly.infoPopup.close()
      page.moreMenu.open()
      verify(page.moreMenu.opened)
      page.moreMenu.close()
      panel.recordShortcut({description: "terminal", chord: "Super+T"})
      tryVerify(() => page.desktopRepeater.itemAt(0).pill.preparing)
      done()
      verify(page.desktopRepeater.itemAt(0).pill.recording)
      panel.visible = false
      verify(!controller.recordingPill)
    }
    function test_menu_reset_all_requires_the_inline_confirmation() {
      row.visible = false
      panel.visible = true
      panel.showPage("shortcuts")
      const page = panel.parts.shortcutsScroll
      page.moreMenu.open()
      compare(page.moreMenu.itemAt(0).text, "2 changes")
      compare(page.moreMenu.itemAt(5).contentItem.color, theme.urgent)
      mouseClick(page.moreMenu.itemAt(5))
      verify(page.resetConfirmation)
      verify(!scene.sent.some(request => request.operation === Protocol.Operations.ShortcutResetAll))
      const confirmation = findChild(page, "confirmResetShortcuts")
      waitForFrame(confirmation)
      mouseClick(confirmation)
      compare(controller.active.operation, Protocol.Operations.ShortcutResetAll)
      verify(controller.active.confirmed)
    }
    function test_launcher_change_reaches_a_saved_added_binding() {
      row.visible = false
      panel.visible = true
      panel.recordShortcut({id: "added:saved-browser", description: "saved BROWSER", chord: "super+alt+b"})
      nextTurn()
      const added = findChild(panel, "savedAddedShortcuts")
      compare(added.count, 1)
      verify(added.itemAt(0).pill.preparing)
      done()
      verify(added.itemAt(0).pill.recording)
    }
    function test_search_shows_each_added_shortcut_once_data() {
      return [{tag: "description", query: "Saved browser"}, {tag: "chord", query: "Super+Alt+B"}]
    }
    function test_search_shows_each_added_shortcut_once(data) {
      row.visible = false
      panel.visible = true
      panel.showPage("shortcuts")
      const page = panel.parts.shortcutsScroll
      waitForFrame(panel)
      mouseClick(page.part_keybindingSearch)
      keyClick(Qt.Key_A, Qt.ControlModifier)
      for (const character of data.query) keyClick(character)
      nextTurn()
      const added = findChild(panel, "savedAddedShortcuts")
      const shown = []
      for (const repeater of [page.desktopRepeater, added]) {
        for (let index = 0; index < repeater.count; index++) shown.push(repeater.itemAt(index).binding.id)
      }
      compare(shown, ["added:saved-browser"])
      compare(findChild(panel, "keybindingHeading").text,
        data.tag === "chord" ? "Super+Alt+B runs one binding." : "1 binding")
    }
    function test_launcher_add_reuses_an_existing_disabled_added_row() {
      row.visible = false
      panel.visible = true
      controller.snapshot = {moduleInstalled: true, hotkeyManaged: true, bindings: [
        {id: "added:disabled", description: "Disabled app", chord: "", changeable: true, changed: true, added: true}]}
      panel.recordShortcut({id: "added:disabled", description: "Disabled app", chord: "", command: "fictional-app"})
      nextTurn()
      const added = findChild(panel, "savedAddedShortcuts")
      compare(added.count, 1)
      verify(added.itemAt(0).pill.preparing)
      done()
      verify(added.itemAt(0).pill.recording)
      verify(!findChild(panel, "addedShortcut").visible)
    }
    function test_launcher_add_opens_a_new_recording_row() {
      row.visible = false
      panel.visible = true
      panel.recordShortcut({id: "added:browser", description: "Browser", chord: "", command: "fictional-browser"})
      const added = findChild(panel, "addedShortcut")
      tryVerify(() => added.visible && added.pill.preparing)
      done()
      verify(added.pill.recording)
      keyClick(Qt.Key_B, Qt.MetaModifier | Qt.AltModifier)
      done()
      compare(controller.active.shortcut.command, "fictional-browser")
      compare(controller.active.shortcut.chord, "Super+Alt+B")
    }
    function test_unbound_and_readonly_accessibility() {
      row.pill.chord = ""
      compare(row.pill.Accessible.description, "Not set")
      compare(row.pill.Accessible.name, "Terminal shortcut")
      row.pill.recordable = false
      clickPill()
      verify(!row.pill.recording)
      row.pill.recordable = true
      row.pill.chord = "Super+T"
    }

    function test_saved_snapshot_preserves_keyboard_position_data() {
      return [{tag: "record"}, {tag: "restore"}, {tag: "add"}, {tag: "moved-on"}]
    }
    function test_saved_snapshot_preserves_keyboard_position(data) {
      row.visible = false
      panel.visible = true
      panel.showPage("shortcuts")
      const page = panel.parts.shortcutsScroll
      waitForFrame(panel)
      mouseClick(page.part_keybindingSearch)
      keyClick(Qt.Key_A, Qt.ControlModifier)
      keyClick(Qt.Key_Backspace)
      nextTurn()
      const binding = page.desktopRepeater.itemAt(0)
      if (data.tag === "restore") mouseClick(binding.restoreControl)
      else {
        if (data.tag === "add") panel.recordShortcut({id: "added:new", description: "New app", command: "fictional-app"})
        else mouseClick(binding.pill)
        nextTurn()
        done()
        keyClick(Qt.Key_Q, Qt.MetaModifier)
        done()
      }
      verify(controller.active !== null)
      const requestId = controller.active.requestId
      if (data.tag === "moved-on") mouseClick(panel.searchControl)
      const snapshot = JSON.parse(JSON.stringify(panel.snapshot.shortcutEditor))
      snapshot.bindings[0].chord = "Super+Q"
      if (data.tag === "restore") snapshot.bindings[0].changed = false
      if (data.tag === "add") snapshot.bindings.push({id: "added:new", description: "New app", chord: "Super+Q", added: true, changed: true, changeable: true})
      controller.handleMessage({requestId: requestId, type: Protocol.Messages.PreferencesResult, preferences: {shortcutEditor: snapshot}})
      controller.handleMessage({requestId: requestId, type: Protocol.Messages.Done})
      nextTurn()
      const target = data.tag === "add" ? findChild(panel, "savedAddedShortcuts").itemAt(1).pill : page.desktopRepeater.itemAt(0).pill
      if (data.tag === "moved-on") verify(panel.searchControl.activeFocus, "a reply does not steal focus after the user moved on")
      else {
        verify(target.activeFocus, "the matching replacement pill keeps the keyboard position")
        keyClick(Qt.Key_Return)
        verify(target.preparing, "recording can resume immediately after saving")
      }
    }
  }
}
