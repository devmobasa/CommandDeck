import ".."
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import "IndexIssueGroups.js" as IssueGroups
import "../HomePaths.js" as HomePaths
import "../Protocol.js" as Protocol

// The inspector's issue list: one row per group, expanded rows list the
// group's items. Up/Down move, Right expands, Left collapses or returns to
// the group, Enter and Space expand or collapse a group.
FocusScope {
  id: root

  required property var theme
  required property var groups
  property string homeDirectory: ""
  property var expanded: ({})
  property string selectedKey: ""
  readonly property var rows: IssueGroups.rows(root.groups, root.expanded)
  readonly property alias view: list
  readonly property var selectedRow: root.rows.find(row => row.key === root.selectedKey) || null
  readonly property int rowHeight: Math.round(root.theme.fontBody * 3.4)

  signal copyRequested()

  function select(index) {
    if (root.rows.length === 0) return
    const bounded = Math.max(0, Math.min(root.rows.length - 1, index))
    root.selectedKey = root.rows[bounded].key
    list.positionViewAtIndex(bounded, ListView.Contain)
  }
  function selectedIndex() { return root.rows.findIndex(row => row.key === root.selectedKey) }
  function setExpanded(key, value) {
    const next = Object.assign({}, root.expanded)
    next[key] = value
    root.expanded = next
  }
  function toggle(row) {
    if (!row || row.kind !== "group") return
    root.setExpanded(row.group.key, root.expanded[row.group.key] !== true)
  }
  // A refresh keeps the selection when its row still exists.
  function ensureSelection() {
    if (root.rows.length > 0 && root.selectedIndex() < 0) root.selectedKey = root.rows[0].key
  }
  onRowsChanged: ensureSelection()

  Keys.onPressed: event => {
    const row = root.selectedRow
    if (event.key === Qt.Key_Down) root.select(root.selectedIndex() + 1)
    else if (event.key === Qt.Key_Up) root.select(root.selectedIndex() - 1)
    else if (event.key === Qt.Key_Home && event.modifiers === Qt.NoModifier) root.select(0)
    else if (event.key === Qt.Key_End && event.modifiers === Qt.NoModifier) root.select(root.rows.length - 1)
    else if (event.key === Qt.Key_Right && row && row.kind === "group") root.setExpanded(row.group.key, true)
    else if (event.key === Qt.Key_Left && row && row.kind === "group") root.setExpanded(row.group.key, false)
    else if (event.key === Qt.Key_Left && row && row.kind === "item") root.selectedKey = row.group.key
    else if ((event.key === Qt.Key_Return || event.key === Qt.Key_Enter || event.key === Qt.Key_Space) && row) root.toggle(row)
    else if (event.key === Qt.Key_C && event.modifiers === Qt.ControlModifier) root.copyRequested()
    else return
    event.accepted = true
  }

  Rectangle {
    anchors.fill: parent
    radius: root.theme.radiusSmall
    color: root.theme.withAlpha(root.theme.text, 0.035)
    border.width: root.activeFocus ? 2 : 0
    border.color: root.theme.focusRing
  }

  ListView {
    id: list
    anchors.fill: parent
    anchors.margins: Math.round(6 * root.theme.scale)
    clip: true
    model: root.rows
    spacing: Math.round(2 * root.theme.scale)
    boundsBehavior: Flickable.StopAtBounds
    ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
    Accessible.role: Accessible.Tree
    Accessible.name: "Index issues"

    delegate: Rectangle {
      id: rowItem
      required property var modelData
      required property int index
      readonly property bool group: rowItem.modelData.kind === "group"
      readonly property bool selected: rowItem.modelData.key === root.selectedKey
      readonly property var issueGroup: rowItem.modelData.group
      width: ListView.view.width
      height: root.rowHeight
      radius: root.theme.radiusSmall
      color: rowItem.selected ? root.theme.selectedBackground : hover.hovered ? root.theme.surface : "transparent"
      Accessible.role: rowItem.group ? Accessible.TreeItem : Accessible.ListItem
      Accessible.name: rowItem.group
        ? IssueGroups.displayFolder(rowItem.issueGroup, root.homeDirectory) + ", " + rowItem.issueGroup.items.length + " issues, "
          + rowItem.issueGroup.title
        : IssueGroups.fullPath(rowItem.modelData.item)
      Accessible.selected: rowItem.selected

      HoverHandler { id: hover }
      TapHandler {
        onTapped: {
          if (rowItem.selected) root.toggle(rowItem.modelData)
          root.selectedKey = rowItem.modelData.key
          root.forceActiveFocus()
        }
      }

      Rectangle {
        anchors.left: parent.left
        anchors.verticalCenter: parent.verticalCenter
        width: Math.max(2, Math.round(3 * root.theme.scale))
        height: Math.round(parent.height * 0.55)
        radius: width / 2
        visible: rowItem.selected
        color: root.theme.accent
      }

      Text {
        id: chevron
        anchors.left: parent.left
        anchors.leftMargin: Math.round((rowItem.group ? 10 : 30) * root.theme.scale)
        anchors.verticalCenter: parent.verticalCenter
        width: Math.round(root.theme.fontBody * 1.2)
        text: rowItem.group ? (root.expanded[rowItem.issueGroup.key] === true ? "▾" : "▸") : ""
        color: root.theme.textMuted
        font.family: root.theme.fontFamily
        font.pixelSize: root.theme.fontBody
      }

      Column {
        anchors.left: chevron.right
        anchors.right: count.left
        anchors.rightMargin: Math.round(8 * root.theme.scale)
        anchors.verticalCenter: parent.verticalCenter
        spacing: Math.round(2 * root.theme.scale)

        Text {
          width: parent.width
          textFormat: Text.PlainText
          text: rowItem.group ? IssueGroups.displayFolder(rowItem.issueGroup, root.homeDirectory)
            : IssueGroups.fullPath(rowItem.modelData.item).split("/").pop()
          color: rowItem.issueGroup.state === Protocol.IndexWorkStates.Failed ? root.theme.text : root.theme.textMuted
          font.family: rowItem.group ? root.theme.monoFontFamily : root.theme.fontFamily
          font.pixelSize: root.theme.fontBody
          font.weight: rowItem.group ? Font.DemiBold : Font.Normal
          elide: Text.ElideLeft
        }
        Text {
          width: parent.width
          textFormat: Text.PlainText
          text: rowItem.group ? rowItem.issueGroup.title + (rowItem.issueGroup.builtIn ? " · " + rowItem.issueGroup.builtIn.description : "")
            : HomePaths.display(IssueGroups.parentOf(IssueGroups.fullPath(rowItem.modelData.item)), root.homeDirectory)
          color: root.theme.textMuted
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontSmall
          elide: rowItem.group ? Text.ElideRight : Text.ElideLeft
        }
      }

      Rectangle {
        id: count
        anchors.right: parent.right
        anchors.rightMargin: Math.round(10 * root.theme.scale)
        anchors.verticalCenter: parent.verticalCenter
        visible: rowItem.group
        implicitWidth: countText.implicitWidth + Math.round(14 * root.theme.scale)
        width: rowItem.group ? implicitWidth : 0
        height: countText.implicitHeight + Math.round(6 * root.theme.scale)
        radius: height / 2
        color: rowItem.issueGroup.state === Protocol.IndexWorkStates.Failed ? root.theme.withAlpha(root.theme.urgent, 0.18) : root.theme.surfaceStrong

        Text {
          id: countText
          anchors.centerIn: parent
          text: rowItem.issueGroup.items.length
          color: rowItem.issueGroup.state === Protocol.IndexWorkStates.Failed ? root.theme.urgent : root.theme.textMuted
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontSmall
          font.weight: Font.DemiBold
        }
      }
    }
  }
}
