import "../.."
pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Layouts

ColumnLayout {
  id: root

  required property var theme
  required property bool compact
  required property bool showPreview
  required property bool showShortcuts
  readonly property int inset: Math.round(10 * root.theme.scale)

  objectName: "settingsLayoutPreview"
  Layout.fillWidth: true
  spacing: Math.round(6 * root.theme.scale)
  Accessible.role: Accessible.Graphic
  Accessible.name: "Launcher layout preview"
  Accessible.description: (root.compact ? "Compact results. " : "Full results. ")
    + (root.showPreview ? "Preview pane shown. " : "Preview pane hidden. ")
    + (root.showShortcuts ? "Home shortcuts shown." : "Home shortcuts hidden.")

  Text {
    text: "Layout preview"
    color: root.theme.textMuted
    font.family: root.theme.fontFamily
    font.pixelSize: root.theme.fontSmall
    Accessible.ignored: true
  }

  Rectangle {
    Layout.fillWidth: true
    implicitHeight: sample.implicitHeight + 2 * root.inset
    radius: root.theme.radiusSmall
    color: root.theme.popupBackground
    border.width: 1
    border.color: root.theme.separator
    Accessible.ignored: true

    ColumnLayout {
      id: sample
      anchors.fill: parent
      anchors.margins: root.inset
      spacing: Math.round(6 * root.theme.scale)

      Flow {
        Layout.fillWidth: true
        visible: root.showShortcuts
        spacing: Math.round(5 * root.theme.scale)

        Repeater {
          model: ["Applications", "Files", "Windows"]
          delegate: Rectangle {
            id: shortcutSample
            required property string modelData
            width: label.implicitWidth + 2 * root.inset
            height: label.implicitHeight + Math.round(6 * root.theme.scale)
            radius: root.theme.radiusSmall
            color: root.theme.surface
            border.width: 1
            border.color: root.theme.separator

            Text {
              id: label
              anchors.centerIn: parent
              text: shortcutSample.modelData
              color: root.theme.textMuted
              font.family: root.theme.fontFamily
              font.pixelSize: root.theme.fontCaption
            }
          }
        }
      }

      RowLayout {
        Layout.fillWidth: true
        spacing: Math.round(6 * root.theme.scale)
        Rectangle {
          id: fileResult
          Layout.fillWidth: true
          Layout.minimumWidth: 0
          implicitHeight: labels.implicitHeight + 2 * root.inset
          color: root.theme.selectedBackground
          radius: root.theme.radiusSmall

          SettingsSymbol {
            id: fileIcon
            x: root.inset
            anchors.verticalCenter: parent.verticalCenter
            width: Math.round(16 * root.theme.scale)
            height: width
            symbol: "editors"
            ink: root.theme.accent
          }

          GridLayout {
            id: labels
            anchors.left: fileIcon.right
            anchors.leftMargin: root.inset
            anchors.right: parent.right
            anchors.rightMargin: root.inset
            anchors.verticalCenter: parent.verticalCenter
            columns: root.compact ? 2 : 1
            columnSpacing: root.inset
            rowSpacing: Math.round(2 * root.theme.scale)

            Text {
              Layout.fillWidth: true
              text: "Project notes.md"
              color: root.theme.text
              font.family: root.theme.fontFamily
              font.pixelSize: root.theme.fontBody
              elide: Text.ElideRight
            }
            Text {
              Layout.fillWidth: true
              text: "Documents/Projects"
              color: root.theme.textMuted
              font.family: root.theme.fontFamily
              font.pixelSize: root.theme.fontSmall
              elide: Text.ElideRight
            }
          }
        }

        Rectangle {
          objectName: "settingsLayoutPreviewDetails"
          visible: root.showPreview && sample.width >= Math.round(348 * root.theme.scale)
          Layout.preferredWidth: Math.round(sample.width * 0.34)
          Layout.preferredHeight: fileResult.implicitHeight
          color: root.theme.surface
          radius: root.theme.radiusSmall

          Text {
            anchors.fill: parent
            anchors.margins: root.inset
            text: "Markdown document"
            color: root.theme.textMuted
            font.family: root.theme.fontFamily
            font.pixelSize: root.theme.fontSmall
            elide: Text.ElideRight
            verticalAlignment: Text.AlignVCenter
          }
        }
      }
    }
  }
}
