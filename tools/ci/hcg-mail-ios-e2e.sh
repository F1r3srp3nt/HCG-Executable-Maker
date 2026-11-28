#!/usr/bin/env bash
# HCG Mail for iOS (.NET for iOS / Avalonia) through Mobile Build Studio, every route, on Xcode 27.2 with the iOS workload
# HCG Mail pins (27.2.10000), on a runner without Apple accounts:
#   1. Simulator Debug build, launched in the iOS Simulator from the result card ("Launch in Simulator").
#   2-4. Signed ios-arm64 device app (Debug), XCArchive (Release) and IPA (Release, development) — signed with a SYNTHETIC
#      "Apple Development: HCG CI (HCGCI00001)" identity (self-signed, codeSigning EKU, trusted only on this throwaway runner)
#      and a synthetic WILDCARD development profile (HCGCI00001.*) CMS-signed by it. Neither is Apple's: the device app and
#      IPA would not install on a real device. They prove the pipeline: the .NET iOS SDK compiles, signs with the chosen
#      identity and profile, archives into ArchiveDir and makes its own IPA; Maker finds exactly those files.
#   Maker's Signing Vault pre-check runs in full. Its one test-only input: the UI driver (a CI harness, never shipped)
#   passes --test-profile-root, so a profile signed by the synthetic CA counts as signed; the app always requires Apple's root.
# Independent checks use Apple's tools (codesign, plutil, security cms, unzip), not Maker.
set -euo pipefail

OUT="${OUT:?set OUT}"
MAIL="${MAIL:?set MAIL (the HCG Mail checkout)}"
ROOT="$(pwd)"
DRIVER="$ROOT/tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll"
check() { if eval "$2"; then echo "✓ $1"; else echo "::error::HCG Mail iOS check failed: $1"; exit 1; fi; }
group() { echo "::group::$1"; }
endgroup() { echo "::endgroup::"; }

TEAM=HCGCI00001
CN="Apple Development: HCG CI ($TEAM)"
PROFILE_NAME="HCG CI Wildcard Development"
BUNDLE=com.hakmicapitalgroup.hcgmail
KC="$OUT/mail-ios-ci.keychain-db"
ORIG_KEYCHAINS="$(security list-keychains -d user | xargs)"
PROFILE_UUID="$(uuidgen)"
PROFILE_DIRS=("$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles" "$HOME/Library/MobileDevice/Provisioning Profiles")
cleanup() {
  security list-keychains -d user -s $ORIG_KEYCHAINS >/dev/null 2>&1 || true
  security delete-keychain "$KC" >/dev/null 2>&1 || true
  for d in "${PROFILE_DIRS[@]}"; do rm -f "$d/$PROFILE_UUID.mobileprovision"; done
  if [ -n "${TRUSTED_SHA1:-}" ]; then sudo security delete-certificate -Z "$TRUSTED_SHA1" /Library/Keychains/System.keychain >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT
mkdir -p "$OUT"

group "Synthetic Apple Development identity and wildcard development profile (temporary keychain; not Apple's)"
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
security set-keychain-settings -lut 7200 "$KC"
security unlock-keychain -p ci "$KC"
security import "$OUT/id.p12" -k "$KC" -P ci -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k ci "$KC" >/dev/null
security list-keychains -d user -s "$KC" $ORIG_KEYCHAINS
SHA1="$(/usr/bin/openssl x509 -in "$OUT/cert.pem" -noout -fingerprint -sha1 | sed 's/.*=//; s/://g')"
# Trusted for every policy on this throwaway runner: Maker reads identities with `security find-identity` (all
# policies, so untrusted, expired and revoked ones are listed with macOS's reason), as an Apple-issued certificate is.
sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$OUT/cert.pem" && TRUSTED_SHA1="$SHA1"
security find-identity | sed -n '1,4p'
security find-identity -v -p codesigning "$KC"
CERT_B64="$(/usr/bin/openssl x509 -in "$OUT/cert.pem" -outform der | base64 | tr -d '\n')"
NOW="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; EXP="$(date -u -v+30d +%Y-%m-%dT%H:%M:%SZ)"
cat > "$OUT/profile.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>AppIDName</key><string>HCG CI Wildcard</string>
  <key>ApplicationIdentifierPrefix</key><array><string>$TEAM</string></array>
  <key>CreationDate</key><date>$NOW</date>
  <key>Platform</key><array><string>iOS</string></array>
  <key>DeveloperCertificates</key><array><data>$CERT_B64</data></array>
  <key>Entitlements</key><dict>
    <key>application-identifier</key><string>$TEAM.*</string>
    <key>com.apple.developer.team-identifier</key><string>$TEAM</string>
    <key>get-task-allow</key><true/>
    <key>keychain-access-groups</key><array><string>$TEAM.*</string></array>
  </dict>
  <key>ExpirationDate</key><date>$EXP</date>
  <key>Name</key><string>$PROFILE_NAME</string>
  <key>ProvisionedDevices</key><array><string>00008140-00000000000C1001</string></array>
  <key>TeamIdentifier</key><array><string>$TEAM</string></array>
  <key>TeamName</key><string>HCG CI</string>
  <key>UUID</key><string>$PROFILE_UUID</string>
  <key>Version</key><integer>1</integer>
</dict></plist>
EOF
/usr/bin/openssl smime -sign -binary -nodetach -outform der -in "$OUT/profile.plist" -signer "$OUT/cert.pem" -inkey "$OUT/key.pem" -out "$OUT/ci.mobileprovision"
for d in "${PROFILE_DIRS[@]}"; do mkdir -p "$d"; cp "$OUT/ci.mobileprovision" "$d/$PROFILE_UUID.mobileprovision"; done
echo "Identity $SHA1 · profile $PROFILE_UUID ($PROFILE_NAME, $TEAM.*)"
endgroup

run() { # name, then driver options
  local name="$1"; shift
  group "Mobile Build Studio: $name"
  dotnet "$DRIVER" --mobile --source "$MAIL" "$@" --output "$OUT/$name" --screenshots "$OUT/screens" --prefix "mail-$name" --test-profile-root "$OUT/cert.pem"
  endgroup
}
signed=(--signing manual --identity "$CN" --profile "$PROFILE_NAME")

run sim --mobile-output sim --variant Debug --launch-simulator --expect-artifacts IosSimulatorApp
run device --mobile-output device --variant Debug "${signed[@]}" --expect-artifacts IosDeviceApp
run archive --mobile-output archive --variant Release "${signed[@]}" --expect-artifacts IosArchive
run ipa --mobile-output ipa --variant Release "${signed[@]}" --expect-artifacts IosIpa,IosArchive

group "Independent checks (codesign, plutil, security cms, unzip)"
app_checks() { # label, app, configuration
  local label="$1" app="$2"
  echo "== $label: $app"
  plutil -p "$app/Info.plist" | grep -E 'CFBundle(Identifier|ShortVersionString|Version|Executable)"|MinimumOSVersion|DTPlatformName'
  check "$label: bundle identifier $BUNDLE" "[ \"\$(plutil -extract CFBundleIdentifier raw '$app/Info.plist')\" = '$BUNDLE' ]"
  check "$label: built for iPhoneOS (not the Simulator)" "[ \"\$(plutil -extract DTPlatformName raw '$app/Info.plist')\" = iphoneos ]"
  check "$label: a version is set" "[ -n \"\$(plutil -extract CFBundleShortVersionString raw '$app/Info.plist')\" ] && [ -n \"\$(plutil -extract CFBundleVersion raw '$app/Info.plist')\" ]"
  local info; info="$(codesign -dv --verbose=4 "$app" 2>&1)"
  check "$label: signed by the chosen identity" "echo \"\$info\" | grep -q 'Authority=$CN'"
  check "$label: signature verifies (codesign --verify --deep --strict)" "codesign --verify --deep --strict '$app'"
  local ents; ents="$(codesign -d --entitlements - --xml "$app" 2>/dev/null)"
  echo "$ents" | plutil -p - 2>/dev/null || true
  check "$label: keychain-access-groups resolved to the team prefix" "echo \"\$ents\" | grep -q '$TEAM.$BUNDLE' && echo \"\$ents\" | grep -q '$TEAM.com.microsoft.adalcache'"
  check "$label: application-identifier is $TEAM.$BUNDLE" "echo \"\$ents\" | grep -q '$TEAM.$BUNDLE'"
  security cms -D -i "$app/embedded.mobileprovision" > "$OUT/embedded-$label.plist"
  check "$label: the chosen wildcard profile is embedded" "[ \"\$(plutil -extract UUID raw '$OUT/embedded-$label.plist')\" = '$PROFILE_UUID' ]"
}
DEVICE_APP="$(find "$OUT/device/artifacts" -maxdepth 4 -name '*.app' -type d | head -1)"
app_checks device "$DEVICE_APP"
check "device: Debug (get-task-allow true)" "codesign -d --entitlements - --xml '$DEVICE_APP' 2>/dev/null | grep -A1 get-task-allow | grep -q true"
ARCHIVE="$(find "$OUT/archive/artifacts" -maxdepth 4 -name '*.xcarchive' -type d | head -1)"
check "archive: a real .xcarchive (Info.plist, Products/Applications)" "[ -f '$ARCHIVE/Info.plist' ] && ls -d '$ARCHIVE'/Products/Applications/*.app"
plutil -p "$ARCHIVE/Info.plist"
app_checks archive "$(ls -d "$ARCHIVE"/Products/Applications/*.app | head -1)"
IPA="$(find "$OUT/ipa/artifacts" -maxdepth 4 -name '*.ipa' -type f | head -1)"
check "ipa: a real IPA (Payload/<App>.app)" "unzip -l '$IPA' | grep -Eq 'Payload/[^/]+\\.app/Info.plist'"
rm -rf "$OUT/ipa-x" && mkdir -p "$OUT/ipa-x" && (cd "$OUT/ipa-x" && unzip -q "$IPA")
app_checks ipa "$(ls -d "$OUT"/ipa-x/Payload/*.app | head -1)"
check "the IPA was made by the .NET iOS SDK, not by xcodebuild -exportArchive" "! grep -q -- '-exportArchive' '$OUT/ipa/mail-ipa-build.log' && grep -q -- '-p:BuildIpa=true' '$OUT/ipa/mail-ipa-build.log'"
check "the archive was written where ArchiveDir named it" "grep -Eq -- '-p:ArchiveDir=[^ ]+\\.xcarchive' '$OUT/archive/mail-archive-build.log'"
check "the effective MSBuild properties were read before compiling" "grep -q 'ArchiveOnBuild = true' '$OUT/archive/mail-archive-build.log'"
endgroup

group "Maker's own verification (production code, real Apple codesign — not a test double)"
for r in device archive ipa; do
  grep -E '^.*codesign --verify:' "$OUT/$r/mail-$r-build.log" || true
  check "$r: Maker's verifier ran Apple's codesign --verify and it passed" "grep -Eq 'codesign --verify: Code signature (of Payload/[^ ]+ )?is valid' '$OUT/$r/mail-$r-build.log'"
done
check "simulator: no codesign verdict is claimed for a Simulator app as device proof" "! grep -q 'Device build (iphoneos)' '$OUT/sim/mail-sim-build.log'"
endgroup

group "Release Passport (build half) of the IPA, written by Mobile Build Studio"
PASSPORT="$IPA.release-passport.json"
check "a Release Passport sits next to the IPA" "[ -f '$PASSPORT' ]"
cat "$PASSPORT"
pj() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); v=d
for k in sys.argv[2].split("."): v=v[k]
print(v if not isinstance(v,(list,dict)) else json.dumps(v))' "$PASSPORT" "$1"; }
MAIL_HEAD="$(git -C "$MAIL" rev-parse HEAD)"
check "passport: format hcg.release-passport v1" "[ \"\$(pj format)\" = hcg.release-passport ] && [ \"\$(pj formatVersion)\" = 1 ]"
check "passport: SHA-256 is the IPA's own (shasum)" "[ \"\$(pj artifact.sha256)\" = \"\$(shasum -a 256 '$IPA' | cut -d' ' -f1)\" ]"
check "passport: size is the IPA's own" "[ \"\$(pj artifact.size)\" = \"\$(stat -f%z '$IPA')\" ]"
check "passport: package is $BUNDLE" "[ \"\$(pj package)\" = '$BUNDLE' ]"
check "passport: version matches the app's Info.plist" "[ \"\$(pj version)\" = \"\$(plutil -extract CFBundleShortVersionString raw '$OUT/ipa-x/Payload/'*.app/Info.plist)\" ]"
check "passport: source commit is HCG Mail's checkout ($MAIL_HEAD)" "[ \"\$(pj source.commit)\" = '$MAIL_HEAD' ]"
check "passport: a development IPA is platform ios, not ios-app-store" "[ \"\$(pj platform)\" = ios ]"
check "passport: Apple's codesign --verify passed (Maker's verdict)" "pj code_signing.codesign_verify | grep -q '^passed'"
check "passport: every Mach-O carries a signature" "[ \"\$(pj code_signing.all_signed)\" = True ]"
check "passport: the embedded profile is the chosen one" "[ \"\$(pj code_signing.profile.name)\" = '$PROFILE_NAME' ] && [ \"\$(pj code_signing.profile.team_id)\" = '$TEAM' ]"
check "passport: entitlements read from the signed executable" "pj code_signing.entitlements | grep -q keychain-access-groups"
check "passport: device installation and launch are not claimed by the builder" "[ \"\$(pj validation.levels.device_installation.state)\" = not-recorded ] && [ \"\$(pj validation.levels.application_launch.state)\" = not-recorded ]"
check "passport: nothing published" "pj publication.note | grep -q 'never publishes'"
endgroup
echo "HCG Mail iOS: simulator launch, signed device app, XCArchive, IPA and its Release Passport verified with a SYNTHETIC identity."
echo "Not proven here: installation or launch on a physical iPhone, and App Store compatibility (they need the owner's Apple certificate, profile and device)."
