#!/bin/bash
# HCG Sentinel end to end on a real Mac: outside Maker with xcodebuild, then through the packaged Maker app.
# Usage: tools/ci/sentinel-e2e.sh <packaged .app> <out dir>
# Needs: network access to clone HCG Sentinel; the default Xcode (/Applications/Xcode.app) with its iOS platforms.
set -u
APP="$1"; OUT="$2"
EXE="$APP/Contents/MacOS/HCGExecutableMaker"
SENTINEL_URL=https://github.com/F1r3srp3nt/HCG-Sentinel.git
PRE_MODERN=9aeef360fac67275db3e2cc3fab6c03d34da5676   # last commit without HCGSentinelModern
FIRST_MODERN=810c6f99103a5e4f60e91a6fecda8259be01e0da # HCGSentinelModern added; project default and tests still 14.7
CURRENT=03bc6e55f0a75c32df0a05415d70b55e95f5ad9d      # main: no implicit 14.7 in the modern scheme
mkdir -p "$OUT"
RESULTS="$OUT/results.txt"; : > "$RESULTS"
fail=0
ok()   { echo "PASS  $1" | tee -a "$RESULTS"; }
bad()  { echo "FAIL  $1" | tee -a "$RESULTS"; fail=1; }
note() { echo "NOTE  $1" | tee -a "$RESULTS"; }
check() { if eval "$2"; then ok "$1"; else bad "$1"; fi; }

export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
echo "### Xcode used outside Maker" | tee -a "$RESULTS"
xcodebuild -version | tee -a "$RESULTS"
for p in iPhoneOS:iphoneos iPhoneSimulator:iphonesimulator; do
  f="$DEVELOPER_DIR/Platforms/${p%%:*}.platform/Developer/SDKs/${p%%:*}.sdk/SDKSettings.json"
  echo "${p%%:*} SDK $(plutil -extract Version raw "$f"): deployment $(plutil -extract "SupportedTargets.${p##*:}.MinimumDeploymentTarget" raw "$f")–$(plutil -extract "SupportedTargets.${p##*:}.MaximumDeploymentTarget" raw "$f")" | tee -a "$RESULTS"
done

# ── 1. Outside Maker: schemes and effective deployment targets at three Sentinel revisions ──
git clone --quiet "$SENTINEL_URL" "$OUT/sentinel.git-cache"
for rev in $PRE_MODERN $FIRST_MODERN $CURRENT; do
  dir="$OUT/rev-${rev:0:7}"
  git clone --quiet "$OUT/sentinel.git-cache" "$dir" && git -C "$dir" checkout --quiet "$rev"
  echo "### Sentinel ${rev:0:7} — $(git -C "$dir" log -1 --format='%ci %s')" | tee -a "$RESULTS"
  (cd "$dir" && xcodebuild -list -json -project HCGSentinel.xcodeproj 2>/dev/null) > "$dir.list.json"
  schemes=$(python3 -c 'import json,sys; print(" ".join(json.load(open(sys.argv[1]))["project"]["schemes"]))' "$dir.list.json")
  echo "schemes: $schemes" | tee -a "$RESULTS"
  for s in $schemes; do
    for pair in "Debug iphonesimulator" "Release iphoneos"; do
      set -- $pair
      line=$( (cd "$dir" && xcodebuild -showBuildSettings -project HCGSentinel.xcodeproj -scheme "$s" -configuration "$1" -sdk "$2" \
                 -derivedDataPath "$OUT/dd-show" 2>&1) \
             | awk '/^Build settings for action .* and target / {n=$NF; sub(/:$/, "", n)} /^ *IPHONEOS_DEPLOYMENT_TARGET =/ {printf "%s=%s ", n, $3}')
      echo "  $s · $1 · $2: ${line:-(no settings)}" | tee -a "$RESULTS"
    done
  done
done

# ── 2. Outside Maker: build the current revision directly (unsigned) ──
CUR="$OUT/rev-${CURRENT:0:7}"
direct() {
  local name="$1"; shift
  echo "### Direct: $*" | tee -a "$RESULTS"
  (cd "$CUR" && "$@" > "$OUT/direct-$name.log" 2>&1); local rc=$?
  echo "  exit $rc · $(grep -m1 -E '\*\* (BUILD|ARCHIVE) (SUCCEEDED|FAILED) \*\*' "$OUT/direct-$name.log") · first error: $(grep -m1 -E '(^|: )error:' "$OUT/direct-$name.log" || echo none)" | tee -a "$RESULTS"
  grep -m2 "deployment target" "$OUT/direct-$name.log" | sed 's/^/  /' | tee -a "$RESULTS"
  return $rc
}
direct modern-sim xcodebuild build -project HCGSentinel.xcodeproj -scheme HCGSentinelModern -configuration Debug -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' -derivedDataPath "$OUT/dd-direct" CODE_SIGNING_ALLOWED=NO
r=$?; check "Direct Simulator build of HCGSentinelModern" '[ $r = 0 ]'
direct modern-archive xcodebuild archive -project HCGSentinel.xcodeproj -scheme HCGSentinelModern -configuration Release \
  -destination 'generic/platform=iOS' -archivePath "$OUT/direct-modern.xcarchive" -derivedDataPath "$OUT/dd-direct-a" CODE_SIGNING_ALLOWED=NO
r=$?; check "Direct unsigned archive of HCGSentinelModern" '[ $r = 0 ]'
m=$(find "$OUT/direct-modern.xcarchive" -name Info.plist -path '*.app/Info.plist' | head -1)
[ -n "$m" ] && echo "  archived app MinimumOSVersion: $(plutil -extract MinimumOSVersion raw "$m")" | tee -a "$RESULTS"
direct legacy-sim xcodebuild build -project HCGSentinel.xcodeproj -scheme HCGSentinel -configuration Debug -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' -derivedDataPath "$OUT/dd-direct-l" CODE_SIGNING_ALLOWED=NO
r=$?; note "Legacy HCGSentinel (iOS 14.7) direct Simulator build with this Xcode: exit $r (information: this Xcode's range decides)"

# ── 3. The packaged Maker app: stale import → refresh → compatible scheme → Simulator build and archive ──
export HCG_EXECUTABLE_MAKER_HOME="$OUT/maker-data"
echo "### Packaged app" | tee -a "$RESULTS"
"$EXE" --version | tee -a "$RESULTS"
ORIGIN="$OUT/src/HCG Sentinel"
git clone --quiet "$OUT/sentinel.git-cache" "$ORIGIN" && git -C "$ORIGIN" checkout --quiet $PRE_MODERN
"$EXE" --mobile-diagnostics --import "$ORIGIN" --out "$OUT/maker-1-import.txt" > "$OUT/maker-1.log" 2>&1
check "Packaged app imports Sentinel ${PRE_MODERN:0:7} and records its revision" 'grep -q "Revision HCG copied: git ${PRE_MODERN:0:12}" "$OUT/maker-1-import.txt"'
check "…whose copy has only the legacy scheme" 'grep -q "Schemes found in the project (1)" "$OUT/maker-1-import.txt"'

git -C "$ORIGIN" checkout --quiet $CURRENT
"$EXE" --mobile-diagnostics --project HCGSentinel --out "$OUT/maker-2-stale.txt" > "$OUT/maker-2.log" 2>&1
check "After Sentinel moves to ${CURRENT:0:7}, the report says the copy is stale (new HCGSentinelModern.xcscheme)" \
  'grep -q "Copy vs original: The original changed since HCG copied it" "$OUT/maker-2-stale.txt" && grep -q "new HCGSentinel.xcodeproj/xcshareddata/xcschemes/HCGSentinelModern.xcscheme" "$OUT/maker-2-stale.txt"'
check "…and Maker still builds the copy until refreshed" 'grep -q "Schemes found in the project (1)" "$OUT/maker-2-stale.txt"'

"$EXE" --mobile-diagnostics --project HCGSentinel --refresh --scheme HCGSentinelModern --build simulator --trust \
  --output-dir "$OUT/maker-out" --out "$OUT/maker-3-refreshed.txt" > "$OUT/maker-3-build.log" 2>&1
rc=$?
check "Refresh from source records ${CURRENT:0:7}" 'grep -q "Revision HCG copied: git ${CURRENT:0:12}" "$OUT/maker-3-refreshed.txt"'
check "…and the copy matches the original" 'grep -q "Copy vs original: HCG.s copy matches" "$OUT/maker-3-refreshed.txt"'
check "The selection is HCGSentinelModern" 'grep -q "^   Scheme: HCGSentinelModern" "$OUT/maker-3-refreshed.txt"'
check "The xcodebuild the packaged app ran names -scheme HCGSentinelModern" 'grep -E "^COMMAND .*xcodebuild" "$OUT/maker-3-build.log" | grep -q -- "-scheme HCGSentinelModern"'
check "Packaged app: Simulator build succeeded and was verified (exit $rc)" '[ $rc = 0 ] && grep -q "^Result: Succeeded" "$OUT/maker-3-build.log"'
grep -E "^(COMMAND|Result|Artifact|Provenance|  (Passed|Failed|Warning|NotTested))" "$OUT/maker-3-build.log" | head -30 | sed 's/^/  /' | tee -a "$RESULTS"

# A new process with no --scheme: the saved selection is what builds.
"$EXE" --mobile-diagnostics --project HCGSentinel --build archive --trust --signing unsigned \
  --output-dir "$OUT/maker-out" --out "$OUT/maker-4-archive.txt" > "$OUT/maker-4-archive.log" 2>&1
rc=$?
check "A new process restores HCGSentinelModern and archives it (unsigned, exit $rc)" \
  '[ $rc = 0 ] && grep -E "^COMMAND .*xcodebuild.* archive" "$OUT/maker-4-archive.log" | grep -q -- "-scheme HCGSentinelModern"'
grep -E "^(COMMAND|Result|Artifact|Provenance|  (Passed|Failed|Warning|NotTested))" "$OUT/maker-4-archive.log" | head -30 | sed 's/^/  /' | tee -a "$RESULTS"
echo "IPA (signed export): $(grep -m1 'iOS IPA (export from archive)' "$OUT/maker-4-archive.txt")" | tee -a "$RESULTS"

# ── 4. The read-only Mac report script against the same data (as a user with an older Maker would run it) ──
mkdir -p "$HOME/Applications" && rm -rf "$HOME/Applications/HCG Executable Maker.app" && cp -R "$APP" "$HOME/Applications/HCG Executable Maker.app"
bash tools/diagnostics/hcg-mobile-mac-report.sh --out "$OUT/mac-report.txt" > /dev/null 2>&1
check "The Mac report script finds the app, its commit, the project, its revision and the schemes' deployment targets" \
  'grep -q "This build has --mobile-diagnostics" "$OUT/mac-report.txt" && grep -q "HCGSentinelModern: IPHONEOS_DEPLOYMENT_TARGET = 15.0" "$OUT/mac-report.txt" && grep -q "HCGSentinel: IPHONEOS_DEPLOYMENT_TARGET = 14.7" "$OUT/mac-report.txt"'
rm -rf "$HOME/Applications/HCG Executable Maker.app"

echo; cat "$RESULTS" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
exit $fail
