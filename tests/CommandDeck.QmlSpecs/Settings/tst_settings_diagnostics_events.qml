import ".."
import QtQuick
import QtTest
import "../../../qml/common/Protocol.js" as Protocol
import "../../../qml/common/UiConstants.js" as Ui
import "../../../qml/common/Settings/DiagnosticsRows.js" as DiagnosticsRows

EventPathFixture {
  id: scene

  readonly property var status: ({
    version: "0.1.0-preview.1+abc",
    processId: 2081,
    startedAt: "2026-10-29T08:16:02.123+00:00",
    socketPath: "/run/user/1000/commanddeck.sock",
    catalog: { itemCount: 22089, fileCount: 3579, configCount: 18090, applicationCount: 87, databaseBytes: 69488640 },
    indexing: { watchedRootCount: 2, pendingEventCount: 0 },
    searchTimings: {
      searches: 258, medianMilliseconds: 23, p95Milliseconds: 91, maximumMilliseconds: 352,
      providers: [
        { id: "catalog", runs: 120, p95Milliseconds: 182, maximumMilliseconds: 350, timeouts: 3 },
        { id: "hyprland", runs: 120, p95Milliseconds: 6, maximumMilliseconds: 9, timeouts: 0 }
      ]
    },
    providers: [
      { id: "catalog", label: "Catalog", state: "available" },
      { id: "extractor-pdf", label: "PDF extraction", state: "unavailable", detail: "pdftotext is not installed" }
    ]
  })

  TextEdit {
    id: pasteTarget
    width: 10
    height: 10
    visible: false
  }

  SignalSpy { id: statusRequests; target: scene.part_settingsPanel; signalName: "runtimeStatusRequested" }

  EventPathTestCase {
    fixture: scene
    name: "CommandDeck_settings_diagnostics"

    function pastedText() {
      pasteTarget.text = ""
      pasteTarget.paste()
      return pasteTarget.text
    }

    function copyButton(parent, label) {
      if (parent.Accessible.name === "Copy " + label) return parent
      for (const child of parent.children || []) {
        const found = copyButton(child, label)
        if (found) return found
      }
      return null
    }

    function cleanup() {
      const panel = fixture.part_settingsPanel
      panel.runtimeStatus = null
      panel.part_settingsSearch.text = ""
      panel.showPage(Ui.SettingsPage.Search)
    }

    function test_rows_describe_the_daemon_and_its_slowest_providers() {
      const rows = DiagnosticsRows.rows(scene.status)
      const text = DiagnosticsRows.report(rows)
      verify(text.indexOf("Version: 0.1.0-preview.1+abc") >= 0, text)
      verify(text.indexOf("Started: 2026-10-29 08:16:02") >= 0, text)
      verify(text.indexOf("Catalog: 22089 items: 3579 files, 18090 configuration, 87 applications") >= 0, text)
      verify(text.indexOf("Database: 66.3 MiB") >= 0, text)
      verify(text.indexOf("Searches: Last 258: median 23 ms, p95 91 ms, slowest 352 ms") >= 0, text)
      verify(text.indexOf("Provider catalog: p95 182 ms, slowest 350 ms, 3 of 120 timed out") >= 0, text)
      verify(text.indexOf("Provider hyprland: p95 6 ms, slowest 9 ms") >= 0, text)
      verify(text.indexOf("PDF extraction: unavailable: pdftotext is not installed") >= 0, text)
      verify(text.indexOf("Catalog: available") < 0, "available providers are not repeated")
      compare(DiagnosticsRows.rows(null).length, 0)
    }

    function test_page_requests_status_and_copies_everything() {
      const panel = fixture.part_settingsPanel
      panel.visible = true
      waitForValue(panel.searchControl, "activeFocus", true)
      statusRequests.clear()
      panel.showPage(Ui.SettingsPage.Diagnostics)
      compare(statusRequests.count, 1, "opening the page reads the status fresh")
      panel.runtimeStatus = scene.status

      const copy = findChild(panel, "copyDebugInfo")
      tryVerify(() => copy.enabled, 2000)
      waitForFrame(copy)
      const page = panel.parts.diagnosticsScroll
      mouseWheel(page, page.width / 2, page.height / 2, 0, -1600)
      tryVerify(() => copy.mapToItem(panel, 0, 0).y + copy.height < panel.height - 40)
      tryCompare(page.contentItem, "moving", false)
      waitForFrame(copy)
      mouseClick(copy)
      tryCompare(copy, "text", "Copied")
      compare(pastedText(), [
        "Version: 0.1.0-preview.1+abc",
        "Process: 2081",
        "Started: 2026-10-29 08:16:02",
        "Socket: /run/user/1000/commanddeck.sock",
        "Catalog: 22089 items: 3579 files, 18090 configuration, 87 applications",
        "Database: 66.3 MiB",
        "Index: 2 watched folders, 0 pending changes",
        "Searches: Last 258: median 23 ms, p95 91 ms, slowest 352 ms",
        "Provider catalog: p95 182 ms, slowest 350 ms, 3 of 120 timed out",
        "Provider hyprland: p95 6 ms, slowest 9 ms",
        "PDF extraction: unavailable: pdftotext is not installed"
      ].join("\n"))
      tryCompare(copy, "text", "Copy debug info", 4000)
    }

    function test_individual_copy_pastes_the_value_without_its_label() {
      const panel = fixture.part_settingsPanel
      panel.visible = true
      waitForValue(panel.searchControl, "activeFocus", true)
      panel.showPage(Ui.SettingsPage.Diagnostics)
      panel.runtimeStatus = scene.status
      const copy = copyButton(panel.parts.diagnosticsScroll, "Version")
      verify(copy !== null)
      waitForFrame(copy)
      mouseClick(copy)
      tryCompare(copy, "text", "Copied")
      compare(pastedText(), "0.1.0-preview.1+abc")
    }

    function test_settings_search_reaches_the_diagnostics_page() {
      const panel = fixture.part_settingsPanel
      panel.visible = true
      waitForValue(panel.searchControl, "activeFocus", true)
      typeText("debug")
      compare(panel.currentPage, Ui.SettingsPage.Diagnostics)
      verify(panel.revealSetting(Protocol.SettingIds.Diagnostics))
      compare(panel.currentPage, Ui.SettingsPage.Diagnostics)
    }
  }
}
