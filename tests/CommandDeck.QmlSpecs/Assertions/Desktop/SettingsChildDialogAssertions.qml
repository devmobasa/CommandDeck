import "../../common"
import QtQuick
import "../../common/Protocol.js" as Protocol

QtObject {
  id: spec
  required property var host
  property var invoker: null

  function find(item, name) {
    if (item.Accessible.name === name && item.visible && item.enabled) return item
    for (const child of item.children || []) {
      const found = find(child, name)
      if (found) return found
    }
    return null
  }

  function steps() {
    const result = []
    for (const route of [
      {name: "Manage query history", page: "customization", kind: "history"},
      {name: "Export personalizations", page: "customization", kind: "export"},
      {name: "Reset personalizations to defaults", page: "customization", kind: "reset"},
      {name: "Delete saved search Work", page: "filters", kind: "saved"},
      {name: "Delete AI template Summary", page: "ai", kind: "template"}
    ]) {
      result.push(() => host.press(Qt.Key_Comma, Qt.ControlModifier))
      result.push(() => {
        const deck = host.deck()
        const panel = deck.testSettingsPanel
        host.expect(() => panel && panel.searchControl.activeFocus, "Settings opens for " + route.kind)
        deck.preferencesSnapshotLoading = false
        deck.aiFeaturesEnabled = true
        deck.aiTemplateBusy = false
        deck.part_personalization.abort("")
        deck.settingsSnapshot = {preferences: {sources: []}, customizations: [], shortcuts: []}
        deck.savedSearches = [{id: "work", name: "Work", expression: {includeFilterIds: [], excludeFilterIds: [], clauses: []}}]
        deck.aiTemplates = [{id: "summary", name: "Summary", userPromptTemplate: "Summarize {input}"}]
        panel.showPage(route.page)
        if (route.kind === "template") panel.part_aiHub.section = "quick"
        host.events().nextTurn()
        spec.invoker = find(panel, route.name)
        host.host.expect(!!spec.invoker, "the Settings invoker exists for " + route.kind)
        if (!spec.invoker) return
        host.host.expect(host.tabUntil(spec.invoker, 180), "keyboard navigation reaches " + route.kind)
        host.press(Qt.Key_Space)
        if (route.kind === "export") {
          const mutation = deck.part_personalization.active
          host.host.expect(!!mutation, "Export sends a customization request")
          if (mutation) deck.handleMessage({type: Protocol.Messages.ExportResult, requestId: mutation.request.requestId, export: {version: 1}})
        }
      })
      result.push(() => {
        const deck = host.deck()
        const opened = route.kind === "history" ? deck.queryHistoryOpen
          : route.kind === "export" ? deck.detailDialogRequested : deck.confirmationOpen
        host.host.expect(opened, "Space opens the Settings child for " + route.kind)
        host.press(Qt.Key_Escape)
      })
      result.push(() => {
        host.host.expect(spec.invoker && spec.invoker.activeFocus,
          "dismissing " + route.kind + " restores its Settings invoker before another key")
        host.deck().closeSettings()
      })
    }
    for (const replacement of ["closed", "confirmation"]) {
      result.push(() => host.press(Qt.Key_Comma, Qt.ControlModifier))
      result.push(() => {
        const deck = host.deck()
        const panel = deck.testSettingsPanel
        host.expect(() => panel.searchControl.activeFocus, "Settings opens for the " + replacement + " guard")
        deck.preferencesSnapshotLoading = false
        panel.showPage("customization")
        host.events().nextTurn()
        spec.invoker = find(panel, "Manage query history")
        host.host.expect(!!spec.invoker && host.tabUntil(spec.invoker, 180), "Tab reaches history for the " + replacement + " guard")
        host.press(Qt.Key_Space)
      })
      result.push(() => {
        const deck = host.deck()
        if (replacement === "closed") deck.closeSettings()
        else { deck.resetConfirmation = true; deck.confirmationOpen = true }
        deck.closeQueryHistory()
      })
      result.push(() => {
        host.host.expect(!spec.invoker.activeFocus, "history dismissal does not focus a " + replacement + " Settings surface")
        if (replacement === "confirmation") {
          const focused = host.focused()
          host.host.expect(focused && focused.text === "Cancel", "the replacement confirmation keeps focus")
          host.press(Qt.Key_Escape)
          host.deck().closeSettings()
        } else host.searchReady("closing Settings leaves launcher search focused")
      })
    }
    return result
  }
}
