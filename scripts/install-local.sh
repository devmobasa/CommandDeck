#!/usr/bin/env bash
set -euo pipefail

# Usage: ./scripts/install-local.sh [linux-x64|linux-arm64|DIRECTORY]
# A RID installs ./artifacts/publish/RID; a directory installs that publish
# output, such as the bin/ folder of a release.
repo_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
source=${1:-linux-x64}
rid=linux-x64
case "$source" in
  linux-x64|linux-arm64)
    rid=$source
    source_dir="$repo_dir/artifacts/publish/$source"
    ;;
  *)
    source_dir=$source
    ;;
esac
install_dir=${XDG_DATA_HOME:-$HOME/.local/share}/commanddeck
bin_dir=${HOME}/.local/bin

test -x "$source_dir/commanddeck" || {
  echo "No commanddeck binary in $source_dir. Publish first: ./scripts/publish-aot.sh $rid" >&2
  exit 2
}
test -f "$source_dir/picker/shell.qml" || {
  echo "The publish output in $source_dir has no picker; republish: ./scripts/publish-aot.sh $rid" >&2
  exit 2
}

mkdir -p "$install_dir" "$bin_dir"
install -m755 "$source_dir/commanddeck" "$install_dir/commanddeck"
if [[ -f $source_dir/commanddeck.schema.json ]]; then
  install -m644 "$source_dir/commanddeck.schema.json" "$install_dir/commanddeck.schema.json"
fi
while IFS= read -r asset; do
  install -m755 "$asset" "$install_dir/$(basename -- "$asset")"
done < <(find "$source_dir" -maxdepth 1 -type f -name '*.so' -print)
# The published picker directory is the delivered inventory; copy it whole and
# drop assets an earlier version delivered.
mkdir -p "$install_dir/picker"
find "$install_dir/picker" -maxdepth 1 -type f -delete
while IFS= read -r asset; do
  install -m644 "$asset" "$install_dir/picker/$(basename -- "$asset")"
done < <(find "$source_dir/picker" -maxdepth 1 -type f -print)
ln -sfn "$install_dir/commanddeck" "$bin_dir/commanddeck"

echo "Installed CommandDeck in $install_dir"
echo "Run: commanddeck config init && commanddeck doctor && commanddeck reindex"
