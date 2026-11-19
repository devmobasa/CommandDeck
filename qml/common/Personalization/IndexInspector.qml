import ".."
pragma ComponentBehavior: Bound
import QtQuick
import "../FocusTrap.js" as FocusTrap
import "IndexIssueGroups.js" as IssueGroups
import "../Protocol.js" as Protocol
import QtQuick.Controls
import QtQuick.Layouts

// The index inspector fills the card at the Settings size: counts, service
// health, a grouped list of the index issues on the left, and what the
// selected issue means and what to do about it on the right.
FocusScope {
  id: root

  required property var theme
  property bool opened: false
  property var inspection: null
  property string healthDetails: ""
  property string healthSummary: ""
  // Configured folders that don't exist, as { id, path }; each gets a Stop indexing button.
  property var missingRoots: []
  property bool foldersRelevant: true
  // Built-in and user excluded folders, for group labels and the built-in switch.
  property var exclusions: []
  property string homeDirectory: ""
  property bool busy: false
  property bool loading: false
  property string statusText: ""
  property string errorText: ""
  readonly property string heading: "Index inspector" + (healthSummary.length > 0 ? " · " + healthSummary : "")
  readonly property var workItems: inspection ? inspection.workItems || [] : []
  readonly property var builtIns: root.exclusions.filter(value => value.source === Protocol.IndexExclusionSources.BuiltIn)
  readonly property var groups: IssueGroups.group(root.workItems, root.builtIns)
  readonly property bool countsExact: !inspection || inspection.workItemCountsExact !== false
  readonly property bool partial: !!(inspection && inspection.workItemsTruncated)
  readonly property string countPrefix: countsExact ? "" : "at least "
  readonly property bool focusTrapConfigured: true
  readonly property bool showsStopIndexing: root.healthDetails.length > 0 && root.missingRoots.length > 0
  readonly property bool showsFolders: root.foldersRelevant && root.healthDetails.length > 0
  readonly property alias closeControl: closeButton
  readonly property alias healthControl: healthText
  readonly property alias stopIndexingControl: stopIndexingButton
  readonly property alias foldersControl: foldersButton
  readonly property alias excludedFoldersControl: excludedFoldersButton
  readonly property alias listControl: issueList
  readonly property alias detailControl: detail
  readonly property var focusTargets: detail.sheetOpen ? detail.focusTargets
    : [closeButton, healthText, stopIndexingButton, foldersButton, issueList, ...detail.focusTargets, excludedFoldersButton]

  signal dismissed()
  signal indexedFoldersRequested()
  signal excludedFoldersRequested()
  signal stopIndexingRequested(string rootId)
  signal excludeRequested(string path)
  signal builtInExclusionRequested(string exclusionId)
  signal revealRequested(string workItemId)

  function close() {
    root.dismissed()
  }

  function copy(path) {
    if (!path) return
    clipboardHelper.text = path
    clipboardHelper.selectAll()
    clipboardHelper.copy()
    clipboardHelper.text = ""
    root.statusText = "Copied the path."
  }

  onOpenedChanged: {
    if (!opened) return
    root.statusText = ""
    issueList.expanded = ({})
    issueList.selectedKey = ""
    issueList.ensureSelection()
    Qt.callLater(function() {
      if (root.groups.length > 0) issueList.forceActiveFocus()
      else closeButton.forceActiveFocus()
    })
  }

  visible: opened
  enabled: opened
  Accessible.role: Accessible.Dialog
  Accessible.name: "Index inspector"
  Accessible.description: inspection
    ? (healthSummary.length > 0 ? healthSummary + ". Files: " : "Files: ")
      + countPrefix + inspection.failedItemCount + " failed, "
      + countPrefix + inspection.staleItemCount + " stale, "
      + countPrefix + inspection.pendingItemCount + " pending"
    : "Index state unavailable"

  Keys.onPressed: event => {
    if (event.key === Qt.Key_Escape) {
      if (detail.sheetOpen) detail.closeSheet()
      else root.close()
      event.accepted = true
    } else if (event.key === Qt.Key_Tab || event.key === Qt.Key_Backtab) {
      FocusTrap.cycleTargets(root.focusTargets, event.key === Qt.Key_Tab && !(event.modifiers & Qt.ShiftModifier))
      event.accepted = true
    }
  }

  TextEdit {
    id: clipboardHelper
    visible: false
    width: 1
  }

  Rectangle {
    anchors.fill: parent
    radius: root.theme.radius
    color: root.theme.popupBackground
    border.width: 1
    border.color: root.theme.border

    MouseArea {
      anchors.fill: parent
      acceptedButtons: Qt.AllButtons
    }
  }

  ColumnLayout {
    anchors.fill: parent
    anchors.margins: Math.round(18 * root.theme.scale)
    spacing: Math.round(12 * root.theme.scale)

    RowLayout {
      Layout.fillWidth: true
      spacing: Math.round(12 * root.theme.scale)

      ColumnLayout {
        Layout.fillWidth: true
        spacing: Math.round(2 * root.theme.scale)
        Text {
          Layout.fillWidth: true
          textFormat: Text.PlainText
          text: root.heading
          color: root.theme.text
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontHeading
          font.weight: Font.DemiBold
          elide: Text.ElideRight
        }
        Text {
          Layout.fillWidth: true
          text: "Files the index could not read or update. Pick an issue to see what to do."
          color: root.theme.textMuted
          font.family: root.theme.fontFamily
          font.pixelSize: root.theme.fontSmall
          elide: Text.ElideRight
        }
      }
      DeckButton {
        id: closeButton
        theme: root.theme
        text: "Close"
        tabFence: false
        Accessible.name: "Close index inspector"
        onClicked: root.close()
      }
    }

    Flow {
      Layout.fillWidth: true
      spacing: Math.round(8 * root.theme.scale)
      visible: root.inspection !== null

      Repeater {
        model: root.inspection ? [
          { label: "Failed", value: root.countPrefix + root.inspection.failedItemCount, urgent: root.inspection.failedItemCount > 0 },
          { label: "Stale", value: root.countPrefix + root.inspection.staleItemCount, urgent: false },
          { label: "Pending", value: root.countPrefix + root.inspection.pendingItemCount, urgent: false },
          { label: "Indexed", value: String(root.inspection.stats ? root.inspection.stats.itemCount : 0), urgent: false },
          { label: "Excluded", value: "about " + root.inspection.totalExclusionDiagnosticCount, urgent: false }
        ] : []
        delegate: Rectangle {
          required property var modelData
          implicitWidth: chip.implicitWidth + Math.round(18 * root.theme.scale)
          implicitHeight: chip.implicitHeight + Math.round(8 * root.theme.scale)
          radius: implicitHeight / 2
          color: modelData.urgent ? root.theme.withAlpha(root.theme.urgent, 0.16) : root.theme.surfaceStrong
          Text {
            id: chip
            anchors.centerIn: parent
            text: parent.modelData.label + "  " + parent.modelData.value
            color: parent.modelData.urgent ? root.theme.urgent : root.theme.textMuted
            font.family: root.theme.fontFamily
            font.pixelSize: root.theme.fontSmall
            font.weight: Font.DemiBold
          }
        }
      }
    }

    ScrollView {
      Layout.fillWidth: true
      Layout.preferredHeight: Math.min(Math.round(90 * root.theme.scale), healthText.implicitHeight + Math.round(8 * root.theme.scale))
      visible: root.healthDetails.length > 0
      clip: true
      contentWidth: availableWidth
      TextArea {
        id: healthText
        text: root.healthDetails
        textFormat: TextEdit.PlainText
        readOnly: true
        wrapMode: TextEdit.Wrap
        color: root.theme.textMuted
        font.family: root.theme.fontFamily
        font.pixelSize: root.theme.fontSmall
        background: null
        Accessible.name: "Index and service issue details"
      }
    }

    Flow {
      Layout.fillWidth: true
      spacing: Math.round(8 * root.theme.scale)
      // A hidden parent hides its children, so this reads the conditions, not their visibility.
      visible: root.showsStopIndexing || root.showsFolders

      DeckButton {
        id: stopIndexingButton
        visible: root.showsStopIndexing
        theme: root.theme
        tabFence: false
        text: root.missingRoots.length === 1
          ? "Stop indexing " + String(root.missingRoots[0].path).replace(/^\/home\/[^/]+(?=\/|$)/, "~")
          : "Stop indexing " + root.missingRoots.length + " missing folders"
        Accessible.name: root.missingRoots.length === 1
          ? "Stop indexing " + root.missingRoots[0].path : text
        Accessible.description: "Turns the missing folders off in indexed folders. Your configuration file is not changed."
        onClicked: root.missingRoots.forEach(missing => root.stopIndexingRequested(String(missing.id)))
      }
      DeckButton {
        id: foldersButton
        visible: root.showsFolders
        theme: root.theme
        tabFence: false
        text: "Manage indexed folders"
        Accessible.name: "Manage indexed folders in Settings"
        onClicked: root.indexedFoldersRequested()
      }
    }

    Text {
      Layout.fillWidth: true
      visible: text.length > 0
      text: root.errorText.length > 0 ? root.errorText : root.loading ? "Loading index details…" : root.statusText
      color: root.errorText.length > 0 ? root.theme.urgent : root.theme.textMuted
      font.family: root.theme.fontFamily
      font.pixelSize: root.theme.fontSmall
      wrapMode: Text.Wrap
      Accessible.role: root.errorText.length > 0 ? Accessible.AlertMessage : Accessible.StaticText
      Accessible.name: text
    }

    RowLayout {
      Layout.fillWidth: true
      Layout.fillHeight: true
      spacing: Math.round(16 * root.theme.scale)

      IndexInspectorList {
        id: issueList
        theme: root.theme
        groups: root.groups
        homeDirectory: root.homeDirectory
        visible: root.groups.length > 0
        Layout.fillHeight: true
        Layout.fillWidth: true
        Layout.preferredWidth: 11
        activeFocusOnTab: false
        onCopyRequested: root.copy(detail.targetPath)
      }

      IndexIssueDetail {
        id: detail
        theme: root.theme
        row: issueList.selectedRow
        homeDirectory: root.homeDirectory
        busy: root.busy
        visible: root.groups.length > 0
        Layout.fillHeight: true
        Layout.fillWidth: true
        Layout.preferredWidth: 9
        onExcludeRequested: path => root.excludeRequested(path)
        onBuiltInExclusionRequested: id => root.builtInExclusionRequested(id)
        onRevealRequested: id => root.revealRequested(id)
        onCopyRequested: path => root.copy(path)
      }

      Text {
        visible: root.groups.length === 0
        Layout.fillWidth: true
        Layout.fillHeight: true
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        text: root.inspection && root.inspection.totalWorkItemCount > 0
          ? "No work items in this loaded page. More may remain."
          : "No failed, stale, or pending index items."
        color: root.theme.textMuted
        font.family: root.theme.fontFamily
        font.pixelSize: root.theme.fontBody
        wrapMode: Text.Wrap
      }
    }

    RowLayout {
      Layout.fillWidth: true
      spacing: Math.round(12 * root.theme.scale)

      Text {
        Layout.fillWidth: true
        text: (root.partial ? root.workItems.length + " issues loaded · " : "")
          + "↑↓ Select     → ← Expand     Tab Actions     Ctrl+C Copy path     Esc Close"
        color: root.theme.textFaint
        font.family: root.theme.fontFamily
        font.pixelSize: root.theme.fontSmall
        elide: Text.ElideRight
      }
      DeckButton {
        id: excludedFoldersButton
        theme: root.theme
        compact: true
        tabFence: false
        text: "Manage excluded folders"
        Accessible.name: "Manage excluded folders in Settings"
        onClicked: root.excludedFoldersRequested()
      }
    }
  }
}
