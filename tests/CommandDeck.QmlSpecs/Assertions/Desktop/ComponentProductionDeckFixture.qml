import "../../common"
import QtQuick
import Quickshell
import "../../common" as CommandDeck

CommandDeck.CommandDeck {
  visible: false
  testMode: true
  desktopIntegration: CommandDeck.DesktopIntegration {
    screens: Quickshell.screens.length > 0 ? [Quickshell.screens[0]] : []
    focusedMonitor: screens.length > 0 ? screens[0] : null
    capabilities: CommandDeck.DesktopCapabilities {
      windows: true
      windowPreviews: true
      workspaces: true
      workspaceLayouts: true
    }
    workspacePreviewBackend: QtObject {
      property var workspaces: []
      signal invalidated()
      function refresh() { }
    }
  }
  property var sentRequests: []
  property var routedInputs: []
  onProtocolRequestSent: request => sentRequests = sentRequests.concat([
    JSON.parse(JSON.stringify(request))
  ])
  onQueryInputRequested: value => routedInputs = routedInputs.concat([value])
}
