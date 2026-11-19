import ".."
import QtQuick
import "../Protocol.js" as Protocol
import "SettingsIndex.js" as SettingsIndex
import "SettingsSearchTarget.js" as SettingsSearchTarget

// Where Settings search and the launcher's Settings rows point. Search outlines
// one control while it lasts; a pause in typing spotlights it once, and Enter
// moves the keyboard to it. Typing opens the page that holds the best control;
// choosing a page afterwards looks only on that page. A launcher row opens a
// card's page and spotlights the card.
Item {
  id: root

  required property var panel
  required property var searchField
  required property var theme
  property var target: null
  property var spotlitTarget: null
  readonly property alias spotlight: dim
  readonly property var matches: {
    if (!root.panel.visible || root.searchField.text.trim().length === 0) return []
    const entries = []
    for (const page of root.panel.filteredPages) {
      for (const card of root.panel.pageCards[page.id] || []) {
        const entry = card ? card.settingEntry : null
        if (entry && card.matchesSearch && (root.panel.aiFeaturesEnabled || entry.section.length === 0))
          entries.push(entry)
      }
    }
    return entries
  }

  // An address opens the Quicklinks editor filled in, titled with its host.
  function revealSetting(settingId, url) {
    const entry = SettingsIndex.entry(settingId)
    if (!entry) return false
    root.searchField.text = ""
    root.panel.showPage(entry.page)
    if (entry.section.length > 0) root.panel.part_aiHub.section = entry.section
    const address = String(url || "")
    if (address.length > 0 && settingId === Protocol.SettingIds.Quicklinks)
      root.panel.part_quicklinksCard.startNew(address, address.replace(/^https?:\/\//i, "").split(/[\/?#]/)[0])
    Qt.callLater(() => dim.reveal(root.settingCard(settingId)))
    return true
  }

  function settingCard(settingId) {
    for (const page in root.panel.pageCards) {
      const card = root.panel.pageCards[page].find(value => value && value.settingId === settingId)
      if (card) return card
    }
    return null
  }

  function updateTarget() {
    root.target = SettingsSearchTarget.find(root.panel.pageCards[root.panel.currentPage] || [], root.searchField.text)
  }

  function followSearch() {
    const best = SettingsSearchTarget.best(root.panel.filteredPages, root.panel.pageCards, root.panel.currentPage, root.searchField.text)
    if (best.control && best.page !== root.panel.currentPage) root.panel.showPage(best.page)
    else root.updateTarget()
  }

  // The search text changed: lift any dim and look again once typing settles.
  function searchChanged() {
    dim.dismiss()
    if (root.searchField.text.trim().length === 0) root.spotlitTarget = null
    searchPause.restart()
    Qt.callLater(root.followSearch)
  }

  function revealTarget() {
    root.updateTarget()
    return root.focusTarget(root.target)
  }

  function revealMatch(entry) {
    root.panel.showPage(entry.page)
    if (entry.section.length > 0) root.panel.part_aiHub.section = entry.section
    Qt.callLater(function() {
      if (!root.panel.visible) return
      const card = root.settingCard(entry.id)
      const control = SettingsSearchTarget.find([card], root.searchField.text)
      root.target = control
      root.focusTarget(control || card)
    })
  }

  function focusTarget(control) {
    if (!control) return false
    const focus = SettingsSearchTarget.focusItem(control)
    if (focus) focus.forceActiveFocus()
    root.spotlitTarget = control
    dim.reveal(control)
    return true
  }

  Timer {
    id: searchPause
    interval: 600
    onTriggered: {
      if (!root.target || root.target === root.spotlitTarget || root.searchField.text.trim().length === 0) return
      root.spotlitTarget = root.target
      dim.reveal(root.target)
    }
  }

  // Lives inside the control it points at, so it follows scrolling and layout.
  Rectangle {
    objectName: "settingsSearchOutline"
    parent: root.target || root
    visible: !!root.target && root.searchField.text.trim().length > 0
    anchors.fill: parent
    anchors.margins: -Math.round(5 * root.theme.scale)
    z: 5
    enabled: false
    color: "transparent"
    radius: root.theme.radius
    border.width: Math.max(1, Math.round(2 * root.theme.scale))
    border.color: root.theme.accent
  }

  SettingsSpotlight {
    id: dim
    anchors.fill: parent
    theme: root.theme
  }
}
