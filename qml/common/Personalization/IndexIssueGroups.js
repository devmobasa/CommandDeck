.pragma library
.import "../Protocol.js" as Protocol
.import "../HomePaths.js" as HomePaths

// Index inspector work items, grouped so one cause shows as one row, with
// plain-language reasons. Group labels are for reading only: an action the
// user confirms is validated by the daemon like any typed path.

var RedactedPath = "[redacted]"
var BucketSegments = 3
// The daemon clips diagnostic paths at this length.
var ClipLength = 512

// Reason codes come from the daemon; the text says what happened and what helps.
var Reasons = Object.freeze({
  "directory-read-failed": { title: "Couldn't read this folder", folder: true,
    hint: "Permission is denied or the folder disappeared. Exclude it if it doesn't belong in search." },
  "metadata-read-failed": { title: "Couldn't read file details", hint: "The file may have changed or be unreadable." },
  "content-extraction-failed": { title: "Couldn't read the file's text", hint: "The file name is still searchable." },
  "extractor-timeout": { title: "Reading the file's text took too long", hint: "The file name is still searchable." },
  "too-large": { title: "Too large to read", hint: "The file name is still searchable." },
  "unreadable": { title: "Couldn't read this file", hint: "Check the file's permissions." },
  "unreadable-or-special-file": { title: "Not a regular file", hint: "Sockets, devices and unreadable files are skipped." },
  "invalid-path": { title: "The path is not valid", hint: "The name cannot be indexed." },
  "incremental-apply-failed": { title: "A change could not be applied", hint: "The next scan retries it." },
  "index-policy-reconciliation-failed": { title: "Index rules could not be rechecked", hint: "The next scan retries it." },
  "event-journal-write-failed": { title: "A change could not be recorded", hint: "The next scan retries it." },
  "rule-file-unreadable": { title: "An ignore file could not be read", folder: true, hint: "Its folder is skipped until it can be read." },
  "rule-file-too-large": { title: "An ignore file is too large", folder: true, hint: "Its folder is skipped until the file is smaller." },
  "filesystem-event-pending": { title: "Waiting to be indexed", hint: "It is indexed shortly." },
  "reindex-required": { title: "Waiting for a full reindex", hint: "It is indexed when the scan finishes." }
})

function reason(code) {
  return Reasons[code] || { title: String(code || "Unknown problem"), hint: "" }
}

function isRedacted(item) {
  return !item || item.root === RedactedPath || item.relativePath === RedactedPath
}

function fullPath(item) {
  const root = String(item.root || "").replace(/\/+$/, "")
  const relative = String(item.relativePath || "")
  if (relative.length === 0 || relative === ".") return root
  return root + "/" + relative
}

function parentOf(path) {
  const index = path.lastIndexOf("/")
  return index <= 0 ? "/" : path.slice(0, index)
}

function isSameOrDescendant(folder, path) {
  return path === folder || path.indexOf(folder === "/" ? "/" : folder + "/") === 0
}

// The folder an item names: a folder item itself, else its parent.
function folderOf(item) {
  const path = fullPath(item)
  return reason(item.reason).folder ? path : parentOf(path)
}

function commonFolder(paths) {
  if (paths.length === 0) return ""
  let common = paths[0].split("/")
  for (let index = 1; index < paths.length; index++) {
    const parts = paths[index].split("/")
    let shared = 0
    while (shared < common.length && shared < parts.length && common[shared] === parts[shared]) shared++
    common = common.slice(0, shared)
  }
  return common.join("/") || "/"
}

function bucketOf(item) {
  const segments = String(item.relativePath || "").split("/").filter(part => part.length > 0)
  const kept = segments.length > BucketSegments ? segments.slice(0, BucketSegments) : segments.slice(0, Math.max(0, segments.length - 1))
  return kept.join("/")
}

// Groups by root, state, reason and the first three path segments. A group
// inside a built-in excluded folder takes that folder as its label.
function group(items, builtIns) {
  const byKey = {}
  const order = []
  for (const item of items || []) {
    const redacted = isRedacted(item)
    const key = [item.root, item.state, item.reason, redacted ? RedactedPath : bucketOf(item)].join("\n")
    if (!byKey[key]) {
      byKey[key] = { key: key, root: item.root, state: item.state, reason: item.reason, redacted: redacted, items: [] }
      order.push(key)
    }
    byKey[key].items.push(item)
  }
  return order
    .map(key => describe(byKey[key], builtIns || []))
    .sort(compareGroups)
}

function describe(entry, builtIns) {
  const folders = entry.redacted ? [] : entry.items.map(folderOf)
  const folder = entry.items.length === 1 ? (folders[0] || "") : commonFolder(folders)
  const builtIn = entry.redacted ? null : builtIns.find(value => isSameOrDescendant(value.path, folder)) || null
  return Object.assign(entry, {
    folder: builtIn ? builtIn.path : folder,
    builtIn: builtIn,
    // Only complete paths may become a copied path or an exclusion prefill.
    complete: !entry.redacted && entry.items.every(item => copyablePath(item, ClipLength).length > 0),
    title: reason(entry.reason).title,
    hint: reason(entry.reason).hint
  })
}

function stateRank(state) {
  if (state === Protocol.IndexWorkStates.Failed) return 0
  if (state === Protocol.IndexWorkStates.Stale) return 1
  return 2
}

function compareGroups(left, right) {
  return stateRank(left.state) - stateRank(right.state) || right.items.length - left.items.length || (left.folder < right.folder ? -1 : 1)
}

// Rows for the list: each group, followed by its items when expanded.
function rows(groups, expanded) {
  const result = []
  for (const value of groups) {
    result.push({ kind: "group", group: value, key: value.key })
    if (expanded[value.key] !== true) continue
    for (const item of value.items) result.push({ kind: "item", group: value, item: item, key: value.key + "\n" + item.id })
  }
  return result
}

function displayFolder(group, home) {
  if (group.redacted) return "Private paths"
  return HomePaths.display(group.folder, home)
}

// A path is copied only when it is complete: not private and not clipped.
function copyablePath(item, clipLength) {
  if (isRedacted(item)) return ""
  if (String(item.root).length >= clipLength || String(item.relativePath).length >= clipLength) return ""
  return fullPath(item)
}
