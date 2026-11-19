import "../../common"
import QtQuick
import Quickshell
import "../../common" as CommandDeck

CommandDeck.IndexInspector {
  required property var host

  id: indexInspector
  parent: host.part_host
  anchors.fill: host.part_host
  opened: false
  theme: host.part_theme
  inspection: ({
    stats: { itemCount: 8 },
    totalExclusionDiagnosticCount: 2,
    failedItemCount: 1,
    staleItemCount: 1,
    pendingItemCount: 1,
    workItems: [
      { id: "work:a", state: "failed", root: "/safe", relativePath: "a.txt", reason: "read-failed" },
      { id: "work:b", state: "stale", root: "/safe", relativePath: "b.txt", reason: "version" },
      { id: "work:c", state: "pending", root: "/safe", relativePath: "c.txt", reason: "event" }
    ]
  })
}
