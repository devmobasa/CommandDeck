import ".."
import QtQuick
import "../Protocol.js" as Protocol

// Loads the index inspector's own page of work items, separate from the 50
// that runtime status carries, and asks the daemon to show an issue's folder.
QtObject {
  id: controller
  required property var nextRequestId
  property bool available: true
  // A page large enough to group, small enough to stay well under the reply limit.
  readonly property int pageSize: 128
  property var inspection: null
  property bool loading: false
  property string error: ""
  property string actionError: ""
  property int activeRequestId: 0
  property int revealRequestId: 0
  signal request(var message)

  function refresh() {
    if (!controller.available) {
      controller.error = "Index details are unavailable while the daemon is offline"
      return 0
    }
    controller.activeRequestId = controller.nextRequestId()
    controller.loading = true
    controller.request({ version: Protocol.Version, requestId: controller.activeRequestId, operation: Protocol.Operations.CatalogInspect,
      offset: 0, limit: controller.pageSize })
    return controller.activeRequestId
  }

  function reveal(workItemId) {
    controller.actionError = ""
    if (!controller.available || !workItemId) return 0
    controller.revealRequestId = controller.nextRequestId()
    controller.request({ version: Protocol.Version, requestId: controller.revealRequestId, operation: Protocol.Operations.IndexReveal,
      workItemId: workItemId })
    return controller.revealRequestId
  }

  onAvailableChanged: {
    if (controller.available) return
    controller.activeRequestId = 0
    controller.revealRequestId = 0
    controller.loading = false
  }

  function handleMessage(message) {
    if (controller.revealRequestId && message.requestId === controller.revealRequestId) {
      if (message.type === Protocol.Messages.Error) controller.actionError = message.error || "The folder could not be opened"
      if (message.type === Protocol.Messages.Error || message.type === Protocol.Messages.Done) controller.revealRequestId = 0
      return true
    }
    if (!controller.activeRequestId || message.requestId !== controller.activeRequestId) return false
    if (message.type === Protocol.Messages.Error) {
      controller.error = message.error || "Index details could not be loaded"
      controller.loading = false
      controller.activeRequestId = 0
      return true
    }
    if (message.type === Protocol.Messages.CatalogInspection) {
      controller.inspection = message.catalogInspection || null
      controller.error = ""
      return true
    }
    if (message.type === Protocol.Messages.Done) {
      controller.loading = false
      controller.activeRequestId = 0
      return true
    }
    return false
  }
}
