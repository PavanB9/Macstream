#!/bin/bash
# Builds the Mac server and wraps it in an ad-hoc signed app bundle:
#   build/mac/Macstream.app   (shows up in Launchpad / Spotlight as "Macstream")
# macOS permissions (Screen Recording, later Accessibility) are then granted to
# this app instead of to Terminal.
#
# All SwiftPM build output, caches and config stay inside the project.
# Usage: ./tools/mac/build-server.sh [release|debug]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PKG="$ROOT/Mac/MacstreamServer"
CONFIG="${1:-release}"
source "$ROOT/tools/mac/app-id.sh"   # MACSTREAM_BUNDLE_ID

SPM_ARGS=(
  --package-path "$PKG"
  -c "$CONFIG"
  --scratch-path "$ROOT/build/mac/spm"
  --cache-path "$ROOT/.local/swiftpm/cache"
  --config-path "$ROOT/.local/swiftpm/config"
  --security-path "$ROOT/.local/swiftpm/security"
)

echo "[build] swift build ($CONFIG)"
swift build "${SPM_ARGS[@]}"
BIN_DIR="$(swift build "${SPM_ARGS[@]}" --show-bin-path)"

APP="$ROOT/build/mac/Macstream.app"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"

# Very old versions had a separate Launchpad launcher; remove it if one is configured
# (tools/mac/app-id.sh) and still there. Does nothing otherwise.
bash "$ROOT/tools/mac/add-to-launchpad.sh" --remove-old-launcher

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN_DIR/MacstreamServer" "$APP/Contents/MacOS/Macstream"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>        <string>$MACSTREAM_BUNDLE_ID</string>
  <key>CFBundleName</key>              <string>Macstream</string>
  <key>CFBundleDisplayName</key>       <string>Macstream</string>
  <key>CFBundleExecutable</key>        <string>Macstream</string>
  <key>CFBundleIconFile</key>          <string>Macstream</string>
  <key>CFBundlePackageType</key>       <string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key>           <string>1</string>
  <key>LSMinimumSystemVersion</key>    <string>13.0</string>
  <key>LSUIElement</key>               <true/>
</dict>
</plist>
PLIST

# App icon from Mac/MacstreamServer/Assets/Macstream-1024.png (sips/iconutil come with macOS).
make_icns() {   # $1 = source PNG (1024 px), $2 = output .icns
  local tmp set s
  tmp="$(mktemp -d "$ROOT/build/mac/icon.XXXXXX")"
  set="$tmp/Macstream.iconset"
  mkdir -p "$set"
  for s in 16 32 128 256 512; do
    sips -z "$s" "$s" "$1" --out "$set/icon_${s}x${s}.png" >/dev/null || { rm -rf "$tmp"; return 1; }
    sips -z $((s * 2)) $((s * 2)) "$1" --out "$set/icon_${s}x${s}@2x.png" >/dev/null || { rm -rf "$tmp"; return 1; }
  done
  iconutil -c icns "$set" -o "$2" || { rm -rf "$tmp"; return 1; }
  rm -rf "$tmp"
}
mkdir -p "$APP/Contents/Resources"
if ! make_icns "$PKG/Assets/Macstream-1024.png" "$APP/Contents/Resources/Macstream.icns"; then
  echo "[build] (icon skipped)"
fi

# --- Code signing --------------------------------------------------------------------------
# macOS ties privacy permissions (Screen Recording, Accessibility) to the app's signature.
# An ad-hoc signature changes on every build, so permissions would be lost each rebuild.
# Instead, sign with a self-made "Macstream Local Signing" certificate that stays the same.
# It lives in a private keychain inside the project (.local/signing/) and is never added to
# your keychain search list. No Apple Developer account is involved.
#
# Tradeoff (accepted for a personal machine): the keychain's random password is stored
# next to it (mode 600) so builds need no prompt. Anyone already able to run code as you
# could read it and sign another program as Macstream, inheriting its permissions.
# The keychain is locked except while signing. Delete .local/signing/ (or run
# tools/mac/uninstall.sh) to revoke it.
SIGN_DIR="$ROOT/.local/signing"
KEYCHAIN="$SIGN_DIR/macstream-signing.keychain-db"
KEYCHAIN_PASS_FILE="$SIGN_DIR/keychain-password"
IDENTITY="Macstream Local Signing"
OPENSSL=/usr/bin/openssl   # macOS LibreSSL: its PKCS#12 output imports cleanly into keychains

ensure_signing_identity() {
  if [[ -f "$KEYCHAIN" && -f "$KEYCHAIN_PASS_FILE" ]] &&
     security unlock-keychain -p "$(cat "$KEYCHAIN_PASS_FILE")" "$KEYCHAIN" 2>/dev/null &&
     security find-identity -p codesigning "$KEYCHAIN" 2>/dev/null | grep -q "$IDENTITY"; then
    return 0   # keep the existing certificate: a new one would invalidate granted permissions
  fi
  # Remove any half-created keychain from an earlier failed attempt.
  [[ -f "$KEYCHAIN" ]] && security delete-keychain "$KEYCHAIN" 2>/dev/null

  echo "[sign] Creating a local code-signing certificate (one time)..."
  # Everything created below is private to this user from the start (no chmod-after-write gap).
  local old_umask
  old_umask="$(umask)"
  umask 077
  rm -rf "$SIGN_DIR"
  mkdir -m 700 -p "$SIGN_DIR"
  local tmp
  tmp="$(mktemp -d "$SIGN_DIR/tmp.XXXXXX")"

  "$OPENSSL" rand -hex 24 > "$KEYCHAIN_PASS_FILE"
  local kc_pass p12_pass
  kc_pass="$(cat "$KEYCHAIN_PASS_FILE")"
  p12_pass="$("$OPENSSL" rand -hex 16)"

  cat > "$tmp/cert.cnf" <<CNF
[req]
distinguished_name = dn
prompt = no
x509_extensions = ext
[dn]
CN = $IDENTITY
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
subjectKeyIdentifier = hash
CNF

  "$OPENSSL" req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -config "$tmp/cert.cnf" -keyout "$tmp/key.pem" -out "$tmp/cert.pem" 2>/dev/null
  "$OPENSSL" pkcs12 -export -inkey "$tmp/key.pem" -in "$tmp/cert.pem" \
    -name "$IDENTITY" -out "$tmp/identity.p12" -passout "pass:$p12_pass"

  # `security create-keychain` adds the new keychain to the user's search list; restore the
  # original list afterwards so nothing outside the project changes.
  local original_list=()
  while IFS= read -r line; do
    line="${line#"${line%%[![:space:]]*}"}"; line="${line%\"}"; line="${line#\"}"
    [[ -n "$line" ]] && original_list+=("$line")
  done < <(security list-keychains -d user)

  security create-keychain -p "$kc_pass" "$KEYCHAIN"
  chmod 600 "$KEYCHAIN"
  security list-keychains -d user -s "${original_list[@]}"
  security set-keychain-settings -l -u -t 300 "$KEYCHAIN"   # auto-lock after 5 min and on sleep
  security unlock-keychain -p "$kc_pass" "$KEYCHAIN"
  security import "$tmp/identity.p12" -k "$KEYCHAIN" -P "$p12_pass" -T /usr/bin/codesign >/dev/null
  # Lets codesign use the key without a GUI prompt on every build (the keychain stays locked
  # except while this script is signing).
  security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$kc_pass" "$KEYCHAIN" >/dev/null
  security lock-keychain "$KEYCHAIN"

  rm -rf "$tmp"
  umask "$old_umask"
  echo "[sign] Created certificate \"$IDENTITY\" in $KEYCHAIN"
}

sign_app() {
  if ensure_signing_identity; then
    security unlock-keychain -p "$(cat "$KEYCHAIN_PASS_FILE")" "$KEYCHAIN"
    trap 'security lock-keychain "$KEYCHAIN" 2>/dev/null' EXIT
    if codesign --force --keychain "$KEYCHAIN" --sign "$IDENTITY" \
         --identifier "$MACSTREAM_BUNDLE_ID" "$APP"; then
      security lock-keychain "$KEYCHAIN"
      return 0
    fi
    security lock-keychain "$KEYCHAIN" 2>/dev/null
  fi
  echo "[sign] WARNING: local certificate signing failed; falling back to ad-hoc signing." >&2
  echo "[sign] macOS will ask for permissions again after every rebuild." >&2
  codesign --force --sign - --identifier "$MACSTREAM_BUNDLE_ID" "$APP"
}

sign_app
codesign --verify --verbose=1 "$APP"
codesign --display --requirements - "$APP" 2>&1 | grep designated || true

# Tell Launchpad / Spotlight about it now rather than whenever they next look.
"$LSREGISTER" -f "$APP" >/dev/null 2>&1 || true

echo "[build] Done: $APP"
echo "[build] Start it from Launchpad (Macstream), with ./tools/mac/run-server.sh, or double-click it in Finder"
