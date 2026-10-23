.pragma library
.import "../Protocol.js" as Protocol

function defaultSavePath(directory, date) {
  if (directory.length === 0) return ""
  const two = value => (value < 10 ? "0" : "") + value
  const stamp = date.getFullYear() + "-" + two(date.getMonth() + 1) + "-" + two(date.getDate())
    + "-" + two(date.getHours()) + two(date.getMinutes()) + two(date.getSeconds())
  return directory + "/commanddeck-answer-" + stamp + Protocol.FileExtensions.Markdown
}
