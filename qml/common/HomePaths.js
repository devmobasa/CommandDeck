.pragma library

// Shows a path under the home folder as ~/…, leaving other paths unchanged.
function display(path, home) {
  const value = String(path || "")
  const base = String(home || "")
  if (base.length === 0) return value
  if (value === base) return "~"
  if (value.indexOf(base + "/") === 0) return "~" + value.slice(base.length)
  return value
}
