.pragma library

function entry(index, revision, presentation) {
  return {id: "entry" + index, incarnation: "inc" + index, revision: revision || 1,
    summary: "clipboard " + index, copiedUnixSeconds: 1000, pinned: false, keywords: [],
    presentation: presentation || {kind: "text", imageWidth: null, imageHeight: null, destinationCount: 0, hasOpenDestination: false, reducedFidelity: false}}
}
function emptySlice() {
  return {text: "", sliceStartUtf16: 0, ranges: [], matchCount: 0, matchCountLimited: false, matchIndex: null,
    rangeCountLimited: false, hasTextBefore: false, hasTextAfter: false, sliceReason: null}
}
