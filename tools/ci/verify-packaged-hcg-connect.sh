#!/bin/bash
# Verifies what a PACKAGED HCG Executable Maker actually loads for HCG Connect, as a user gets it: the distributable for
# this host is unpacked into a new folder and run with an EMPTY data folder (no hcg-connect.json anywhere) and the
# registration selector unset. Fails unless the package
#   - reports the expected version and commit (so a user can tell this build from an older one),
#   - has Maker's own production client built in and uses it (no file imported),
#   - is accepted by HCG's live identity server for that client with EVERY registered callback (PKCE S256).
# Read-only towards HCG: the authorize requests stop at HCG's consent redirect; nobody signs in, nothing is stored.
#
# Usage: verify-packaged-hcg-connect.sh <dist dir> <expected version> <expected commit (full or short SHA)>
set -euo pipefail
DIST="$1"; VERSION="$2"; COMMIT="$3"
CLIENT=fb9b6edf-2bfc-4b5e-a354-dbb1af697029
REGISTRY=hcg-executable-maker-desktop-production
fail() { echo "FAIL: $*"; exit 1; }
pass() { echo "PASS: $*"; }

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID=osx-arm64 ;; Darwin-x86_64) RID=osx-x64 ;;
  Linux-x86_64) RID=linux-x64 ;; Linux-aarch64) RID=linux-arm64 ;;
  *) fail "no packaged build to run on $(uname -s)-$(uname -m)" ;;
esac
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/unpacked" "$WORK/home"

if [ "$RID" = osx-arm64 ] || [ "$RID" = osx-x64 ]; then
  PKG="$DIST/HCG-Executable-Maker-$VERSION-$RID.app.zip"
  [ -f "$PKG" ] || fail "missing $PKG"
  ditto -x -k "$PKG" "$WORK/unpacked"
  APP="$(find "$WORK/unpacked" -maxdepth 2 -type d -name "*.app" | head -1)"
  [ -n "$APP" ] || fail "no .app inside $PKG"
  EXE="$APP/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist")"
else
  PKG="$(ls "$DIST"/HCG-Executable-Maker-"$VERSION"-"$RID".{zip,tar.gz} 2>/dev/null | head -1 || true)"
  [ -n "$PKG" ] || fail "no portable package for $RID $VERSION in $DIST ($(ls "$DIST" | tr '\n' ' '))"
  case "$PKG" in *.zip) unzip -q "$PKG" -d "$WORK/unpacked" ;; *) tar -xzf "$PKG" -C "$WORK/unpacked" ;; esac
  EXE="$(find "$WORK/unpacked" -type f -name HCGExecutableMaker | head -1)"
fi
[ -n "$EXE" ] && [ -x "$EXE" ] || fail "no executable HCGExecutableMaker in $PKG"
echo "Package: $(basename "$PKG") ($(shasum -a 256 "$PKG" | cut -d' ' -f1))"
echo "Executable: $EXE"

# As a user runs it: empty data folder, no registration selector, no imported configuration.
export HCG_EXECUTABLE_MAKER_HOME="$WORK/home"
unset HCG_EXECUTABLE_MAKER_HCG_ENVIRONMENT || true
[ ! -e "$WORK/home/hcg-connect.json" ] || fail "data folder is not empty"

"$EXE" --version | tee "$WORK/version.out"
grep -q "^HCG Executable Maker $VERSION (commit " "$WORK/version.out" || fail "--version does not report $VERSION"
reported="$(sed -n 's/^HCG Executable Maker [^ ]* (commit \([^,]*\),.*/\1/p' "$WORK/version.out")"
case "$reported" in unknown|*uncommitted*|"") fail "the package does not name a clean commit (\"$reported\")" ;; esac
case "$COMMIT" in "$reported"*) ;; *) fail "the package reports commit $reported, expected $COMMIT" ;; esac
grep -q "^HCG Connect: Production registration ($REGISTRY), client $CLIENT built in" "$WORK/version.out" \
  || fail "--version does not name the built-in production client"
pass "build identity: $VERSION, commit $reported; production client $CLIENT built in"

set +e; "$EXE" --hcg-session status > "$WORK/status.out" 2>&1; code=$?; set -e
cat "$WORK/status.out"
[ "$code" = 3 ] || fail "--hcg-session status: exit $code, expected 3 (configured, not signed in)"
grep -q "^Registration: Production ($REGISTRY) · client $CLIENT$" "$WORK/status.out" || fail "status does not show client $CLIENT in use"
grep -q "^Configuration source: built into this build: Production registration ($REGISTRY) (no hcg-connect.json needed)$" "$WORK/status.out" \
  || fail "the configuration in use is not the one built into this build"
! grep -qi "NotConfigured\|clientId — missing\|no client ID is configured" "$WORK/status.out" || fail "the package reports HCG Connect as not configured"
[ ! -e "$WORK/home/hcg-connect.json" ] || fail "the app wrote an hcg-connect.json; the built-in registration must need none"
pass "with an empty data folder the packaged app uses client $CLIENT and is ready to sign in (not signed in)"

set +e; "$EXE" --hcg-session check > "$WORK/check.out" 2>&1; code=$?; set -e
cat "$WORK/check.out"
grep -q "^Check Client registration: Passed — .*accepted client $CLIENT with each redirect URI (http://127.0.0.1:47820/oauth/callback, http://127.0.0.1:47821/oauth/callback, http://127.0.0.1:47822/oauth/callback)" "$WORK/check.out" \
  || fail "HCG's identity server did not accept client $CLIENT with every registered callback"
[ "$code" = 0 ] || fail "--hcg-session check: exit $code (sign-in cannot be attempted)"
pass "HCG's live identity server accepts client $CLIENT with callbacks 47820, 47821 and 47822 (PKCE S256)"
