#!/usr/bin/env bash
set -euo pipefail

repo_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)

case "$(uname -m)" in
  x86_64) host_rid=linux-x64 ;;
  aarch64|arm64) host_rid=linux-arm64 ;;
  *)
    echo "Unsupported host architecture: $(uname -m)" >&2
    exit 2
    ;;
esac

if [[ ${1:-} == "-h" || ${1:-} == "--help" ]]; then
  cat <<'EOF'
Usage: ./scripts/reinstall-all.sh [linux-x64|linux-arm64] [INSTALL OPTIONS]

Validates, publishes, and reinstalls CommandDeck from this local working tree
for the current user, including uncommitted changes. After the Native AOT
publish, it runs ./scripts/install.sh on the publish output with the other
options, for example --hotkey="SUPER + SPACE" or --reset-config. That installs
the binary, systemd user service, catalog, Omarchy plugin, Hyprland hotkey
(Ctrl+Space by default), and all required reloads.
It does not fetch or pull remote changes.
Run ./scripts/install.sh --help for the install options.

By default, an existing CommandDeck configuration is preserved. Pass
--reset-config to move the CommandDeck configuration directory to a backup
under ~/.local/state/commanddeck/config-backups/ before generating the current
default configuration.

Set COMMANDDECK_SKIP_VALIDATE=1 to skip the repository validation gate.
EOF
  exit 0
fi

rid=$host_rid
if [[ ${1:-} == linux-x64 || ${1:-} == linux-arm64 ]]; then
  rid=$1
  shift
fi

if [[ $rid != "$host_rid" ]]; then
  echo "Cannot install $rid on this $host_rid host." >&2
  exit 2
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Required command not found: dotnet" >&2
  exit 1
fi

# install.sh owns the install options and reads them in order. With --help
# last, a wrong option fails here, before the long validate and publish.
"$repo_dir/scripts/install.sh" "$@" --help >/dev/null

step() {
  printf '\n==> %s\n' "$1"
}

cd "$repo_dir"

if [[ ${COMMANDDECK_SKIP_VALIDATE:-0} != "1" ]]; then
  step "Validate repository"
  "$repo_dir/scripts/validate-repository.sh"
fi

step "Publish Native AOT for $rid"
"$repo_dir/scripts/publish-aot.sh" "$rid"

# install.sh checks the desktop prerequisites before it changes anything.
step "Install the published build"
exec "$repo_dir/scripts/install.sh" --from="$repo_dir/artifacts/publish/$rid" "$@"
