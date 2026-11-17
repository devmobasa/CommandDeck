import QtQuick
import QtTest
import QtQuick.Layouts
import "../../../qml/common" as CommandDeck

// A card draws a divider above each shown row after the first one. The
// dividers follow the rows once per batch of changes: a Repeater adds its rows
// one at a time, and rebuilding every divider for each row froze Settings for
// seconds with 200 desktop shortcuts.
Item {
  id: scene
  width: 500
  height: 400

  CommandDeck.Theme { id: theme; loadShellTokens: false }

  property int rowCount: 3

  CommandDeck.SettingsCard {
    id: card
    width: scene.width
    theme: theme
    title: "Rows"

    Rectangle { objectName: "hiddenRow"; visible: false; implicitHeight: 20; Layout.fillWidth: true }
    Repeater {
      model: scene.rowCount
      Rectangle {
        required property int index
        readonly property bool settingsRow: true
        objectName: "row" + index
        implicitHeight: 20
        Layout.fillWidth: true
      }
    }
  }

  ListModel {
    id: movingRows
    ListElement { name: "a" }
    ListElement { name: "b" }
    ListElement { name: "c" }
  }

  // Rows from a model that moves them in place, and one padded row that is not a settings row.
  CommandDeck.SettingsCard {
    id: movingCard
    y: 200
    width: scene.width
    theme: theme
    title: "Moving rows"

    Repeater {
      model: movingRows
      Rectangle {
        required property string name
        readonly property bool settingsRow: name !== "c"
        objectName: "moving-" + name
        implicitHeight: 20
        Layout.fillWidth: true
      }
    }
  }

  TestCase {
    name: "CommandDeck_settings_card_dividers"
    when: windowShown

    function shownDividers(target) {
      const surface = findChild(target || card, "settingsCardSurface")
      const dividers = []
      for (const child of surface.children) {
        if (child.objectName === "settingsCardDivider" && child.visible) dividers.push(child)
      }
      return dividers.sort((left, right) => left.y - right.y)
    }

    function init() {
      scene.rowCount = 3
      tryVerify(() => findChild(card, "settingsGroupBody").dividedRows.length === 5)
    }

    function test_a_divider_goes_above_each_shown_row_after_the_first() {
      tryVerify(() => shownDividers().length === 2)
      const dividers = shownDividers()
      compare(dividers[0].y, findChild(card, "row1").y)
      compare(dividers[1].y, findChild(card, "row2").y)
    }

    function test_rows_added_at_once_rebuild_the_dividers_once() {
      const body = findChild(card, "settingsGroupBody")
      let rebuilds = 0
      const count = () => rebuilds++
      body.dividedRowsChanged.connect(count)
      try {
        scene.rowCount = 200
        // The Repeater added 197 rows; the dividers wait for the batch to end.
        compare(rebuilds, 0)
        tryVerify(() => body.dividedRows.length === 202)
        wait(50)
        compare(rebuilds, 1)
        tryVerify(() => shownDividers().length === 199)
      } finally {
        body.dividedRowsChanged.disconnect(count)
      }
    }

    // Moving c to the top reorders the rows without changing the children
    // list; the divider moves from above c to above a.
    function test_a_row_moved_in_place_moves_its_divider() {
      const a = findChild(movingCard, "moving-a")
      const c = findChild(movingCard, "moving-c")
      tryVerify(() => shownDividers(movingCard).length === 2)
      verify(c.Layout.topMargin > 0)
      movingRows.move(2, 0, 1)
      tryVerify(() => c.y === c.Layout.topMargin && a.y > c.y)
      tryVerify(() => shownDividers(movingCard).length === 2)
      const tops = shownDividers(movingCard).map(divider => divider.y)
      compare(tops, [a.y, findChild(movingCard, "moving-b").y])
      movingRows.move(0, 2, 1)
      tryVerify(() => shownDividers(movingCard).map(divider => divider.y)[0] === findChild(movingCard, "moving-b").y)
    }

    function test_removed_rows_take_their_dividers_with_them() {
      scene.rowCount = 1
      tryVerify(() => findChild(card, "settingsGroupBody").dividedRows.length === 3)
      tryVerify(() => shownDividers().length === 0)
    }

    function test_hidden_and_empty_rows_get_no_divider() {
      const row1 = findChild(card, "row1")
      row1.visible = false
      tryVerify(() => shownDividers().length === 1)
      compare(shownDividers()[0].y, findChild(card, "row2").y)
      row1.visible = true
      const row0 = findChild(card, "row0")
      row0.Layout.maximumHeight = 0
      tryCompare(row0, "height", 0)
      // Row 1 is now the first row shown, so it has no divider above it.
      tryVerify(() => shownDividers().length === 1)
      compare(shownDividers()[0].y, findChild(card, "row2").y)
      row0.Layout.maximumHeight = Number.POSITIVE_INFINITY
      tryVerify(() => shownDividers().length === 2)
    }
  }
}
