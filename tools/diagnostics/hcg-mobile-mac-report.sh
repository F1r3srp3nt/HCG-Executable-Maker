#!/bin/bash
# HCG Executable Maker — Mobile Build Studio report for this Mac (read-only by default).
#
# Works with any installed Maker (including builds without "Export diagnostics"). It records:
#   1. the HCG Executable Maker apps on this Mac: path, version, build commit, which one is running;
#   2. Maker's imported projects: original folder or ZIP, HCG's copy, recorded revision, saved scheme selection;
#   3. HCG's copy compared with the original folder (git commit, changed files, schemes on each side);
#   4. every installed Xcode with the iOS deployment range of its iPhoneOS and iPhoneSimulator SDKs (SDKSettings.json),
#      xcode-select and the Xcode chosen in Maker;
#   5. outside Maker, with the Xcode Maker uses: `xcodebuild -list` and the effective IPHONEOS_DEPLOYMENT_TARGET of every
#      target for each scheme, Simulator (Debug) and device (Release) — on HCG's copy (what Maker builds).
#   6. only with --build: a Simulator build and an unsigned archive of --scheme (default: every scheme), run on a
#      temporary copy of the ORIGINAL folder with CODE_SIGNING_ALLOWED=NO, keeping the command and the first error.
#      This runs the project's build scripts; nothing is installed, signed or uploaded.
#
# Usage:  bash hcg-mobile-mac-report.sh [--project NAME] [--build] [--scheme NAME] [--out FILE]
# Output: ~/Desktop/hcg-mobile-report-<time>.txt (or --out). It contains file paths and project names from this Mac,
#         but no passwords, keys, tokens or environment secrets.
set -u
PROJECT_FILTER="" ; DO_BUILD=0 ; ONLY_SCHEME="" ; OUT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --project) PROJECT_FILTER="$2"; shift 2 ;;
    --build) DO_BUILD=1; shift ;;
    --scheme) ONLY_SCHEME="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
done
[ "$(uname)" = "Darwin" ] || { echo "Run this on the Mac where HCG Executable Maker is installed." >&2; exit 2; }
OUT="${OUT:-$HOME/Desktop/hcg-mobile-report-$(date -u +%Y%m%d-%H%M%S).txt}"
TMP="$(mktemp -d /tmp/hcg-mobile-report.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT
exec > >(tee "$OUT") 2>&1

section() { printf '\n== %s ==\n' "$1"; }
sha() { [ -f "$1" ] && shasum -a 256 "$1" | cut -c1-16 || echo "(missing)"; }
plist() { /usr/libexec/PlistBuddy -c "Print :$2" "$1" 2>/dev/null; }

echo "HCG Executable Maker — Mobile Build Studio report (script, read-only$([ $DO_BUILD = 1 ] && echo '; --build requested'))"
echo "Generated: $(date -u '+%Y-%m-%d %H:%M:%S') UTC · macOS $(sw_vers -productVersion) ($(uname -m))"

section "1. MAKER"
running=$(pgrep -f 'Contents/MacOS/HCGExecutableMaker' 2>/dev/null | while read -r pid; do ps -o comm= -p "$pid"; done | sort -u)
echo "Running now: ${running:-(not running)}"
found=0
for app in /Applications/*.app "$HOME"/Applications/*.app "$HOME"/Downloads/*.app "$HOME"/Downloads/*/*.app; do
  exe="$app/Contents/MacOS/HCGExecutableMaker"
  [ -x "$exe" ] || continue
  found=1
  echo "App: $app"
  echo "  CFBundleShortVersionString: $(plist "$app/Contents/Info.plist" CFBundleShortVersionString) · CFBundleVersion: $(plist "$app/Contents/Info.plist" CFBundleVersion)"
  echo "  Modified: $(stat -f '%Sm' -t '%Y-%m-%d %H:%M' "$exe")"
  # --version exists since 0.3.0; older builds would open the UI instead, so only ask builds that know the option.
  if grep -aqs -- "--toolchain-report" "$app"/Contents/MacOS/*.dll "$exe"; then
    "$exe" --version 2>/dev/null | sed 's/^/  /' | head -3
  else
    echo "  (this build predates --version; its commit is shown in the sidebar under the logo)"
  fi
  if grep -aqs -- "--mobile-diagnostics" "$app"/Contents/MacOS/*.dll "$exe"; then
    echo "  This build has --mobile-diagnostics and Export diagnostics."
  else
    echo "  This build has NO Refresh from source / Export diagnostics / deployment-target status fixes (older than the Sentinel end-to-end fix)."
  fi
done
[ $found = 1 ] || echo "No HCG Executable Maker.app found in /Applications, ~/Applications or ~/Downloads."

section "2. MAKER DATA AND IMPORTED PROJECTS"
DATA=""
for d in "${HCG_EXECUTABLE_MAKER_HOME:-}" "$HOME/Library/Application Support/HCG/ExecutableMaker" "$HOME/.local/share/HCG/ExecutableMaker"; do
  [ -n "$d" ] && [ -f "$d/hcg-executable-maker.db" ] && { DATA="$d"; break; }
done
if [ -z "$DATA" ]; then echo "No Maker database found."; exit 0; fi
DB="$DATA/hcg-executable-maker.db"
echo "Data folder: $DATA"
q() { sqlite3 -readonly -separator $'\t' "$DB" "$1" 2>/dev/null; }
cols=$(q "SELECT group_concat(name, ',') FROM pragma_table_info('projects');")
# Empty fields would collapse with a tab separator: NULLs are printed as "-".
extra=", '-', '-'"; case ",$cols," in *,source_revision,*) extra=", ifnull(source_revision,'-'), ifnull(refreshed_at,'-')";; esac
DEV_SETTING=$(q "SELECT value FROM settings WHERE key='app-settings';" | python3 -c 'import json,sys; d=json.loads(sys.stdin.read() or "{}"); print(d.get("developerDirectory") or d.get("DeveloperDirectory") or "")' 2>/dev/null)
echo "Xcode chosen in Maker (Toolchains › Xcode): ${DEV_SETTING:-(none: Maker uses xcode-select)}"
q "SELECT id, name, source_kind, source_location, workspace_path, imported_at$extra FROM projects ORDER BY imported_at;" > "$TMP/projects.tsv"
while IFS=$'\t' read -r id name kind src ws imported rev refreshed; do
  echo "Project: $name  (id $id)"
  echo "  Imported from: $src ($kind) at $imported; revision recorded: $([ "$rev" = - ] && echo 'none (imported by a Maker that did not record it)' || echo "$rev"); refreshed from source: $([ "$refreshed" = - ] && echo never || echo "$refreshed")"
  echo "  HCG's copy: $ws/source"
  sel=$(q "SELECT value FROM settings WHERE key='mobile-selection:$id';")
  echo "  Saved selection: ${sel:-(none: defaults)}"
done < "$TMP/projects.tsv"

if [ -n "$DEV_SETTING" ]; then DEV="$DEV_SETTING"; else DEV="${DEVELOPER_DIR:-$(xcode-select -p 2>/dev/null)}"; fi

section "3. XCODE"
echo "xcode-select -p: $(xcode-select -p 2>/dev/null)"
echo "DEVELOPER_DIR in this shell: ${DEVELOPER_DIR:-(unset)}"
echo "Xcode Maker builds with: $DEV"
range() {
  local f="$1/Platforms/$2.platform/Developer/SDKs/$2.sdk/SDKSettings.json" k="$3"
  if [ -f "$f" ]; then
    printf '%s %s–%s' "$(plutil -extract Version raw "$f" 2>/dev/null)" "$(plutil -extract "SupportedTargets.$k.MinimumDeploymentTarget" raw "$f" 2>/dev/null)" "$(plutil -extract "SupportedTargets.$k.MaximumDeploymentTarget" raw "$f" 2>/dev/null)"
  else printf 'not found (%s)' "$f"; fi
}
for x in /Applications/Xcode*.app "$HOME"/Applications/Xcode*.app; do
  [ -d "$x/Contents/Developer" ] || continue
  v=$(DEVELOPER_DIR="$x/Contents/Developer" xcodebuild -version 2>/dev/null | tr '\n' ' ')
  echo "$x: ${v:-?}"
  echo "  iPhoneOS SDK (device):  $(range "$x/Contents/Developer" iPhoneOS iphoneos)"
  echo "  iPhoneSimulator SDK:    $(range "$x/Contents/Developer" iPhoneSimulator iphonesimulator)"
done

section "4. HCG'S COPY VS THE ORIGINAL, AND THE PROJECT OUTSIDE MAKER"
while IFS=$'\t' read -r id name kind src ws imported rev refreshed; do
  [ -n "$PROJECT_FILTER" ] && [ "$name" != "$PROJECT_FILTER" ] && continue
  copy="$ws/source"
  proj=$(cd "$copy" 2>/dev/null && find . -maxdepth 3 -name '*.xcodeproj' -not -path '*/.build/*' | head -1)
  [ -n "$proj" ] || continue
  echo "--- $name"
  echo "Copy:     $copy"
  echo "  schemes: $(cd "$copy" && find . -path '*xcschemes/*.xcscheme' -not -path '*/.build/*' | sed 's|.*/||; s|\.xcscheme||' | sort | tr '\n' ' ')"
  echo "  project.pbxproj sha256: $(sha "$copy/$proj/project.pbxproj")"
  if [ "$kind" = "Directory" ] && [ -d "$src" ]; then
    echo "Original: $src"
    if git -C "$src" rev-parse HEAD >/dev/null 2>&1; then
      echo "  git: $(git -C "$src" rev-parse HEAD) ($(git -C "$src" rev-parse --abbrev-ref HEAD)); uncommitted files: $(git -C "$src" status --porcelain | wc -l | tr -d ' ')"
      echo "  last commit: $(git -C "$src" log -1 --format='%h %ci %s')"
    else echo "  not a git checkout"; fi
    echo "  schemes: $(cd "$src" && find . -path '*xcschemes/*.xcscheme' -not -path './.git/*' -not -path '*/.build/*' | sed 's|.*/||; s|\.xcscheme||' | sort | tr '\n' ' ')"
    echo "  project.pbxproj sha256: $(sha "$src/$proj/project.pbxproj")"
    diff -rq -x .git -x .build -x .swiftpm -x DerivedData -x xcuserdata -x node_modules "$src" "$copy" 2>/dev/null | sed "s|$src|ORIGINAL|; s|$copy|COPY|" > "$TMP/diff.txt"
    n=$(wc -l < "$TMP/diff.txt" | tr -d ' ')
    if [ "$n" = 0 ]; then echo "  Copy matches the original."
    else echo "  Copy differs from the original in $n place(s) — Maker builds the COPY:"; head -30 "$TMP/diff.txt" | sed 's/^/    /'; fi
  elif [ "$kind" = "ZipArchive" ]; then
    echo "Original ZIP: $src $( [ -f "$src" ] && echo "(sha256 $(sha "$src"))" || echo '(missing)')"
  else echo "Original: $src (missing)"; fi

  echo "xcodebuild -list (copy, $DEV):"
  (cd "$copy" && DEVELOPER_DIR="$DEV" xcodebuild -list -project "$proj" 2>&1 | sed 's/^/  /' | head -40)
  schemes=$(cd "$copy" && DEVELOPER_DIR="$DEV" xcodebuild -list -json -project "$proj" 2>/dev/null | python3 -c 'import json,sys; print("\n".join(json.load(sys.stdin)["project"]["schemes"]))' 2>/dev/null)
  for s in $schemes; do
    for pair in "Debug iphonesimulator" "Release iphoneos"; do
      set -- $pair
      echo "Effective settings: scheme $s · $1 · $2"
      (cd "$copy" && DEVELOPER_DIR="$DEV" xcodebuild -showBuildSettings -project "$proj" -scheme "$s" -configuration "$1" -sdk "$2" \
         -derivedDataPath "$TMP/dd-show" 2>&1) | awk '/^Build settings for action/ {t=$0} /^ *TARGET_NAME =/ {n=$3} /^ *IPHONEOS_DEPLOYMENT_TARGET =/ {print "  " n ": IPHONEOS_DEPLOYMENT_TARGET = " $3} /error:/ {print "  " $0}' | sort -u
    done
  done

  if [ $DO_BUILD = 1 ] && [ "$kind" = "Directory" ] && [ -d "$src" ]; then
    work="$TMP/build/$(basename "$src")"
    mkdir -p "$(dirname "$work")"
    rsync -a --exclude .git --exclude .build --exclude DerivedData "$src/" "$work/"
    for s in $schemes; do
      [ -n "$ONLY_SCHEME" ] && [ "$s" != "$ONLY_SCHEME" ] && continue
      for kind2 in simulator archive; do
        if [ $kind2 = simulator ]; then
          cmd=(xcodebuild build -project "$proj" -scheme "$s" -configuration Debug -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' -derivedDataPath "$TMP/dd-$s" CODE_SIGNING_ALLOWED=NO)
        else
          cmd=(xcodebuild archive -project "$proj" -scheme "$s" -configuration Release -destination 'generic/platform=iOS' -archivePath "$TMP/$s.xcarchive" -derivedDataPath "$TMP/dd-$s-a" CODE_SIGNING_ALLOWED=NO)
        fi
        echo "Direct $kind2 build (original, unsigned): DEVELOPER_DIR=$DEV ${cmd[*]}"
        (cd "$work" && DEVELOPER_DIR="$DEV" "${cmd[@]}" > "$TMP/$s-$kind2.log" 2>&1); rc=$?
        echo "  exit $rc · first error: $(grep -m1 -E 'error:|xcodebuild: error' "$TMP/$s-$kind2.log" || echo none)"
        grep -m3 -E "deployment target|IPHONEOS_DEPLOYMENT_TARGET" "$TMP/$s-$kind2.log" | sed 's/^/  /'
        tail -3 "$TMP/$s-$kind2.log" | sed 's/^/  | /'
      done
    done
  fi
done < "$TMP/projects.tsv"

section "DONE"
echo "Report: $OUT"
