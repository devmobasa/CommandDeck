# CommandDeck overview

CommandDeck is a command palette and launcher for Omarchy and Hyprland. You
press **Ctrl+Space**, type a few letters, and act on a result. This page
explains the parts of CommandDeck in plain words. Then it lists every guide.

## The parts

### Launcher

The launcher is the window that you see. It is a Quickshell (QML) plugin for
the Omarchy shell. It shows the search field, the results, a preview pane, and
the action panel. The launcher does not search or run anything itself. It sends
each request to the daemon.

### Daemon

The daemon is a background program, `commanddeck`. It runs as a systemd user
service. It is written in C# and compiled with Native AOT. The daemon searches,
keeps the index, checks each action, and runs it. A small bridge connects the
launcher to the daemon through a Unix socket.

### Search

The daemon keeps an SQLite database with a full-text index (FTS5). The index
holds apps, file names, the text of selected files, config keys, and Omarchy
menu actions. Other results, such as open windows, processes, and Herdr tabs,
come live at search time.

Results appear in groups, one group for each source. **Top Matches** shows the
best one to three results above the groups. The order learns from the results
that you open. With nothing typed, **Home** shows bookmarks, recent and frequent
items, open windows, and Omarchy controls.

### Sources

A source is one kind of result. CommandDeck has sources for apps, files, config
keys, windows and workspaces, Omarchy menus, desktop controls, processes, user
services, projects, snippets, script commands, clipboard history, Herdr, HEY
mail, quicklinks, web search, time zones, emoji, the calculator, and AI.

A prefix limits a search to one source. For example, `app: firefox` finds only
apps. Type `?` to see every prefix with an example. In Settings, you can turn
each source on or off and choose the sources that appear on Home.

### Actions

Each result has actions, such as open, copy, reveal, focus, or run. **Enter**
runs the first action, and **Ctrl+K** shows all of them. The daemon makes every
action itself. The launcher can only ask for an action on a result that the
daemon sent, and it never gets a shell command to run. Some actions, such as
**Force kill**, ask you to confirm first.

### Extensions

Extensions are experimental. An extension is a separate C# program that adds
commands, views, and search results. Each extension runs in its own sandbox and
gets only the permissions that you grant. Extensions are off until you turn
them on.

### Settings

Open Settings with the gear button or **Ctrl+,**. Settings has six pages:
Search, AI, Editors, Filters, Shortcuts, and Customization. You can search the
cards on these pages to find a control. The configuration file,
`~/.config/commanddeck/commanddeck.jsonc`, holds the other options.

### AI

AI is optional and off by default. A direct provider shows you the exact request
before it sends anything. The embedded assistant uses trust levels that the
daemon enforces. A prompt cannot give the assistant more access than the level
that you selected.

### Privacy

CommandDeck has no telemetry. AI, clipboard history, search history, and
extensions are off until you turn them on. Usage learning is on and stays on
your computer. You can turn it off or reset it.

## Guides

### Use CommandDeck

- [Gallery](gallery.md): screenshots of each feature.
- [Install](install.md): install a release or build from source, update, uninstall, fix problems, and roll back.
- [Launcher workflows](launcher-workflows.md): use projects, snippets, script
  commands, clipboard history, folders, desktop controls, and processes.
- [Search and ranking](search-and-ranking.md): the prefixes, the calculator, and
  how CommandDeck orders results.
- [Configuration](configuration.md): every option in the configuration file.
- [AI](ai.md): set up AI providers and the embedded assistant.
- [HEY mail](hey-mail.md): search HEY mail through the HEY CLI.

### Security and privacy

- [Security policy](../SECURITY.md): report a security problem privately.
- [Security and privacy](security.md): what CommandDeck stores, sends, and runs.
- [Permissions](permissions.md): each capability, with its default and how to
  turn it off.
- [Threat model](threat-model.md): the trust boundaries and the risks that remain.

### Extensions (experimental)

- [Extensions](extensions/README.md): start here.
- [Authoring](extensions/authoring.md): create, build, package, and test an extension.
- [Packages](extensions/packages.md): install, grant, update, and remove extension packages.
- [Connections](extensions/connections.md): give an extension files, HTTP access,
  and credentials.
- [Extension search](extensions/search.md): let an extension add search results.
- [Examples](../samples/extensions): sample extension projects.

### Contribute

- [Contributing](../CONTRIBUTING.md): build, test, code style, and pull requests.
- [Architecture](architecture.md): the projects, the processes, and where the code is.
- [Testing](testing.md): test suites, validation profiles, and performance budgets.
- [Screenshot capture](screenshots/README.md): how the gallery images are made.
- [Protocol](generated/current-protocol-contract.md): the generated contract
  between the launcher and the daemon.
- [QML frontend](../qml/README.md): how the launcher code is organized.
- [Standalone host](../qml/quickshell-standalone/README.md): run the launcher
  on Hyprland without the Omarchy plugin system (experimental).
