#!/bin/bash
# Ship to Store, end to end, with harmless sample artifacts. TEST environment only: a local HCG Store server in test
# mode (publishing enabled, TEST entitlement ledger standing in for HCG) and the mock HCG Connect provider from the HCG
# Store repository. Nothing is deployed and no real HCG system is contacted.
#
# Usage: tools/ci/ship-to-store-e2e.sh <HCG Store web-hcg-store dir> <out dir> [HCGExecutableMaker.dll]
# Set SHIP_REAL_BUILD=0 to skip the real build (section 9: New Build → "Ship to Store…" in the app, headless).
set -u
STORE_DIR="$(cd "$1" && pwd)"; OUT="$(mkdir -p "$2" && cd "$2" && pwd)"
MAKER="${3:-src/HCG.ExecutableMaker.App/bin/Release/net10.0/HCGExecutableMaker.dll}"
RESULTS="$OUT/results.txt"; : > "$RESULTS"
fail=0
ok()  { echo "PASS  $1" | tee -a "$RESULTS"; }
bad() { echo "FAIL  $1" | tee -a "$RESULTS"; fail=1; }
check() { if eval "$2"; then ok "$1"; else bad "$1"; fi; }
note() { echo "NOTE  $1" | tee -a "$RESULTS"; }

# ── Harmless sample artifacts: an iOS IPA, a Windows ZIP and a Linux .deb (each prints "hello") ──
S="$OUT/samples"; rm -rf "$S"; mkdir -p "$S/ipa/Payload/HCGSample.app/_CodeSignature" "$S/win" "$S/deb/DEBIAN" "$S/deb/usr/bin"
cat > "$S/ipa/Payload/HCGSample.app/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.example.hcg.sample</string><key>CFBundleShortVersionString</key><string>1.3.0</string>
<key>MinimumOSVersion</key><string>15.0</string><key>UIDeviceFamily</key><array><integer>1</integer><integer>2</integer></array></dict></plist>
EOF
echo "sample" > "$S/ipa/Payload/HCGSample.app/_CodeSignature/CodeResources"
# An ad hoc provisioning profile as Apple ships one (a CMS envelope around a plist), signed by a throwaway test key.
cat > "$S/profile.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict><key>AppIDName</key><string>HCG Sample</string><key>Name</key><string>HCG Sample Ad Hoc (TEST)</string>
<key>UUID</key><string>6F1C0E2A-9C55-4B7A-8F59-3A2B1C0D9E8F</string><key>TeamIdentifier</key><array><string>ABCDE12345</string></array>
<key>ExpirationDate</key><date>$(date -u -d '+180 days' +%Y-%m-%dT%H:%M:%SZ)</date><key>Platform</key><array><string>iOS</string></array>
<key>Entitlements</key><dict><key>application-identifier</key><string>ABCDE12345.com.example.hcg.sample</string><key>get-task-allow</key><false/></dict>
<key>ProvisionedDevices</key><array><string>00008110-000000000000001E</string></array></dict></plist>
EOF
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$S/profile-key.pem" -out "$S/profile-cert.pem" -days 2 -subj "/CN=HCG TEST profile signer" 2>/dev/null
openssl cms -sign -binary -nodetach -outform DER -in "$S/profile.plist" -signer "$S/profile-cert.pem" -inkey "$S/profile-key.pem" -out "$S/ipa/Payload/HCGSample.app/embedded.mobileprovision"
head -c 3000000 /dev/urandom > "$S/ipa/Payload/HCGSample.app/HCGSample"   # incompressible, so the upload spans 3 chunks
(cd "$S/ipa" && zip -qr ../HCGSample-1.3.0.ipa Payload)
printf 'MZ' > "$S/win/hello.exe"; echo "echo hello" > "$S/win/README.txt"
(cd "$S/win" && zip -q ../HCGSample-1.3.0-windows.zip hello.exe README.txt)
printf 'Package: com.example.hcg.sample\nVersion: 1.3.0\nArchitecture: amd64\nMaintainer: HCG Test\nDescription: harmless sample\n' > "$S/deb/DEBIAN/control"
printf '#!/bin/sh\necho hello\n' > "$S/deb/usr/bin/hcg-sample"; chmod 755 "$S/deb/usr/bin/hcg-sample"
dpkg-deb --build -Zgzip --root-owner-group "$S/deb" "$S/hcg-sample_1.3.0_amd64.deb" >/dev/null
for f in "$S"/HCGSample-1.3.0.ipa "$S"/HCGSample-1.3.0-windows.zip "$S"/hcg-sample_1.3.0_amd64.deb; do echo "sample $(basename "$f") $(sha256sum "$f" | cut -c1-64)" | tee -a "$RESULTS"; done

# ── Local HCG Store (TEST) + mock HCG Connect; the 2nd upload chunk is cut off once (retry test) ──
INFO="$OUT/server.json"; rm -f "$INFO"
(cd "$STORE_DIR" && DROP_CHUNK=2 SLOW_UPLOAD_MS=250 npx tsx server/test/ship-e2e-server.ts "$INFO" > "$OUT/server.log" 2>&1) &
SERVER=$!
trap 'pkill -f "ship-e2e-server.ts $INFO" 2>/dev/null; true' EXIT
for _ in $(seq 1 120); do [ -s "$INFO" ] && break; sleep 0.5; done
STORE=$(jq -r .storeOrigin "$INFO"); ISS=$(jq -r .issuer "$INFO"); CTL=$(jq -r .control "$INFO"); CLIENT=$(jq -r .makerClientId "$INFO")
echo "Store $STORE · issuer $ISS" | tee -a "$RESULTS"
ctl() { curl -s -X POST "$CTL/$1" -H 'content-type: application/json' -d "$2"; }
# HCG's decisions (stood in for by the TEST ledger): alice may publish; bob approves; carol is a customer.
ctl grant '{"sub":"e2e-alice","central":"hcg.store.publish","edition":"*"}' >/dev/null
ctl grant '{"sub":"e2e-bob","central":"hcg.store.approve","edition":"*"}' >/dev/null
as_alice() { curl -s -X POST "${ISS%/auth/v1}/__test/next-user/e2e-alice" >/dev/null; }   # who signs in next in the mock browser
as_alice

# ── Maker's own HCG Connect configuration and data folder ──
export HCG_EXECUTABLE_MAKER_HOME="$OUT/maker-home"; rm -rf "$HCG_EXECUTABLE_MAKER_HOME"; mkdir -p "$HCG_EXECUTABLE_MAKER_HOME"
jq -n --arg iss "$ISS" --arg c "$CLIENT" '{contractVersion:"hcg.executable-maker.connect/1", clientId:$c, issuer:$iss,
  authorizationEndpoint:($iss+"/oauth/authorize"), tokenEndpoint:($iss+"/oauth/token"), userinfoEndpoint:($iss+"/oauth/userinfo"),
  jwksEndpoint:($iss+"/.well-known/jwks.json"), apiBaseUrl:($iss|sub("/auth/v1$";""))}' > "$HCG_EXECUTABLE_MAKER_HOME/hcg-connect.json"
SHIP=(dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --product fixture-quasar-shell --edition standard --release-version 1.3.0 --build-number 7
      --note "Harmless Ship to Store sample." --distribution paid --eligibility public
      --file "$S/HCGSample-1.3.0.ipa" --file "$S/HCGSample-1.3.0-windows.zip" --file "$S/hcg-sample_1.3.0_amd64.deb" --verify-download)

# 1–4: authenticate (PKCE through the mock HCG Connect), private draft, upload (with one dropped chunk), SHA-256.
"${SHIP[@]}" > "$OUT/ship-1.log" 2>&1; rc=$?
check "1 Authenticated with HCG Connect (PKCE, desktop client) and accepted by HCG Store" 'grep -q "HCG account: SignedIn" "$OUT/ship-1.log" && grep -q "signed in as e2e-alice" "$OUT/ship-1.log"'
REL=$(grep -oE "Release rel-[a-f0-9]{16}" "$OUT/ship-1.log" | head -1 | cut -d" " -f2)
check "2 Private draft release created ($REL)" '[ -n "$REL" ] && grep -q "— draft; requested public" "$OUT/ship-1.log"'
check "3 Three platform files uploaded; the dropped chunk was retried" '[ $rc = 0 ] && [ "$(grep -c ": verified · SHA-256" "$OUT/ship-1.log")" = 3 ] && grep -q "Attempt 1 failed" "$OUT/ship-1.log"'
check "4 SHA-256 verified by the Store and by downloading each file back" '[ "$(grep -c "— matches" "$OUT/ship-1.log")" = 3 ]'
grep -E "^File |  .*: verified|^Download |Attempt" "$OUT/ship-1.log" | sed 's/^/    /' | tee -a "$RESULTS"

# 7 (before approval): no token / another person / wrong client.
IPA_ID=$(curl -s -H "authorization: Bearer $(ctl token '{"sub":"e2e-alice"}' | jq -r .token)" "$STORE/api/v1/publishing/releases/$REL" | jq -r '.artifacts[] | select(.format=="ipa") | .id')
CAROL=$(ctl token '{"sub":"e2e-carol"}' | jq -r .token)
dl() { curl -s -o /dev/null -w '%{http_code}' -X POST ${1:+-H "authorization: Bearer $1"} "$STORE/api/v1/publishing/artifacts/$IPA_ID/download-url"; }
check "7a Signed-out download refused (401)" '[ "$(dl "")" = 401 ]'
check "7b Another HCG account without permission refused before approval (403)" '[ "$(dl "$CAROL")" = 403 ]'
# Unauthorised uploads: carol (no publishing permission from HCG) through Maker, and straight at the API.
curl -s -X POST "${ISS%/auth/v1}/__test/next-user/e2e-carol" >/dev/null
dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --product fixture-quasar-shell --edition standard --release-version 9.9.9 --build-number 1 \
  --distribution paid --eligibility private --file "$S/HCGSample-1.3.0-windows.zip" > "$OUT/ship-carol.log" 2>&1; rc=$?
check "7e Upload by an account HCG has not allowed to publish is refused by the shared feature-access check (Maker exit $rc: not permitted, ask the administrator, no purchase link), and no release is created" \
  '[ $rc = 8 ] && grep -q "Feature store.publish: Not permitted" "$OUT/ship-carol.log" && grep -q "Ask your organisation.s HCG administrator" "$OUT/ship-carol.log" && ! grep -q "store.hakmicapitalgroup.com/apps" "$OUT/ship-carol.log" && [ "$(curl -s -H "authorization: Bearer $(ctl token "{\"sub\":\"e2e-alice\"}" | jq -r .token)" "$STORE/api/v1/publishing/releases?productId=fixture-quasar-shell" | jq "[.releases[] | select(.version==\"9.9.9\")] | length")" = 0 ]'
sed 's/^/    /' "$OUT/ship-carol.log" | grep -E "HCG Store:|signed in|^    Feature" | tee -a "$RESULTS"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "authorization: Bearer $CAROL" -H 'content-type: application/json' "$STORE/api/v1/publishing/releases/$REL/artifacts" \
  -d '{"fileName":"evil.zip","platform":"windows","format":"zip","sizeBytes":10,"sha256":"'"$(printf x | sha256sum | cut -c1-64)"'"}')
check "7f Adding a file to someone else's draft without permission is refused ($code)" '[ "$code" = 403 ]'
code=$(curl -s -o /dev/null -w '%{http_code}' -X PUT -H 'content-range: bytes 0-0/1' --data-binary x "$STORE/publishing-upload/v1/forged.token")
check "7g A forged upload URL is refused ($code)" '[ "$code" = 403 ] || [ "$code" = 400 ]'

# Submit (Maker), then 5: a person reviews and approves in the Store (web session, other person, typed version).
as_alice; dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --product fixture-quasar-shell --edition standard --release-version 1.3.0 --build-number 7 --release "$REL" \
  --file "$S/HCGSample-1.3.0.ipa" --file "$S/HCGSample-1.3.0-windows.zip" --file "$S/hcg-sample_1.3.0_amd64.deb" --submit > "$OUT/ship-2.log" 2>&1
check "Re-running against the same draft skips verified files and submits for manual review" 'grep -q "Already uploaded and verified" "$OUT/ship-2.log" && grep -q "Submitted for manual review: submitted" "$OUT/ship-2.log"'
ctl approve "{\"releaseId\":\"$REL\",\"eligibility\":\"public\",\"confirm\":\"1.3.0\",\"approver\":\"e2e-alice\"}" > "$OUT/approve-self.json"
check "Public approval by the publisher is refused (separation of duties / no approver permission)" '[ "$(jq -r .result.error "$OUT/approve-self.json")" != null ]'
ctl approve "{\"releaseId\":\"$REL\",\"eligibility\":\"public\",\"confirm\":\"1.3.0\",\"approver\":\"e2e-bob\"}" > "$OUT/approve.json"
jq -r .reviewPage "$OUT/approve.json" > "$OUT/review.html"
check "5 The Store's review page shows the release metadata (version, build, notes, all three SHA-256)" \
  'grep -q "1.3.0" "$OUT/review.html" && grep -q "build 7" "$OUT/review.html" && grep -q "Harmless Ship to Store sample." "$OUT/review.html" && for f in "$S"/HCGSample-1.3.0.ipa "$S"/HCGSample-1.3.0-windows.zip "$S"/hcg-sample_1.3.0_amd64.deb; do grep -q "$(sha256sum "$f" | cut -c1-64)" "$OUT/review.html" || exit 1; done'
check "5b Each file's kind and install label, from Maker and from HCG Store: ad hoc IPA (registered devices only), Windows ZIP (signature not checked), Linux package; only the Linux package is marked installable as is" \
  'grep -q "install: Ad hoc-signed: installs only on the devices listed" "$OUT/ship-1.log" && grep -q "Ad hoc-signed: installs only on the devices listed" "$OUT/review.html" && grep -q "Signature not checked" "$OUT/review.html" && grep -q "Linux package: no platform signature" "$OUT/review.html" && [ "$(grep -o "class=\"ok\"" "$OUT/review.html" | wc -l)" = 1 ]'
check "Approved for public by a different person in a browser session" '[ "$(jq -r .result.approvedEligibility "$OUT/approve.json")" = public ]'

# 6/7 after approval: paid → entitlement required.
check "7c Customer without entitlement refused after approval (403)" '[ "$(dl "$CAROL")" = 403 ]'
ctl grant '{"sub":"e2e-carol","central":"fixture.quasar.shell","edition":"standard"}' >/dev/null
URL=$(curl -s -X POST -H "authorization: Bearer $CAROL" "$STORE/api/v1/publishing/artifacts/$IPA_ID/download-url" | jq -r .url)
curl -s -o "$OUT/carol.ipa" "$URL"
check "6 Entitled customer downloads the exact file (SHA-256 matches)" '[ "$(sha256sum "$OUT/carol.ipa" | cut -c1-64)" = "$(sha256sum "$S/HCGSample-1.3.0.ipa" | cut -c1-64)" ]'
check "7d A modified link is refused" '[ "$(curl -s -o /dev/null -w "%{http_code}" "${URL}x")" != 200 ]'

# 8: rollback (withdraw) stops delivery immediately; failed-upload recovery resumes an interrupted upload.
as_alice; dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --release "$REL" --withdraw "E2E rollback test" > "$OUT/withdraw.log" 2>&1
check "8a Rollback: the withdrawn release stops delivering an already-issued link (410)" '[ "$(curl -s -o /dev/null -w "%{http_code}" "$URL")" = 410 ] && grep -q "withdrawn" "$OUT/withdraw.log"'
head -c 6000000 /dev/urandom > "$S/ipa/Payload/HCGSample.app/HCGSample"; rm -f "$S/HCGSample-1.3.1.ipa"
(cd "$S/ipa" && zip -qr ../HCGSample-1.3.1.ipa Payload)
SHIP2=(dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --product fixture-quasar-shell --edition standard --release-version 1.3.1 --build-number 8 --distribution paid --eligibility private --note "Recovery test." --file "$S/HCGSample-1.3.1.ipa")
# Start the upload, and kill Maker (SIGKILL) as soon as HCG Store has confirmed part of the file but not all of it.
as_alice; "${SHIP2[@]}" > "$OUT/ship-3-killed.log" 2>&1 &
UP=$!
TOK=$(ctl token '{"sub":"e2e-alice"}' | jq -r .token)
REL2=""; RECV=0; TOTAL=$(stat -c %s "$S/HCGSample-1.3.1.ipa")
for _ in $(seq 1 600); do
  REL2=$(curl -s -H "authorization: Bearer $TOK" "$STORE/api/v1/publishing/releases?productId=fixture-quasar-shell" | jq -r '.releases[] | select(.version=="1.3.1") | .id')
  [ -n "$REL2" ] && RECV=$(curl -s -H "authorization: Bearer $TOK" "$STORE/api/v1/publishing/releases/$REL2" | jq -r '.artifacts[0].receivedBytes // 0')
  if [ "${RECV:-0}" -gt 0 ] && [ "$RECV" -lt "$TOTAL" ]; then kill -9 $UP; break; fi
  sleep 0.05
done
wait $UP 2>/dev/null
note "Upload killed mid-way: release $REL2 has $RECV of $(stat -c %s "$S/HCGSample-1.3.1.ipa") bytes"
# The rerun is the same command, without the release ID: HCG Store hands back the same draft (no duplicate release).
as_alice; "${SHIP2[@]}" --verify-download > "$OUT/ship-3-resumed.log" 2>&1
check "8b Failed-upload recovery: the same command again continued draft $REL2, resumed the interrupted upload and the file verified" \
  'grep -q "Continued the existing draft $REL2" "$OUT/ship-3-resumed.log" && grep -q "Resuming at byte" "$OUT/ship-3-resumed.log" && grep -q "— matches" "$OUT/ship-3-resumed.log"'
check "8c No duplicate release was created for 1.3.1 build 8" \
  '[ "$(curl -s -H "authorization: Bearer $TOK" "$STORE/api/v1/publishing/releases?productId=fixture-quasar-shell" | jq "[.releases[] | select(.version==\"1.3.1\")] | length")" = 1 ]'

# Simulator builds are refused before any byte is sent, with the reason.
mkdir -p "$S/sim/Payload/HCGSample.app"
printf '<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.example.hcg.sample</string><key>DTPlatformName</key><string>iphonesimulator</string><key>CFBundleSupportedPlatforms</key><array><string>iPhoneSimulator</string></array></dict></plist>' > "$S/sim/Payload/HCGSample.app/Info.plist"
echo x > "$S/sim/Payload/HCGSample.app/HCGSample"; rm -f "$S/HCGSample-sim.ipa"; (cd "$S/sim" && zip -qr ../HCGSample-sim.ipa Payload)
as_alice; dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --product fixture-quasar-shell --edition standard --release-version 1.3.2 --build-number 9 \
  --distribution paid --eligibility private --file "$S/HCGSample-sim.ipa" > "$OUT/ship-sim.log" 2>&1; rc=$?
check "8d An iOS Simulator build is refused with the reason (exit $rc), never uploaded" '[ $rc = 2 ] && grep -q "iOS Simulator build" "$OUT/ship-sim.log" && grep -q "cannot be installed on an iPhone or iPad" "$OUT/ship-sim.log"'
AUDIT=$(curl -s -H "authorization: Bearer $TOK" "$STORE/api/v1/publishing/releases/$REL")
check "Audit log intact and complete for the release" '[ "$(echo "$AUDIT" | jq -r .auditIntact)" = true ] && echo "$AUDIT" | jq -r ".audit[].action" | tr "\n" " " | grep -q "release.draft-created.*artifact.verified.*release.submitted.*release.approved.*artifact.downloaded.*release.withdrawn"'
echo "$AUDIT" | jq -r '.audit[] | "    audit: \(.action) by \(.actor)"' | tee -a "$RESULTS"

# ── 9. A REAL build: New Build (HelloAvalonia, .NET, cross-built for Windows x64) → "Ship to Store…" on the result →
#       HCG Connect sign-in → private draft; then an approver reviews the draft in HCG Store and downloads the bytes. ──
if [ "${SHIP_REAL_BUILD:-1}" = 1 ]; then
  as_alice
  dotnet run --project tools/ui-driver --no-build -- --ship-build --source tests/Fixtures/HelloAvalonia --ship-store "$INFO" --ship-build-number 10 \
    --output "$OUT/real-build" --screenshots "$OUT/real-build/screenshots" --prefix ship-build > "$OUT/real-build.log" 2>&1; rc=$?
  grep -E "^(PASS|FAIL)|^Artifact|^File|^Not offered|^Release:" "$OUT/real-build.log" | sed 's/^/    /' | tee -a "$RESULTS"
  check "9a Real build → Ship to Store from the build result → authorised upload → DRAFT release (UI driver exit $rc)" '[ $rc = 0 ] && grep -q "UI DRIVER: PASSED" "$OUT/real-build.log"'
  RB_REL=$(jq -r .releaseId "$OUT/real-build/ship-build.json" 2>/dev/null); RB_SHA=$(jq -r .sha256 "$OUT/real-build/ship-build.json" 2>/dev/null)
  ctl review "{\"releaseId\":\"$RB_REL\",\"reviewer\":\"e2e-bob\"}" > "$OUT/review-real.json"
  check "9b The draft is visible in HCG Store to HCG's approver (review page: draft, portable app, unsigned label)" \
    '[ "$(jq -r .reviewPageStatus "$OUT/review-real.json")" = 200 ] && jq -r .reviewPage "$OUT/review-real.json" | grep -q "Status: <b>draft</b>" && jq -r .reviewPage "$OUT/review-real.json" | grep -q "Unsigned: Windows SmartScreen"'
  check "9c Draft review download (approver, browser session) returns the build's exact bytes (SHA-256 $RB_SHA)" \
    '[ "$(jq -r ".downloads[0].sha256" "$OUT/review-real.json")" = "$RB_SHA" ] && [ "$(jq -r ".downloads[0].basis" "$OUT/review-real.json")" = approver-review ]'
  check "9d The draft is not visible to a customer (404) or anonymously (401)" \
    '[ "$(ctl review "{\"releaseId\":\"$RB_REL\",\"reviewer\":\"e2e-carol\"}" | jq -r .reviewPageStatus)" = 404 ] && [ "$(curl -s -o /dev/null -w "%{http_code}" "$STORE/publishing/releases/$RB_REL")" = 401 ]'
else
  note "Real build skipped (SHIP_REAL_BUILD=0)"
fi
exit $fail
