#!/usr/bin/env bash
set -euo pipefail

# Installs or updates CommandDeck from a prebuilt bundle. It needs no .NET SDK.
# In a release folder this script sits beside bin/, qml/, packaging/, and
# scripts/. In the source tree it lives in scripts/, and reinstall-all.sh
# passes the fresh publish output with --from. A symlink to it also works.
script_path=$(readlink -f -- "${BASH_SOURCE[0]}")
script_dir=$(dirname -- "$script_path")
if [[ -f $script_dir/packaging/systemd/commanddeck.service ]]; then
  root_dir=$script_dir
else
  root_dir=$(dirname -- "$script_dir")
fi

usage() {
  cat <<'EOF'
Usage: ./install.sh [--from=DIR] [--hotkey=CHORD] [--reset-config] [--dry-run]

Installs or updates CommandDeck for the current user from a prebuilt bundle.
It needs no .NET SDK. Run it inside your Omarchy (Hyprland) session, as your
own user.

It installs the binary in ~/.local/share/commanddeck with a link in
~/.local/bin, the systemd user service, and the Omarchy plugin. Then it indexes
your folders, binds the hotkey, reloads Hyprland, and restarts the Omarchy
shell. It keeps your configuration, and it backs up the plugin and binding
files that it changes under ~/.local/state/commanddeck. If a step fails or you
interrupt it, it restores the previous installation.

  --from=DIR       Install this publish output. Default: the release's bin/.
  --hotkey=CHORD   Bind this chord. Default: "CTRL + SPACE".
  --reset-config   Move the CommandDeck configuration folder to
                   ~/.local/state/commanddeck/config-backups/, then create the
                   default configuration.
  --dry-run        Check the prerequisites and the bundle, show what would be
                   installed, and change nothing.
  -h, --help       Show this message.
EOF
}

binary_dir=$root_dir/bin
hotkey="CTRL + SPACE"
reset_config=0
dry_run=0
for argument in "$@"; do
  case "$argument" in
    --from=*)
      binary_dir=${argument#--from=}
      ;;
    --hotkey=*)
      hotkey=${argument#--hotkey=}
      ;;
    --reset-config)
      reset_config=1
      ;;
    --dry-run)
      dry_run=1
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $argument" >&2
      echo "Run $0 --help for usage." >&2
      exit 2
      ;;
  esac
done

if (( EUID == 0 )); then
  echo "Do not run the installer as root or with sudo. Run it as your desktop user." >&2
  exit 1
fi

config_home=${XDG_CONFIG_HOME:-$HOME/.config}
data_home=${XDG_DATA_HOME:-$HOME/.local/share}
state_dir=${XDG_STATE_HOME:-$HOME/.local/state}/commanddeck
program_dir="$data_home/commanddeck"
installed_binary="$program_dir/commanddeck"
config_dir="$config_home/commanddeck"
config_file="$config_dir/commanddeck.jsonc"
service_file="$config_home/systemd/user/commanddeck.service"
plugin_dir="$config_home/omarchy/plugins/local.commanddeck"
hypr_dir="$config_home/hypr"
qml_dir="$root_dir/qml"
unit_source="$root_dir/packaging/systemd/commanddeck.service"

step() {
  printf '\n==> %s\n' "$1"
}

# A restarted shell answers a ping before it has loaded every plugin, and until
# then its IPC calls can outlast omarchy-shell's 2-second default. Give each
# call longer and retry it, so a slow start does not fail the install.
shell_ipc_timeout=10s
shell_ready_seconds=60

shell_call() {
  local deadline=$((SECONDS + shell_ready_seconds))
  local output
  until output=$(OMARCHY_SHELL_IPC_TIMEOUT=$shell_ipc_timeout "$@" 2>&1); do
    if (( SECONDS >= deadline )); then
      printf '%s\n' "$output" >&2
      echo "Omarchy Shell did not complete '$*' within ${shell_ready_seconds}s." >&2
      return 1
    fi
    sleep 1
  done

  if [[ -n $output ]]; then
    printf '%s\n' "$output"
  fi
}

# The plugin registers commanddeck:toggle when the shell loads it. Until then
# the hotkey has nothing to reach, so wait for the registration like shell_call.
wait_for_global_shortcut() {
  local deadline=$((SECONDS + shell_ready_seconds))
  until hyprctl -j globalshortcuts | jq -e 'any(.[]; .name == "commanddeck:toggle")' >/dev/null; do
    if (( SECONDS >= deadline )); then
      echo "The CommandDeck plugin did not register commanddeck:toggle within ${shell_ready_seconds}s." >&2
      return 1
    fi
    sleep 1
  done
}

has_command() {
  command -v "$1" >/dev/null 2>&1
}

# Native AOT .NET loads ICU when it runs. Arch keeps it in /usr/lib; other
# distributions use lib64 or a multiarch folder.
has_library() {
  local library
  for library in "/usr/lib/$1.so."* "/usr/lib64/$1.so."* "/usr/lib/"*-linux-gnu"/$1.so."*; do
    if [[ -e $library ]]; then
      return 0
    fi
  done
  return 1
}

# Rollback. Before the first change, every path that the install changes is
# copied to rollback_dir. For a symlink to a file, the target file is copied
# too, because writes such as `hotkey install` go through the link. A failure
# or a signal after the first change puts all of them back.
rollback_dir=""
saved_paths=()
changes_started=0
had_previous=0
daemon_was_active=0
service_was_enabled=0
shell_restarted=0

save_for_rollback() {
  local path
  for path in "$@"; do
    if [[ -e $path || -L $path ]]; then
      mkdir -p -- "$rollback_dir/saved$(dirname -- "$path")"
      cp -a -- "$path" "$rollback_dir/saved$path"
    fi
    saved_paths+=("$path")
    if [[ -L $path && -f $path ]]; then
      save_for_rollback "$(readlink -f -- "$path")"
    fi
  done
}

# Puts back what the install changed, best effort, and returns the service to
# its earlier enabled and running state. Units are stopped and disabled before
# their files go, and enabled and started after the files are back.
roll_back() {
  local path
  set +e
  echo "The install failed or was interrupted; undoing its changes." >&2
  if (( ! daemon_was_active )); then
    systemctl --user stop commanddeck.service >/dev/null 2>&1
  fi
  if (( ! service_was_enabled )); then
    systemctl --user disable commanddeck.service >/dev/null 2>&1
  fi
  for path in "${saved_paths[@]}"; do
    rm -rf -- "$path"
    if [[ -e $rollback_dir/saved$path || -L $rollback_dir/saved$path ]]; then
      mkdir -p -- "$(dirname -- "$path")"
      cp -a -- "$rollback_dir/saved$path" "$path"
    fi
  done
  systemctl --user daemon-reload >/dev/null 2>&1
  if (( service_was_enabled )); then
    systemctl --user enable commanddeck.service >/dev/null 2>&1
  fi
  if (( daemon_was_active )); then
    systemctl --user restart commanddeck.service >/dev/null 2>&1
  fi
  hyprctl reload >/dev/null 2>&1
  if (( shell_restarted )); then
    omarchy restart shell >/dev/null 2>&1
  fi
  if (( had_previous )); then
    echo "Restored the previous CommandDeck installation." >&2
  else
    echo "Removed the partial installation. Fix the error above, then run the installer again." >&2
  fi
}

# A signal handler runs `exit` with a non-zero status, so finish rolls back.
# During the rollback, signals run a no-op handler and cannot cut it short.
finish() {
  local exit_status=$?
  trap - EXIT
  trap ':' INT TERM HUP
  if (( exit_status != 0 && changes_started )); then
    roll_back
  fi
  if [[ -n $rollback_dir ]]; then
    rm -rf -- "$rollback_dir"
  fi
  exit "$exit_status"
}

# Check everything before the first change, so a missing tool or a wrong
# bundle stops the install while the old installation still runs.
problems=()
missing_commands=0
for required in systemctl omarchy omarchy-shell hyprctl jq cmp diff; do
  if ! has_command "$required"; then
    problems+=("Required command not found: $required")
    missing_commands=1
  fi
done
if ! has_library libicuuc || ! has_library libicui18n; then
  problems+=("The ICU libraries (libicuuc, libicui18n) are not installed. On Arch: sudo pacman -S icu")
fi
if has_command hyprctl && ! hyprctl -j version >/dev/null 2>&1; then
  problems+=("hyprctl cannot reach Hyprland. Run this inside your Hyprland session.")
fi
if has_command systemctl && ! systemctl --user show-environment >/dev/null 2>&1; then
  problems+=("The systemd user manager is not reachable. Run this as your desktop user, without sudo.")
fi

version=""
if [[ ! -x $binary_dir/commanddeck ]]; then
  problems+=("No commanddeck binary in $binary_dir.")
elif ! version=$("$binary_dir/commanddeck" version 2>&1); then
  problems+=("$binary_dir/commanddeck does not run on this computer: ${version%%$'\n'*}")
  problems+=("Check that the build is for this CPU ($(uname -m)), that glibc is at least as new as on the build machine, and that icu is installed.")
elif ! hotkey_check=$("$binary_dir/commanddeck" hotkey check --chord "$hotkey" 2>&1); then
  problems+=("Cannot bind the hotkey: $hotkey_check")
fi
if [[ ! -f $binary_dir/picker/shell.qml ]]; then
  problems+=("No picker in $binary_dir/picker.")
fi
if [[ ! -f $qml_dir/manifest.json ]]; then
  problems+=("No Omarchy plugin in $qml_dir.")
elif (( ! missing_commands )) && ! omarchy plugin validate "$qml_dir" >/dev/null 2>&1; then
  problems+=("Omarchy rejected the plugin. Run: omarchy plugin validate $qml_dir")
fi
if [[ ! -f $unit_source ]]; then
  problems+=("No systemd unit at $unit_source.")
fi

if (( ${#problems[@]} > 0 )); then
  printf 'error: %s\n' "${problems[@]}" >&2
  echo "Nothing was changed." >&2
  exit 1
fi

if (( reset_config )); then
  config_plan="move $config_dir to $state_dir/config-backups/, then create the default configuration"
elif [[ -f $config_file ]]; then
  config_plan="keep $config_file"
else
  config_plan="create $config_file"
fi
printf 'Install %s\n' "${version%%$'\n'*}"
printf '  from     %s\n' "$binary_dir"
printf '  binary   %s, linked from %s\n' "$installed_binary" "$HOME/.local/bin/commanddeck"
printf '  service  %s\n' "$service_file"
printf '  plugin   %s\n' "$plugin_dir"
printf '  hotkey   %s\n' "$hotkey"
printf '  config   %s\n' "$config_plan"

if (( dry_run )); then
  printf '\nDry run: all checks passed. Nothing was changed.\n'
  exit 0
fi

mkdir -p -- "$state_dir"
rollback_dir=$(mktemp -d "$state_dir/install-rollback.XXXXXX")
trap finish EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
if [[ -e $installed_binary ]]; then
  had_previous=1
fi
if systemctl --user is-active --quiet commanddeck.service; then
  daemon_was_active=1
fi
if systemctl --user is-enabled --quiet commanddeck.service; then
  service_was_enabled=1
fi
save_for_rollback "$program_dir" "$HOME/.local/bin/commanddeck" "$service_file" "$config_dir" \
  "$plugin_dir" "$config_home/omarchy/shell.json" \
  "$hypr_dir/commanddeck.lua" "$hypr_dir/hyprland.lua" "$hypr_dir/bindings.lua"
changes_started=1

step "Stop the existing daemon"
systemctl --user stop commanddeck.service 2>/dev/null || true

step "Install binary and systemd user service"
"$root_dir/scripts/install-local.sh" "$binary_dir"
install -Dm644 "$unit_source" "$service_file"
systemctl --user daemon-reload

if (( reset_config )); then
  step "Move the existing CommandDeck configuration to a backup"
  "$root_dir/scripts/reset-local-config.sh" --yes
fi

if [[ ! -f $config_file ]]; then
  step "Create default configuration"
  "$installed_binary" config init
else
  echo "Preserving existing configuration: $config_file"
fi

step "Install Omarchy shell plugin"
"$root_dir/scripts/install-omarchy-plugin.sh"

step "Verify the installed files match the bundle"
if ! cmp -s -- "$binary_dir/commanddeck" "$installed_binary"; then
  echo "The installed binary differs from $binary_dir/commanddeck." >&2
  exit 1
fi
if ! diff -qr -- "$qml_dir" "$plugin_dir"; then
  echo "The installed plugin differs from $qml_dir." >&2
  exit 1
fi

step "Rebuild search catalog"
reindex_degraded=0
if ! "$installed_binary" reindex --quiet; then
  reindex_degraded=1
  echo "Reindex reported unavailable or failed inputs; continuing with its usable catalog." >&2
fi

step "Install the $hotkey hotkey"
"$installed_binary" hotkey install --chord "$hotkey"

step "Import desktop session environment and start daemon"
session_variables=()
for variable_name in \
  HYPRLAND_INSTANCE_SIGNATURE \
  WAYLAND_DISPLAY \
  XDG_CURRENT_DESKTOP \
  XDG_SESSION_TYPE \
  PATH; do
  if [[ -v $variable_name ]]; then
    session_variables+=("$variable_name")
  fi
done
if (( ${#session_variables[@]} > 0 )); then
  systemctl --user import-environment "${session_variables[@]}"
fi
systemctl --user enable commanddeck.service >/dev/null
systemctl --user restart commanddeck.service

step "Reload Hyprland and Omarchy Shell"
hyprctl reload >/dev/null
config_errors=$(hyprctl configerrors)
if [[ -n $config_errors ]]; then
  echo "$config_errors" >&2
  exit 1
fi
shell_restarted=1
omarchy restart shell
shell_call omarchy-shell shell rescanPlugins >/dev/null
shell_call omarchy plugin enable local.commanddeck >/dev/null

step "Verify live installation"
systemctl --user is-active --quiet commanddeck.service
shell_call omarchy-shell shell ping >/dev/null
shell_call omarchy plugin list --json | jq -e '
  any(.[]; .id == "local.commanddeck" and .enabled == true)
' >/dev/null
wait_for_global_shortcut
"$installed_binary" doctor
"$installed_binary" search "= 2 + 2" --json | jq -e '
  any(.[]; .kind == "calculator" and .title == "4")
' >/dev/null

changes_started=0

if (( reindex_degraded )); then
  echo "Warning: reindex reported unavailable or failed inputs; inspect commanddeck index status." >&2
fi
if [[ :$PATH: != *":$HOME/.local/bin:"* ]]; then
  echo "Note: add ~/.local/bin to your PATH to run commanddeck from a terminal."
fi

printf '\nCommandDeck is installed. Press %s to open it.\n' "$hotkey"
