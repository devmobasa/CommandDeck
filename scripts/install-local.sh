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

# The list of files this script installed. Each install removes the files that
# an earlier install listed and this build no longer has, and the uninstaller
# removes exactly the listed files. Entries are plain names, or names in picker/.
manifest_name=.installed-files
manifest_entry='^(picker/)?[A-Za-z0-9_][A-Za-z0-9._+-]*$'
# Releases before the list, v0.1.0-preview.1 and v0.1.0-preview.2, installed
# exactly these files. install-release.sh keeps the same list.
legacy_entries='commanddeck
commanddeck.schema.json
libe_sqlite3.so
picker/ColorThemeFormat.js
picker/DeckButton.qml
picker/InterfaceFonts.js
picker/KeyboardButton.qml
picker/ListKeys.js
picker/PickerView.qml
picker/TabFence.js
picker/Theme.qml
picker/UiConstants.js
picker/shell.qml'
new_entries=$(
  {
    echo commanddeck
    if [[ -f $source_dir/commanddeck.schema.json ]]; then
      echo commanddeck.schema.json
    fi
    find "$source_dir" -maxdepth 1 -type f -name '*.so' -printf '%f\n'
    find "$source_dir/picker" -maxdepth 1 -type f -printf 'picker/%f\n'
  } | sort -u
)
while IFS= read -r entry; do
  if [[ ! $entry =~ $manifest_entry ]]; then
    echo "The publish output in $source_dir has a file name that cannot be listed: $entry" >&2
    exit 2
  fi
done <<<"$new_entries"

mkdir -p "$install_dir" "$bin_dir"
previous_entries=""
if [[ -f $install_dir/$manifest_name ]]; then
  previous_entries=$(<"$install_dir/$manifest_name")
elif [[ -e $install_dir/commanddeck ]]; then
  previous_entries=$legacy_entries
fi
# A linked picker folder is not this script's, so nothing is removed through it.
picker_is_ours=1
if [[ -L $install_dir/picker ]]; then
  picker_is_ours=0
fi
while IFS= read -r entry; do
  if [[ ! $entry =~ $manifest_entry ]] || grep -qxF -- "$entry" <<<"$new_entries"; then
    continue
  fi
  if [[ $entry == picker/* ]] && ((!picker_is_ours)); then
    continue
  fi
  rm -f -- "${install_dir:?}/$entry"
done <<<"$previous_entries"
install -m755 "$source_dir/commanddeck" "$install_dir/commanddeck"
if [[ -f $source_dir/commanddeck.schema.json ]]; then
  install -m644 "$source_dir/commanddeck.schema.json" "$install_dir/commanddeck.schema.json"
fi
while IFS= read -r asset; do
  install -m755 "$asset" "$install_dir/$(basename -- "$asset")"
done < <(find "$source_dir" -maxdepth 1 -type f -name '*.so' -print)
# The published picker directory is the delivered inventory. The loop above
# removed the listed files that this build dropped.
mkdir -p "$install_dir/picker"
while IFS= read -r asset; do
  install -m644 "$asset" "$install_dir/picker/$(basename -- "$asset")"
done < <(find "$source_dir/picker" -maxdepth 1 -type f -print)
ln -sfn "$install_dir/commanddeck" "$bin_dir/commanddeck"
printf '%s\n' "$new_entries" >"$install_dir/$manifest_name.tmp"
mv -f -- "$install_dir/$manifest_name.tmp" "$install_dir/$manifest_name"

echo "Installed CommandDeck in $install_dir"
echo "Run: commanddeck config init && commanddeck doctor && commanddeck reindex"
