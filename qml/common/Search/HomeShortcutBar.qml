import ".."
pragma ComponentBehavior: Bound
import QtQuick
import "../UiConstants.js" as Ui

Flow {
  id: root

  required property var theme
  property var filterDescriptors: []
  property var sources: []
  property Item previousControl: null
  property Item nextControl: null
  readonly property Item firstControl: shortcuts.count > 0 ? shortcuts.itemAt(0) : null
  readonly property Item lastControl: shortcuts.count > 0 ? shortcuts.itemAt(shortcuts.count - 1) : null
  signal filterRequested(string token)

  // Use the daemon's labels and canonical tokens. A source can be absent on
  // another desktop or become unavailable between launches.
  readonly property var filters: [Ui.QueryPrefix.App, Ui.QueryPrefix.File, Ui.QueryPrefix.Win]
    .map(token => (root.filterDescriptors || []).find(filter => filter.token === token && filter.available !== false))
    .filter(filter => filter !== undefined
      && !root.sources.some(source => source.sourceId === filter.sourceId && source.enabledInSearch === false))

  spacing: Math.round(6 * root.theme.scale)
  Accessible.role: Accessible.Grouping
  Accessible.name: "Home shortcuts"

  Repeater {
    id: shortcuts
    model: root.filters

    delegate: KeyboardButton {
      id: shortcut
      required property int index
      required property var modelData
      text: String(shortcut.modelData.label || shortcut.modelData.token)
      implicitWidth: caption.implicitWidth + Math.round(20 * root.theme.scale)
      implicitHeight: Math.round(28 * root.theme.scale)
      width: Math.min(root.width, implicitWidth)
      hoverEnabled: true
      focusPolicy: Qt.TabFocus
      Accessible.description: "Search only this source. Prefix " + shortcut.modelData.token
      KeyNavigation.backtab: shortcut.index > 0 ? shortcuts.itemAt(shortcut.index - 1) : root.previousControl
      KeyNavigation.tab: shortcut.index + 1 < shortcuts.count ? shortcuts.itemAt(shortcut.index + 1) : root.nextControl
      onClicked: root.filterRequested(shortcut.modelData.token)

      contentItem: Text {
        id: caption
        text: shortcut.text
        textFormat: Text.PlainText
        color: shortcut.activeFocus || shortcut.hovered ? root.theme.text : root.theme.textMuted
        font.family: root.theme.fontFamily
        font.pixelSize: root.theme.fontSmall
        font.weight: Font.DemiBold
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        elide: Text.ElideRight
      }

      background: Rectangle {
        radius: root.theme.radiusSmall
        color: shortcut.down || shortcut.hovered ? root.theme.surfaceStrong : root.theme.surface
        border.width: 1
        border.color: shortcut.activeFocus ? root.theme.focusRing : root.theme.separator
      }
    }
  }
}
