import "../.."
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Layouts
import "../../Protocol.js" as Protocol
import "../../HomePaths.js" as HomePaths

// Settings › Search › Excluded folders. Your own folders come first, with
// Edit and Remove; built-in folders follow by category, each with a switch.
// Changes go through the indexed folders controller and apply immediately.
SettingsCard {
  id: card
  required property var host
  readonly property var exclusions: card.host.indexExclusions || []
  readonly property var userFolders: card.exclusions.filter(value => value.source === Protocol.IndexExclusionSources.User)
  readonly property var builtIns: card.exclusions.filter(value => value.source === Protocol.IndexExclusionSources.BuiltIn)
  readonly property var categories: [
    { id: Protocol.IndexExclusionCategories.Caches, title: "Caches" },
    { id: Protocol.IndexExclusionCategories.Packages, title: "Packages and toolchains" },
    { id: Protocol.IndexExclusionCategories.Agents, title: "AI coding agents" },
    { id: Protocol.IndexExclusionCategories.Containers, title: "Containers" },
    { id: Protocol.IndexExclusionCategories.Applications, title: "Apps and browser profiles" }
  ]
  readonly property alias pathControl: pathField
  readonly property alias addControl: addButton
  readonly property alias errorControl: errorText
  readonly property alias statusControl: statusText
  readonly property int renderedUserCount: userRows.count
  readonly property string error: card.isExclusionOperation(card.host.indexRootsErrorOperation) ? card.host.indexRootsError : ""
  property var expanded: ({})
  property string editingId: ""
  property bool editSubmitted: false
  property string lastOperation: ""
  property string pendingFocusId: ""
  // Set when Add is sent; the reply that answers it clears the path once.
  property bool clearPathOnReply: false
  // Any id no row has: restoreFocus then falls back to the path field.
  readonly property string pathFieldFocus: "path-field"

  function isExclusionOperation(operation) { return card.host.isExclusionOperation(operation) }
  function displayPath(path) { return HomePaths.display(path, card.host.homeDirectory) }
  // Whether the typed path is now one of your excluded folders.
  function lists(typed) {
    const value = String(typed).trim().replace(/\/+$/, "")
    // Reads exclusions itself: userFolders may not be updated yet in onExclusionsChanged.
    return card.exclusions.some(folder => folder.source === Protocol.IndexExclusionSources.User
      && (folder.path === value || card.displayPath(folder.path) === value))
  }
  function focusPath() { pathField.forceActiveFocus() }
  function request(operation, path, exclusionId) {
    card.lastOperation = operation
    card.host.indexExclusionChangeRequested(operation, path, exclusionId)
  }
  function add() {
    card.clearPathOnReply = true
    card.request(Protocol.Operations.IndexExclusionAdd, pathField.text, "")
  }
  function startEdit(exclusion) {
    card.editingId = exclusion.id
    card.editSubmitted = false
  }
  function saveEdit(exclusionId, text) {
    card.editSubmitted = true
    card.request(Protocol.Operations.IndexExclusionUpdate, text, exclusionId)
  }
  function cancelEdit() {
    const id = card.editingId
    card.editingId = ""
    card.editSubmitted = false
    card.pendingFocusId = id
    Qt.callLater(card.restoreFocus)
  }
  function categoryEntries(categoryId) { return card.builtIns.filter(value => value.category === categoryId) }
  function isExpanded(categoryId) { return card.expanded[categoryId] === true }
  function toggleCategory(categoryId) {
    const next = Object.assign({}, card.expanded)
    next[categoryId] = !card.isExpanded(categoryId)
    card.expanded = next
  }
  function userRowAt(index) { return userRows.itemAt(index) as UserRow }
  // After a refresh, focus returns to the row with the same id; after a
  // removal, to the next row, or to the path field when none is left.
  function restoreFocus() {
    if (!card.pendingFocusId) return
    const id = card.pendingFocusId
    card.pendingFocusId = ""
    for (let index = 0; index < userRows.count; index++) {
      const row = card.userRowAt(index)
      if (row && row.modelData.id === id) {
        row.editControl.forceActiveFocus()
        return
      }
    }
    card.focusPath()
  }

  // Excluding takes effect at once; including again waits for the reindex.
  function statusMessage() {
    const operation = card.lastOperation
    if (card.host.indexRootsBusy && card.isExclusionOperation(operation)) return "Updating excluded folders…"
    if (card.error.length > 0 || operation.length === 0) return ""
    if (operation === Protocol.Operations.IndexExclusionAdd || operation === Protocol.Operations.IndexExclusionEnable)
      return "Saved. The folder is out of search."
    if (card.host.indexReindexPending) return "Reindexing. Results return as indexing finishes."
    return "Saved."
  }

  onExclusionsChanged: {
    if (card.editSubmitted && card.error.length === 0) {
      card.pendingFocusId = card.editingId
      card.editingId = ""
      card.editSubmitted = false
    }
    if (card.clearPathOnReply && card.lists(pathField.text)) {
      pathField.text = ""
      card.clearPathOnReply = false
    }
    Qt.callLater(card.restoreFocus)
  }
  onErrorChanged: {
    if (card.error.length === 0) return
    card.editSubmitted = false
    card.clearPathOnReply = false
  }

  theme: card.host.theme
  searchQuery: card.host.part_settingsSearch.text
  settingId: Protocol.SettingIds.ExcludedFolders
  title: "Excluded folders"
  description: "Skip folders inside indexed folders. Built-in exclusions keep caches and package stores out of search. Changes apply immediately."

  RowLayout {
    Layout.fillWidth: true
    SettingsField {
      id: pathField
      machineText: true
      theme: card.theme
      Layout.fillWidth: true
      placeholderText: "Folder path, for example ~/Videos/raw"
      Accessible.name: "Folder to exclude"
      Accessible.description: "Enter a folder path, then press Enter to remove it from search."
      onAccepted: card.add()
    }
    DeckButton {
      id: addButton
      theme: card.theme
      text: "Exclude folder"
      focusPolicy: Qt.TabFocus
      Accessible.name: "Exclude folder"
      onClicked: { card.add(); card.focusPath() }
    }
  }

  Text {
    id: errorText
    Layout.fillWidth: true
    visible: text.length > 0
    text: card.error
    textFormat: Text.PlainText
    wrapMode: Text.Wrap
    color: card.theme.urgent
    font.family: card.theme.fontFamily
    font.pixelSize: card.theme.fontBody
    Accessible.role: Accessible.AlertMessage
    Accessible.name: text
  }

  Text {
    id: statusText
    Layout.fillWidth: true
    visible: text.length > 0
    text: card.statusMessage()
    textFormat: Text.PlainText
    wrapMode: Text.Wrap
    color: card.theme.textMuted
    font.family: card.theme.fontFamily
    font.pixelSize: card.theme.fontSmall
    Accessible.role: Accessible.StaticText
    Accessible.name: text
  }

  Text {
    Layout.fillWidth: true
    Layout.topMargin: Math.round(4 * card.theme.scale)
    text: card.userFolders.length === 0 ? "Your excluded folders · none yet" : "Your excluded folders"
    color: card.theme.textMuted
    font.family: card.theme.fontFamily
    font.pixelSize: card.theme.fontSmall
    font.weight: Font.DemiBold
  }

  component UserRow: RowLayout {
    id: row
    required property var modelData
    required property int index
    readonly property bool editing: card.editingId === row.modelData.id
    readonly property alias editControl: editButton
    readonly property alias removeControl: removeButton
    readonly property alias editFieldControl: editField
    Layout.fillWidth: true
    spacing: Math.round(10 * card.theme.scale)

    ColumnLayout {
      Layout.fillWidth: true
      visible: !row.editing
      Text {
        Layout.fillWidth: true
        text: card.displayPath(row.modelData.path)
        textFormat: Text.PlainText
        wrapMode: Text.WrapAnywhere
        color: card.theme.text
        font.family: card.theme.monoFontFamily
        font.pixelSize: card.theme.fontBody
      }
      Text {
        Layout.fillWidth: true
        visible: text.length > 0
        text: row.modelData.issue || (row.modelData.exists ? "" : "Applies when this folder exists")
        textFormat: Text.PlainText
        wrapMode: Text.Wrap
        color: row.modelData.issue ? card.theme.urgent : card.theme.textMuted
        font.family: card.theme.fontFamily
        font.pixelSize: card.theme.fontSmall
      }
    }
    SettingsField {
      id: editField
      visible: row.editing
      machineText: true
      theme: card.theme
      Layout.fillWidth: true
      Accessible.name: "Excluded folder path"
      Accessible.description: "Enter saves the new path. Escape cancels."
      onVisibleChanged: if (visible) { text = card.displayPath(row.modelData.path); forceActiveFocus(); selectAll() }
      onAccepted: card.saveEdit(row.modelData.id, text)
      Keys.onEscapePressed: event => { card.cancelEdit(); event.accepted = true }
    }
    DeckButton {
      id: editButton
      theme: card.theme
      compact: true
      text: row.editing ? "Save" : "Edit"
      focusPolicy: Qt.TabFocus
      Accessible.name: (row.editing ? "Save excluded folder " : "Edit excluded folder ") + row.modelData.path
      onClicked: {
        if (row.editing) card.saveEdit(row.modelData.id, editField.text)
        else card.startEdit(row.modelData)
      }
    }
    DeckButton {
      id: removeButton
      theme: card.theme
      compact: true
      text: row.editing ? "Cancel" : "Remove"
      focusPolicy: Qt.TabFocus
      Accessible.name: (row.editing ? "Cancel editing " : "Remove excluded folder ") + row.modelData.path
      onClicked: {
        if (row.editing) {
          card.cancelEdit()
          return
        }
        const next = card.userRowAt(row.index + 1)
        card.pendingFocusId = next ? next.modelData.id : card.pathFieldFocus
        card.request(Protocol.Operations.IndexExclusionRemove, "", row.modelData.id)
      }
    }
  }
  Repeater {
    id: userRows
    model: card.userFolders
    delegate: UserRow {}
  }

  Text {
    Layout.fillWidth: true
    Layout.topMargin: Math.round(8 * card.theme.scale)
    text: "Built-in"
    color: card.theme.textMuted
    font.family: card.theme.fontFamily
    font.pixelSize: card.theme.fontSmall
    font.weight: Font.DemiBold
  }

  component CategorySection: ColumnLayout {
    id: section
    required property var modelData
    readonly property var entries: card.categoryEntries(section.modelData.id)
    readonly property int excludedCount: section.entries.filter(value => value.enabled).length
    readonly property bool open: card.isExpanded(section.modelData.id)
    Layout.fillWidth: true
    visible: section.entries.length > 0
    spacing: Math.round(6 * card.theme.scale)

    DeckButton {
      objectName: "exclusionCategory-" + section.modelData.id
      theme: card.theme
      compact: true
      flat: true
      Layout.fillWidth: true
      text: (section.open ? "▾  " : "▸  ") + section.modelData.title + "   " + section.excludedCount + " of " + section.entries.length + " excluded"
      focusPolicy: Qt.TabFocus
      Accessible.name: section.modelData.title + ", " + section.excludedCount + " of " + section.entries.length + " excluded"
      Accessible.description: section.open ? "Expanded. Press to collapse." : "Collapsed. Press to show the folders."
      onClicked: card.toggleCategory(section.modelData.id)
    }

    Repeater {
      model: section.open ? section.entries : []
      delegate: SettingsToggle {
        required property var modelData
        objectName: "builtInExclusion-" + modelData.id
        theme: card.theme
        Layout.fillWidth: true
        Layout.leftMargin: Math.round(18 * card.theme.scale)
        text: card.displayPath(modelData.path)
        help: modelData.description + (modelData.enabledByDefault ? "" : " · Suggested, off by default")
          + (modelData.exists ? "" : " · Not on this computer")
        checked: modelData.enabled
        enabled: !card.host.indexRootsBusy
        Accessible.name: "Exclude " + modelData.path + " from search"
        onClicked: card.request(checked ? Protocol.Operations.IndexExclusionEnable : Protocol.Operations.IndexExclusionDisable, "", modelData.id)
      }
    }
  }
  Repeater {
    model: card.categories
    delegate: CategorySection {}
  }
}
