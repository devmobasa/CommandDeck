# Installation and desktop integration

CommandDeck is a preview for Omarchy and Hyprland. It needs an Omarchy version
with shell plugins (the `omarchy plugin` command), also called Omarchy Quattro.
You can install a prebuilt release for Linux x64 or ARM64, or build it from
source. The shared frontend also has an experimental standalone Quickshell
host. The plugin installer does not support an older Omarchy without shell
plugins.

## Install a release

A release is a ready-to-run program. It is compiled ahead of time, so you do
not need .NET.

### Release requirements

- Omarchy with shell plugins, Quickshell, and Hyprland. Run the installer in
  your graphical session, as your own user. It refuses to run as root.
- A systemd user manager, `jq`, and the ICU library. CommandDeck loads ICU when
  it runs. On Arch: `sudo pacman -S --needed jq icu wl-clipboard`.
- glibc 2.39 or later. The release program is built on Ubuntu 24.04, which has
  glibc 2.39. It runs only with that glibc or a newer one. Arch and Omarchy
  have a newer glibc.

`./install.sh --dry-run` checks these requirements, the release files, and the
hotkey. It changes nothing.

### Download, check, and install

On the [releases page](https://github.com/devmobasa/CommandDeck/releases),
download the archive for your CPU and its `.sha256` file. Use `linux-x64` for
x86-64 and `linux-arm64` for ARM64. Set `version` to the release that you
downloaded, then check, extract, and install it:

```bash
version=v0.1.0-preview.1
sha256sum -c commanddeck-$version-linux-x64.tar.gz.sha256
tar -xzf commanddeck-$version-linux-x64.tar.gz
cd commanddeck-$version-linux-x64
./install.sh --dry-run
./install.sh
```

`sha256sum -c` must print `OK`. If it does not, download the files again.

The installer stops the running daemon, then:

1. Installs the binary, its libraries, and the picker in
   `~/.local/share/commanddeck`, and links `~/.local/bin/commanddeck`.
2. Installs `commanddeck.service` in `~/.config/systemd/user`.
3. Creates the default configuration if you do not have one.
4. Installs the Omarchy plugin in `~/.config/omarchy/plugins/local.commanddeck`.
5. Checks that the installed binary and plugin match the release.
6. Indexes your folders and installs the Ctrl+Space hotkey.
7. Imports the session environment, then enables and starts the service.
8. Reloads Hyprland, restarts the Omarchy shell, and enables the plugin.
9. Checks the live installation with `commanddeck doctor` and a test search.

The Omarchy shell restart makes the bar and other shell parts reload for a
moment.

If a step fails, or you interrupt the installer (for example with Ctrl+C), it
undoes its changes. It puts back the previous program folder, the
`~/.local/bin/commanddeck` link, the service file, the configuration, the
plugin, and the hotkey files. If a Hyprland file is a symlink, it also puts back
the file that the link points to. Then it returns the service to its earlier
enabled and running state. A failed first install removes everything that it
added. The installer does not undo the catalog rebuild.

The installer keeps plugin, binding, and configuration backups under
`~/.local/state/commanddeck/`.

| Option | Effect |
|---|---|
| `--dry-run` | Check the requirements, the release files, and the hotkey. Show what would be installed, and change nothing. |
| `--hotkey="SUPER + SPACE"` | Bind a different chord. The default is `CTRL + SPACE`. |
| `--reset-config` | Move the CommandDeck configuration folder to `~/.local/state/commanddeck/config-backups/`, then create the default configuration. Catalog and other state stay. |
| `--from=DIR` | Install a different publish folder. The default is the release's `bin/` folder. |

Add `~/.local/bin` to PATH if it is absent.

### Update a release

Download and extract the new release, then run its `./install.sh`. The
installer replaces the binary, the plugin, and the service file. It keeps your
configuration and your state, rebuilds the catalog, and restarts the daemon and
the Omarchy shell. If the plugin changed, it backs up the previous plugin under
`~/.local/state/commanddeck/plugin-backups/`.

### Uninstall

Remove the hotkey, the plugin, the service, and the program files. The first
command needs `~/.local/bin` on your PATH. If `commanddeck` is not found, run
`~/.local/bin/commanddeck hotkey remove` instead.

```bash
commanddeck hotkey remove
hyprctl reload
omarchy plugin disable local.commanddeck
systemctl --user disable --now commanddeck.service
rm ~/.config/systemd/user/commanddeck.service
systemctl --user daemon-reload
rm -r ~/.config/omarchy/plugins/local.commanddeck
omarchy restart shell
rm ~/.local/bin/commanddeck
rm -r ~/.local/share/commanddeck
```

Your configuration and state stay. The state folder holds the catalog, history,
customizations, and the plugin, binding, and configuration backups. To also
delete them:

```bash
rm -r ~/.config/commanddeck ~/.local/state/commanddeck
```

## Build from source

### Prerequisites

- The .NET SDK pinned in [global.json](../global.json).
- A native build toolchain, Clang, zlib, SQLite, and ICU.
- Omarchy with shell plugins (`omarchy plugin`), Quickshell (`qs`), and Hyprland.
- A systemd user manager, `hyprctl`, `xdg-open`, `jq`, and `wl-copy`.
- ShellCheck for repository validation and Bubblewrap for isolated extensions/AI.
- Qt 6 `qmllint` and `qmltestrunner` for desktop validation.

On Arch:

```bash
sudo pacman -S --needed base-devel clang zlib sqlite icu shellcheck bubblewrap wl-clipboard jq
```

Optional PDF/document content extraction needs `poppler`/`pandoc` and explicit
configuration. The calculator needs no optional dependency. AI CLIs, Ollama, HEY,
and Herdr are separately installed optional integrations.

### Get the source

```bash
git clone https://github.com/devmobasa/CommandDeck.git
cd CommandDeck
```

### Install the pinned SDK

Use a .NET SDK installation that satisfies `global.json`. If your package manager
does not provide that preview version, the
[Microsoft installer](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)
can read the pin directly:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/commanddeck-dotnet-install.sh
bash /tmp/commanddeck-dotnet-install.sh --jsonfile global.json --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

Keep these environment settings in your shell configuration if you use this
SDK installation for later builds. The installer does not install native system
dependencies. With the SDK available, continue:

```bash
dotnet --version
dotnet restore CommandDeck.slnx --locked-mode
./scripts/build.sh
./scripts/test.sh
```

Inspect [the configuration example](../config/commanddeck.example.jsonc).
Default roots index home filenames/paths, including hidden files, and enable
content search in selected document/configuration roots. Credential exclusions
remain active. Narrow roots before the first reindex if you prefer.

### Complete user-local installation

Run inside your graphical session:

```bash
./scripts/reinstall-all.sh
```

The script validates the repository and publishes the Native AOT build to
`artifacts/publish/RID`. Then it runs `scripts/install.sh` on that folder, the
same installer that a release uses, so the install steps are those in
[Download, check, and install](#download-check-and-install). It finishes when
the plugin has registered the hotkey's global shortcut. Pass
`--hotkey="SUPER + SPACE"` (or another chord) to bind a different chord.
Existing configuration is preserved. Plugin, binding, and configuration
backups are kept under `~/.local/state/commanddeck/`.

Add `~/.local/bin` to PATH if it is absent. The installer requires a working
Omarchy session; it does not install a compositor or Omarchy itself.

`./scripts/reinstall-all.sh --reset-config` moves the CommandDeck configuration
directory to `~/.local/state/commanddeck/config-backups/` and regenerates
defaults. Catalog and other state stay. Other options after the RID go to
`scripts/install.sh` unchanged.

## Manual installation

Publish and install the binary/assets:

```bash
./scripts/publish-aot.sh linux-x64
./scripts/install-local.sh linux-x64
commanddeck config init
```

`install-local.sh` also accepts a publish folder, such as the `bin/` folder of
an extracted release.

Review `~/.config/commanddeck/commanddeck.jsonc`, then install the service:

```bash
mkdir -p ~/.config/systemd/user
install -m644 packaging/systemd/commanddeck.service ~/.config/systemd/user/commanddeck.service
systemctl --user daemon-reload
systemctl --user import-environment HYPRLAND_INSTANCE_SIGNATURE WAYLAND_DISPLAY XDG_CURRENT_DESKTOP XDG_SESSION_TYPE PATH
commanddeck reindex
systemctl --user enable --now commanddeck.service
```

Import only environment names present in your graphical session. If using custom
XDG locations, inspect the service's executable and writable paths and provide
a user-unit override.

Install/validate the plugin:

```bash
./scripts/install-omarchy-plugin.sh
omarchy-shell shell rescanPlugins
omarchy plugin enable local.commanddeck
omarchy restart shell
omarchy-shell shell toggle local.commanddeck '{}'
```

The manual plugin installer backs up an existing plugin but does not add a
binding or enable it. Add the hotkey with the installed binary:

```bash
commanddeck hotkey check --chord "SUPER + SPACE"    # validate only; changes nothing
commanddeck hotkey install                          # Ctrl+Space
commanddeck hotkey install --chord "SUPER + SPACE"  # or another chord
hyprctl reload
hyprctl configerrors
```

The command writes CommandDeck's own generated module,
`~/.config/hypr/commanddeck.lua`:

```lua
hl.unbind("CTRL + SPACE")
hl.bind("CTRL + SPACE", hl.dsp.global("commanddeck:toggle"), { description = "CommandDeck" })
```

It also adds one line to `~/.config/hypr/hyprland.lua`, directly after
`require("hypr.bindings")`, so the chord replaces a personal binding of the same
keys:

```lua
if package.searchpath("hypr.commanddeck", package.path) then require("hypr.commanddeck") end -- CommandDeck hotkey
```

The guard keeps the rest of `hyprland.lua` loading if the module is deleted.
Hyprland's global dispatcher sends `commanddeck:toggle` to the shortcut the
plugin registers inside `omarchy-shell`, so a key press starts no process. The
standalone host does not register the shortcut.

Rerunning the command changes nothing when the module and line are current; it
never adds a second require line. A changed chord rewrites only the module. An
edited file is first copied to `~/.local/state/commanddeck/binding-backups/`.
The command also removes the `-- BEGIN COMMANDDECK CTRL SPACE` block that earlier
installers appended to `~/.config/hypr/bindings.lua`. If `bindings.lua` already
binds the chord to CommandDeck itself, the command leaves your binding in place
and writes nothing; for the default chord, `commanddeck doctor` then accepts that
binding instead of the module. Validate Ctrl+Space before choosing a replacement for your
desktop's existing Super+Space binding.

## Check and troubleshoot

```bash
commanddeck version
commanddeck doctor
commanddeck status --json
systemctl --user status commanddeck.service
journalctl --user -u commanddeck.service -n 100 --no-pager
omarchy plugin validate ./qml
```

`commanddeck doctor` also reads the desktop wiring without changing it. It
checks that the plugin is installed and enabled in `~/.config/omarchy/shell.json`,
and that `commanddeck.lua` exists and `hyprland.lua` requires it once. In a
Hyprland session, it also checks that `hyprctl -j binds` shows the CommandDeck
chord and `hyprctl -j globalshortcuts` lists `commanddeck:toggle`. These checks
only warn or skip, so they never fail `doctor`. Each warning prints a `Fix:` line.

- No files: check roots/exclusions, run `commanddeck reindex`, and inspect
  `commanddeck index status`.
- No windows/workspaces: import the graphical session environment and restart
  `commanddeck.service`.
- No copy: check `command -v wl-copy` and the Wayland session.
- No overlay: inspect plugin validation and shell diagnostics; check that the
  daemon and `commanddeck` on the shell PATH match the installed frontend.
- Hotkey does nothing: run `commanddeck doctor` and follow its `Fix:` lines.
  Usually `commanddeck hotkey install` and `hyprctl reload` repair the binding.
  If `hyprctl globalshortcuts` does not list `commanddeck:toggle`, the shell has
  not loaded the plugin; run `omarchy restart shell`.
- AI failure: review [AI prerequisites](ai.md). Never print a key
  while checking systemd credential/environment setup.

Test open/type, arrows, Enter, actions, Escape, reopen, and immediate typing
after a mouse click. Multi-monitor focus, real clipboard ownership, compositor
previews, and recovery require a live Wayland session; offscreen tests alone
do not prove them.

## Disable or roll back

```bash
commanddeck hotkey remove
hyprctl reload
omarchy plugin disable local.commanddeck
omarchy restart shell
systemctl --user disable --now commanddeck.service
```

`commanddeck hotkey remove` deletes the require line and the generated
`commanddeck.lua`, after backing up `hyprland.lua`. It leaves a `commanddeck.lua`
that it did not generate. To restore a previous plugin or binding file instead,
copy it from the recorded backups, then reload Hyprland and rescan/restart the
shell. Do not erase user config, catalog, private stores, or credentials as part
of a binary/plugin rollback.

## Standalone host and ARM64

With the daemon running on Hyprland:

```bash
qs -p ./qml
# Another terminal:
qs ipc -p ./qml call commanddeck toggle '{}'
```

See [the standalone host guide](../qml/quickshell-standalone/README.md).
Other compositors require frontend adapters and backend providers.

The publish script accepts `linux-arm64`, but build/runtime smoke and installed
acceptance need a matching ARM64 host. Cross-publishing alone is not platform
support evidence. The release workflow builds `linux-arm64` on a native ARM64
runner, so its publish smoke runs there. Installed desktop acceptance on ARM64
is not yet recorded.
