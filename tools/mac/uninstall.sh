#!/bin/bash
# Removes everything Macstream added on this Mac.
#
#   ./tools/mac/uninstall.sh            # stop the server, revoke its permissions, remove builds/caches/logs; keep source
#   ./tools/mac/uninstall.sh --all      # ...and delete the whole project folder
#   ./tools/mac/uninstall.sh --dry-run  # only show what would be done
#
# Macstream keeps its build output, SwiftPM caches and logs inside the project folder.
# Outside it, the only traces are: the macOS permissions (Screen Recording, Accessibility,
# Documents folder), the Start at Login entry, the old Launchpad launcher (~/Applications/
# Macstream.app, older versions only), the pairing keys in the login Keychain, the app's LaunchServices
# registration and small per-app files macOS keeps (menu-bar icon position in Preferences,
# caches, saved state), and possibly a firewall entry. This script removes those too, and
# only items with Macstream's own names / bundle IDs, so nothing else is touched. Xcode, git, gh and Homebrew were already installed
# before Macstream, so they are left alone.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APP="$ROOT/build/mac/Macstream.app"
source "$ROOT/tools/mac/app-id.sh"
BUNDLE_ID="$MACSTREAM_BUNDLE_ID"
LAUNCHER_ID="$MACSTREAM_OLD_LAUNCHER_ID"   # may be empty
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
FIREWALL="/usr/libexec/ApplicationFirewall/socketfilterfw"

ALL=0
DRY=0
for arg in "$@"; do
  case "$arg" in
    --all) ALL=1 ;;
    --dry-run) DRY=1 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

size_of() { [ -e "$1" ] && du -sh "$1" 2>/dev/null | cut -f1 || echo "-"; }

if [ "$ALL" -eq 1 ]; then
  TARGETS=("$ROOT")
else
  TARGETS=()
  for rel in build .local logs Mac/MacstreamServer/.build Mac/MacstreamServer/.swiftpm; do
    [ -e "$ROOT/$rel" ] && TARGETS+=("$ROOT/$rel")
  done
fi

# Xcode DerivedData folders for this package (only exist if it was opened in Xcode).
DERIVED=()
for d in "$HOME/Library/Developer/Xcode/DerivedData/"MacstreamServer-*; do
  [ -e "$d" ] && DERIVED+=("$d")
done

echo
echo "Macstream uninstaller (Mac)"
echo "Project folder: $ROOT"
echo
echo "Will do:"
echo "  - remove its Start at Login entry, if any, and stop Macstream if it is running"
echo "  - remove the old Launchpad launcher ~/Applications/Macstream.app, if an older version added it"
echo "  - revoke every macOS permission given to $BUNDLE_ID (Screen Recording, Accessibility, Documents...)"
echo "  - delete macOS's per-app files for it (Preferences, caches, saved state, ~/Library/Logs/Macstream)"
echo "  - unregister the app from LaunchServices and remove its local signing keychain"
echo "  - delete its pairing keys from your login Keychain (paired PCs must pair again)"
echo "  - remove its firewall entry, if any (asks for your Mac password)"
if [ "${#TARGETS[@]}" -gt 0 ]; then
  for t in "${TARGETS[@]}"; do printf "  - delete %6s  %s\n" "$(size_of "$t")" "$t"; done
fi
if [ "${#DERIVED[@]}" -gt 0 ]; then
  for t in "${DERIVED[@]}"; do printf "  - delete %6s  %s\n" "$(size_of "$t")" "$t"; done
fi
[ "$ALL" -eq 0 ] && echo && echo "Source code is kept. Use --all to delete the whole project folder."

if [ "$DRY" -eq 1 ]; then
  echo
  echo "Dry run: nothing was changed."
  exit 0
fi

echo
read -r -p "Type YES to continue: " answer
if [ "$answer" != "YES" ]; then
  echo "Cancelled. Nothing was changed."
  exit 0
fi

# 1. Remove the Start at Login entry (menu bar setting), then stop the server
if [ -x "$APP/Contents/MacOS/Macstream" ]; then
  "$APP/Contents/MacOS/Macstream" --unregister-login-item >/dev/null 2>&1 && echo "Removed the Start at Login entry (if there was one)."
fi
pkill -INT -x Macstream 2>/dev/null && sleep 1
pkill -x Macstream 2>/dev/null
echo "Stopped Macstream (if it was running)."

# 1b. Launchpad: the old launcher (only removed if it really is Macstream's) and the app's entry
if [ -f "$ROOT/tools/mac/add-to-launchpad.sh" ]; then
  bash "$ROOT/tools/mac/add-to-launchpad.sh" --remove
fi

# 2. Revoke privacy permissions granted to the app (all services, for these bundle IDs only)
for id in "$BUNDLE_ID" $LAUNCHER_ID; do
  if tccutil reset All "$id" >/dev/null 2>&1; then
    echo "Revoked all permissions for $id."
  else
    for service in ScreenCapture Accessibility ListenEvent PostEvent SystemPolicyDocumentsFolder AppleEvents; do
      tccutil reset "$service" "$id" >/dev/null 2>&1 && echo "Revoked $service permission for $id."
    done
  fi
done

# 2b. Small files macOS keeps per app (named after the bundle IDs only)
for id in "$BUNDLE_ID" $LAUNCHER_ID; do
  defaults delete "$id" >/dev/null 2>&1 && echo "Removed preferences for $id."
  for path in "$HOME/Library/Preferences/$id.plist" "$HOME/Library/Caches/$id" \
              "$HOME/Library/HTTPStorages/$id" "$HOME/Library/Saved Application State/$id.savedState"; do
    [ -e "$path" ] && rm -rf "$path" && echo "Deleted $path"
  done
done
[ -d "$HOME/Library/Logs/Macstream" ] && rm -rf "$HOME/Library/Logs/Macstream" && echo "Deleted ~/Library/Logs/Macstream"

# 3. Firewall entry (only exists if you clicked Allow on the incoming-connections prompt)
if "$FIREWALL" --listapps 2>/dev/null | grep -qF "$APP"; then
  echo "Removing firewall entry for Macstream (needs your Mac password)..."
  sudo "$FIREWALL" --remove "$APP" || echo "Could not remove it; remove Macstream in System Settings > Network > Firewall > Options."
fi

# 4. Local code-signing keychain (also takes it off the keychain search list if it got there)
SIGN_KEYCHAIN="$ROOT/.local/signing/macstream-signing.keychain-db"
if [ -f "$SIGN_KEYCHAIN" ]; then
  security delete-keychain "$SIGN_KEYCHAIN" 2>/dev/null && echo "Removed local signing keychain."
fi

# 4b. Pairing keys in the login Keychain (this Mac's key and the list of paired PCs)
for account in identity trusted-clients; do
  security delete-generic-password -s com.macstream.server -a "$account" >/dev/null 2>&1     && echo "Removed pairing key: $account."
done

# 5. LaunchServices registration
[ -d "$APP" ] && "$LSREGISTER" -u "$APP" >/dev/null 2>&1 && echo "Unregistered Macstream.app from LaunchServices."

# 6. Delete files
if [ "$ALL" -eq 1 ]; then cd "$ROOT/.." || exit 1; fi
if [ "${#TARGETS[@]}" -gt 0 ]; then
  for t in "${TARGETS[@]}"; do rm -rf "$t" && echo "Deleted $t"; done
fi
if [ "${#DERIVED[@]}" -gt 0 ]; then
  for t in "${DERIVED[@]}"; do rm -rf "$t" && echo "Deleted $t"; done
fi

echo
echo "Done. Not removed automatically:"
echo "  - Any copy of the code on GitHub or elsewhere online: delete it there if you want it gone."
echo "  - Xcode, git, gh, Homebrew: these were already on your Mac before Macstream."
echo "  - Windows side: run .\\tools\\uninstall-windows.ps1 on the PC."
