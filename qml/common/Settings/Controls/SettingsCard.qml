import "../.."
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Layouts
import "../SettingsIndex.js" as SettingsIndex

// A searchable group: heading above an inset surface, with dividers between
// visible children. The outer item remains the search/spotlight target.
Item {
  id: root
  required property var theme
  function tint(value, amount) { return Qt.rgba(value.r, value.g, value.b, amount) }
  property string title: ""
  property string description: ""
  property Component headingAccessory: null
  readonly property alias accessoryItem: accessory.item
  property string searchQuery: ""
  property string settingId: ""
  readonly property var settingEntry: SettingsIndex.entry(root.settingId)
  readonly property string searchTerms: root.settingEntry ? root.settingEntry.keywords : ""
  property var wheelForwarder: null
  readonly property bool matchesSearch: {
    const text = (root.title + " " + root.description + " " + root.searchTerms).toLowerCase()
    return root.searchQuery.toLowerCase().trim().split(/\s+/).every(term => text.indexOf(term) >= 0)
  }
  visible: matchesSearch
  property bool danger: false
  default property alias body: content.data
  readonly property int pad: Math.round(14 * root.theme.scale)
  Layout.fillWidth: true
  implicitHeight: column.implicitHeight
  Accessible.role: Accessible.Grouping
  Accessible.name: root.title

  WheelHandler {
    target: null
    enabled: typeof root.wheelForwarder === "function"
    onWheel: event => root.wheelForwarder(event)
  }

  ColumnLayout {
    id: column
    width: parent.width
    spacing: Math.round(6 * root.theme.scale)
    ColumnLayout {
      Layout.fillWidth: true
      Layout.leftMargin: root.pad
      Layout.rightMargin: root.pad
      spacing: Math.round(3 * root.theme.scale)
      visible: root.title.length > 0 || root.description.length > 0
      RowLayout {
        Layout.fillWidth: true
        Text {
        Layout.fillWidth: true
        visible: root.title.length > 0
        text: root.title
        color: root.theme.text
        font.family: root.theme.fontFamily
        font.pixelSize: root.theme.fontBody
        font.weight: Font.DemiBold
        elide: Text.ElideRight
        }
        Loader { id: accessory; sourceComponent: root.headingAccessory }
      }
      Text {
        Layout.fillWidth: true
        visible: root.description.length > 0
        text: root.description
        color: root.theme.textMuted
        wrapMode: Text.Wrap
        font.family: root.theme.fontFamily
        font.pixelSize: root.theme.fontSmall
      }
    }
    Rectangle {
      id: surface
      Layout.fillWidth: true
      implicitHeight: content.implicitHeight
      objectName: "settingsCardSurface"
      radius: Math.min(root.theme.radius, Math.round(10 * root.theme.scale))
      color: root.theme.surface
      border.width: root.danger ? 1 : 0
      border.color: root.tint(root.theme.urgent, 0.35)
      ColumnLayout {
        id: content
        objectName: "settingsGroupBody"
        x: root.pad
        width: parent.width - root.pad * 2
        spacing: 0
        // Controls that supply their own row padding mark themselves. Other
        // content (lists, editors, buttons) gets padding as one group row.
        function padChildren() {
          for (const child of children) {
            if (child.settingsRow === true || !child.implicitHeight) continue
            child.Layout.topMargin = Math.round(10 * root.theme.scale)
            child.Layout.bottomMargin = Math.round(10 * root.theme.scale)
          }
        }
        // The dividers follow the children once per batch of changes. A
        // Repeater adds its rows one at a time, and rebuilding every divider
        // for each row took seconds for a list of 200 desktop shortcuts.
        property var dividedRows: []
        function divideRows() { content.dividedRows = Array.from(content.children) }
        onChildrenChanged: {
          Qt.callLater(padChildren)
          Qt.callLater(divideRows)
        }
        onImplicitHeightChanged: Qt.callLater(padChildren)
        Component.onCompleted: {
          Qt.callLater(padChildren)
          divideRows()
        }
        Connections {
          target: root.theme
          function onScaleChanged() { content.padChildren() }
        }
      }
      // A divider goes above each shown row that has a row above it. The rows'
      // laid-out positions decide, not their order in the list, so a row that
      // moves in place needs no new batch. The layout starts at 0 and skips
      // hidden rows, so only the first shown row starts at its own top margin.
      Repeater {
        model: content.dividedRows
        delegate: Rectangle {
          // Null once the row is destroyed, until the next batch drops it.
          required property var modelData
          readonly property real rowTop: modelData ? modelData.y - modelData.Layout.topMargin : 0
          objectName: "settingsCardDivider"
          x: root.pad
          y: rowTop
          width: content.width
          height: 1
          visible: !!modelData && rowTop > 0.5 && modelData.visible && modelData.height > 0
          color: root.theme.separator
        }
      }
    }
  }
}
