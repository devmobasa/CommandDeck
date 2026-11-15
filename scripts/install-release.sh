#!/usr/bin/env bash
# Installs, updates, checks, or uninstalls a prebuilt CommandDeck release.
# It needs no .NET SDK. Run it as your desktop user, in your Omarchy session:
#
#   curl -fsSL https://raw.githubusercontent.com/devmobasa/CommandDeck/main/scripts/install-release.sh | bash
#   curl -fsSL .../install-release.sh | bash -s -- --hotkey="SUPER + SPACE"
#
# Bash runs a piped script one complete command at a time. Everything here is
# a definition until the last line, and that line is one braced command. So a
# download that stops early runs nothing, and never runs main without its
# options.
set -euo pipefail

readonly repository=devmobasa/CommandDeck
readonly script_url=https://raw.githubusercontent.com/$repository/main/scripts/install-release.sh
readonly version_pattern='^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'

usage() {
  cat <<EOF
Usage: install-release.sh [VERSION] [OPTIONS]

Install or update CommandDeck from a prebuilt release. Run it in your Omarchy
session, as your own user. VERSION is a tag such as v0.1.0-preview.2. The
default is the newest release, previews included.

  (no option)      Install CommandDeck, or update it to VERSION. When that
                   version is already installed, change nothing.
  --check          Show the installed and the newest version. Change nothing.
  --uninstall      Remove CommandDeck. Keep your configuration and state.
  --force          Install again, also when that version is installed.
  --dry-run        Download and verify the release, check the requirements,
                   and change nothing.
  --hotkey=CHORD   Bind this chord, for example --hotkey="SUPER + SPACE".
  --reset-config   Move your configuration to a backup and start from the
                   defaults.
  -h, --help       Show this message.

From the network, put the options after "bash -s --":

  curl -fsSL $script_url | bash -s -- --check
EOF
}

say() {
  printf '%s\n' "$*"
}

step() {
  printf '\n==> %s\n' "$*"
}

fail() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

# Prints the XDG base folder in variable $1, or the default $2. The release's
# installer uses the same folders, so a relative value is refused, not ignored.
xdg_dir() {
  local value=${!1:-}
  if [[ -z $value ]]; then
    printf '%s\n' "$2"
  elif [[ $value == /* ]]; then
    printf '%s\n' "$value"
  else
    fail "$1 must be an absolute path, not \"$value\"."
  fi
}

# Prints "vX.Y.Z[-suffix]" for the installed binary, or nothing.
installed_version() {
  local binary=$1 output
  [[ -x $binary ]] || return 0
  output=$("$binary" version 2>/dev/null) || return 0
  output=${output%%$'\n'*}
  output=${output#commanddeck }
  output=${output%%+*}
  if [[ v$output =~ $version_pattern ]]; then
    printf 'v%s\n' "$output"
  fi
}

# Compares two decimal numbers of any length. Prints -1, 0, or 1.
compare_numbers() {
  local LC_ALL=C left=$1 right=$2
  while [[ $left == 0?* ]]; do left=${left#0}; done
  while [[ $right == 0?* ]]; do right=${right#0}; done
  if ((${#left} != ${#right})); then
    ((${#left} < ${#right})) && echo -1 || echo 1
  elif [[ $left == "$right" ]]; then
    echo 0
  else
    [[ $left < "$right" ]] && echo -1 || echo 1
  fi
}

# Compares two versions by SemVer precedence. Prints -1, 0, or 1. SemVer
# orders text identifiers by ASCII, so the comparison uses the C locale.
compare_versions() {
  local LC_ALL=C
  local left=${1#v} right=${2#v}
  local left_core=${left%%-*} right_core=${right%%-*}
  local left_pre="" right_pre="" order
  [[ $left == *-* ]] && left_pre=${left#*-}
  [[ $right == *-* ]] && right_pre=${right#*-}

  local -a a b
  local i
  IFS=. read -ra a <<<"$left_core"
  IFS=. read -ra b <<<"$right_core"
  for i in 0 1 2; do
    order=$(compare_numbers "${a[i]}" "${b[i]}")
    if [[ $order != 0 ]]; then
      echo "$order"
      return
    fi
  done

  # A release without a suffix ranks above its previews.
  if [[ -z $left_pre || -z $right_pre ]]; then
    if [[ $left_pre == "$right_pre" ]]; then
      echo 0
    elif [[ -z $left_pre ]]; then
      echo 1
    else
      echo -1
    fi
    return
  fi

  IFS=. read -ra a <<<"$left_pre"
  IFS=. read -ra b <<<"$right_pre"
  for ((i = 0; i < ${#a[@]} || i < ${#b[@]}; i++)); do
    if ((i >= ${#a[@]})); then echo -1; return; fi
    if ((i >= ${#b[@]})); then echo 1; return; fi
    [[ ${a[i]} == "${b[i]}" ]] && continue
    if [[ ${a[i]} =~ ^[0-9]+$ && ${b[i]} =~ ^[0-9]+$ ]]; then
      compare_numbers "${a[i]}" "${b[i]}"
    elif [[ ${a[i]} =~ ^[0-9]+$ ]]; then
      echo -1
    elif [[ ${b[i]} =~ ^[0-9]+$ ]]; then
      echo 1
    else
      [[ ${a[i]} < "${b[i]}" ]] && echo -1 || echo 1
    fi
    return
  done
  echo 0
}

require_commands() {
  local tool missing=()
  for tool in "$@"; do
    command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
  done
  ((${#missing[@]} == 0)) && return 0

  printf 'error: required commands are missing: %s\n' "${missing[*]}" >&2
  if command -v pacman >/dev/null 2>&1; then
    local packages=()
    for tool in "${missing[@]}"; do
      case $tool in
        sha256sum|mktemp) packages+=(coreutils) ;;
        *) packages+=("$tool") ;;
      esac
    done
    printf 'Install them with: sudo pacman -S --needed %s\n' "$(printf '%s\n' "${packages[@]}" | sort -u | paste -sd ' ')" >&2
  fi
  exit 1
}

newest_version() {
  local tag
  if ! tag=$(curl "${curl_options[@]}" "https://api.github.com/repos/$repository/releases?per_page=1" |
    jq -er '.[0].tag_name // empty'); then
    fail "Could not ask GitHub for the newest release. You may be offline, or GitHub may limit requests. Retry later, or pass a version, for example: bash -s -- v0.1.0-preview.2"
  fi
  [[ $tag =~ $version_pattern ]] || fail "GitHub returned an invalid release version: $tag"
  printf '%s\n' "$tag"
}

# True when the file is a Hyprland module that CommandDeck generated: the
# hotkey module, the Settings shortcut module, or a .bak copy of either.
is_generated_lua() {
  local first=""
  [[ -f $1 ]] || return 1
  IFS= read -r first <"$1" || true
  # The backticks are part of the header text.
  # shellcheck disable=SC2016
  [[ $first == '-- Generated by `commanddeck hotkey install`.'* || $first == '-- Generated by CommandDeck.'* ]]
}

# The files that install-local.sh installed. Releases before it kept a list
# shipped exactly these; install-local.sh keeps the same list.
readonly manifest_entry='^(picker/)?[A-Za-z0-9_][A-Za-z0-9._+-]*$'
readonly legacy_program_files='commanddeck
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

hyprland_running() {
  [[ -n ${HYPRLAND_INSTANCE_SIGNATURE:-} ]] && return 0
  command -v pgrep >/dev/null 2>&1 && pgrep -u "$EUID" -x Hyprland >/dev/null 2>&1
}

# Removes only what the installer created. The configuration and the state
# folder stay. The plugin folder moves to the state folder's plugin backups,
# like the installer does with a replaced plugin.
uninstall() {
  local dry_run=$1
  local binary=$program_dir/commanddeck
  local link=$HOME/.local/bin/commanddeck
  local hypr_dir=$config_home/hypr
  local hotkey_module=$hypr_dir/commanddeck.lua
  local keys_module=$hypr_dir/commanddeck_keys.lua
  local manifest=$program_dir/.installed-files
  local installed linked=0 generated=0 file
  installed=$(installed_version "$binary")
  if [[ -L $link && $(readlink -- "$link") == "$binary" ]]; then
    linked=1
  fi
  for file in "$hotkey_module" "$keys_module" "$hotkey_module.bak" "$keys_module.bak"; do
    if is_generated_lua "$file"; then
      generated=1
    fi
  done

  if [[ ! -e $program_dir && ! -e $service_file && ! -e $plugin_dir ]] && ((!linked && !generated)); then
    say "CommandDeck is not installed. Nothing to remove."
    return 0
  fi

  # Check what the steps need before the first change, so a failure leaves
  # the installation as it was.
  if ! command -v systemctl >/dev/null 2>&1; then
    fail "systemctl is missing, so the CommandDeck daemon cannot be stopped. Nothing was changed."
  elif ! systemctl --user show-environment >/dev/null 2>&1; then
    fail "The systemd user manager is not reachable. Run this in your desktop session, as your own user. Nothing was changed."
  elif hyprland_running && ! hyprctl -j version >/dev/null 2>&1; then
    fail "Hyprland is running, but hyprctl cannot reach it. Run this inside your Hyprland session. Nothing was changed."
  fi
  # Read the plugin state now: an unknown state must not let the plugin folder
  # go while the shell configuration still enables it.
  local plugin_enabled=0 plugins
  if [[ -e $plugin_dir || -L $plugin_dir ]] && command -v omarchy >/dev/null 2>&1; then
    command -v jq >/dev/null 2>&1 ||
      fail "jq is missing, so the Omarchy plugin state cannot be read. Nothing was changed."
    if ! plugins=$(omarchy plugin list --json 2>/dev/null) || ! jq -e 'type == "array"' >/dev/null 2>&1 <<<"$plugins"; then
      fail "Omarchy did not list its plugins. Run this inside your Omarchy session. Nothing was changed."
    fi
    if jq -e 'any(.[]; .id == "local.commanddeck" and .enabled == true)' >/dev/null <<<"$plugins"; then
      plugin_enabled=1
    fi
  fi

  say "Uninstall CommandDeck ${installed:-(unknown version)}"
  say "  service  stop it, then remove $service_file"
  say "  hotkey   $hotkey_module and its line in hyprland.lua"
  say "  plugin   disable it, then move $plugin_dir to $state_dir/plugin-backups/"
  say "  program  the files that the installer put in $program_dir, and $link"
  say "  keep     $config_home/commanddeck and $state_dir"
  if ((dry_run)); then
    say ""
    say "Dry run: nothing was changed."
    return 0
  fi

  local failures=0
  stop_here() {
    printf 'error: %s\n' "$*" >&2
    printf 'Nothing else was removed. Fix the error, then run --uninstall again.\n' >&2
    exit 1
  }
  warn() {
    printf 'warning: %s\n' "$*" >&2
    failures=$((failures + 1))
  }

  # A running daemon would keep the removed binary open, so it must stop
  # first. An absent unit fails to stop, but systemd then reports it
  # inactive. Only a reported inactive or failed state counts as stopped.
  step "Stop the service"
  systemctl --user disable commanddeck.service >/dev/null 2>&1 || true
  systemctl --user stop commanddeck.service >/dev/null 2>&1 || true
  local state
  state=$(systemctl --user is-active commanddeck.service 2>/dev/null) || true
  case $state in
    inactive|failed) ;;
    *) stop_here "commanddeck.service did not stop (state: ${state:-unknown}). Check: systemctl --user status commanddeck.service" ;;
  esac

  # The binary owns the hotkey files, so it stays until they are gone. When it
  # is missing or cannot run, delete the generated module directly.
  step "Remove the hotkey"
  if [[ -x $binary ]] && "$binary" version >/dev/null 2>&1; then
    "$binary" hotkey remove || stop_here "commanddeck hotkey remove failed."
  elif is_generated_lua "$hotkey_module"; then
    rm -f -- "$hotkey_module"
    say "Deleted $hotkey_module. The commanddeck binary is missing or does not run, so"
    say "its guarded require line stays in $hypr_dir/hyprland.lua. It does nothing without the module."
  fi
  # Settings generates the shortcut module, and only commanddeck.lua loads it.
  for file in "$keys_module" "$keys_module.bak" "$hotkey_module.bak"; do
    if is_generated_lua "$file"; then
      rm -f -- "$file"
    fi
  done
  if hyprland_running; then
    hyprctl reload >/dev/null || warn "hyprctl reload failed. Run it yourself to drop the binding."
  fi

  # Disable an enabled plugin before its folder goes, so the shell
  # configuration does not keep it. A failed disable keeps everything else.
  step "Remove the Omarchy plugin"
  if [[ -e $plugin_dir || -L $plugin_dir ]]; then
    if ((plugin_enabled)); then
      omarchy plugin disable local.commanddeck >/dev/null ||
        stop_here "omarchy plugin disable local.commanddeck failed. Run this inside your Omarchy session."
    fi
    local backup
    backup=$state_dir/plugin-backups/local.commanddeck.$(date +%Y%m%d-%H%M%S-%N)
    mkdir -p -- "$state_dir/plugin-backups"
    mv -T -- "$plugin_dir" "$backup"
    say "Moved the plugin to $backup"
  fi

  step "Remove the service file"
  rm -f -- "$service_file"
  systemctl --user daemon-reload || warn "systemctl --user daemon-reload failed."

  # Remove exactly the listed files, and keep anything else in the folder. A
  # linked picker folder is not the installer's, so nothing goes through it.
  step "Remove the program"
  if ((linked)); then
    rm -f -- "$link"
  fi
  if [[ -d $program_dir ]]; then
    local entries entry picker_is_ours=1
    if [[ -f $manifest ]]; then
      entries=$(<"$manifest")
    else
      entries=$legacy_program_files
    fi
    if [[ -L $program_dir/picker ]]; then
      picker_is_ours=0
    fi
    while IFS= read -r entry; do
      if [[ ! $entry =~ $manifest_entry ]]; then
        continue
      fi
      if [[ $entry == picker/* ]] && ((!picker_is_ours)); then
        continue
      fi
      rm -f -- "${program_dir:?}/$entry"
    done <<<"$entries"
    rm -f -- "$manifest"
    rmdir -- "$program_dir/picker" 2>/dev/null || true
    if ! rmdir -- "$program_dir" 2>/dev/null; then
      say "Kept $program_dir, because it holds files that the installer did not put there."
    fi
  fi

  step "Restart Omarchy Shell"
  if command -v omarchy >/dev/null 2>&1 && hyprland_running; then
    omarchy restart shell || warn "omarchy restart shell failed."
  fi

  printf '\nCommandDeck is uninstalled. Your configuration and state are kept.\n'
  printf 'To delete them too, run:\n  rm -r %q %q\n' "$config_home/commanddeck" "$state_dir"
  ((failures == 0)) || fail "$failures uninstall steps failed. Read the warnings above."
}

main() {
  local version="" mode="" force=0 dry_run=0 argument
  local install_options=()
  for argument in "$@"; do
    case $argument in
      --check|--uninstall)
        if [[ -n $mode && $mode != "${argument#--}" ]]; then
          printf -- '--%s and %s cannot be used together.\n' "$mode" "$argument" >&2
          exit 2
        fi
        mode=${argument#--}
        ;;
      --force) force=1 ;;
      --dry-run) dry_run=1; install_options+=("$argument") ;;
      --hotkey=*|--reset-config) install_options+=("$argument") ;;
      -h|--help) usage; exit 0 ;;
      -*) printf 'Unknown option: %s\n\n' "$argument" >&2; usage >&2; exit 2 ;;
      *)
        [[ -z $version ]] || { printf 'Only one VERSION is allowed: %s\n' "$argument" >&2; exit 2; }
        [[ $argument =~ $version_pattern ]] || { printf 'Invalid release version: %s\n' "$argument" >&2; exit 2; }
        version=$argument
        ;;
    esac
  done
  mode=${mode:-install}

  # The XDG specification treats an empty variable as unset, but the
  # CommandDeck binary would use it as a relative path. Unset empty ones for
  # this script and for everything it runs.
  for argument in XDG_CONFIG_HOME XDG_DATA_HOME XDG_STATE_HOME XDG_CACHE_HOME; do
    if [[ -v $argument && -z ${!argument} ]]; then
      unset "$argument"
    fi
  done

  if [[ $mode == uninstall && ( -n $version || $force == 1 || ${#install_options[@]} -gt $dry_run ) ]]; then
    printf -- '--uninstall accepts only --dry-run.\n' >&2
    exit 2
  fi
  if [[ $mode == check && ( $force == 1 || ${#install_options[@]} -gt 0 ) ]]; then
    printf -- '--check accepts only VERSION.\n' >&2
    exit 2
  fi

  ((EUID != 0)) || fail "Run this as your desktop user, without sudo."
  [[ $(uname -s) == Linux ]] || fail "CommandDeck releases require Linux."

  config_home=$(xdg_dir XDG_CONFIG_HOME "$HOME/.config")
  program_dir=$(xdg_dir XDG_DATA_HOME "$HOME/.local/share")/commanddeck
  state_dir=$(xdg_dir XDG_STATE_HOME "$HOME/.local/state")/commanddeck
  service_file=$config_home/systemd/user/commanddeck.service
  plugin_dir=$config_home/omarchy/plugins/local.commanddeck

  if [[ $mode == uninstall ]]; then
    uninstall "$dry_run"
    return
  fi

  local cache_dir
  cache_dir=$(xdg_dir XDG_CACHE_HOME "$HOME/.cache")/commanddeck

  local rid
  case $(uname -m) in
    x86_64) rid=linux-x64 ;;
    aarch64|arm64) rid=linux-arm64 ;;
    *) fail "CommandDeck releases support only Linux x64 and ARM64, not $(uname -m)." ;;
  esac

  require_commands curl jq sha256sum tar mktemp
  curl_options=(--fail --show-error --silent --location --retry 3 --connect-timeout 15 --proto '=https' --proto-redir '=https')

  local installed requested=$version
  installed=$(installed_version "$program_dir/commanddeck")
  if [[ -z $version ]]; then
    version=$(newest_version)
  fi

  if [[ $mode == check ]]; then
    say "Installed: ${installed:-not installed}"
    if [[ -n $requested ]]; then
      say "Requested: $version"
    else
      say "Newest:    $version"
    fi
    if [[ -z $installed ]]; then
      say "Install it with: curl -fsSL $script_url | bash"
    elif [[ $(compare_versions "$installed" "$version") == -1 ]]; then
      say "Update with: curl -fsSL $script_url | bash"
    else
      say "CommandDeck is up to date."
    fi
    return 0
  fi

  # The installer restarts the daemon and the Omarchy shell, so do not run it
  # for nothing, and never replace a newer build unless VERSION asks for it.
  # A dry run still checks the release, because that is what it is for.
  local order="" changes_setup=0 option
  for option in "${install_options[@]}"; do
    [[ $option == --dry-run ]] || changes_setup=1
  done
  if [[ -n $installed ]]; then
    order=$(compare_versions "$installed" "$version")
  fi
  if [[ $order == 1 && -z $requested ]]; then
    say "CommandDeck $installed is newer than the newest release, $version."
    if ((!dry_run)); then
      say "To install $version anyway, pass it: bash -s -- $version"
      if ((changes_setup)); then
        say "To change only the hotkey, run: commanddeck hotkey install --chord CHORD"
      fi
      return 0
    fi
    say "Without VERSION, a real run changes nothing. The dry run checks $version anyway."
  elif [[ $order == 0 && $force == 0 && $changes_setup == 0 ]]; then
    say "CommandDeck $installed is already installed and up to date."
    if ((!dry_run)); then
      say "To install it again, add --force."
      return 0
    fi
    say "A real run changes nothing. The dry run checks the release anyway."
  fi

  # The installer runs the new binary before it changes anything. Extract it
  # under the home folder, where it will run later, and not in a /tmp that can
  # be mounted noexec.
  local download_dir
  mkdir -p -- "$cache_dir"
  download_dir=$(mktemp -d "$cache_dir/download.XXXXXX")
  # The EXIT trap runs after main returns, so it gets the paths, not the names
  # of main's local variables.
  # shellcheck disable=SC2064
  trap "rm -rf -- $(printf %q "$download_dir"); rmdir -- $(printf %q "$cache_dir") 2>/dev/null || true" EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  trap 'exit 129' HUP

  local name=commanddeck-$version-$rid
  local archive=$name.tar.gz
  local release_url=https://github.com/$repository/releases/download/$version

  if [[ -z $installed ]]; then
    step "Download CommandDeck $version for $rid"
  elif [[ $installed == "$version" ]]; then
    step "Download CommandDeck $version for $rid again"
  else
    step "Download CommandDeck $version for $rid (installed: $installed)"
  fi
  local progress=()
  [[ -t 2 ]] && progress=(--progress-bar)
  if ! curl "${curl_options[@]}" "${progress[@]}" "$release_url/$archive" -o "$download_dir/$archive" ||
    ! curl "${curl_options[@]}" "$release_url/$archive.sha256" -o "$download_dir/$archive.sha256"; then
    fail "Could not download $archive. Check that release $version exists: https://github.com/$repository/releases"
  fi

  step "Verify the SHA-256 checksum"
  local digest filename
  read -r digest filename <"$download_dir/$archive.sha256"
  if [[ ! $digest =~ ^[[:xdigit:]]{64}$ || $filename != "$archive" ]]; then
    fail "The release checksum file is invalid."
  fi
  if ! (cd -- "$download_dir" && printf '%s  %s\n' "$digest" "$archive" | sha256sum -c --quiet -); then
    fail "The download does not match its checksum. Run the command again."
  fi
  say "OK"

  tar -xzf "$download_dir/$archive" -C "$download_dir"

  # Ctrl+C, a closed terminal, systemd, and timeout signal the whole process
  # group, so the installer gets the signal itself and rolls back. Forwarding
  # it would signal the installer twice and could cut its rollback short.
  # Bash runs these traps only after the installer exits, so a signal sent
  # only to this script cannot stop the installer. Report that honestly.
  local signal="" status=0
  trap 'signal=INT' INT
  trap 'signal=TERM' TERM
  trap 'signal=HUP' HUP
  (cd -- "$download_dir/$name" && bash ./install.sh "${install_options[@]}") || status=$?
  trap 'exit 130' INT
  trap 'exit 143' TERM
  trap 'exit 129' HUP
  if [[ -n $signal && $status == 0 ]]; then
    say "Received SIG$signal while the installer ran. The installer had already finished, so it was not stopped."
  fi
  ((status == 0)) || return "$status"

  if ((!dry_run)); then
    say "Release notes: https://github.com/$repository/releases/tag/$version"
  fi
}

{ main "$@"; }
