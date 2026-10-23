#!/usr/bin/env bash
set -euo pipefail

repo_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
source_dir="$repo_dir/qml"
plugin_dir=${XDG_CONFIG_HOME:-$HOME/.config}/omarchy/plugins/local.commanddeck
backup_root=${XDG_STATE_HOME:-$HOME/.local/state}/commanddeck/plugin-backups

command -v omarchy >/dev/null
omarchy plugin validate "$source_dir"

if [[ -e $plugin_dir ]] && diff -qr -- "$source_dir" "$plugin_dir" >/dev/null 2>&1; then
  echo "The plugin in $plugin_dir is already current; nothing to back up."
else
  if [[ -e $plugin_dir ]]; then
    # Nanoseconds keep two runs in the same second apart, and -T never moves
    # the plugin into an existing backup.
    mkdir -p "$backup_root"
    backup_dir="$backup_root/local.commanddeck.$(date +%Y%m%d-%H%M%S-%N)"
    mv -T -- "$plugin_dir" "$backup_dir"
    echo "Backed up the previous plugin to $backup_dir"
  fi

  mkdir -p "$plugin_dir"
  cp -a "$source_dir/." "$plugin_dir/"
  omarchy plugin validate "$plugin_dir"
  echo "Installed plugin in $plugin_dir"
fi
echo "Enable with: omarchy plugin enable local.commanddeck"
echo "Bind the hotkey with: commanddeck hotkey install && hyprctl reload"
