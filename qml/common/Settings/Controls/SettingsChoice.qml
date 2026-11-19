import "../.."
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Window
import "../../TabFence.js" as TabFence

// A drop-down whose options carry a title, a one-line description, and an
// optional badge, the way a model or a trust level should be presented.
// `options` is an array of { id, title, description, badge, warning }.
// The ComboBox contract (currentIndex, count, activated) is unchanged.
ComboBox {
  id: root

  required property var theme
  function tint(value, amount) { return Qt.rgba(value.r, value.g, value.b, amount) }
  property var options: []
  property string placeholder: "Nothing to choose"
  property bool showCurrentDescription: true
  property bool showSelection: true
  property Item windowProbe: Item { parent: root; visible: false }

  // Tab and Shift+Tab stay inside the settings dialog.
  Keys.priority: Keys.BeforeItem
  Keys.onPressed: event => TabFence.route(root.parent, event)

  readonly property var currentOption: root.currentIndex >= 0
    && root.currentIndex < (root.options || []).length
      ? root.options[root.currentIndex] : null
  readonly property string currentTitle: root.currentOption
    ? String(root.currentOption.title || root.currentOption.id || "") : ""
  readonly property string currentDescription: root.currentOption
    ? String(root.currentOption.description || "") : ""

  readonly property string currentWarning: root.currentOption && root.currentOption.warning
    ? String(root.currentOption.badge || "Warning") : ""

  // Options feed the model as objects so the ComboBox value contract holds:
  // currentText is the title (falling back to the id) and currentValue the id.
  textRole: "title"
  valueRole: "id"
  model: (root.options || []).map(option => ({ id: option.id, title: String(option.title || option.id || "") }))
  implicitHeight: Math.round(root.theme.fontBody * 2.1)
  implicitWidth: Math.max(Math.round(90 * root.theme.scale), Math.min(Math.round(280 * root.theme.scale), valueText.implicitWidth + rightPadding + leftPadding))
  leftPadding: Math.round(12 * root.theme.scale)
  rightPadding: Math.round(root.theme.fontBody * 1.3 + 20 * root.theme.scale)
  hoverEnabled: true
  focusPolicy: Qt.StrongFocus

  function optionIndex(id) {
    for (let index = 0; index < (root.options || []).length; index++) {
      if (String(root.options[index].id) === String(id)) return index
    }
    return -1
  }

  contentItem: Text {
    id: valueText
    textFormat: Text.PlainText
    text: root.displayText || root.placeholder
    color: root.enabled && root.count > 0 ? root.theme.text : root.theme.textFaint
    horizontalAlignment: Text.AlignRight
    verticalAlignment: Text.AlignVCenter
    elide: Text.ElideRight
    font.family: root.theme.fontFamily
    font.pixelSize: root.theme.fontBody
  }
  indicator: Rectangle {
    x: root.width - width - Math.round(6 * root.theme.scale)
    y: (root.height - height) / 2
    width: Math.round(root.theme.fontBody * 1.3)
    height: width
    radius: width * 0.3
    color: root.theme.surfaceStrong
    Canvas {
      id: chevrons
      anchors.fill: parent
      readonly property color ink: root.theme.text
      onInkChanged: requestPaint()
      onPaint: {
        const ctx = getContext("2d")
        ctx.reset()
        ctx.strokeStyle = ink
        ctx.lineWidth = 1.5
        ctx.lineCap = "round"
        ctx.lineJoin = "round"
        ctx.beginPath()
        ctx.moveTo(width * 0.33, height * 0.4)
        ctx.lineTo(width * 0.5, height * 0.23)
        ctx.lineTo(width * 0.67, height * 0.4)
        ctx.moveTo(width * 0.33, height * 0.6)
        ctx.lineTo(width * 0.5, height * 0.77)
        ctx.lineTo(width * 0.67, height * 0.6)
        ctx.stroke()
      }
    }
  }
  background: Rectangle {
    radius: height / 2
    color: root.down || root.popup.visible || root.hovered ? root.theme.surface : "transparent"
    border.width: root.activeFocus ? 2 : 0
    border.color: root.theme.focusRing
  }

  delegate: ItemDelegate {
    id: row
    required property int index
    readonly property var option: index < (root.options || []).length ? root.options[index] : null
    readonly property bool current: root.showSelection && index === root.currentIndex
    width: ListView.view ? ListView.view.width : root.width
    height: Math.round((row.option && row.option.description ? 52 : 38) * root.theme.scale)
    hoverEnabled: true
    highlighted: root.highlightedIndex === row.index
    padding: 0

    contentItem: Item {
      Text {
        x: Math.round(8 * root.theme.scale)
        anchors.verticalCenter: parent.verticalCenter
        text: row.current ? "✓" : ""
        color: row.highlighted ? root.theme.accentText : root.theme.text
        font.family: root.theme.fontFamily
        font.pixelSize: root.theme.fontBody
      }
      Column {
        anchors.left: parent.left
        anchors.right: rowBadge.visible ? rowBadge.left : parent.right
        anchors.leftMargin: Math.round(28 * root.theme.scale)
        anchors.rightMargin: Math.round(10 * root.theme.scale)
        anchors.verticalCenter: parent.verticalCenter
        spacing: Math.round(1 * root.theme.scale)

        Text {
          width: parent.width
          textFormat: Text.PlainText
          text: row.option ? String(row.option.title || row.option.id || "") : ""
          color: row.highlighted ? root.theme.accentText : root.theme.text
          elide: Text.ElideRight
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontBody
          font.weight: Font.Normal
        }

        Text {
          width: parent.width
          visible: row.option && row.option.description ? true : false
          textFormat: Text.PlainText
          text: row.option ? String(row.option.description || "") : ""
          color: row.highlighted ? root.theme.accentText : root.theme.textMuted
          elide: Text.ElideRight
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontSmall
        }
      }

      Rectangle {
        id: rowBadge
        visible: row.option && row.option.badge ? true : false
        anchors.right: parent.right
        anchors.rightMargin: Math.round(12 * root.theme.scale)
        anchors.verticalCenter: parent.verticalCenter
        width: rowBadgeText.implicitWidth + Math.round(10 * root.theme.scale)
        height: rowBadgeText.implicitHeight + Math.round(4 * root.theme.scale)
        radius: height / 2
        color: row.option && row.option.warning
          ? root.tint(root.theme.urgent, 0.16)
          : root.tint(root.theme.accent, 0.18)

        Text {
          id: rowBadgeText
          anchors.centerIn: parent
          textFormat: Text.PlainText
          text: row.option && row.option.badge ? String(row.option.badge) : ""
          color: row.highlighted ? root.theme.accentText : row.option && row.option.warning ? root.theme.urgent : root.theme.text
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontCaption
          font.weight: Font.DemiBold
        }
      }
    }

    background: Rectangle {
      color: row.highlighted ? root.theme.accent : row.hovered ? root.theme.surfaceStrong : "transparent"
      radius: Math.max(0, root.theme.radiusSmall - 2)
    }
  }

  popup: Popup {
    // Right-aligned controls open their list leftwards so it never leaves
    // the window.
    readonly property real windowWidth: root.windowProbe.Window.window
      ? root.windowProbe.Window.window.width
      : 1e9
    property real anchorX: 0
    onAboutToShow: anchorX = root.mapToItem(null, 0, 0).x
    x: Math.max(Math.round(8 * root.theme.scale) - anchorX, root.width - width)
    y: root.height + Math.round(4 * root.theme.scale)
    width: Math.min(Math.max(root.width, Math.round(340 * root.theme.scale)), windowWidth - Math.round(16 * root.theme.scale))
    implicitHeight: Math.min(contentItem.implicitHeight + topPadding + bottomPadding, Math.round(420 * root.theme.scale))
    padding: Math.round(6 * root.theme.scale)

    contentItem: ListView {
      clip: true
      implicitHeight: contentHeight
      model: root.popup.visible ? root.delegateModel : null
      currentIndex: root.highlightedIndex
      boundsBehavior: Flickable.StopAtBounds
      ScrollIndicator.vertical: ScrollIndicator {}
    }

    background: Rectangle {
      radius: root.theme.radiusSmall
      color: root.theme.popupBackground
      border.width: 1
      border.color: root.theme.border
    }
  }
}
