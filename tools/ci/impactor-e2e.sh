#!/usr/bin/env bash
# HCG Impactor end to end on a macOS runner — REAL Apple tools, SYNTHETIC signing assets, NO physical device.
#
#   1. Xcode builds a real, unsigned iOS device app (tests/Fixtures/HelloIOS, -sdk iphoneos) → HelloIOS-unsigned.ipa.
#   2. A synthetic "Apple Development: HCG CI (HCGCI00001)" identity (self-signed, codeSigning EKU) is created in a
#      temporary keychain, and a synthetic Development provisioning profile is CMS-signed by it. Neither is Apple's:
#      HCG reports the profile as "not verified as Apple's", and no device would accept the result.
#   3. HCG Impactor's command line: inspect → identities → profiles → plan → sign (codesign) → inspect the signed IPA.
#   4. Apple's tools verify the result independently: codesign --verify --deep --strict, codesign -d (authority, team,
#      entitlements), the embedded profile is byte-identical, the original IPA is unchanged.
#   5. Integrity: a resource changed after signing is reported by HCG and rejected by codesign.
#   6. devicectl (real): device list, and an installation to a device that does not exist — its real error output is
#      translated by HCG's diagnostics.
#   7. The workspace UI (headless Avalonia) signs the same IPA through Signing Studio and visits every section.
#
# Physical-device installation is NOT tested here (no device on CI). See docs/IMPACTOR.md › Physical device validation.
set -euo pipefail

OUT="${OUT:?set OUT}"
ROOT="$(pwd)"
mkdir -p "$OUT"
export HCG_EXECUTABLE_MAKER_HOME="$OUT/hcg-data"
APPDLL="$ROOT/src/HCG.ExecutableMaker.App/bin/Release/net10.0/HCGExecutableMaker.dll"
imp() { dotnet "$APPDLL" --impactor "$@"; }
check() { if eval "$2"; then echo "✓ $1"; else echo "::error::Impactor check failed: $1"; exit 1; fi; }
group() { echo "::group::$1"; }
endgroup() { echo "::endgroup::"; }

TEAM=HCGCI00001
CN="Apple Development: HCG CI ($TEAM)"
BUNDLE=com.hakmicapital.helloios
UDID=00008140-00000000000C1001
KC="$OUT/impactor-ci.keychain-db"
PROFILES_DIR="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"
ORIG_KEYCHAINS="$(security list-keychains -d user | xargs)"
PROFILE_UUID="$(uuidgen)"

cleanup() {
  security list-keychains -d user -s $ORIG_KEYCHAINS >/dev/null 2>&1 || true
  security delete-keychain "$KC" >/dev/null 2>&1 || true
  rm -f "$PROFILES_DIR/$PROFILE_UUID.mobileprovision"
  if [ -n "${TRUSTED_SHA1:-}" ]; then sudo security delete-certificate -Z "$TRUSTED_SHA1" /Library/Keychains/System.keychain >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

# ---------------------------------------------------------------- 1. real unsigned device IPA
group "1. Build a real unsigned iOS device app (HelloIOS, iphoneos)"
xcodebuild -version
xcodebuild -project tests/Fixtures/HelloIOS/HelloIOS.xcodeproj -scheme HelloIOS -configuration Release -sdk iphoneos \
  -destination generic/platform=iOS -derivedDataPath "$OUT/dd" CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO build -quiet
APP="$OUT/dd/Build/Products/Release-iphoneos/HelloIOS.app"
rm -rf "$OUT/pkg" && mkdir -p "$OUT/pkg/Payload"
ditto "$APP" "$OUT/pkg/Payload/HelloIOS.app"
(cd "$OUT/pkg" && zip -qry "$OUT/HelloIOS-unsigned.ipa" Payload)
UNSIGNED="$OUT/HelloIOS-unsigned.ipa"
SHA_BEFORE="$(shasum -a 256 "$UNSIGNED" | cut -d' ' -f1)"
lipo -info "$APP/HelloIOS"
endgroup

# ---------------------------------------------------------------- 2. synthetic identity + profile
group "2. Synthetic signing identity (temporary keychain) and provisioning profile"
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
# Trust the synthetic root for code signing on this throwaway runner so codesign builds the chain (removed on exit).
sudo security add-trusted-cert -d -r trustRoot -p codeSign -k /Library/Keychains/System.keychain "$OUT/cert.pem" && TRUSTED_SHA1="$SHA1"
security find-identity -p codesigning "$KC"

CERT_B64="$(/usr/bin/openssl x509 -in "$OUT/cert.pem" -outform der | base64 | tr -d '\n')"
NOW="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
EXP="$(date -u -v+30d +%Y-%m-%dT%H:%M:%SZ)"
cat > "$OUT/profile.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>AppIDName</key><string>HCG CI HelloIOS</string>
  <key>CreationDate</key><date>$NOW</date>
  <key>Platform</key><array><string>iOS</string></array>
  <key>DeveloperCertificates</key><array><data>$CERT_B64</data></array>
  <key>Entitlements</key><dict>
    <key>application-identifier</key><string>$TEAM.$BUNDLE</string>
    <key>com.apple.developer.team-identifier</key><string>$TEAM</string>
    <key>get-task-allow</key><true/>
    <key>keychain-access-groups</key><array><string>$TEAM.*</string></array>
  </dict>
  <key>ExpirationDate</key><date>$EXP</date>
  <key>Name</key><string>HCG CI Development</string>
  <key>ProvisionedDevices</key><array><string>$UDID</string></array>
  <key>TeamIdentifier</key><array><string>$TEAM</string></array>
  <key>TeamName</key><string>HCG CI</string>
  <key>UUID</key><string>$PROFILE_UUID</string>
  <key>Version</key><integer>1</integer>
</dict></plist>
EOF
/usr/bin/openssl smime -sign -binary -nodetach -outform der -in "$OUT/profile.plist" -signer "$OUT/cert.pem" -inkey "$OUT/key.pem" -out "$OUT/ci.mobileprovision"
mkdir -p "$PROFILES_DIR"
cp "$OUT/ci.mobileprovision" "$PROFILES_DIR/$PROFILE_UUID.mobileprovision"
echo "Identity $SHA1 · profile $PROFILE_UUID"
endgroup

# ---------------------------------------------------------------- 3. HCG Impactor CLI
group "3a. inspect (unsigned)"
imp inspect "$UNSIGNED" | tee "$OUT/inspect-unsigned.txt"
check "the unsigned IPA is inspected: identifier" "grep -q 'App: .* · $BUNDLE' '$OUT/inspect-unsigned.txt'"
# The arm64 linker may add an ad hoc ("linker-signed") signature to a build Xcode did not sign.
check "the unsigned IPA is reported as unsigned or ad hoc" "grep -Eq 'Signature: (unsigned|ad hoc)' '$OUT/inspect-unsigned.txt'"
check "the architecture is read from the binary (arm64, iOS)" "grep -q 'arm64 \[iOS\]' '$OUT/inspect-unsigned.txt'"
imp inspect "$UNSIGNED" --json > "$OUT/inspect-unsigned.json"
endgroup

group "3b. identities and profiles"
imp identities | tee "$OUT/identities.txt"
check "the Keychain identity is detected" "grep -q '$SHA1' '$OUT/identities.txt'"
imp profiles --ipa "$UNSIGNED" | tee "$OUT/profiles.txt"
check "the installed profile is detected and fits the app" "grep -q 'HCG CI Development' '$OUT/profiles.txt' && grep -q 'App ($BUNDLE): \"HCG CI Development\"' '$OUT/profiles.txt'"
endgroup

group "3c. plan (automatic identity and profile)"
imp plan "$UNSIGNED" --report "$OUT/reports" | tee "$OUT/plan.txt"
check "automatic signing plan is ready" "grep -q 'Ready to sign.' '$OUT/plan.txt'"
check "the profile is reported as not Apple's (synthetic)" "grep -q 'Profile not verified as Apple' '$OUT/plan.txt'"
check "entitlements come from the profile with the app identifier" "grep -q 'application-identifier=$TEAM.$BUNDLE' '$OUT/plan.txt'"
endgroup

group "3d. sign (codesign)"
SIGNED="$OUT/HelloIOS-signed.ipa"
imp sign "$UNSIGNED" --identity "$SHA1" --out "$SIGNED" --report "$OUT/reports" | tee "$OUT/sign.txt"
check "a signed IPA was written" "test -s '$SIGNED'"
check "the original IPA is unchanged" "test \"\$(shasum -a 256 '$UNSIGNED' | cut -d' ' -f1)\" = '$SHA_BEFORE'"
check "HCG refuses to overwrite an existing file" "! imp sign '$UNSIGNED' --identity '$SHA1' --out '$SIGNED' > '$OUT/sign-again.txt' 2>&1 && grep -q 'already exists' '$OUT/sign-again.txt'"
endgroup

# ---------------------------------------------------------------- 4. independent verification by Apple's tools
group "4. Apple's codesign verifies the result independently"
rm -rf "$OUT/verify" && mkdir -p "$OUT/verify"
ditto -x -k "$SIGNED" "$OUT/verify"
VAPP="$OUT/verify/Payload/HelloIOS.app"
codesign --verify --deep --strict --verbose=2 "$VAPP"
codesign -dvvv "$VAPP" > "$OUT/codesign-d.txt" 2>&1; cat "$OUT/codesign-d.txt"
check "signed by the chosen identity" "grep -q 'Authority=$CN' '$OUT/codesign-d.txt'"
check "team identifier recorded" "grep -q 'TeamIdentifier=$TEAM' '$OUT/codesign-d.txt'"
check "identifier is the bundle identifier" "grep -q 'Identifier=$BUNDLE' '$OUT/codesign-d.txt'"
(codesign -d --entitlements - --xml "$VAPP" 2>/dev/null || codesign -d --entitlements :- "$VAPP" 2>/dev/null) > "$OUT/entitlements.xml"; cat "$OUT/entitlements.xml"; echo
check "application-identifier entitlement" "grep -q '$TEAM.$BUNDLE' '$OUT/entitlements.xml'"
check "get-task-allow (development) entitlement" "grep -q 'get-task-allow' '$OUT/entitlements.xml'"
check "the embedded profile is the chosen profile, byte for byte" "cmp -s '$VAPP/embedded.mobileprovision' '$OUT/ci.mobileprovision'"
imp inspect "$SIGNED" | tee "$OUT/inspect-signed.txt"
check "HCG reads the new signature" "grep -q 'Signature: $CN · team $TEAM' '$OUT/inspect-signed.txt'"
check "HCG verifies the sealed resources" "grep -q 'Sealed resources: ' '$OUT/inspect-signed.txt' && ! grep -q ' modified, ' '$OUT/inspect-signed.txt'"
endgroup

# ---------------------------------------------------------------- 5. integrity
group "5. A file changed after signing is detected (HCG and codesign agree)"
RES="$(cd "$VAPP" && find . -type f ! -path './_CodeSignature/*' ! -name HelloIOS ! -name Info.plist ! -name PkgInfo ! -name embedded.mobileprovision | head -1)"
echo "Tampering with $RES"
printf 'changed after signing' >> "$VAPP/$RES"
(cd "$OUT/verify" && rm -f "$OUT/HelloIOS-tampered.ipa" && zip -qry "$OUT/HelloIOS-tampered.ipa" Payload)
imp inspect "$OUT/HelloIOS-tampered.ipa" | tee "$OUT/inspect-tampered.txt" || true
check "HCG reports the changed file" "grep -q 'Files changed after signing' '$OUT/inspect-tampered.txt'"
check "codesign also rejects the tampered app" "! codesign --verify --deep --strict '$VAPP' 2>/dev/null"
endgroup

# ---------------------------------------------------------------- 6. devicectl
group "6. devicectl: devices and a real installation error"
xcrun devicectl --version || true
imp devices | tee "$OUT/devices.txt" || true
imp devices --json > "$OUT/devices.json" || true
check "the device list ran through devicectl" "grep -Eq 'Devices \\(devicectl|No iPhone or iPad' '$OUT/devices.txt'"
xcrun devicectl device install app --device 00000000-0000-0000-0000-000000000000 "$OUT/pkg/Payload/HelloIOS.app" > "$OUT/devicectl-install.txt" 2>&1 || true
cat "$OUT/devicectl-install.txt"
imp diagnose "$OUT/devicectl-install.txt" | tee "$OUT/diagnose.txt"
check "devicectl's real error is explained (not 'unknown')" "! grep -q '\[Unknown\]' '$OUT/diagnose.txt'"
imp install "$SIGNED" --device 00000000-0000-0000-0000-000000000000 > "$OUT/install-missing.txt" 2>&1 || true
cat "$OUT/install-missing.txt"
check "installing to a missing device stops before anything is sent" "grep -Eq 'No device matches|Device not found|Apple tools not available' '$OUT/install-missing.txt'"
endgroup

# ---------------------------------------------------------------- reports
group "Diagnostic reports"
ls -R "$OUT/reports"
REPORT="$(ls -d "$OUT"/reports/hcg-impactor-* | tail -1)"
check "report.json uses hcg.diagnostic-report/1" "grep -q '\"schema\": \"hcg.diagnostic-report/1\"' '$REPORT/report.json'"
check "reports never contain the device UDID" "! grep -rq '$UDID' '$OUT/reports'"
check "reports never contain the runner's user name" "! grep -rq '/Users/$USER' '$OUT/reports'"
endgroup

# ---------------------------------------------------------------- 7. workspace UI
group "7. HCG Impactor workspace (headless UI): sign through Signing Studio"
dotnet "$ROOT/tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll" --impactor --impactor-sign --source "$UNSIGNED" --identity "$SHA1" \
  --expect-bundle-id "$BUNDLE" --output "$OUT/ui" --screenshots "$OUT/screens" --prefix impactor --window-height 1200
UI_SIGNED="$OUT/HelloIOS-unsigned-signed.ipa"
check "the UI wrote a signed IPA" "test -s '$UI_SIGNED'"
rm -rf "$OUT/verify-ui" && mkdir -p "$OUT/verify-ui" && ditto -x -k "$UI_SIGNED" "$OUT/verify-ui"
check "codesign verifies the IPA signed from the UI" "codesign --verify --deep --strict '$OUT/verify-ui/Payload/HelloIOS.app'"
imp monitor | tee "$OUT/monitor.txt"
check "Signing Monitor records the signed apps" "grep -q '$BUNDLE' '$OUT/monitor.txt'"
endgroup

echo "HCG IMPACTOR E2E: PASSED (real Xcode build, real codesign/devicectl; synthetic identity and profile; no physical device)"
