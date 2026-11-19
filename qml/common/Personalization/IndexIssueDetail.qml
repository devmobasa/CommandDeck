import ".."
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Layouts
import "IndexIssueGroups.js" as IssueGroups
import "../HomePaths.js" as HomePaths
import "../UiConstants.js" as Ui

// What the selected issue or group is and what to do about it. Exclude
// folder… opens an editable path; the daemon validates whatever is confirmed.
FocusScope {
  id: root

  required property var theme
  property var row: null
  property string homeDirectory: ""
  property bool busy: false
  readonly property var issueGroup: root.row ? root.row.group : null
  readonly property var item: root.row && root.row.kind === "item" ? root.row.item : null
  readonly property bool privatePaths: root.issueGroup !== null && root.issueGroup.redacted
  // A group inside a built-in exclusion that is turned off offers that switch.
  readonly property var disabledBuiltIn: root.issueGroup && root.issueGroup.builtIn && !root.issueGroup.builtIn.enabled
    ? root.issueGroup.builtIn : null
  readonly property string targetFolder: root.item
    ? (IssueGroups.copyablePath(root.item, IssueGroups.ClipLength).length > 0 ? IssueGroups.folderOf(root.item) : "")
    : root.issueGroup && root.issueGroup.complete ? root.issueGroup.folder : ""
  readonly property string targetPath: root.item ? IssueGroups.copyablePath(root.item, IssueGroups.ClipLength) : root.targetFolder
  readonly property string revealId: root.item ? root.item.id : root.issueGroup ? root.issueGroup.items[0].id : ""
  property bool sheetOpen: false
  readonly property alias excludeControl: excludeButton
  readonly property alias revealControl: revealButton
  readonly property alias copyControl: copyButton
  readonly property alias sheetFieldControl: sheetField
  readonly property alias sheetConfirmControl: confirmButton
  readonly property alias sheetCancelControl: cancelButton
  readonly property var focusTargets: root.sheetOpen ? [sheetField, confirmButton, cancelButton] : [excludeButton, revealButton, copyButton]

  signal excludeRequested(string path)
  signal builtInExclusionRequested(string exclusionId)
  signal revealRequested(string workItemId)
  signal copyRequested(string path)

  function openSheet() {
    if (root.disabledBuiltIn) {
      root.builtInExclusionRequested(root.disabledBuiltIn.id)
      return
    }
    sheetField.text = HomePaths.display(root.targetFolder, root.homeDirectory)
    root.sheetOpen = true
    Qt.callLater(function() { sheetField.forceActiveFocus(); sheetField.selectAll() })
  }
  function closeSheet() {
    root.sheetOpen = false
    Qt.callLater(excludeButton.forceActiveFocus)
  }
  function confirmSheet() {
    root.excludeRequested(sheetField.text)
    root.sheetOpen = false
    Qt.callLater(excludeButton.forceActiveFocus)
  }
  function coveredCount(path) {
    if (!root.issueGroup) return 0
    return root.issueGroup.items.filter(value => IssueGroups.isSameOrDescendant(path, IssueGroups.fullPath(value))).length
  }
  // A refresh rebuilds the row object; only a different selection closes the sheet.
  readonly property string rowKey: root.row ? root.row.key : ""
  onRowKeyChanged: root.sheetOpen = false

  ColumnLayout {
    anchors.fill: parent
    spacing: Math.round(10 * root.theme.scale)
    visible: root.row !== null

    Text {
      Layout.fillWidth: true
      text: root.issueGroup ? root.issueGroup.title : ""
      color: root.theme.text
      font.family: root.theme.fontFamily
      font.pixelSize: Math.round(root.theme.fontBody * 1.15)
      font.weight: Font.DemiBold
      wrapMode: Text.Wrap
    }
    Text {
      Layout.fillWidth: true
      textFormat: Text.PlainText
      text: root.privatePaths ? "Paths under privacy rules are hidden."
        : HomePaths.display(root.item ? IssueGroups.fullPath(root.item) : root.targetFolder, root.homeDirectory)
      color: root.theme.text
      font.family: root.theme.monoFontFamily
      font.pixelSize: root.theme.fontBody
      wrapMode: Text.WrapAnywhere
    }
    Text {
      Layout.fillWidth: true
      visible: root.issueGroup !== null && root.item === null
      text: root.issueGroup ? root.issueGroup.items.length + (root.issueGroup.items.length === 1 ? " loaded issue · " : " loaded issues · ")
        + root.issueGroup.state : ""
      color: root.theme.textMuted
      font.family: root.theme.fontFamily
      font.pixelSize: root.theme.fontSmall
    }
    Text {
      Layout.fillWidth: true
      visible: text.length > 0
      text: root.disabledBuiltIn
        ? "This is inside " + root.disabledBuiltIn.description + ", a built-in excluded folder that is turned off."
        : root.issueGroup ? root.issueGroup.hint : ""
      color: root.theme.textMuted
      font.family: root.theme.fontFamily
      font.pixelSize: root.theme.fontBody
      wrapMode: Text.Wrap
    }

    Flow {
      Layout.fillWidth: true
      visible: !root.sheetOpen && !root.privatePaths
      spacing: Math.round(8 * root.theme.scale)

      DeckButton {
        id: excludeButton
        theme: root.theme
        kind: Ui.ButtonVariant.Primary
        text: root.disabledBuiltIn ? "Turn on built-in exclusion" : "Exclude folder…"
        enabled: !root.busy && root.targetFolder.length > 0
        tabFence: false
        Accessible.name: root.disabledBuiltIn ? "Turn on the built-in exclusion for " + root.disabledBuiltIn.path : "Exclude a folder from search"
        onClicked: root.openSheet()
      }
      DeckButton {
        id: revealButton
        theme: root.theme
        text: "Show in file manager"
        enabled: root.revealId.length > 0
        tabFence: false
        Accessible.name: "Show the folder in the file manager"
        onClicked: root.revealRequested(root.revealId)
      }
      DeckButton {
        id: copyButton
        theme: root.theme
        text: "Copy path"
        enabled: root.targetPath.length > 0
        tabFence: false
        Accessible.name: "Copy the path"
        Accessible.description: "Ctrl+C in the list copies it too."
        onClicked: root.copyRequested(root.targetPath)
      }
    }

    Rectangle {
      Layout.fillWidth: true
      visible: root.sheetOpen
      implicitHeight: sheet.implicitHeight + Math.round(24 * root.theme.scale)
      radius: root.theme.radiusSmall
      color: root.theme.surface
      border.width: 1
      border.color: root.theme.border
      Accessible.role: Accessible.Dialog
      Accessible.name: "Exclude folder"

      ColumnLayout {
        id: sheet
        anchors.fill: parent
        anchors.margins: Math.round(12 * root.theme.scale)
        spacing: Math.round(8 * root.theme.scale)

        Text {
          Layout.fillWidth: true
          text: "Exclude this folder and everything in it from search"
          color: root.theme.text
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontBody
          font.weight: Font.DemiBold
          wrapMode: Text.Wrap
        }
        SettingsField {
          id: sheetField
          machineText: true
          theme: root.theme
          Layout.fillWidth: true
          Accessible.name: "Folder to exclude"
          Accessible.description: "Shorten the path to exclude a larger folder. Enter excludes it; Escape cancels."
          onAccepted: root.confirmSheet()
          Keys.onEscapePressed: event => { root.closeSheet(); event.accepted = true }
        }
        Text {
          Layout.fillWidth: true
          readonly property int covered: root.coveredCount(String(sheetField.text).replace(/^~(?=\/|$)/, root.homeDirectory))
          text: (covered > 0 ? "Covers " + covered + (covered === 1 ? " loaded issue. " : " loaded issues. ") : "")
            + "Its files leave search now. Undo in Settings › Search › Excluded folders."
          color: root.theme.textMuted
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontSmall
          wrapMode: Text.Wrap
        }
        RowLayout {
          Layout.fillWidth: true
          Item { Layout.fillWidth: true }
          DeckButton {
            id: cancelButton
            theme: root.theme
            text: "Cancel"
            tabFence: false
            onClicked: root.closeSheet()
          }
          DeckButton {
            id: confirmButton
            theme: root.theme
            kind: Ui.ButtonVariant.Primary
            text: "Exclude"
            enabled: sheetField.text.trim().length > 0 && !root.busy
            tabFence: false
            Accessible.name: "Exclude this folder"
            onClicked: root.confirmSheet()
          }
        }
      }
    }

    Item { Layout.fillHeight: true }
  }
}
