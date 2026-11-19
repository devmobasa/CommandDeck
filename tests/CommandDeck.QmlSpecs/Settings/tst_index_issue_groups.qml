import QtQuick
import QtTest
import "../../../qml/common/Personalization/IndexIssueGroups.js" as IssueGroups

// The inspector groups issues by cause: the reported case, 39 unreadable
// Podman layer folders, reads as one row labelled with the built-in folder.
TestCase {
  name: "CommandDeck_index_issue_groups"

  readonly property string layers: "/home/demo/.local/share/containers/storage/overlay"

  function podmanItems(count) {
    const items = []
    for (let index = 0; index < count; index++) {
      items.push({ id: "work:" + index, state: "failed", root: "/home/demo", reason: "directory-read-failed",
        relativePath: ".local/share/containers/storage/overlay/" + index.toString(16).padStart(8, "0") + "/diff/var/cache/apt/archives/partial" })
    }
    return items
  }

  function test_one_cause_is_one_group_rolled_up_to_its_built_in() {
    const builtIn = { id: "podman-layers", path: layers, description: "Podman image layers", enabled: false }
    const groups = IssueGroups.group(podmanItems(39), [builtIn])
    compare(groups.length, 1)
    compare(groups[0].items.length, 39)
    compare(groups[0].folder, layers)
    compare(groups[0].builtIn.id, "podman-layers")
    compare(groups[0].title, "Couldn't read this folder")
    compare(IssueGroups.displayFolder(groups[0], "/home/demo"), "~/.local/share/containers/storage/overlay")
  }

  function test_without_a_built_in_the_label_is_the_common_folder() {
    const groups = IssueGroups.group(podmanItems(3), [])
    compare(groups.length, 1)
    compare(groups[0].folder, layers)
    compare(groups[0].builtIn, null)
  }

  function test_causes_and_folders_stay_apart_and_failures_come_first() {
    const items = [
      { id: "a", state: "pending", root: "/r", relativePath: "docs/a.txt", reason: "filesystem-event-pending" },
      { id: "b", state: "failed", root: "/r", relativePath: "docs/b.pdf", reason: "extractor-timeout" },
      { id: "c", state: "failed", root: "/r", relativePath: "music/c.flac", reason: "extractor-timeout" },
      { id: "d", state: "failed", root: "/r", relativePath: "docs/locked", reason: "directory-read-failed" }
    ]
    const groups = IssueGroups.group(items, [])
    compare(groups.length, 4)
    compare(groups.filter(value => value.state === "failed").length, 3)
    compare(groups[groups.length - 1].state, "pending")
    const folder = groups.find(value => value.reason === "directory-read-failed")
    compare(folder.folder, "/r/docs/locked", "a folder item names itself")
    const file = groups.find(value => value.items[0].id === "b")
    compare(file.folder, "/r/docs", "a file item names its folder")
  }

  function test_private_and_clipped_paths_offer_no_target() {
    const redacted = { id: "x", state: "failed", root: "/r", relativePath: "[redacted]", reason: "unreadable" }
    const groups = IssueGroups.group([redacted], [])
    verify(groups[0].redacted)
    compare(IssueGroups.displayFolder(groups[0], "/home/demo"), "Private paths")
    compare(IssueGroups.copyablePath(redacted, 512), "")
    const clipped = { id: "y", state: "failed", root: "/r", relativePath: "a".repeat(512), reason: "unreadable" }
    compare(IssueGroups.copyablePath(clipped, 512), "")
    compare(IssueGroups.copyablePath({ id: "z", root: "/r", relativePath: "ok.txt" }, 512), "/r/ok.txt")
  }

  function test_rows_expand_only_open_groups() {
    const groups = IssueGroups.group(podmanItems(2), [])
    compare(IssueGroups.rows(groups, {}).length, 1)
    const open = {}
    open[groups[0].key] = true
    compare(IssueGroups.rows(groups, open).length, 3)
  }
}
