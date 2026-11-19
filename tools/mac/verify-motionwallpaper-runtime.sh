#!/bin/bash
# RUNTIME acceptance for a MotionWallpaper.app that HCG Executable Maker built (separate from build verification).
# Run it only on a disposable Mac session (the CI runner VM): it writes MotionWallpaper's own data folder
# (~/Library/Application Support/MotionWallpaper) and shows a video on that session's desktop.
#
# It never: removes quarantine, changes Gatekeeper or other security settings, sets the native wallpaper, touches the
# lock screen or the Aerial/screen-saver configuration, or registers a login item. Lock-screen replacement and
# "Launch at login" stay explicit actions a person takes in the app.
#
# Steps (each reported PASS / FAIL / NOT VERIFIED):
#   1. launch the main UI from the packaged ZIP; its window appears;
#   2. import a generated sample MP4 through the app's own File ▸ Add Video… dialog (UI scripting; needs the
#      Accessibility permission for this shell — NOT VERIFIED when the session does not grant it);
#   3. desktop playback: relaunch; the app starts its `--desktop-agent` renderer for the imported video; the renderer
#      owns desktop-level windows covering the screen, and two screenshots 1.5 s apart differ and show the sample.
#
# Usage: verify-motionwallpaper-runtime.sh <MotionWallpaper .app.zip or .app> <work dir>
set -uo pipefail
INPUT="$1"; WORK="$2"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$WORK/screens"
REPORT="$WORK/runtime-report.md"
DATA="$HOME/Library/Application Support/MotionWallpaper"
results=()
record() { results+=("| $1 | $2 | $3 |"); echo "$2: $1 — $3"; }

if [ "$(uname -s)" != Darwin ]; then echo "NOT VERIFIED: runtime checks need macOS (this is $(uname -s))"; exit 3; fi
case "$INPUT" in
  *.zip) mkdir -p "$WORK/unpacked"; ditto -x -k "$INPUT" "$WORK/unpacked"; APP="$(find "$WORK/unpacked" -maxdepth 2 -name "*.app" -type d | head -1)" ;;
  *) APP="$INPUT" ;;
esac
EXE="$APP/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist")"
BUNDLE_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP/Contents/Info.plist")"
echo "App: $APP ($BUNDLE_ID)"; sw_vers; echo "User: $(id -un) (disposable CI session)"
if xattr -p com.apple.quarantine "$APP" >/dev/null 2>&1; then
  echo "note: the app carries a quarantine attribute; it is left in place (Gatekeeper decides)."
fi

for t in window-id window-report compare-frames make-sample-video; do
  [ -x "$WORK/$t" ] || swiftc -O "$HERE/$t.swift" -o "$WORK/$t" || { echo "cannot build helper $t"; exit 1; }
done
"$WORK/make-sample-video" "$WORK/HCG Sample Wallpaper.mp4" 6 1280 720

stop_all() {
  pkill -f "$EXE --desktop-agent" 2>/dev/null; pkill -f "$EXE" 2>/dev/null; sleep 1
  pkill -9 -f "$EXE" 2>/dev/null; true
}
main_pid() { pgrep -f "^$EXE\$" | head -1; }
agent_pid() { pgrep -f "$EXE --desktop-agent" | head -1; }
trap stop_all EXIT
stop_all
# Start from MotionWallpaper's defaults in this disposable session (its own data folder only).
rm -rf "$DATA"

# ── 1. Launch ────────────────────────────────────────────────────────────────────────────────────────────────────────
open -n "$APP"
win=""; for _ in $(seq 1 40); do pid="$(main_pid)"; [ -n "$pid" ] && win="$("$WORK/window-id" "$pid" 2>/dev/null)" && [ -n "$win" ] && break; sleep 0.5; done
if [ -n "$win" ]; then
  screencapture -x -o -l"$win" "$WORK/screens/1-launch.png" 2>/dev/null
  record "Launch: main window appears" PASS "process $pid, window $win; screenshot screens/1-launch.png"
else
  record "Launch: main window appears" FAIL "no window within 20 s (process: ${pid:-none})"
  log show --last 1m --predicate "process == \"$(basename "$EXE")\"" --style compact 2>/dev/null | tail -20
fi

# ── 2. Import through the app's own Add Video… dialog ────────────────────────────────────────────────────────────────
imported=no
if [ -n "${pid:-}" ]; then
  osascript > "$WORK/import-ui.log" 2>&1 <<EOF
tell application "System Events"
  set frontmost of (first process whose unix id is $pid) to true
  delay 1
  keystroke "o" using command down
  delay 2
  keystroke "g" using {command down, shift down}
  delay 1
  keystroke "$WORK/HCG Sample Wallpaper.mp4"
  delay 1
  key code 36
  delay 2
  key code 36
end tell
EOF
  ui=$?
  for _ in $(seq 1 20); do [ -f "$DATA/library.json" ] && grep -q "HCG Sample Wallpaper" "$DATA/library.json" && imported=yes && break; sleep 0.5; done
  if [ "$imported" = yes ]; then
    win="$("$WORK/window-id" "$pid" 2>/dev/null)"; [ -n "$win" ] && screencapture -x -o -l"$win" "$WORK/screens/2-imported.png" 2>/dev/null
    record "Import a sample MP4 (File ▸ Add Video…)" PASS "library.json lists \"HCG Sample Wallpaper\"; copy in $(ls "$DATA/Videos" | head -1)"
  elif [ $ui -ne 0 ] && grep -qiE "assistive|not allowed|1002|25211|1719" "$WORK/import-ui.log"; then
    record "Import a sample MP4 (File ▸ Add Video…)" "NOT VERIFIED" "this session does not allow UI scripting (Accessibility): $(tr '\n' ' ' < "$WORK/import-ui.log" | cut -c1-160)"
  else
    record "Import a sample MP4 (File ▸ Add Video…)" FAIL "the video was not added (osascript exit $ui: $(tr '\n' ' ' < "$WORK/import-ui.log" | cut -c1-160))"
  fi
fi
stop_all

# ── 3. Desktop playback ──────────────────────────────────────────────────────────────────────────────────────────────
how="imported through the app"
if [ "$imported" != yes ]; then
  # Same files WallpaperStore.addVideo writes (Videos/<uuid>.mp4, library.json, and the default desktop selection), so
  # playback can still be checked; reported as such.
  how="placed in the library the way the app's import stores it (UI import not verified)"
  id="$(uuidgen)"; mkdir -p "$DATA/Videos" "$DATA/Pictures"
  cp "$WORK/HCG Sample Wallpaper.mp4" "$DATA/Videos/$id.mp4"
  now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '[{"dateAdded":"%s","fileName":"%s.mp4","id":"%s","title":"HCG Sample Wallpaper"}]\n' "$now" "$id" "$id" > "$DATA/library.json"
  printf '{"aerialDesktopEnabled":false,"desktopEnabled":true,"desktopVideoID":"%s","fillScreen":true,"launchAtLogin":false,"muted":true,"startDesktopOnLaunch":true}\n' "$id" > "$DATA/settings.json"
fi
# The app's own launch path: "Start desktop wallpaper when the app launches" (default on) starts the renderer.
open -n "$APP"
apid=""; for _ in $(seq 1 40); do apid="$(agent_pid)"; [ -n "$apid" ] && break; sleep 0.5; done
if [ -z "$apid" ]; then
  record "Desktop playback" FAIL "the app did not start its --desktop-agent renderer (video $how)"
else
  sleep 3
  "$WORK/window-report" "$apid" > "$WORK/agent-windows.jsonl"
  screen="$(system_profiler SPDisplaysDataType 2>/dev/null | awk -F': ' '/Resolution/{print $2; exit}')"
  desktop_windows="$(grep -c '"desktopLayer":true' "$WORK/agent-windows.jsonl")"
  # Hide the main window so the desktop is visible; the renderer keeps running after the UI quits (by design).
  mp="$(main_pid)"; [ -n "$mp" ] && kill "$mp"; sleep 2
  screencapture -x "$WORK/screens/3-desktop-a.png"; sleep 1.5; screencapture -x "$WORK/screens/3-desktop-b.png"
  cmp="$("$WORK/compare-frames" "$WORK/screens/3-desktop-a.png" "$WORK/screens/3-desktop-b.png")"
  echo "frames: $cmp"
  changed="$(echo "$cmp" | python3 -c 'import json,sys;print(json.load(sys.stdin)["changedFraction"])')"
  sample="$(echo "$cmp" | python3 -c 'import json,sys;d=json.load(sys.stdin);print(min(d["sampleColourFractionA"],d["sampleColourFractionB"]))')"
  if [ "$desktop_windows" -ge 1 ] && python3 -c "import sys; sys.exit(0 if $changed > 0.2 and $sample > 0.4 else 1)"; then
    record "Desktop playback" PASS "renderer $apid owns $desktop_windows desktop-level window(s) (screen $screen); $(printf '%.0f' "$(python3 -c "print($changed*100)")")% of the screen changed in 1.5 s and $(printf '%.0f' "$(python3 -c "print($sample*100)")")% shows the sample (video $how)"
  else
    record "Desktop playback" FAIL "desktop-level windows: $desktop_windows; changed $changed, sample colours $sample (video $how)"
    cat "$WORK/agent-windows.jsonl"
  fi
fi
# Native wallpaper, lock screen and login items were not touched; confirm the login item is off.
grep -q '"launchAtLogin" *: *false' "$DATA/settings.json" && record "Login item" PASS "not enabled (launchAtLogin false; never changed by this test)" \
  || record "Login item" FAIL "settings.json does not say launchAtLogin false"
record "Lock screen / native wallpaper" "NOT TESTED" "explicit user actions; this test never applies them"
stop_all

{
  echo "# MotionWallpaper runtime verification"
  echo; echo "Host: $(sw_vers -productName) $(sw_vers -productVersion) ($(uname -m)), user $(id -un) on a disposable CI session"
  echo "App: \`$APP\` ($BUNDLE_ID)"; echo
  echo "| Check | Result | Detail |"; echo "|---|---|---|"
  printf '%s\n' "${results[@]}"
} > "$REPORT"
cat "$REPORT"
printf '%s\n' "${results[@]}" | grep -q "| FAIL |" && exit 1
exit 0
