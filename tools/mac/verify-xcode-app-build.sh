#!/bin/bash
# BUILD verification of what HCG Executable Maker produced for a native macOS Xcode app, with Apple's own tools and
# independently of HCG's code: the output folder's .app, ZIP and DMG, the checksums, the licence files and the report.
# Read-only: nothing is launched, signed, unquarantined or installed.
#
# Usage: verify-xcode-app-build.sh <output folder> <expected bundle ID> <expected version> <expected minimum macOS> <arm64|universal|x86_64>
set -uo pipefail
OUT="$1"; BUNDLE_ID="$2"; VERSION="$3"; MIN_OS="$4"; ARCH="$5"
fails=0
pass() { echo "PASS: $*"; }
fail() { echo "FAIL: $*"; fails=$((fails + 1)); }

APP="$(find "$OUT" -maxdepth 1 -name "*.app" -type d | head -1)"
ZIP="$(find "$OUT" -maxdepth 1 -name "*.zip" | head -1)"
DMG="$(find "$OUT" -maxdepth 1 -name "*.dmg" | head -1)"
[ -n "$APP" ] || { echo "FAIL: no .app in $OUT"; exit 1; }
echo "Output folder: $OUT"; ls -la "$OUT"
PL="$APP/Contents/Info.plist"
EXE="$APP/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$PL")"

# Identity (the project's, not rebranded)
id="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PL")"
ver="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$PL")"
build="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$PL")"
min="$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$PL")"
name="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleName' "$PL" 2>/dev/null)"
[ "$id" = "$BUNDLE_ID" ] && pass "bundle ID $id" || fail "bundle ID $id (expected $BUNDLE_ID)"
[ "$ver" = "$VERSION" ] && pass "version $ver (build $build), name \"$name\"" || fail "version $ver (expected $VERSION)"
[ "$min" = "$MIN_OS" ] && pass "LSMinimumSystemVersion $min" || fail "LSMinimumSystemVersion $min (expected $MIN_OS)"
case "$name$id" in *HCG*|*hakmi*) fail "the app carries HCG branding ($name / $id)";; *) pass "no HCG branding in the app's name or identity";; esac

# Architectures and deployment target (lipo, vtool)
archs="$(lipo -archs "$EXE")"
case "$ARCH" in
  universal) want="arm64 x86_64" ;; x86_64) want="x86_64" ;; *) want="arm64" ;;
esac
[ "$(echo "$archs" | tr ' ' '\n' | sort | xargs)" = "$(echo "$want" | tr ' ' '\n' | sort | xargs)" ] && pass "lipo -archs: $archs" || fail "lipo -archs: $archs (expected $want)"
for a in $archs; do
  minos="$(vtool -arch "$a" -show-build "$EXE" 2>/dev/null | awk '/minos/{print $2; exit}')"
  [ "$minos" = "$MIN_OS" ] && pass "vtool $a minos $minos" || fail "vtool $a minos $minos (expected $MIN_OS)"
done

# Icon and resources (what Xcode built)
icon="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconName' "$PL" 2>/dev/null)"
[ -n "$icon" ] && [ -f "$APP/Contents/Resources/Assets.car" ] && pass "icon $icon in Assets.car" || fail "no app icon (CFBundleIconName '$icon', Assets.car $( [ -f "$APP/Contents/Resources/Assets.car" ] && echo present || echo missing))"
[ -f "$APP/Contents/Resources/AppIcon.icns" ] && pass "AppIcon.icns present" || echo "note: no AppIcon.icns (icon only in Assets.car)"

# Signature (classified, never changed) and Gatekeeper's verdict (reported, never changed)
codesign --verify --deep --strict --verbose=2 "$APP" 2>&1 | tail -2 && pass "codesign --verify --deep --strict" || fail "codesign --verify"
sig="$(codesign -dv --verbose=4 "$APP" 2>&1)"
echo "$sig" | grep -E "^(Identifier|Signature|Authority|TeamIdentifier|CodeDirectory)" | sed 's/^/  /'
if echo "$sig" | grep -q "^Signature=adhoc"; then pass "signature: ad hoc (no certificate, no team) — not Developer ID, not notarized"
elif echo "$sig" | grep -q "^Authority=Developer ID Application"; then pass "signature: Developer ID (not notarized)"
else echo "note: signature: $(echo "$sig" | grep -m1 '^Authority=' || echo unknown)"; fi
echo "Gatekeeper (spctl --assess --type execute): $(spctl --assess --type execute -vv "$APP" 2>&1 | tr '\n' ' ')"
xattr -p com.apple.quarantine "$APP" >/dev/null 2>&1 && echo "note: the app has a quarantine attribute (left as is)" || pass "no quarantine attribute was added (and none was removed: the build never downloads the app)"

# ZIP: expands to the same app, still verifies
if [ -n "$ZIP" ]; then
  x="$(mktemp -d)"; ditto -x -k "$ZIP" "$x"
  zapp="$x/$(basename "$APP")"
  [ -d "$zapp" ] && pass "$(basename "$ZIP") expands to $(basename "$APP")" || fail "$(basename "$ZIP") does not contain $(basename "$APP")"
  [ "$(ls "$x")" = "$(basename "$APP")" ] && pass "ZIP holds only the app bundle" || fail "ZIP holds: $(ls "$x" | xargs)"
  codesign --verify --deep --strict "$zapp" 2>/dev/null && pass "expanded app verifies" || fail "expanded app's signature does not verify"
  diff -rq "$APP" "$zapp" >/dev/null && pass "expanded app is identical" || fail "expanded app differs: $(diff -rq "$APP" "$zapp" | head -3)"
  rm -rf "$x"
else fail "no ZIP"; fi

# DMG: app + Applications shortcut + licence files
if [ -n "$DMG" ]; then
  hdiutil verify "$DMG" >/dev/null 2>&1 && pass "hdiutil verify $(basename "$DMG")" || fail "hdiutil verify"
  m="$(mktemp -d)"
  if hdiutil attach -readonly -nobrowse -noautoopen -mountpoint "$m" "$DMG" >/dev/null; then
    echo "DMG contents: $(ls "$m" | xargs)"
    [ -d "$m/$(basename "$APP")" ] && pass "DMG holds $(basename "$APP")" || fail "DMG lacks the app"
    [ "$(readlink "$m/Applications")" = "/Applications" ] && pass "DMG has an Applications shortcut" || fail "DMG lacks the Applications shortcut"
    for l in "$OUT"/LICENSE*; do [ -f "$l" ] && { cmp -s "$l" "$m/$(basename "$l")" && pass "DMG holds $(basename "$l") unchanged" || fail "DMG lacks $(basename "$l")"; }; done
    diff -rq "$APP" "$m/$(basename "$APP")" >/dev/null && pass "the app in the DMG is identical" || fail "the app in the DMG differs"
    hdiutil detach "$m" -force >/dev/null
  else fail "hdiutil attach failed"; fi
fi

# Checksums, licence, report
(cd "$OUT" && shasum -a 256 -c SHA256SUMS.txt) && pass "SHA256SUMS.txt matches" || fail "SHA256SUMS.txt does not match"
ls "$OUT"/LICENSE* >/dev/null 2>&1 && pass "licence file(s) next to the app: $(cd "$OUT" && ls LICENSE* | xargs)" || echo "note: no licence file next to the app"
for f in BUILD-REPORT.md build-report.json; do [ -s "$OUT/$f" ] && pass "$f written" || fail "$f missing"; done
grep -q "does not make it an HCG product" "$OUT/BUILD-REPORT.md" && pass "report states the app keeps its own identity" || fail "report lacks the identity statement"

echo; [ $fails -eq 0 ] && echo "BUILD VERIFICATION: PASSED" || echo "BUILD VERIFICATION: $fails FAILED"
exit $fails
