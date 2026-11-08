#!/bin/bash
# Forgets every PC paired with this Mac. They will have to pair again (the Mac asks, with a
# code to compare) the next time they connect. This Mac's own key is kept.
# The menu bar's "Forget Paired PCs…" does the same without a restart.
#   ./tools/mac/unpair.sh
set -uo pipefail
if security delete-generic-password -s com.macstream.server -a trusted-clients >/dev/null 2>&1; then
  echo "Forgot all paired PCs."
else
  echo "No paired PCs to forget."
fi
if pgrep -x Macstream >/dev/null; then
  echo "Restart Macstream for this to take effect: Quit Macstream in the menu bar, then start it again from Launchpad"
fi
