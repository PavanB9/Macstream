#!/bin/bash
# Starts build/mac/Macstream.app through LaunchServices, so macOS treats the app
# (not Terminal) as the process asking for permissions, and follows its log here.
# Press Ctrl+C to stop the server.
#
# Any MACSTREAM_* variables set in this shell are passed to the server, e.g.
#   MACSTREAM_CODEC=hevc ./tools/mac/run-server.sh
# Defaults for this stage: mirror the Mac's own display (Mode A).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APP="$ROOT/build/mac/Macstream.app"
LOG="$ROOT/logs/mac-server.log"

if [[ ! -d "$APP" ]]; then
  echo "App not built yet. Run ./tools/mac/build-server.sh first." >&2
  exit 1
fi

if pgrep -x Macstream >/dev/null; then
  echo "Macstream is already running. Stop it with: pkill -INT -x Macstream" >&2
  exit 1
fi

# Mirror the Mac's own screen or use an extra display: chosen in the Windows app (Settings >
# Mac screen). MACSTREAM_MIRROR_MAIN=1 here would force the mirror.
# The server opens this file itself (see Main.swift). Using `open --stdout` instead fails
# with LaunchServices error -10810 when the project is inside ~/Documents.
export MACSTREAM_LOG_FILE="$LOG"

ENV_ARGS=()
while IFS='=' read -r name value; do
  ENV_ARGS+=(--env "$name=$value")
done < <(env | grep -E '^(MACSTREAM|WIRED_MONITOR)_' || true)   # old names still work

mkdir -p "$ROOT/logs"
: > "$LOG"

stop_server() {
  echo
  echo "[run] Stopping Macstream..."
  pkill -INT -x Macstream 2>/dev/null || true
  sleep 1
  pkill -x Macstream 2>/dev/null || true
  [ -n "${TAIL_PID:-}" ] && kill "$TAIL_PID" 2>/dev/null
  exit 0
}
trap stop_server INT TERM

echo "[run] Starting $APP"
echo "[run] Log: $LOG   (Ctrl+C to stop)"
echo "[run] Macstream is also in the menu bar (display icon): status, pause the PC's mouse/keyboard, pairing, Start at Login, Quit."
echo "[run] Without Terminal: double-click build/mac/Macstream.app in Finder (or tick Start at Login)."
if ! open -n "${ENV_ARGS[@]}" "$APP"; then
  echo "[run] macOS could not launch the app." >&2
  echo "[run] To see why, run it directly once (permissions then go to Terminal, test only):" >&2
  echo "      MACSTREAM_MIRROR_MAIN=1 \"$APP/Contents/MacOS/Macstream\"" >&2
  exit 1
fi
echo "[run] If macOS asks whether Macstream may access your Documents folder, click Allow."

tail -n +1 -f "$LOG" &
TAIL_PID=$!

# Keep running while the server is alive; exit if it quits on its own.
sleep 2
while pgrep -x Macstream >/dev/null; do sleep 1; done
kill "$TAIL_PID" 2>/dev/null || true
echo "[run] Macstream exited."
