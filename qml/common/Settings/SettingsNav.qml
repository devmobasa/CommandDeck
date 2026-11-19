import ".."
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Layouts
import "../TabFence.js" as TabFence

// Left rail of the settings dialog. One entry per page; Up/Down move the
// selection when the rail has focus.
Item {
  id: root

  required property var theme
  property var pages: []
  property string currentId: ""
  signal selected(string pageId)

  readonly property int rowHeight: Math.round(root.theme.fontBody * 2.35)
  implicitWidth: Math.round(212 * root.theme.scale)
  implicitHeight: column.implicitHeight
  activeFocusOnTab: true
  Accessible.role: Accessible.List
  Accessible.name: "Settings pages"

  function indexOf(pageId) {
    for (let index = 0; index < (root.pages || []).length; index++) {
      if (root.pages[index].id === pageId) return index
    }
    return -1
  }

  function step(delta) {
    const count = (root.pages || []).length
    if (count === 0) return
    const next = Math.max(0, Math.min(count - 1, root.indexOf(root.currentId) + delta))
    root.selected(root.pages[next].id)
  }

  Keys.onUpPressed: root.step(-1)
  Keys.onDownPressed: root.step(1)
  // Tab and Shift+Tab stay inside the settings dialog.
  Keys.priority: Keys.BeforeItem
  Keys.onPressed: event => TabFence.route(root.parent, event)

  ColumnLayout {
    id: column
    anchors.left: parent.left
    anchors.right: parent.right
    spacing: Math.round(2 * root.theme.scale)

    Repeater {
      model: root.pages || []

      delegate: Rectangle {
        id: entry
        required property var modelData
        readonly property bool current: entry.modelData.id === root.currentId
        Layout.fillWidth: true
        readonly property bool groupStart: entry.index > 0
          && root.pages[entry.index - 1].group !== entry.modelData.group
        required property int index
        Layout.topMargin: entry.groupStart ? Math.round(root.theme.fontBody * 0.9) : 0
        Layout.preferredHeight: root.rowHeight
        radius: Math.min(root.theme.radius, Math.round(7 * root.theme.scale))
        color: entry.current
          ? root.theme.selectedBackground
          : hover.hovered ? root.theme.surface : "transparent"
        border.width: entry.current && root.activeFocus ? 2 : 0
        border.color: entry.current && root.activeFocus ? root.theme.focusRing : root.theme.selectedBorder
        Accessible.role: Accessible.ListItem
        Accessible.name: String(entry.modelData.title || "")
        Accessible.focusable: true
        Accessible.description: String(entry.modelData.subtitle || "")
        Accessible.selected: entry.current
        Accessible.onPressAction: root.selected(entry.modelData.id)

        HoverHandler { id: hover }
        TapHandler { onTapped: root.selected(entry.modelData.id) }

        Rectangle {
          anchors.left: parent.left
          anchors.verticalCenter: parent.verticalCenter
          anchors.leftMargin: Math.round(3 * root.theme.scale)
          width: Math.max(2, Math.round(2 * root.theme.scale))
          height: Math.round(parent.height * 0.5)
          radius: width / 2
          color: root.theme.accent
          visible: entry.current
        }

        RowLayout {
          anchors.fill: parent
          anchors.leftMargin: Math.round(12 * root.theme.scale)
          anchors.rightMargin: Math.round(10 * root.theme.scale)
          spacing: Math.round(12 * root.theme.scale)

          SettingsSymbol {
            objectName: "settingsNavGlyph"
            Layout.preferredWidth: Math.round(root.theme.fontBody * 1.5)
            Layout.preferredHeight: width
            symbol: String(entry.modelData.id)
            ink: entry.current ? root.theme.accent : root.theme.textMuted
          }
          Text {
            Layout.fillWidth: true
            text: String(entry.modelData.title || "")
            color: root.theme.text
            elide: Text.ElideRight
            font.family: root.theme.fontFamily
            font.pixelSize: root.theme.fontBody
            font.weight: entry.current ? Font.DemiBold : Font.Normal
          }
        }
      }
    }
  }
}
