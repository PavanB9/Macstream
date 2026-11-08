#!/bin/bash
# Frees disk space Macstream doesn't need, without uninstalling anything: the app, its
# permissions, pairing and Start at Login all stay as they are.
#
#   ./tools/mac/tidy.sh             # remove leftovers from older builds and old logs
#   ./tools/mac/tidy.sh --deep      # also the build cache (the next build takes a minute longer)
#   ./tools/mac/tidy.sh --dry-run   # only show what would be removed
#
# Never touches: build/mac/Macstream.app, .local/signing (the local signing key that
# keeps the Screen Recording / Accessibility permissions across rebuilds), the Keychain.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DEEP=0
DRY=0
for arg in "$@"; do
  case "$arg" in
    --deep) DEEP=1 ;;
    --dry-run) DRY=1 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

TARGETS=()
add() { [ -e "$1" ] && TARGETS+=("$1"); }

# Build output from before builds moved into build/mac (swift build's default folders).
add "$ROOT/Mac/MacstreamServer/.build"
add "$ROOT/Mac/MacstreamServer/.swiftpm"
# Older log copies (the current log is kept).
for f in "$ROOT"/logs/*.previous.log; do add "$f"; done
# Temporary folders a stopped build may have left.
for d in "$ROOT"/build/mac/icon.* "$ROOT"/.local/signing/tmp.*; do add "$d"; done
# Xcode's copy, only there if the package was ever opened in Xcode.
for d in "$HOME/Library/Developer/Xcode/DerivedData/"MacstreamServer-*; do add "$d"; done
if [ "$DEEP" -eq 1 ]; then
  add "$ROOT/build/mac/spm"           # SwiftPM build cache, rebuilt by build-server.sh
  add "$ROOT/.local/swiftpm/cache"
fi

if [ "${#TARGETS[@]}" -eq 0 ]; then
  echo "Nothing to tidy."
else
  echo "Will remove:"
  for t in "${TARGETS[@]}"; do
    printf "  %8s  %s\n" "$(du -sh "$t" 2>/dev/null | cut -f1)" "$t"
  done
  if [ "$DRY" -eq 1 ]; then
    echo "Dry run: nothing was removed."
  else
    for t in "${TARGETS[@]}"; do rm -rf "$t"; done
    echo "Done."
  fi
fi
[ "$DEEP" -eq 0 ] && [ -d "$ROOT/build/mac/spm" ] &&
  echo "(Build cache kept: $(du -sh "$ROOT/build/mac/spm" | cut -f1). --deep removes it too.)"
echo "Project folder now: $(du -sh "$ROOT" | cut -f1)"
