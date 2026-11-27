#!/usr/bin/env bash
# Sign & Notarize (macOS) for an app built elsewhere, end to end on a macOS runner, without Apple credentials:
#   1. A real HCG-Mail-shaped app is compiled (app host + 3 native libraries in Contents/MacOS, data in Resources,
#      version 1.0.0 build 5), zipped with ditto --keepParent like HCG Mail's signing input, with a SHA256SUMS list.
#   2. A synthetic "Developer ID Application: HCG CI (HCGCI00001)" identity (self-signed, codeSigning EKU) is created in a
#      temporary keychain. It is not Apple's, so Apple's notary service would never accept what it signs.
#   3. HCGExecutableMaker --mac-distribute: inspect; a tampered checksum is refused; sign (inside out, hardened runtime,
#      secure timestamp, allow-jit). codesign itself then proves each nested library and the app are signed with that
#      identity, the runtime and a timestamp, the seal is strict, the entitlements are exactly allow-jit, and the
#      original signing input is byte-for-byte unchanged. Maker's own Developer ID check refuses the synthetic identity
#      only because it is not an Apple team's certificate (or passes if codesign reports its team).
#   4. The same through the Sign & Notarize page (tools/ui-driver --mac-distribute --sign).
# Notarization, stapling and Gatekeeper acceptance need a real Developer ID and Apple's notary service: not run here.
set -euo pipefail

OUT="${OUT:?set OUT}"
ROOT="$(pwd)"
mkdir -p "$OUT"
export HCG_EXECUTABLE_MAKER_HOME="$OUT/hcg-data"
APPDLL="$ROOT/src/HCG.ExecutableMaker.App/bin/Release/net10.0/HCGExecutableMaker.dll"
maker() { dotnet "$APPDLL" --mac-distribute "$@"; }
check() { if eval "$2"; then echo "✓ $1"; else echo "::error::Existing-app check failed: $1"; exit 1; fi; }
group() { echo "::group::$1"; }
endgroup() { echo "::endgroup::"; }

TEAM=HCGCI00001
CN="Developer ID Application: HCG CI ($TEAM)"
KC="$OUT/existing-app-ci.keychain-db"
ORIG_KEYCHAINS="$(security list-keychains -d user | xargs)"
cleanup() {
  security list-keychains -d user -s $ORIG_KEYCHAINS >/dev/null 2>&1 || true
  security delete-keychain "$KC" >/dev/null 2>&1 || true
  if [ -n "${TRUSTED_SHA1:-}" ]; then sudo security delete-certificate -Z "$TRUSTED_SHA1" /Library/Keychains/System.keychain >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

group "1. A real HCG-Mail-shaped app, unsigned (linker signature only), as a CI signing input"
SRC="$OUT/src"; APP="$OUT/in/HCG Mail.app"; rm -rf "$SRC" "$OUT/in"; mkdir -p "$SRC" "$APP/Contents/MacOS" "$APP/Contents/Resources"
for lib in AvaloniaNative SkiaSharp HarfBuzzSharp; do
  printf 'const char *%s_name(void) { return "%s"; }\n' "$lib" "$lib" > "$SRC/$lib.c"
  clang -arch arm64 -mmacosx-version-min=12.0 -dynamiclib -install_name "@executable_path/lib$lib.dylib" "$SRC/$lib.c" -o "$APP/Contents/MacOS/lib$lib.dylib"
done
cat > "$SRC/main.c" <<'EOF'
#include <stdio.h>
const char *AvaloniaNative_name(void); const char *SkiaSharp_name(void); const char *HarfBuzzSharp_name(void);
int main(void) { printf("HCG Mail probe: %s %s %s\n", AvaloniaNative_name(), SkiaSharp_name(), HarfBuzzSharp_name()); return 0; }
EOF
clang -arch arm64 -mmacosx-version-min=12.0 "$SRC/main.c" -L"$APP/Contents/MacOS" -lAvaloniaNative -lSkiaSharp -lHarfBuzzSharp -o "$APP/Contents/MacOS/HCG Mail"
echo '{"probe":true}' > "$APP/Contents/Resources/appsettings.json"
cat > "$APP/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>com.hakmicapital.hcgmail.ci</string>
  <key>CFBundleName</key><string>HCG Mail</string><key>CFBundleDisplayName</key><string>HCG Mail</string>
  <key>CFBundleExecutable</key><string>HCG Mail</string><key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0.0</string><key>CFBundleVersion</key><string>5</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
</dict></plist>
EOF
"$APP/Contents/MacOS/HCG Mail"
IN="$OUT/signing-input"; mkdir -p "$IN"
ZIP="$IN/HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip"
ditto -c -k --keepParent "$APP" "$ZIP"
(cd "$IN" && shasum -a 256 *.zip > SHA256SUMS-signing-input.txt && cat SHA256SUMS-signing-input.txt)
SHA_BEFORE="$(shasum -a 256 "$ZIP" | cut -d' ' -f1)"
for f in "$APP"/Contents/MacOS/*; do echo "  ${f##*/}: $(codesign -dv "$f" 2>&1 | grep -E '^Signature' || echo 'not signed')"; done
endgroup

group "2. Synthetic Developer ID Application identity (temporary keychain; not Apple's)"
cat > "$OUT/ext.cnf" <<'EOF'
[req]
distinguished_name = dn
[dn]
[codesign]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
EOF
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -keyout "$OUT/key.pem" -out "$OUT/cert.pem" -days 30 \
  -subj "/CN=$CN/OU=$TEAM/O=HCG CI/C=US" -config "$OUT/ext.cnf" -extensions codesign 2>/dev/null
/usr/bin/openssl pkcs12 -export -inkey "$OUT/key.pem" -in "$OUT/cert.pem" -out "$OUT/id.p12" -passout pass:ci -name "$CN"
security create-keychain -p ci "$KC"
security set-keychain-settings -lut 3600 "$KC"
security unlock-keychain -p ci "$KC"
security import "$OUT/id.p12" -k "$KC" -P ci -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k ci "$KC" >/dev/null
security list-keychains -d user -s "$KC" $ORIG_KEYCHAINS
SHA1="$(/usr/bin/openssl x509 -in "$OUT/cert.pem" -noout -fingerprint -sha1 | sed 's/.*=//; s/://g')"
sudo security add-trusted-cert -d -r trustRoot -p codeSign -k /Library/Keychains/System.keychain "$OUT/cert.pem" && TRUSTED_SHA1="$SHA1"
maker --list-identities | tee "$OUT/identities.txt"
check "the Developer ID Application identity is listed from the keychain" "grep -q '$SHA1' '$OUT/identities.txt'"
endgroup

group "3a. Inspect (read only) and a tampered checksum list"
maker --inspect --input "$ZIP" --checksums "$IN/SHA256SUMS-signing-input.txt" | tee "$OUT/inspect.txt"
check "inspection matches the published checksum" "grep -q '\[Passed\] Published checksum' '$OUT/inspect.txt'"
check "three nested libraries are found" "grep -q 'Nested code (3)' '$OUT/inspect.txt'"
printf '%064d  %s\n' 0 "$(basename "$ZIP")" > "$OUT/tampered-SHA256SUMS.txt"
set +e; maker --inspect --input "$ZIP" --checksums "$OUT/tampered-SHA256SUMS.txt" > "$OUT/tampered.txt" 2>&1; rc=$?; set -e
cat "$OUT/tampered.txt"
check "a signing input that does not match its published checksum is refused (exit 6)" "[ $rc -eq 6 ] && grep -q 'not the file that was published' '$OUT/tampered.txt'"
endgroup

group "3b. Sign with the synthetic identity: inside out, hardened runtime, timestamp, allow-jit (nothing sent to Apple)"
set +e
maker --input "$ZIP" --checksums "$IN/SHA256SUMS-signing-input.txt" --origin "CI fixture shaped like HCG Mail run 46007763971" \
  --release-version 1.0.0-beta.2 --build-number 5 --arch arm64 --identity "$SHA1" \
  --entitlement com.apple.security.cs.allow-jit --confirm-entitlements --formats zip --out "$OUT/dist" > "$OUT/sign.txt" 2>&1
rc=$?
set -e
cat "$OUT/sign.txt"
SIGNED="$(find "$OUT/dist" -maxdepth 2 -name 'HCG Mail.app' -type d | head -1)"
check "the signed copy exists in its own job folder" "[ -n '$SIGNED' ] && [ -d '$SIGNED' ]"
for lib in AvaloniaNative SkiaSharp HarfBuzzSharp; do
  info="$(codesign -dv --verbose=4 "$SIGNED/Contents/MacOS/lib$lib.dylib" 2>&1)"
  check "lib$lib.dylib signed by the chosen identity" "echo \"\$info\" | grep -q 'Authority=$CN'"
  check "lib$lib.dylib has the hardened runtime" "echo \"\$info\" | grep -Eq 'flags=0x[0-9a-f]*\(runtime\)|\(runtime\)'"
  check "lib$lib.dylib has a secure timestamp" "echo \"\$info\" | grep -q '^Timestamp='"
done
appinfo="$(codesign -dv --verbose=4 "$SIGNED" 2>&1)"
check "the app is signed by the chosen identity with the hardened runtime" "echo \"\$appinfo\" | grep -q 'Authority=$CN' && echo \"\$appinfo\" | grep -q 'runtime'"
check "codesign --verify --deep --strict passes (the whole bundle is sealed)" "codesign --verify --deep --strict --verbose=2 '$SIGNED'"
ents="$(codesign -d --entitlements - --xml "$SIGNED" 2>/dev/null || true)"
check "the app's entitlements are exactly allow-jit" "echo \"\$ents\" | grep -q 'com.apple.security.cs.allow-jit' && [ \$(echo \"\$ents\" | grep -o '<key>' | wc -l) -eq 1 ]"
check "the original signing input is unchanged" "[ \"$(shasum -a 256 "$ZIP" | cut -d' ' -f1)\" = '$SHA_BEFORE' ]"
check "no ZIP/PKG/DMG was made before notarization" "! ls '$OUT'/dist/*/*.zip >/dev/null 2>&1"
if [ $rc -eq 0 ]; then
  echo "Maker accepted the synthetic identity as Developer ID (codesign reported its team)."
else
  check "the only refusal is the Developer ID team check (a self-signed certificate is no Apple team's)" \
    "[ $rc -eq 6 ] && grep -q 'Developer ID:' '$OUT/sign.txt' && ! grep -E 'Job .* · Failed · ' '$OUT/sign.txt' | grep -Eq 'Code signature:|Hardened runtime|Nested code:|Entitlements|Contents/MacOS:'"
fi
echo "--- the signed app runs (informational: library validation of a non-Apple identity is up to macOS):"
"$SIGNED/Contents/MacOS/HCG Mail" || echo "(did not run under the hardened runtime with the synthetic identity)"
endgroup

group "4. The same through the Sign & Notarize page"
dotnet "$ROOT/tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll" --mac-distribute --source "$ZIP" --sums "$IN/SHA256SUMS-signing-input.txt" \
  --origin "CI fixture shaped like HCG Mail" --expect-bundle-id com.hakmicapital.hcgmail.ci --expect-nested 3 \
  --entitlement com.apple.security.cs.allow-jit --release-version 1.0.0-beta.2 --build-number 5 --sign --out-folder "$OUT/ui-dist" \
  --output "$OUT/ui" --screenshots "$OUT/screens" --prefix existing-mac-app
check "the page left the original signing input unchanged" "[ \"$(shasum -a 256 "$ZIP" | cut -d' ' -f1)\" = '$SHA_BEFORE' ]"
endgroup
echo "Existing-app distribution: signing path verified with a synthetic identity. Notarization, stapling and Gatekeeper acceptance need a real Developer ID and Apple's notary service."
