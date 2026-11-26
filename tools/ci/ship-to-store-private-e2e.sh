#!/bin/bash
# Ship to Store with public publication switched off: the TEST HCG Store server (HCG Store repository,
# server/test/ship-e2e-server.ts, HCG_STORE_PUBLISHING=submissions) and the mock HCG Connect provider. A GENUINE
# executable is built (a .NET console program published for win-x64), packaged by Executable Maker into a portable ZIP
# with its Release Passport, uploaded as a private draft, verified, and left waiting privately for approval.
# Nothing is deployed, no real HCG system is contacted, and nothing becomes public.
#
# Usage: tools/ci/ship-to-store-private-e2e.sh <HCG Store web-hcg-store dir> <out dir> [HCGExecutableMaker.dll]
set -u
STORE_DIR="$(cd "$1" && pwd)"; OUT="$(mkdir -p "$2" && cd "$2" && pwd)"
MAKER="${3:-src/HCG.ExecutableMaker.App/bin/Release/net10.0/HCGExecutableMaker.dll}"
RESULTS="$OUT/results.txt"; : > "$RESULTS"
fail=0
ok()  { echo "PASS  $1" | tee -a "$RESULTS"; }
bad() { echo "FAIL  $1" | tee -a "$RESULTS"; fail=1; }
check() { if eval "$2"; then ok "$1"; else bad "$1"; fi; }
VERSION=2.0.0-beta.1

# ── 0 · A genuine executable: a .NET console program for Windows x64, packaged by Executable Maker (ZIP + passport) ──
G="$OUT/genuine"; rm -rf "$G"; mkdir -p "$G"
dotnet new console -n QuasarShell -o "$G/app" --force >/dev/null
cat > "$G/app/Program.cs" <<'EOF'
// A genuine Windows console program for the Ship to Store end-to-end test (harmless: prints its version).
Console.WriteLine($"Quasar Shell {typeof(Program).Assembly.GetName().Version} — HCG Ship to Store test build");
EOF
(cd "$G/app" && git init -q && git -c user.email=ci@example.test -c user.name="HCG CI" add -A && git -c user.email=ci@example.test -c user.name="HCG CI" commit -qm "Quasar Shell 2.0.0-beta.1 test build")
COMMIT=$(git -C "$G/app" rev-parse HEAD)
dotnet publish "$G/app" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:Version=2.0.0 -p:DebugType=none -o "$G/publish" > "$OUT/publish.log" 2>&1
dotnet "$MAKER" --package-windows --app-dir "$G/publish" --arch x64 --formats zip --name "Quasar Shell" --publisher "HCG Test" --app-id com.example.quasarshell \
  --version "$VERSION" --exe QuasarShell.exe --build-number 7 --product-key fixture-quasar-shell --edition standard --out "$G/out" --dist "$G/dist" \
  --dist-name "QuasarShell-{version}-{rid}" --built-with ".NET SDK $(dotnet --version)" --source-repo "$G/app" > "$OUT/package.log" 2>&1
ZIP="$G/dist/QuasarShell-$VERSION-win-x64.zip"
SHA=$(sha256sum "$ZIP" | cut -c1-64)
check "0 Genuine executable built (PE32+ x64 QuasarShell.exe) and packaged by Executable Maker: $(basename "$ZIP"), SHA-256 $SHA, with its Release Passport" \
  '[ -s "$ZIP" ] && [ -s "$ZIP.release-passport.json" ] && unzip -p "$ZIP" "*QuasarShell.exe" | head -c 2 | grep -q MZ && [ "$(jq -r .artifact.sha256 "$ZIP.release-passport.json")" = "$SHA" ] && [ "$(jq -r .source.commit "$ZIP.release-passport.json")" = "$COMMIT" ]'

# ── TEST Store, first with Ship to Store switched off (what the deployed server does today) ──
start_store() {   # $1 = publishing mode; $2 = info file. DROP_CHUNK cuts off one upload chunk once (resumable test).
  rm -f "$2"
  (cd "$STORE_DIR" && SHIP_E2E_PUBLISHING="$1" DROP_CHUNK=2 npx tsx server/test/ship-e2e-server.ts "$2" > "$OUT/server-$1.log" 2>&1) &
  for _ in $(seq 1 120); do [ -s "$2" ] && break; sleep 0.5; done
}
trap 'pkill -f "ship-e2e-server.ts $OUT/" 2>/dev/null; true' EXIT
maker_home() {   # $1 = info file, $2 = person: a fresh Maker data folder with Maker's own HCG Connect client for the mock provider
  export HCG_EXECUTABLE_MAKER_HOME="$OUT/maker-home-$(basename "$1" .json)-$2"; rm -rf "$HCG_EXECUTABLE_MAKER_HOME"; mkdir -p "$HCG_EXECUTABLE_MAKER_HOME"
  local iss; iss=$(jq -r .issuer "$1")
  jq -n --arg iss "$iss" --arg c "$(jq -r .makerClientId "$1")" '{contractVersion:"hcg.executable-maker.connect/1", clientId:$c, issuer:$iss,
    authorizationEndpoint:($iss+"/oauth/authorize"), tokenEndpoint:($iss+"/oauth/token"), userinfoEndpoint:($iss+"/oauth/userinfo"),
    jwksEndpoint:($iss+"/.well-known/jwks.json"), apiBaseUrl:($iss|sub("/auth/v1$";""))}' > "$HCG_EXECUTABLE_MAKER_HOME/hcg-connect.json"
}
sign_in_as() { curl -s -X POST "${ISS%/auth/v1}/__test/next-user/$1" >/dev/null; }
ctl() { curl -s -X POST "$CTL/$1" -H 'content-type: application/json' -d "$2"; }

OFF="$OUT/store-off.json"; start_store disabled "$OFF"
STORE=$(jq -r .storeOrigin "$OFF"); ISS=$(jq -r .issuer "$OFF"); CTL=$(jq -r .control "$OFF")
maker_home "$OFF" alice
ctl grant '{"sub":"e2e-alice","central":"hcg.store.publish","edition":"*"}' >/dev/null
# Signed out first: the diagnostics say so, and still read the server.
dotnet "$MAKER" --ship-to-store --store-status --store "$STORE" > "$OUT/diag-off-signed-out.txt" 2>&1; rc=$?
check "D1 Diagnostics while signed out: server reachable, API v1, HCG Connect sign-in missing (exit $rc)" \
  '[ $rc = 5 ] && grep -q "\[OK\] Store server connectivity" "$OUT/diag-off-signed-out.txt" && grep -q "\[OK\] API version compatibility" "$OUT/diag-off-signed-out.txt" && grep -q "\[Problem\] HCG Connect authentication: Not signed in" "$OUT/diag-off-signed-out.txt"'
sign_in_as e2e-alice
dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --product fixture-quasar-shell --edition standard --release-version "$VERSION" --build-number 7 \
  --distribution free --eligibility private --file "$ZIP" > "$OUT/ship-off.log" 2>&1; rc=$?
dotnet "$MAKER" --ship-to-store --store-status --sign-in --store "$STORE" --diagnostics-out "$OUT/diag-off.txt" > /dev/null 2>&1; rc2=$?
check "D2 Ship to Store switched off on the server: refused (exit $rc), and the diagnostics name it stage by stage with the server's reason, not one generic status (exit $rc2)" \
  '[ $rc != 0 ] && [ $rc2 = 5 ] && grep -q "\[OK\] HCG Connect authentication" "$OUT/diag-off.txt" && grep -q "\[OK\] Token validity" "$OUT/diag-off.txt" && grep -q "\[OK\] Desktop client registration" "$OUT/diag-off.txt" && grep -q "\[Problem\] Private draft creation: Switched off" "$OUT/diag-off.txt" && grep -q "HCG_STORE_PUBLISHING=disabled" "$OUT/diag-off.txt" && grep -q "HCG_STORE_PUBLISHING=submissions" "$OUT/diag-off.txt" && grep -q "Server: GET /api/v1/publishing/status" "$OUT/diag-off.txt"'
pkill -f "ship-e2e-server.ts $OFF" 2>/dev/null; sleep 1

# ── TEST Store with private submissions on and public publication off ──
INFO="$OUT/store.json"; start_store submissions "$INFO"
STORE=$(jq -r .storeOrigin "$INFO"); ISS=$(jq -r .issuer "$INFO"); CTL=$(jq -r .control "$INFO"); CLIENT=$(jq -r .makerClientId "$INFO")
echo "TEST Store $STORE (publishing: submissions) · issuer $ISS" | tee -a "$RESULTS"
maker_home "$INFO" alice
ctl grant '{"sub":"e2e-alice","central":"hcg.store.publish","edition":"*"}' >/dev/null
ctl grant '{"sub":"e2e-bob","central":"hcg.store.approve","edition":"*"}' >/dev/null
ctl grant '{"sub":"e2e-bob","central":"store.release.publish","edition":"*"}' >/dev/null
sign_in_as e2e-alice

# 1 · Diagnostics: every check passes for uploads; public publication is off, by design.
dotnet "$MAKER" --ship-to-store --store-status --sign-in --store "$STORE" --diagnostics-out "$OUT/diag.txt" > /dev/null 2>&1; rc=$?
check "1 Diagnostics: connectivity, API v1, HCG Connect, Maker's own client ($CLIENT) registered, token verified by the Store, store.publish granted, products listed, draft/upload/submission available; public publication Off (exit $rc)" \
  '[ $rc = 0 ] && for c in "Store server connectivity" "API version compatibility" "HCG Connect authentication" "Desktop client registration" "Token validity" "Product publishing permissions" "Authorized product availability" "Private draft creation" "Artifact upload availability" "Release submission"; do grep -q "\[OK\] $c" "$OUT/diag.txt" || exit 1; done && grep -q "\[Off\] Public publication" "$OUT/diag.txt" && grep -q "client $CLIENT" "$OUT/diag.txt" && grep -q "fixture-quasar-shell" "$OUT/diag.txt"'
check "1b Diagnostics contain no token, cookie or credential" '! grep -qE "eyJ[A-Za-z0-9_-]{10,}\.|Bearer [A-Za-z0-9]|refresh_token|__Host-" "$OUT/diag.txt"'
sed 's/^/    /' "$OUT/diag.txt" | tee -a "$RESULTS" >/dev/null

# 2–6 · Private draft, resumable upload (one chunk dropped), server SHA-256, passport, submission, Release Manager.
dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --product fixture-quasar-shell --edition standard --release-version "$VERSION" --build-number 7 \
  --note "Genuine test build, private." --distribution free --eligibility private --file "$ZIP" --verify-download --submit > "$OUT/ship.log" 2>&1; rc=$?
REL=$(grep -oE "Release rel-[a-f0-9]{16}" "$OUT/ship.log" | head -1 | cut -d" " -f2)
check "2 Private draft created ($REL), requested private" '[ -n "$REL" ] && grep -q "— draft; requested private" "$OUT/ship.log"'
check "3 Resumable upload: a dropped chunk was retried and the upload resumed at the byte the Store had" 'grep -q "Attempt 1 failed" "$OUT/ship.log" && grep -q "Verified by HCG Store: SHA-256 $SHA matches" "$OUT/ship.log"'
check "4 Server-side checksum: HCG Store's SHA-256 equals this file's, and the file downloaded back from the Store matches" 'grep -q ": verified · SHA-256 $SHA" "$OUT/ship.log" && grep -q "— matches" "$OUT/ship.log"'
check "5 Release Passport attached (matched by HCG Store to the stored bytes)" 'grep -q "Release Passport attached" "$OUT/ship.log" && grep -q "QuasarShell-$VERSION-win-x64.zip: Release Passport attached" "$OUT/ship.log"'
check "6 Submitted for review; Release Manager lists it waiting for approval, audit trail intact (exit $rc)" \
  '[ $rc = 0 ] && grep -q "Submitted for manual review: submitted" "$OUT/ship.log" && grep -q "Release Manager: .* stage awaiting-approval, 1 file(s); audit trail intact" "$OUT/ship.log"'
grep -E "^File |  .*: (Verified|Uploading.*Attempt|verified)|Release Passport|Release Manager|Submitted|^Download " "$OUT/ship.log" | sed 's/^/    /' | tee -a "$RESULTS"

# 7 · Unauthorised uploads are refused: an account HCG has not allowed, no token, a forged upload URL, a wrong checksum.
sign_in_as e2e-carol
maker_home "$INFO" carol
dotnet "$MAKER" --ship-to-store --sign-in --store "$STORE" --product fixture-quasar-shell --edition standard --release-version 9.9.9 --build-number 1 \
  --distribution free --eligibility private --file "$ZIP" > "$OUT/ship-carol.log" 2>&1; rc=$?
dotnet "$MAKER" --ship-to-store --store-status --sign-in --store "$STORE" > "$OUT/diag-carol.txt" 2>&1
check "7a Upload by an account without HCG's publishing permission is refused (exit $rc); its diagnostics say store.publish is not granted" \
  '[ $rc != 0 ] && grep -q "\[Problem\] Product publishing permissions: HCG has not granted" "$OUT/diag-carol.txt"'
ALICE=$(ctl token '{"sub":"e2e-alice"}' | jq -r .token)
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'content-type: application/json' "$STORE/api/v1/publishing/releases/$REL/artifacts" -d '{}')
check "7b Declaring a file without a sign-in is refused ($code)" '[ "$code" = 401 ]'
code=$(curl -s -o /dev/null -w '%{http_code}' -X PUT -H 'content-range: bytes 0-0/1' --data-binary x "$STORE/publishing-upload/v1/forged.token")
check "7c A forged upload URL is refused ($code)" '[ "$code" = 403 ] || [ "$code" = 400 ]'
DRAFT=$(curl -s -X POST -H "authorization: Bearer $ALICE" -H 'content-type: application/json' "$STORE/api/v1/publishing/releases" \
  -d '{"productId":"fixture-quasar-shell","editionId":"standard","version":"2.0.1","build":"8","distribution":"free","eligibility":"private"}' | jq -r .id)
D=$(curl -s -X POST -H "authorization: Bearer $ALICE" -H 'content-type: application/json' "$STORE/api/v1/publishing/releases/$DRAFT/artifacts" \
  -d '{"fileName":"wrong.exe","platform":"windows","format":"exe","sizeBytes":2,"sha256":"'"$(printf zz | sha256sum | cut -c1-64)"'","buildProvider":"manual-upload"}')
curl -s -o /dev/null -X PUT -H 'content-range: bytes 0-1/2' --data-binary MZ "$(echo "$D" | jq -r .uploadUrl)"
ST=$(curl -s -X POST -H "authorization: Bearer $ALICE" "$STORE/api/v1/publishing/artifacts/$(echo "$D" | jq -r .artifact.id)/complete" | jq -r .artifact.status)
check "7d Bytes that differ from the declared SHA-256 are not accepted by the server ($ST)" '[ "$ST" != verified ] && [ -n "$ST" ]'

# 8 · Public publication stays disabled: an approver may approve in the browser; publication is refused; nothing is delivered.
ctl approve "{\"releaseId\":\"$REL\",\"eligibility\":\"private\",\"confirm\":\"$VERSION\",\"approver\":\"e2e-bob\"}" > "$OUT/approve.json"
ctl publish "{\"releaseId\":\"$REL\",\"confirm\":\"$VERSION\",\"publisher\":\"e2e-bob\"}" > "$OUT/publish.json"
STATUS=$(curl -s "$STORE/api/v1/publishing/status")
check "8 Public publication disabled: approval works ($(jq -r '.result.approvedEligibility // .result.error' "$OUT/approve.json")), publication refused ($(jq -c '.result.error // .result' "$OUT/publish.json" | head -c 120)), status says off, no public release" \
  '[ "$(jq -r .result.approvedEligibility "$OUT/approve.json")" = private ] && grep -q publication_disabled "$OUT/publish.json" && [ "$(echo "$STATUS" | jq -r .stages.publicPublication.available)" = false ] && ! curl -s "$STORE/api/public/v1/release-resolution/fixture-quasar-shell" | grep -q "$VERSION"'

# 9 · The Ship to Store page in the real app (headless): signed out it contacts no Store; signed in, Check connection
#     shows the eleven checks and the products; screenshot ship-diagnostics.png.
UI=tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll
if [ -f "$UI" ]; then
  sign_in_as e2e-alice
  dotnet "$UI" --source "$ZIP" --ship-page --ship-store "$INFO" --output "$OUT/ui" --screenshots "$OUT/shots" --prefix ship > "$OUT/ui.log" 2>&1; rc=$?
  check "9 Ship to Store page (real app, headless): eleven diagnostics on the page, ready to upload private drafts, products listed (exit $rc)" \
    '[ $rc = 0 ] && grep -q "PASS  Eleven separate connection checks are shown" "$OUT/ui.log" && ls "$OUT"/shots/*ship-diagnostics.png >/dev/null 2>&1'
fi

echo; [ $fail = 0 ] && echo "ALL PASSED" | tee -a "$RESULTS" || echo "SOME FAILED" | tee -a "$RESULTS"
exit $fail
