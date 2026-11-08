#!/bin/bash
# Packages Distil as dist/Distil.app (release build, ad-hoc signed, for local use).
# Packaging only: it compiles the Distil product and its library, never the test targets, and runs no
# tests (use scripts/test.sh). Works from any directory. Does not notarize, publish, upload or install.
#
# Usage: bash distil/scripts/build-macos.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PRODUCT="Distil"                       # executable product in Package.swift (the package is named TokenLab)
BUNDLE_ID="dev.tokenlab.prototype"     # unchanged since Token Lab, so preferences and data carry over
VERSION="0.5.0"
BUILD_NUMBER="5"
MIN_MACOS="14.0"

if [[ "${SKIP_TESTS:-}" != "" ]]; then
  echo "Note: SKIP_TESTS is no longer needed; this script never runs tests. Use scripts/test.sh for tests."
fi
if [[ "$(uname -s)" != Darwin ]]; then
  echo "Packaging Distil.app needs macOS (SwiftUI/AppKit). On Linux, scripts/test.sh runs the core tests." >&2
  exit 1
fi
bash "$ROOT/scripts/check-prereqs.sh"

echo "Compiling the $PRODUCT product (release)…"
swift build -c release --product "$PRODUCT"
binary_dir="$(swift build -c release --show-bin-path)"
[[ -x "$binary_dir/$PRODUCT" ]] || { echo "Packaging failed: $binary_dir/$PRODUCT was not produced." >&2; exit 1; }

# Identify this build so the sidebar's Build card and Settings › About show which copy is running.
commit="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
if [[ -n "$(git -C "$ROOT" status --porcelain -- . 2>/dev/null)" ]]; then commit="$commit+uncommitted"; fi
built_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# Only this build's own output, dist/Distil.app, is replaced. Other copies (for example
# dist/Token Lab.app from builds before 0.4) and all user data are left untouched.
app="$ROOT/dist/$PRODUCT.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$binary_dir/$PRODUCT" "$app/Contents/MacOS/$PRODUCT"
printf 'APPL????' > "$app/Contents/PkgInfo"
# SwiftPM resource bundles, if a target ever declares resources (none do today), go in Contents/Resources.
for bundle in "$binary_dir"/*.bundle; do
  [[ -e "$bundle" ]] && cp -R "$bundle" "$app/Contents/Resources/"
done
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>Distil</string>
<key>CFBundleDisplayName</key><string>Distil</string>
<key>CFBundleExecutable</key><string>${PRODUCT}</string>
<key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
<key>CFBundleDevelopmentRegion</key><string>en</string>
<key>CFBundleShortVersionString</key><string>${VERSION}</string>
<key>CFBundleVersion</key><string>${BUILD_NUMBER}</string>
<key>DistilGitCommit</key><string>${commit}</string>
<key>DistilBuildDate</key><string>${built_at}</string>
<key>LSMinimumSystemVersion</key><string>${MIN_MACOS}</string>
<key>NSHighResolutionCapable</key><true/>
<key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict></plist>
PLIST
codesign --force --sign - --timestamp=none "$app"

# Check the bundle before reporting success.
plutil -lint "$app/Contents/Info.plist" >/dev/null
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")" == "$PRODUCT" ]]
[[ -x "$app/Contents/MacOS/$PRODUCT" ]]
codesign --verify --strict --verbose=1 "$app"

printf 'Packaged: %s\n  version %s (%s), commit %s, built %s, bundle id %s\n' "$app" "$VERSION" "$BUILD_NUMBER" "$commit" "$built_at" "$BUNDLE_ID"
echo "Tests were not run by this command (bash $ROOT/scripts/test.sh runs them)."
printf 'Open exactly this copy with:\n  open "%s"\n' "$app"
