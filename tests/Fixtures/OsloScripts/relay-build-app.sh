#!/usr/bin/env bash
# macOS only. Packages Relay as build/Relay.app (release build, ad-hoc signed, for local use), with the
# microphone usage description macOS requires for push-to-talk. Packaging only: it compiles the Relay
# product and its libraries, never the test targets, and runs no tests (use scripts/test.sh).
# Works from any directory. Nothing is uploaded.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PRODUCT="Relay"
BUNDLE_ID="com.oslosprint.relay"
VERSION="0.2.0"
BUILD_NUMBER="2"
MIN_MACOS="14.0"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "build-app.sh needs macOS (SwiftUI/AppKit). On Linux use scripts/test.sh." >&2
  exit 1
fi
bash "$ROOT/scripts/check-prereqs.sh"

echo "Compiling the $PRODUCT product (release)…"
swift build -c release --product "$PRODUCT"
BIN_DIR="$(swift build -c release --show-bin-path)"
[[ -x "$BIN_DIR/$PRODUCT" ]] || { echo "Packaging failed: $BIN_DIR/$PRODUCT was not produced." >&2; exit 1; }
APP="$ROOT/build/$PRODUCT.app"
# Identify this build so the app (sidebar Build card, Settings › About this build) shows which copy runs.
COMMIT="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
if [[ -n "$(git -C "$ROOT" status --porcelain -- . 2>/dev/null)" ]]; then COMMIT="$COMMIT+uncommitted"; fi
BUILT_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
# Only build/Relay.app is replaced; other copies and all user data (preferences, Keychain item) are untouched.
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$PRODUCT" "$APP/Contents/MacOS/$PRODUCT"
printf 'APPL????' > "$APP/Contents/PkgInfo"
# SwiftPM resource bundles, if a target ever declares resources (none do today), go in Contents/Resources.
for bundle in "$BIN_DIR"/*.bundle; do
  [[ -e "$bundle" ]] && cp -R "$bundle" "$APP/Contents/Resources/"
done
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Relay</string>
  <key>CFBundleDisplayName</key><string>Relay</string>
  <key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
  <key>CFBundleExecutable</key><string>${PRODUCT}</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${BUILD_NUMBER}</string>
  <key>RelayGitCommit</key><string>${COMMIT}</string>
  <key>RelayBuildDate</key><string>${BUILT_AT}</string>
  <key>LSMinimumSystemVersion</key><string>${MIN_MACOS}</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key>
  <string>Relay records only while you hold the push-to-talk button, and sends that recording to ElevenLabs for transcription.</string>
</dict>
</plist>
PLIST
codesign --force --sign - --timestamp=none "$APP"

# Check the bundle before reporting success.
plutil -lint "$APP/Contents/Info.plist" >/dev/null
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist")" == "$PRODUCT" ]]
/usr/libexec/PlistBuddy -c 'Print :NSMicrophoneUsageDescription' "$APP/Contents/Info.plist" >/dev/null
[[ -x "$APP/Contents/MacOS/$PRODUCT" ]]
codesign --verify --strict --verbose=1 "$APP"

echo "Packaged: $APP"
echo "  version $VERSION ($BUILD_NUMBER), commit $COMMIT, built $BUILT_AT, bundle id $BUNDLE_ID"
echo "Tests were not run by this command (bash $ROOT/scripts/test.sh runs them)."
echo "Open exactly this copy with:"
echo "  open \"$APP\"     (or: bash $ROOT/scripts/run-latest.sh)"
