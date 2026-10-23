import "../../common"
import QtQuick
import Quickshell
import Quickshell.Wayland

Item {
  id: fixture
  required property var deck
  readonly property alias reservedBar: reservedBar
  readonly property alias workArea: workArea

  // A real exclusive zone, plus a non-exclusive witness that confirms the
  // compositor applied it before we inspect the launcher's full-screen scrim.
  // qmllint disable uncreatable-type
  PanelWindow {
    id: reservedBar
    visible: false
    screen: fixture.deck.desktopIntegration.screens[0] || null
    anchors { top: true; left: true; right: true }
    implicitHeight: 48
    exclusiveZone: 48
    color: "transparent"
    WlrLayershell.layer: WlrLayer.Top
    WlrLayershell.keyboardFocus: WlrKeyboardFocus.None
  }
  // qmllint disable uncreatable-type
  PanelWindow {
    id: workArea
    visible: false
    screen: reservedBar.screen
    anchors { top: true; bottom: true; left: true; right: true }
    exclusionMode: ExclusionMode.Normal
    color: "transparent"
    WlrLayershell.layer: WlrLayer.Overlay
    WlrLayershell.keyboardFocus: WlrKeyboardFocus.None
  }

}
