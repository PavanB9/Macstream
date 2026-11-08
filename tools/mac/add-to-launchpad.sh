#!/bin/bash
# Makes sure Macstream shows up in Launchpad / Spotlight. The app itself (build/mac/
# Macstream.app) is what appears there; build-server.sh already does this after every build,
# so this script is only needed if Launchpad doesn't show it.
#
#   ./tools/mac/add-to-launchpad.sh            # register the app with Launchpad / Spotlight
#   ./tools/mac/add-to-launchpad.sh --remove   # unregister it (uninstall.sh does this too)
#
# Earlier versions put a separate small launcher in ~/Applications/Macstream.app. It is not
# needed any more and is removed here (only if it really is Macstream's). No admin rights.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APP="$ROOT/build/mac/Macstream.app"
APPS_DIR="$HOME/Applications"
LAUNCHER="$APPS_DIR/Macstream.app"
source "$ROOT/tools/mac/app-id.sh"
LAUNCHER_ID="$MACSTREAM_OLD_LAUNCHER_ID"   # empty: there never was a launcher to clean up
MARKER_DIR="$ROOT/.local/launcher"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"

is_old_launcher() {
  [[ -n "$LAUNCHER_ID" && -d "$LAUNCHER" ]] &&
    [[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$LAUNCHER/Contents/Info.plist" 2>/dev/null)" == "$LAUNCHER_ID" ]]
}

remove_old_launcher() {
  if is_old_launcher; then
    "$LSREGISTER" -u "$LAUNCHER" >/dev/null 2>&1 || true
    rm -rf "$LAUNCHER"
    echo "Removed the old launcher $LAUNCHER (Launchpad now shows the Macstream app itself)."
    echo "If it was in your Dock, drag Macstream from Launchpad onto the Dock again."
  fi
  # Remove ~/Applications only if Macstream created it and it is now empty.
  if [[ -f "$MARKER_DIR/created-applications-folder" ]]; then
    rmdir "$APPS_DIR" 2>/dev/null && echo "Removed the empty $APPS_DIR (it was created for Macstream)"
    rm -f "$MARKER_DIR/created-applications-folder"
  fi
  rmdir "$MARKER_DIR" 2>/dev/null || true
}

case "${1:-}" in
  --remove-old-launcher)
    remove_old_launcher
    exit 0 ;;
  --remove)
    remove_old_launcher
    [[ -d "$APP" ]] && "$LSREGISTER" -u "$APP" >/dev/null 2>&1 && echo "Unregistered $APP from Launchpad / Spotlight."
    exit 0 ;;
  "") ;;
  *) echo "Unknown option: $1" >&2; exit 2 ;;
esac

remove_old_launcher
if [[ ! -d "$APP" ]]; then
  echo "Macstream isn't built yet. Run ./tools/mac/build-server.sh first (it registers the app too)." >&2
  exit 1
fi
"$LSREGISTER" -f "$APP" >/dev/null 2>&1 || true
touch "$APP"
echo "Registered $APP"
echo "Open Launchpad (or Spotlight: type Macstream) and click Macstream; it appears in the menu bar."
echo "For the Dock: drag Macstream from Launchpad onto the Dock."
