import "../../common"
import QtQuick
import "../../common" as CommandDeck

CommandDeck.HelpSheet {
  width: 900
  height: 620
  visible: false
  opened: true
  filterDescriptors: [
    { id: "applications", label: "Applications", token: "app:", aliases: [] },
    { id: "files", label: "Files", token: "file:", aliases: ["text:", "files:"] },
    { id: "workspaces", label: "Workspaces", token: "workspace:", aliases: ["ws:", "workspaces:"] }
  ]
}
