#!/bin/bash
# Distribution Studio acceptance (1–18) against the TEST HCG Store server (HCG Store repository,
# server/test/ship-e2e-server.ts with the REAL catalogue, so HCG Mail is a listed product) and the mock HCG Connect
# provider. Genuine executables are built (a .NET console program published for win-x64 and win-arm64), packaged by
# Executable Maker into portable ZIPs with Release Passports, and uploaded as private drafts. A person's browser
# approval and publication are simulated only through the TEST server's control endpoint (standing in for HCG Store's
# website). Nothing is deployed, no real HCG system is contacted, and Maker publishes nothing.
#
# Usage: tools/ci/distribution-studio-e2e.sh <HCG Store web-hcg-store dir> <out dir> [HCGExecutableMaker.dll]
set -u
STORE_DIR="$(cd "$1" && pwd)"; OUT="$(mkdir -p "$2" && cd "$2" && pwd)"
MAKER="${3:-src/HCG.ExecutableMaker.App/bin/Release/net10.0/HCGExecutableMaker.dll}"
RESULTS="$OUT/results.txt"; : > "$RESULTS"
fail=0
ok()  { echo "PASS  $1" | tee -a "$RESULTS"; }
bad() { echo "FAIL  $1" | tee -a "$RESULTS"; fail=1; }
check() { if eval "$2"; then ok "$1"; else bad "$1"; fi; }
WHO=e2e-alice
# Each Maker run signs in as $WHO (the TEST provider's next user; a browser approval switches it to the approver).
M() { [ -n "${ISS:-}" ] && curl -s -X POST "${ISS%/auth/v1}/__test/next-user/$WHO" >/dev/null; dotnet "$MAKER" "$@"; }

# ── 0 · Genuine executables: one .NET console program, published for Windows x64 and ARM64, packaged by Maker ──
G="$OUT/genuine"; [ "${REUSE_GENUINE:-}" = 1 ] || rm -rf "$G"; mkdir -p "$G"
[ -d "$G/app" ] || dotnet new console -n MailProbe -o "$G/app" --force >/dev/null
cat > "$G/app/Program.cs" <<'EOF'
// A genuine Windows console program for the Distribution Studio acceptance run (harmless: prints its version).
Console.WriteLine($"Mail Probe {typeof(Program).Assembly.GetName().Version} — HCG Distribution Studio test build");
EOF
[ -d "$G/app/.git" ] || (cd "$G/app" && git init -q && git -c user.email=ci@example.test -c user.name="HCG CI" add -A && git -c user.email=ci@example.test -c user.name="HCG CI" commit -qm "Mail Probe test build")
pkg() {   # $1 rid, $2 arch, $3 version, $4 build
  [ -s "$G/dist/MailProbe-$3-$1.zip" ] && { echo "$G/dist/MailProbe-$3-$1.zip"; return; }
  dotnet publish "$G/app" -c Release -r "$1" --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:Version="${3%%-*}" -p:DebugType=none -o "$G/publish-$1-$3" > "$OUT/publish-$1-$3.log" 2>&1
  M --package-windows --app-dir "$G/publish-$1-$3" --arch "$2" --formats zip --name "Mail Probe" --publisher "HCG Test" --app-id com.example.mailprobe \
    --version "$3" --exe MailProbe.exe --build-number "$4" --product-key hcg-mail --edition windows-desktop --out "$G/out-$1-$3" --dist "$G/dist" \
    --dist-name "MailProbe-{version}-{rid}" --built-with ".NET SDK $(dotnet --version)" --source-repo "$G/app" > "$OUT/package-$1-$3.log" 2>&1
  echo "$G/dist/MailProbe-$3-$1.zip"
}
V1=1.0.0; V2=2.0.0; VB=1.1.0-beta.1
Z1=$(pkg win-x64 x64 $V1 1); Z1A=$(pkg win-arm64 arm64 $V1 2); Z2=$(pkg win-x64 x64 $V2 3); ZB=$(pkg win-x64 x64 $VB 4)
sha() { sha256sum "$1" | cut -c1-64; }
check "0 Genuine executables built and packaged by Executable Maker with Release Passports: $(for z in "$Z1" "$Z1A" "$Z2" "$ZB"; do printf '%s ' "$(basename "$z")"; done)" \
  'for z in "$Z1" "$Z1A" "$Z2" "$ZB"; do [ -s "$z" ] && [ -s "$z.release-passport.json" ] && unzip -p "$z" "*MailProbe.exe" | head -c 2 | grep -q MZ && [ "$(jq -r .artifact.sha256 "$z.release-passport.json")" = "$(sha "$z")" ] || exit 1; done'

# ── TEST Store helpers ──
start_store() {   # $1 = publishing mode, $2 = info file
  rm -f "$2"
  (cd "$STORE_DIR" && SHIP_E2E_CATALOGUE=real SHIP_E2E_PUBLISHING="$1" DROP_CHUNK="${DROP_CHUNK:-0}" SLOW_UPLOAD_MS="${SLOW_UPLOAD_MS:-0}" npx tsx server/test/ship-e2e-server.ts "$2" > "$OUT/server-$1.log" 2>&1) &
  for _ in $(seq 1 120); do [ -s "$2" ] && break; sleep 0.5; done
  STORE=$(jq -r .storeOrigin "$2"); ISS=$(jq -r .issuer "$2"); CTL=$(jq -r .control "$2"); CLIENT=$(jq -r .makerClientId "$2")
}
trap 'pkill -f "ship-e2e-server.ts $OUT/" 2>/dev/null; true' EXIT
maker_home() {   # $1 = info file, $2 = person
  export HCG_EXECUTABLE_MAKER_HOME="$OUT/maker-home-$(basename "$1" .json)-$2"; rm -rf "$HCG_EXECUTABLE_MAKER_HOME"; mkdir -p "$HCG_EXECUTABLE_MAKER_HOME"
  local iss; iss=$(jq -r .issuer "$1")
  jq -n --arg iss "$iss" --arg c "$(jq -r .makerClientId "$1")" '{contractVersion:"hcg.executable-maker.connect/1", clientId:$c, issuer:$iss,
    authorizationEndpoint:($iss+"/oauth/authorize"), tokenEndpoint:($iss+"/oauth/token"), userinfoEndpoint:($iss+"/oauth/userinfo"),
    jwksEndpoint:($iss+"/.well-known/jwks.json"), apiBaseUrl:($iss|sub("/auth/v1$";""))}' > "$HCG_EXECUTABLE_MAKER_HOME/hcg-connect.json"
}
sign_in_as() { curl -s -X POST "${ISS%/auth/v1}/__test/next-user/$1" >/dev/null; }
ctl() { curl -s -X POST "$CTL/$1" -H 'content-type: application/json' -d "$2"; }
rel_of() { grep -oE "Release rel-[a-f0-9]{16}" "$1" | tail -1 | cut -d" " -f2; }
status_of() { M --ship-to-store --sign-in --store "$STORE" --store-history --product hcg-mail 2>/dev/null | grep -E "^- $1 " | sed -E 's/.* · ([a-z]+) \(.*/\1/'; }

# ════════ TEST Store, publishing "enabled" (approval and publication by a person in the browser) ════════
INFO="$OUT/store.json"; SLOW_UPLOAD_MS=120 start_store enabled "$INFO"
echo "TEST Store $STORE (real catalogue, publishing enabled) · issuer $ISS" | tee -a "$RESULTS"
SHIP=(--ship-to-store --sign-in --store "$STORE" --product hcg-mail --edition windows-desktop --distribution free)
maker_home "$INFO" alice
ctl grant '{"sub":"e2e-alice","central":"store.publish"}' >/dev/null
ctl grant '{"sub":"e2e-bob","central":"store.release.approve"}' >/dev/null
ctl grant '{"sub":"e2e-bob","central":"store.release.publish"}' >/dev/null
sign_in_as e2e-alice

M --ship-to-store --store-status --sign-in --store "$STORE" --diagnostics-out "$OUT/diag.txt" > /dev/null 2>&1; rc=$?
check "Diagnostics against the live contract: ten checks; server, API v1, mode, uploads, HCG Connect, Maker's own client ($CLIENT), store.publish, catalogue, Release Manager all OK (exit $rc)" \
  '[ $rc = 0 ] && for c in "Server availability" "Live API version" "Publishing mode" "Private upload availability" "HCG Connect authentication" "Executable Maker client authorization" "Central publisher permission" "Authorized catalogue" "Release Manager connectivity" "Public publication"; do grep -q "\] $c" "$OUT/diag.txt" || exit 1; done && ! grep -q "\[Problem\]" "$OUT/diag.txt" && grep -q "client $CLIENT" "$OUT/diag.txt"'
check "Diagnostics contain no token, cookie or credential" '! grep -qE "eyJ[A-Za-z0-9_-]{10,}\.|Bearer [A-Za-z0-9]|refresh_token|__Host-" "$OUT/diag.txt"'
sed 's/^/    /' "$OUT/diag.txt" >> "$RESULTS"

# 1 · Retrieve the authorised catalogue.
M "${SHIP[@]}" --store-catalogue > "$OUT/catalogue.txt" 2>&1; rc=$?
check "1 Authorised catalogue retrieved from HCG Store with official metadata (name, identifier, category, status, platforms, editions, icon) (exit $rc)" \
  '[ $rc = 0 ] && grep -q "^- HCG Mail \[hcg-mail\] catalogue; status testing; category communication; icon yes" "$OUT/catalogue.txt" && grep -q "editions: windows-desktop, ios" "$OUT/catalogue.txt" && grep -q "official metadata from HCG Store" "$OUT/catalogue.txt"'
# 2–3 · HCG Mail, Add New Update: comparison before anything is uploaded.
M "${SHIP[@]}" --store-compare --release-version $V1 --build-number 1 --file "$Z1" > "$OUT/compare-1.txt" 2>&1; rc=$?
check "2–3 HCG Mail selected for Add New Update: current version and history read (none yet), version $V1 proposed, no conflict (exit $rc)" \
  '[ $rc = 0 ] && grep -q "current published none" "$OUT/compare-1.txt" && grep -q "proposed $V1 (build 1)" "$OUT/compare-1.txt"'

# 4–9 · Import, inspect, passport, upload to private staging; an upload interrupted half-way resumes; exact checksums.
( M "${SHIP[@]}" --release-version $V1 --build-number 1 --channel stable --eligibility public --note "First Windows x64 release." --file "$Z1" > "$OUT/ship-1-interrupted.log" 2>&1 ) &
PID=$!; for _ in $(seq 1 600); do grep -q "Uploading [1-9][0-9,]*/" "$OUT/ship-1-interrupted.log" 2>/dev/null && break; sleep 0.1; done
pkill -KILL -P $PID 2>/dev/null; kill -KILL $PID 2>/dev/null; wait $PID 2>/dev/null
INTERRUPTED_AT=$(grep -oE "Uploading [0-9,]+/" "$OUT/ship-1-interrupted.log" | tail -1 | grep -oE "[0-9,]+" | tr -d ,)
M "${SHIP[@]}" --release-version $V1 --build-number 1 --channel stable --eligibility public --note "First Windows x64 release." --file "$Z1" --verify-download > "$OUT/ship-1.log" 2>&1; rc=$?
R1=$(rel_of "$OUT/ship-1.log")
check "4 Genuine artifact imported: $(basename "$Z1")" 'grep -q "^File $(basename "$Z1"): Windows · zip · Portable app" "$OUT/ship-1.log"'
check "5 Version and architecture inspected from the file (x64) and its passport (provenance reported from the passport of these bytes)" \
  'grep -q "provenance: commit [0-9a-f]\{40\}" "$OUT/ship-1.log" && jq -e ".architecture == \"x64\"" "$Z1.release-passport.json" >/dev/null'
check "6 Release Passport attached and accepted by HCG Store" 'grep -q "$(basename "$Z1"): Release Passport attached" "$OUT/ship-1.log"'
check "7 Uploaded to private staging: draft $R1 (continued after the interruption, no second release)" '[ -n "$R1" ] && grep -q "— draft; requested public" "$OUT/ship-1.log" && grep -q "Continued the existing draft" "$OUT/ship-1.log"'
check "8 Interrupted upload (process killed at ${INTERRUPTED_AT:-?} bytes) resumed at the byte HCG Store had" '[ "${INTERRUPTED_AT:-0}" -gt 0 ] && grep -qE "Resuming at byte [1-9]" "$OUT/ship-1.log"'
check "9 Exact server checksums: HCG Store's SHA-256 equals the file's, and the bytes downloaded back match (exit $rc)" \
  '[ $rc = 0 ] && grep -q ": verified · SHA-256 $(sha "$Z1")" "$OUT/ship-1.log" && grep -q "— matches" "$OUT/ship-1.log"'
grep -E "^File |Resuming|MB/s|Verified|Passport|Release Manager|^Download " "$OUT/ship-1.log" | head -12 | sed 's/^/    /' >> "$RESULTS"

# 10 · The same release in Release Manager (Maker, the website's browser session, HCG Store Manager's client).
ctl manager '{"sub":"e2e-alice"}' > "$OUT/manager-web.json"
SM=$(ctl token '{"sub":"e2e-alice","client":"store-manager"}' | jq -r .token)
curl -s -H "authorization: Bearer $SM" "$STORE/api/v1/publishing/manager" > "$OUT/manager-sm.json"
check "10 The same release ($R1) opens in Release Manager: Maker, the HCG Store website and HCG Store Manager's client list the same ID and stage; review URL $STORE/publishing/releases/$R1" \
  'grep -q "Release Manager: HCG Mail $V1 build 1 — stage uploaded" "$OUT/ship-1.log" && [ "$(jq -r --arg r "$R1" ".releases[] | select(.id==\$r) | .stage" "$OUT/manager-web.json")" = uploaded ] && [ "$(jq -r --arg r "$R1" ".releases[] | select(.id==\$r) | .stage" "$OUT/manager-sm.json")" = uploaded ] && grep -q "Review in HCG Store: $STORE/publishing/releases/$R1" "$OUT/ship-1.log"'

# v1 is submitted, approved and published by a person in the browser (TEST control endpoint), so it is current.
M "${SHIP[@]}" --release "$R1" --release-version $V1 --build-number 1 --channel stable --eligibility public --file "$Z1" --submit > "$OUT/submit-1.log" 2>&1
ctl approve "{\"releaseId\":\"$R1\",\"eligibility\":\"public\",\"confirm\":\"$V1\",\"approver\":\"e2e-bob\"}" > "$OUT/approve-1.json"
ctl publish "{\"releaseId\":\"$R1\",\"confirm\":\"$V1\",\"publisher\":\"e2e-bob\"}" > "$OUT/publish-1.json"
check "   (setup) $V1 submitted from Maker, approved and published by another person in the browser" '[ "$(jq -r .result.status "$OUT/publish-1.json")" = published ]'

# 11 · Add to Listed Product: Windows ARM64 for the published $V1 → a new revision; the published release is unchanged.
M "${SHIP[@]}" --add-to-release "$R1" --eligibility public --note "Windows ARM64 build of 1.0.0." --file "$Z1A" > "$OUT/add-arm64.log" 2>&1; rc=$?
RA=$(rel_of "$OUT/add-arm64.log")
M "${SHIP[@]}" --store-history > "$OUT/history-after-add.txt" 2>&1
check "11 New platform distribution (Windows ARM64): $V1 is published so its files are not changed; a new revision $V1 build 2 ($RA) holds the ARM64 file (exit $rc)" \
  '[ $rc = 0 ] && grep -q "build 1 is published; its files cannot change" "$OUT/add-arm64.log" && grep -q "Release $RA: HCG Mail $V1 build 2 — draft" "$OUT/add-arm64.log" && grep -A2 "^- $R1 " "$OUT/history-after-add.txt" | grep -q "MailProbe-$V1-win-x64.zip: windows x64 zip · verified" && ! grep -A3 "^- $R1 " "$OUT/history-after-add.txt" | grep -q arm64'

# 12 · Add New Product: a private product draft (not listed, no access); the registration request for HCG.
M "${SHIP[@]/hcg-mail/hcg-notes-test}" --draft-product "HCG Notes (TEST)" --description "Notes, private draft." --category productivity --platform macos --platform windows \
  --registration-out "$OUT/registration" --release-version 0.1.0 > "$OUT/new-product.log" 2>&1; rc=$?
M "${SHIP[@]}" --store-catalogue > "$OUT/catalogue-2.txt" 2>&1
check "12 Private product draft created (category, platforms recorded; not listed publicly; nothing granted) and the central registration request written for HCG (exit $rc)" \
  '[ $rc = 0 ] && grep -q "Draft product hcg-notes-test" "$OUT/new-product.log" && grep -q "^- HCG Notes (TEST) \[hcg-notes-test\] draft; status draft; category productivity" "$OUT/catalogue-2.txt" && jq -e ".product.id==\"hcg-notes-test\"" "$OUT/registration/hcg-notes-test-registration-request/request.json" >/dev/null && [ "$(curl -s -o /dev/null -w "%{http_code}" "$STORE/api/public/v1/release-resolution/hcg-notes-test")" != 200 ]'

# 13 · Version history.
check "13 Version history retrieved: $V1 build 1 published, $V1 build 2 draft" 'grep -q "^- $R1 $V1 build 1 · stable · published" "$OUT/history-after-add.txt" && grep -q "^- $RA $V1 build 2 · stable · draft" "$OUT/history-after-add.txt"'

# The ARM64 revision and a beta-channel release are published too (other architecture, other channel).
for r in "$RA:$V1:$Z1A:2:stable" ; do :; done
M "${SHIP[@]}" --release "$RA" --release-version $V1 --build-number 2 --channel stable --eligibility public --file "$Z1A" --submit > "$OUT/submit-a.log" 2>&1
ctl approve "{\"releaseId\":\"$RA\",\"eligibility\":\"public\",\"confirm\":\"$V1\",\"approver\":\"e2e-bob\"}" >/dev/null
ctl publish "{\"releaseId\":\"$RA\",\"confirm\":\"$V1\",\"publisher\":\"e2e-bob\"}" > "$OUT/publish-a.json"
M "${SHIP[@]}" --release-version $VB --build-number 4 --channel beta --eligibility public --note "Beta channel build." --file "$ZB" --submit > "$OUT/ship-beta.log" 2>&1
RB=$(rel_of "$OUT/ship-beta.log")
ctl approve "{\"releaseId\":\"$RB\",\"eligibility\":\"public\",\"confirm\":\"$VB\",\"approver\":\"e2e-bob\"}" >/dev/null
ctl publish "{\"releaseId\":\"$RB\",\"confirm\":\"$VB\",\"publisher\":\"e2e-bob\"}" > "$OUT/publish-b.json"
check "   (setup) ARM64 revision ($RA) and beta-channel $VB ($RB) published by a person in the browser" '[ "$(jq -r .result.status "$OUT/publish-a.json")" = published ] && [ "$(jq -r .result.status "$OUT/publish-b.json")" = published ]'

# 14 · Supersede request: $V2 (Windows x64, stable) asks to become current and supersede compatible releases.
M "${SHIP[@]}" --store-compare --release-version $V1 --build-number 1 --file "$Z2" > "$OUT/compare-dup.txt" 2>&1; rcd=$?
M "${SHIP[@]}" --store-compare --release-version 0.9.0 --build-number 9 --file "$Z2" > "$OUT/compare-down.txt" 2>&1
M "${SHIP[@]}" --release-version $V2 --build-number 3 --channel stable --eligibility public --note "Version 2." --file "$Z2" --make-current --supersede-compatible --submit > "$OUT/ship-2.log" 2>&1; rc=$?
R2=$(rel_of "$OUT/ship-2.log")
M "${SHIP[@]}" --store-request supersede --release "$R1" --reason "Replaced by $V2 once it is published." --confirm $V1 > "$OUT/request.log" 2>&1; rcr=$?
check "14 Supersede prepared: $V2 ($R2) carries \"make current, supersede compatible\" for the publisher; a recorded supersede request on $V1 changed nothing; duplicate ($rcd) and downgrade are flagged first" \
  '[ $rc = 0 ] && grep -q "Publication request recorded: make current True, supersede compatible" "$OUT/ship-2.log" && [ $rcr = 0 ] && grep -q "Nothing changed" "$OUT/request.log" && [ "$(status_of "$R1")" = published ] && [ $rcd = 6 ] && grep -q "duplicate" "$OUT/compare-dup.txt" && grep -q "downgrade" "$OUT/compare-down.txt"'

# 15 · A failed publication preserves the current release.
ctl publish "{\"releaseId\":\"$R2\",\"confirm\":\"$V2\",\"publisher\":\"e2e-bob\",\"supersede\":true}" > "$OUT/publish-2-early.json"
check "15 Publication of $V2 before approval fails ($(jq -r '.result.error.code' "$OUT/publish-2-early.json")) and $V1 stays published" \
  '[ "$(jq -r .result.error.code "$OUT/publish-2-early.json")" = not_approved ] && [ "$(status_of "$R1")" = published ]'

# 18 · After approval, publication supersedes only the compatible release: other architectures and channels unaffected.
ctl approve "{\"releaseId\":\"$R2\",\"eligibility\":\"public\",\"confirm\":\"$V2\",\"approver\":\"e2e-bob\"}" >/dev/null
ctl publish "{\"releaseId\":\"$R2\",\"confirm\":\"$V2\",\"publisher\":\"e2e-bob\",\"supersede\":true}" > "$OUT/publish-2.json"
M "${SHIP[@]}" --store-history > "$OUT/history-final.txt" 2>&1
check "18 Publication of $V2 (by a person) supersedes $V1 x64 only; the ARM64 revision and the beta channel stay published; $V1's file is kept" \
  '[ "$(jq -r .result.status "$OUT/publish-2.json")" = published ] && [ "$(status_of "$R1")" = superseded ] && [ "$(status_of "$RA")" = published ] && [ "$(status_of "$RB")" = published ] && grep -A2 "^- $R1 " "$OUT/history-final.txt" | grep -q "MailProbe-$V1-win-x64.zip"'
sed 's/^/    /' "$OUT/history-final.txt" | grep -E "^    - " >> "$RESULTS"

# 16 · Unauthorised accounts cannot upload.
sign_in_as e2e-carol; WHO=e2e-carol; maker_home "$INFO" carol
M "${SHIP[@]}" --release-version 9.9.9 --build-number 1 --eligibility private --file "$Z1" > "$OUT/ship-carol.log" 2>&1; rc=$?
M --ship-to-store --store-status --sign-in --store "$STORE" > "$OUT/diag-carol.txt" 2>&1
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'content-type: application/json' "$STORE/api/v1/publishing/releases/$R2/artifacts" -d '{}')
fcode=$(curl -s -o /dev/null -w '%{http_code}' -X PUT -H 'content-range: bytes 0-0/1' --data-binary x "$STORE/publishing-upload/v1/forged.token")
check "16 Unauthorised: an account without store.publish is refused (exit $rc; diagnostics name the permission), no sign-in ($code) and a forged upload URL ($fcode) are refused" \
  '[ $rc != 0 ] && grep -q "\[Problem\] Central publisher permission" "$OUT/diag-carol.txt" && [ "$code" = 401 ] && { [ "$fcode" = 403 ] || [ "$fcode" = 400 ]; }'
pkill -f "ship-e2e-server.ts $INFO" 2>/dev/null; sleep 1; WHO=e2e-alice

# ════════ 17 · TEST Store in "uploads" mode (as production): publication off, authorised private uploads work ════════
UP="$OUT/store-uploads.json"; start_store uploads "$UP"
SHIP=(--ship-to-store --sign-in --store "$STORE" --product hcg-mail --edition windows-desktop --distribution free)
maker_home "$UP" alice
ctl grant '{"sub":"e2e-alice","central":"store.publish"}' >/dev/null
ctl grant '{"sub":"e2e-bob","central":"store.release.approve"}' >/dev/null
sign_in_as e2e-alice
M --ship-to-store --store-status --sign-in --store "$STORE" --diagnostics-out "$OUT/diag-uploads.txt" > /dev/null 2>&1; rcd=$?
M "${SHIP[@]}" --release-version $V1 --build-number 1 --eligibility private --note "Private upload while publication is off." --file "$Z1" --make-current --submit > "$OUT/ship-uploads.log" 2>&1; rc=$?
RU=$(rel_of "$OUT/ship-uploads.log")
ctl approve "{\"releaseId\":\"$RU\",\"eligibility\":\"private\",\"confirm\":\"$V1\",\"approver\":\"e2e-bob\"}" > "$OUT/approve-uploads.json"
check "17 Publication disabled (uploads mode): diagnostics ready (exit $rcd, Public publication Off), the authorised private upload is verified and submitted (exit $rc), approval is refused ($(jq -r .result.error.code "$OUT/approve-uploads.json"))" \
  '[ $rcd = 0 ] && grep -q "\[Off\] Public publication" "$OUT/diag-uploads.txt" && grep -q "\[OK\] Publishing mode: uploads" "$OUT/diag-uploads.txt" && [ $rc = 0 ] && grep -q ": verified · SHA-256 $(sha "$Z1")" "$OUT/ship-uploads.log" && [ "$(jq -r .result.error.code "$OUT/approve-uploads.json")" = publication_disabled ]'

# The Distribution Studio page in the real app (headless) against this Store: screenshots.
UI=tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll
if [ -f "$UI" ]; then
  sign_in_as e2e-alice
  ISS_UI=$ISS; curl -s -X POST "${ISS_UI%/auth/v1}/__test/next-user/e2e-alice" >/dev/null
  dotnet "$UI" --source "$Z2" --ship-page --ship-store "$UP" --ship-product hcg-mail --ship-edition windows-desktop --ship-version 3.0.0 --ship-build-number 30 \
    --output "$OUT/ui" --screenshots "$OUT/shots" --prefix studio > "$OUT/ui.log" 2>&1; rc=$?
  check "UI Distribution Studio page (real app, headless): ten checks, catalogue, history, comparison, private upload verified, sync, versions, new product (exit $rc)" \
    '[ $rc = 0 ] && grep -q "PASS  Ten separate connection checks are shown" "$OUT/ui.log" && ls "$OUT"/shots/*studio-uploaded.png >/dev/null 2>&1'
fi

# Disabled publishing is named precisely.
pkill -f "ship-e2e-server.ts $UP" 2>/dev/null; sleep 1
OFF="$OUT/store-off.json"; start_store disabled "$OFF"
maker_home "$OFF" alice; ctl grant '{"sub":"e2e-alice","central":"store.publish"}' >/dev/null; sign_in_as e2e-alice
M --ship-to-store --store-status --sign-in --store "$STORE" > "$OUT/diag-off.txt" 2>&1; rc=$?
check "Publishing disabled on the server: named in the diagnostics with who fixes it (exit $rc)" '[ $rc = 5 ] && grep -q "\[Problem\] Publishing mode" "$OUT/diag-off.txt" && grep -q "HCG_STORE_PUBLISHING=uploads" "$OUT/diag-off.txt"'

echo; [ $fail = 0 ] && echo "ALL PASSED" | tee -a "$RESULTS" || echo "SOME FAILED" | tee -a "$RESULTS"
exit $fail
