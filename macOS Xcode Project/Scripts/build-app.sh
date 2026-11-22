#!/bin/bash
# Builds the real HCG Executable Maker (.NET / Avalonia) as a macOS app bundle for Xcode.
#
# Called by the "HCG Executable Maker (.NET)" External Build System target with Xcode's build settings in the
# environment (ACTION, CONFIGURATION, BUILT_PRODUCTS_DIR, …). It does not contain its own build logic: it runs the
# repository's self-packaging tool (tools/self-package/self-package.cs, the same pipeline CI uses for releases:
# import → inspection → dotnet restore/build/publish for osx-arm64 → .app bundle with the .NET runtime, resources and
# icon → verification → SHA-256), then validates the bundle and places it at
#   $BUILT_PRODUCTS_DIR/HCG Executable Maker.app
# where the "Run HCG Executable Maker" launcher target starts it.
#
# Lines starting with "error:" / "warning:" appear in Xcode's Issue navigator. The full log is written to
#   $BUILT_PRODUCTS_DIR/HCGExecutableMaker-build-logs/<configuration>.log
# Nothing here publishes, uploads, tags or notarizes. Release signing is optional (HCG_CODESIGN_IDENTITY).
#
# Every dotnet command runs in a clean environment (clean_env below): Xcode passes all its build settings as
# environment variables, and MSBuild reads environment variables as properties (case-insensitively), so e.g. Xcode's
# TARGETNAME would become MSBuild's TargetName and break the build.
set -uo pipefail

ACTION="${ACTION:-build}"
CONFIGURATION="${CONFIGURATION:-Debug}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"
PRODUCTS="${BUILT_PRODUCTS_DIR:-$REPO/../HCGExecutableMaker-xcode-build/$CONFIGURATION}"
APP_NAME="HCG Executable Maker"
APP="$PRODUCTS/$APP_NAME.app"
EXECUTABLE="HCGExecutableMaker"
BUNDLE_ID="com.hakmicapital.executablemaker"   # the identifier the app's packaging has always used (tools/self-package)
LOGS="$PRODUCTS/HCGExecutableMaker-build-logs"
STAMP="$PRODUCTS/HCGExecutableMaker-build-logs/$CONFIGURATION.stamp"

err()  { echo "error: $*" >&2; }
warn() { echo "warning: $*" >&2; }
note() { echo "note: $*"; }

inside() { case "$1/" in "$2"/*) return 0;; *) return 1;; esac; }

if [ "$ACTION" = "clean" ]; then
    rm -rf "$APP" "$LOGS" "$PRODUCTS/HCGExecutableMaker-stage-$CONFIGURATION"
    note "Removed $APP_NAME.app and its build logs."
    exit 0
fi

# ── 1. Host and locations ────────────────────────────────────────────────────────────────────────────────────────
if [ "$(uname -s)" != "Darwin" ]; then err "This project builds the macOS app and must run on macOS."; exit 1; fi
if [ "$(uname -m)" != "arm64" ]; then warn "This Mac is $(uname -m); the app is built for Apple Silicon (osx-arm64) and needs Rosetta-free arm64 to run."; fi
if inside "$PRODUCTS" "$REPO"; then
    err "Xcode's build folder ($PRODUCTS) is inside the repository, which the build imports. Use the default Derived Data location (Xcode › Settings › Locations › Derived Data: Default, and File › Project Settings › Derived Data: Default Location)."
    exit 1
fi
mkdir -p "$PRODUCTS" "$LOGS"
LOG="$LOGS/$CONFIGURATION.log"

main() {
echo "HCG Executable Maker — Xcode $ACTION, $CONFIGURATION, $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "Repository: $REPO"
echo "Products:   $PRODUCTS"

# ── 2. .NET SDK (Xcode does not inherit your shell's PATH) ───────────────────────────────────────────────────────
DOTNET=""
for c in "${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}" "$(command -v dotnet 2>/dev/null)" /usr/local/share/dotnet/dotnet "$HOME/.dotnet/dotnet" /opt/homebrew/bin/dotnet /usr/local/bin/dotnet; do
    if [ -n "$c" ] && [ -x "$c" ]; then DOTNET="$c"; break; fi
done
if [ -z "$DOTNET" ]; then
    err ".NET SDK not found. Install the .NET 10 SDK (Apple Silicon, arm64) from https://dotnet.microsoft.com/download/dotnet/10.0 — it installs to /usr/local/share/dotnet — then build again."
    exit 1
fi
DOTNET_HOME_DIR="$(dirname "$DOTNET")"
# Only what dotnet needs: no Xcode build settings (see the note at the top). Proxy and NuGet settings pass through.
clean_env=(env -i HOME="$HOME" USER="${USER:-}" LOGNAME="${LOGNAME:-}" TMPDIR="${TMPDIR:-/tmp}" LANG="${LANG:-en_US.UTF-8}"
    PATH="$DOTNET_HOME_DIR:/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin:/usr/local/bin" DOTNET_ROOT="$DOTNET_HOME_DIR"
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1)
for v in HTTPS_PROXY HTTP_PROXY NO_PROXY https_proxy http_proxy no_proxy NUGET_PACKAGES NUGET_HTTP_CACHE_PATH SSL_CERT_FILE HCG_EXECUTABLE_MAKER_HOME; do
    if [ -n "${!v:-}" ]; then clean_env+=("$v=${!v}"); fi
done
REQUIRED_SDK="$(sed -n 's/.*"version": *"\([0-9.]*\)".*/\1/p' "$REPO/global.json" | head -1)"
if ! (cd "$REPO" && "${clean_env[@]}" "$DOTNET" --version >/dev/null 2>&1); then
    err "The .NET SDK at $DOTNET does not satisfy global.json (needs $REQUIRED_SDK or a later 10.0 feature band). Installed: $("${clean_env[@]}" "$DOTNET" --list-sdks 2>/dev/null | tr '\n' ' ')"
    exit 1
fi
SDK_VERSION="$(cd "$REPO" && "${clean_env[@]}" "$DOTNET" --version)"
echo ".NET SDK:   $SDK_VERSION ($DOTNET; global.json asks for $REQUIRED_SDK, rollForward latestFeature)"
# No workloads are needed: the desktop app is a plain net10.0 Avalonia app (iOS/Android workloads are only for the
# projects Maker builds, and Maker's Toolchains page reports them).

# ── 3. Version: one source of truth (Directory.Build.props) ──────────────────────────────────────────────────────
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$REPO/Directory.Build.props" | head -1)"
if [ -z "$VERSION" ]; then err "No <Version> in Directory.Build.props."; exit 1; fi
COMMIT="$(git -C "$REPO" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"
DIRTY="$(git -C "$REPO" status --porcelain --untracked-files=no 2>/dev/null | head -c1)"
echo "Version:    $VERSION (Directory.Build.props), commit $COMMIT${DIRTY:++uncommitted}"

# ── 4. Up to date? (pressing Run again without changes does not rebuild) ────────────────────────────────────────
FINGERPRINT="$CONFIGURATION $VERSION $SDK_VERSION $(git -C "$REPO" rev-parse HEAD 2>/dev/null) $( (git -C "$REPO" diff HEAD; git -C "$REPO" ls-files --others --exclude-standard -s 2>/dev/null) | shasum -a 256 | cut -c1-64)"
if [ -x "$APP/Contents/MacOS/$EXECUTABLE" ] && [ -f "$STAMP" ] && [ "$(cat "$STAMP")" = "$FINGERPRINT" ]; then
    note "$APP_NAME.app is up to date ($CONFIGURATION, $VERSION, $COMMIT). Clean the build folder (⇧⌘K) to force a rebuild."
    return 0
fi

# ── 5. Build and package with the repository's own pipeline ──────────────────────────────────────────────────────
STAGE="$PRODUCTS/HCGExecutableMaker-stage-$CONFIGURATION"
rm -rf "$STAGE"; mkdir -p "$STAGE"
ARGS=(run --no-cache "$REPO/tools/self-package/self-package.cs" -- "$REPO" "$STAGE/out" --targets osx-arm64 --version "$VERSION" --configuration "$CONFIGURATION")
if [ "$CONFIGURATION" = "Release" ]; then ARGS+=(--dist "$STAGE/dist"); else ARGS+=(--app-only); fi
echo "\$ dotnet ${ARGS[*]}"
if ! (cd "$REPO" && "${clean_env[@]}" "$DOTNET" "${ARGS[@]}"); then
    err "The .NET build or packaging failed. The first error above is the cause; the full log is $LOG."
    exit 1
fi
BUILT="$(find "$STAGE/out" -maxdepth 3 -name "$APP_NAME.app" -type d | head -1)"
if [ -z "$BUILT" ]; then err "The packaging step reported success but produced no $APP_NAME.app under $STAGE/out."; exit 1; fi
rm -rf "$APP"
/usr/bin/ditto "$BUILT" "$APP" || { err "Could not copy the app bundle to $APP."; exit 1; }

# ── 6. Validate the bundle ───────────────────────────────────────────────────────────────────────────────────────
fail=0
check() { if eval "$2"; then echo "  ✓ $1"; else err "Bundle validation: $1"; fail=1; fi; }
PLIST="$APP/Contents/Info.plist"
echo "Validating $APP"
check "Info.plist is valid" "plutil -lint '$PLIST' >/dev/null"
check "CFBundleIdentifier is $BUNDLE_ID" "[ \"\$(plutil -extract CFBundleIdentifier raw '$PLIST')\" = '$BUNDLE_ID' ]"
check "CFBundleShortVersionString is $VERSION" "[ \"\$(plutil -extract CFBundleShortVersionString raw '$PLIST')\" = '$VERSION' ]"
check "CFBundleExecutable is $EXECUTABLE" "[ \"\$(plutil -extract CFBundleExecutable raw '$PLIST')\" = '$EXECUTABLE' ]"
check "the executable exists and is executable" "[ -x '$APP/Contents/MacOS/$EXECUTABLE' ]"
check "the executable is arm64" "lipo -archs '$APP/Contents/MacOS/$EXECUTABLE' 2>/dev/null | grep -qw arm64"
check "the .NET runtime is bundled (self-contained)" "[ -f '$APP/Contents/MacOS/libcoreclr.dylib' ]"
check "the app icon is in Resources" "ICON=\$(plutil -extract CFBundleIconFile raw '$PLIST' 2>/dev/null) && ls '$APP/Contents/Resources/'\"\${ICON%.icns}\".icns >/dev/null 2>&1"
# The app host carries the .NET SDK's ad-hoc signature (Apple Silicon refuses unsigned code); the bundle as a whole is
# not signed unless a Release build is signed below with HCG_CODESIGN_IDENTITY. codesign treats a bundle's main
# executable as the bundle, so the executable's own signature is verified on a standalone copy.
SIGCHECK="$(mktemp -d)"; cp "$APP/Contents/MacOS/$EXECUTABLE" "$SIGCHECK/$EXECUTABLE"
check "the app host carries a valid code signature (ad hoc)" "codesign --verify '$SIGCHECK/$EXECUTABLE' 2>/dev/null"
rm -rf "$SIGCHECK"
IDENTITY="$("${clean_env[@]}" "$APP/Contents/MacOS/$EXECUTABLE" --version 2>/dev/null | head -1)"
check "the app starts and reports $VERSION ($IDENTITY)" "echo '$IDENTITY' | grep -q 'HCG Executable Maker $VERSION'"
if [ $fail -ne 0 ]; then err "The app bundle is not valid; see the lines above and $LOG."; exit 1; fi

# ── 7. Release: checksum, optional signing (never notarized or published here) ──────────────────────────────────
if [ "$CONFIGURATION" = "Release" ]; then
    if [ -n "${HCG_CODESIGN_IDENTITY:-}" ]; then
        echo "Signing with \"$HCG_CODESIGN_IDENTITY\" (hardened runtime)…"
        codesign --force --deep --options runtime --timestamp --sign "$HCG_CODESIGN_IDENTITY" "$APP" || { err "codesign failed with identity \"$HCG_CODESIGN_IDENTITY\"."; exit 1; }
        codesign --verify --deep --strict "$APP" || { err "The signed app does not verify."; exit 1; }
    else
        note "Release is ad hoc signed (set HCG_CODESIGN_IDENTITY to a Developer ID Application identity to sign it). Not notarized."
    fi
    ZIP="$PRODUCTS/HCG-Executable-Maker-$VERSION-osx-arm64.app.zip"
    rm -f "$ZIP"
    /usr/bin/ditto -c -k --keepParent "$APP" "$ZIP"
    (cd "$PRODUCTS" && shasum -a 256 "$(basename "$ZIP")" > "$ZIP.sha256")
    echo "Release archive: $ZIP"
    echo "SHA-256:         $(cut -c1-64 "$ZIP.sha256")"
fi

echo "$FINGERPRINT" > "$STAMP"
echo "Built $APP ($IDENTITY)."
}

# The log is written as the build runs and shown in Xcode; the script's exit status is the build's.
main 2>&1 | tee "$LOG"
exit "${PIPESTATUS[0]}"
