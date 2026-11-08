# Sourced by the Mac scripts (build, uninstall, Launchpad). Sets the app's bundle ID.
#
# macOS ties Screen Recording / Accessibility permissions, Start at Login and the app's
# preferences to this ID, so keep it the same from build to build. To use your own ID, put
#   MACSTREAM_BUNDLE_ID="com.yourname.macstream.server"
# in tools/personal/mac.conf (read below if it exists) or set it in the environment.
#
# Expects ROOT to be set to the project folder.

if [[ -f "$ROOT/tools/personal/mac.conf" ]]; then
  # shellcheck source=/dev/null
  source "$ROOT/tools/personal/mac.conf"
fi

MACSTREAM_BUNDLE_ID="${MACSTREAM_BUNDLE_ID:-local.macstream.server}"
# Bundle ID of the small Launchpad launcher very old versions installed (only cleaned up, never
# created). Empty when there never was one.
MACSTREAM_OLD_LAUNCHER_ID="${MACSTREAM_OLD_LAUNCHER_ID:-}"
