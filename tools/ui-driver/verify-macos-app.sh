#!/usr/bin/env bash
# Independent post-build checks of an HCG-built Swift .app on a Mac (CI): signature, Info.plist, slices, launch.
# Usage: verify-macos-app.sh <path/to/Name.app> <screenshot.png> [expected-archs e.g. "arm64" or "x86_64 arm64"]
# Launching executes the built app: CI only does this for the pinned reference projects.
set -euo pipefail
app="$1"; shot="$2"; expected="${3:-}"
plist="$app/Contents/Info.plist"
exe_name="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$plist")"
exe="$app/Contents/MacOS/$exe_name"

echo "== $app"
plutil -lint "$plist"
plutil -p "$plist"
codesign --verify --strict --deep --verbose=2 "$app"
codesign -dv --verbose=2 "$app" 2>&1 | grep -E 'Identifier|Format|Signature|TeamIdentifier|Sealed' || true
spctl --assess --type execute -vv "$app" 2>&1 || echo "(Gatekeeper rejects ad-hoc signed apps from other Macs: expected, not notarized)"
archs="$(lipo -archs "$exe")"
echo "Architectures: $archs"
if [ -n "$expected" ]; then
  for a in $expected; do
    case " $archs " in *" $a "*) ;; *) echo "Missing slice $a"; exit 1 ;; esac
  done
fi
vtool -show-build "$exe" 2>/dev/null | grep -E 'platform|minos|sdk' || otool -l "$exe" | grep -A4 LC_BUILD_VERSION | head -8

echo "Launching $exe_name (open -n)"
open -n "$app"
for _ in $(seq 1 30); do
  pgrep -f "$exe" >/dev/null && break
  sleep 1
done
sleep 8
if ! pgrep -f "$exe" >/dev/null; then
  echo "The app is not running 8 seconds after launch."
  log show --last 2m --style compact --predicate "process == \"$exe_name\" OR eventMessage CONTAINS \"$exe_name\"" 2>/dev/null | tail -40 || true
  ls -t ~/Library/Logs/DiagnosticReports 2>/dev/null | head -5 || true
  exit 1
fi
echo "Running: $(pgrep -f "$exe" | tr '\n' ' ')"
osascript -e "tell application id \"$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$plist")\" to activate" 2>/dev/null || true
sleep 2
screencapture -x "$shot"
echo "Screenshot: $shot"
pkill -f "$exe" || true
sleep 1
echo "LAUNCH OK: $app"
