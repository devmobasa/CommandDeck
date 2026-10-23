#!/usr/bin/env bash
set -euo pipefail

if [[ ${1:-} == "-h" || ${1:-} == "--help" ]]; then
  cat <<'EOF'
Usage: ./scripts/reset-local-config.sh --yes

Moves the current user's CommandDeck configuration directory to a backup, so
CommandDeck starts again from the defaults. It moves $XDG_CONFIG_HOME/commanddeck
(~/.config/commanddeck when XDG_CONFIG_HOME is unset) to
$XDG_STATE_HOME/commanddeck/config-backups/ (~/.local/state when XDG_STATE_HOME
is unset). It does not change the catalog or other state.
EOF
  exit 0
fi

if [[ $# -ne 1 || $1 != "--yes" ]]; then
  echo "Refusing to reset CommandDeck configuration without --yes." >&2
  echo "Run: ./scripts/reset-local-config.sh --yes" >&2
  exit 2
fi

config_home=${XDG_CONFIG_HOME:-${HOME:?HOME is required}/.config}
if [[ $config_home != /* ]]; then
  echo "Refusing relative configuration root: $config_home" >&2
  exit 2
fi
state_home=${XDG_STATE_HOME:-$HOME/.local/state}
if [[ $state_home != /* ]]; then
  echo "Refusing relative state root: $state_home" >&2
  exit 2
fi

resolved_config_home=$(realpath -m -- "$config_home")
config_dir="$resolved_config_home/commanddeck"
if [[ $resolved_config_home == "/" || $config_dir == "/commanddeck" || \
      $(basename -- "$config_dir") != "commanddeck" || \
      $(dirname -- "$config_dir") != "$resolved_config_home" ]]; then
  echo "Refusing unsafe CommandDeck configuration target: $config_dir" >&2
  exit 2
fi

if command -v mountpoint >/dev/null 2>&1 && mountpoint -q -- "$config_dir"; then
  echo "Refusing to move mounted configuration directory: $config_dir" >&2
  exit 2
fi

if [[ -e $config_dir || -L $config_dir ]]; then
  # Nanoseconds keep two resets in the same second apart, and -T never moves
  # the folder into an existing backup.
  backup_root="$state_home/commanddeck/config-backups"
  backup_dir="$backup_root/commanddeck.$(date +%Y%m%d-%H%M%S-%N)"
  mkdir -p -- "$backup_root"
  mv -T -- "$config_dir" "$backup_dir"
  echo "Moved CommandDeck configuration to $backup_dir"
else
  echo "CommandDeck configuration is already absent: $config_dir"
fi
