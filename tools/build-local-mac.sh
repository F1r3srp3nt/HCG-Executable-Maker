#!/usr/bin/env bash
# Builds HCG Executable Maker from THIS checkout as a macOS app for this Mac, prints the exact .app to open and its
# build identity, and (optionally) runs the Swift workflow checks against your Oslo-Sprint folder.
#
#   bash tools/build-local-mac.sh                          # build only
#   bash tools/build-local-mac.sh --verify ~/src/Oslo-Sprint  # build, then import/build/launch Distil and Relay
#
# Needs: macOS, the .NET 10 SDK; for --verify also Xcode or the Command Line Tools (swift, codesign).
# Nothing is installed, pushed or published. Builds go to ~/HCG-Executable-Maker-builds/<commit>/.
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ "$(uname -s)" == Darwin ]] || { echo "Run this on a Mac." >&2; exit 1; }
command -v dotnet >/dev/null || { echo "Install the .NET 10 SDK first: https://dotnet.microsoft.com/download" >&2; exit 1; }

commit="$(git -C "$REPO" rev-parse --short=12 HEAD)"
[[ -n "$(git -C "$REPO" status --porcelain --untracked-files=no)" ]] && commit="$commit+uncommitted"
rid="osx-$( [[ "$(uname -m)" == arm64 ]] && echo arm64 || echo x64 )"
out="$HOME/HCG-Executable-Maker-builds/${commit}"
rm -rf "$out"
mkdir -p "$out"

echo "Building HCG Executable Maker $commit for $rid…"
HCG_SOURCE_REVISION="$commit" dotnet run --no-cache "$REPO/tools/self-package/self-package.cs" -- "$REPO" "$out/package" --targets "$rid" > "$out/self-package.log" 2>&1 \
  || { tail -40 "$out/self-package.log"; echo "Build failed; full log: $out/self-package.log" >&2; exit 1; }
app="$(find "$out/package" -maxdepth 3 -name '*.app' -type d | head -1)"
[[ -n "$app" ]] || { echo "No .app was produced; see $out/self-package.log" >&2; exit 1; }
exe="$(find "$app/Contents/MacOS" -maxdepth 1 -type f -perm -u+x -name 'HCGExecutableMaker' | head -1)"
echo
"$exe" --version
echo
printf 'Open exactly this copy:\n  open "%s"\n' "$app"
echo "(Settings › About this build shows the same commit and path. Quit any older copy first.)"

if [[ "${1:-}" == "--verify" ]]; then
  oslo="$(cd "${2:?Pass the path of your Oslo-Sprint checkout}" && pwd)"
  shots="$out/verify"
  mkdir -p "$shots"
  dotnet build "$REPO/tools/ui-driver" -c Release > "$out/ui-driver-build.log" 2>&1
  drv() { dotnet "$REPO/tools/ui-driver/bin/Release/net10.0/hcg-ui-driver.dll" "$@" --screenshots "$shots"; }
  echo; echo "== Distil (distil/ alone)"
  drv --source "$oslo/distil" --package "" --product Distil --arch "${rid#osx-}" --formats app --output "$out/distil" --prefix distil
  echo; echo "== Relay (relay/ alone)"
  drv --source "$oslo/relay" --package "" --product Relay --arch "${rid#osx-}" --formats app --output "$out/relay" --prefix relay
  echo; echo "== Parent repository → distil/"
  drv --source "$oslo" --package distil --product Distil --arch "${rid#osx-}" --formats app --output "$out/parent" --prefix parent
  echo; echo "== SwiftPM resources (fixture)"
  drv --source "$REPO/tests/Fixtures/ResourceProbe" --package "" --product ResourceProbe --name "Resource Probe" \
      --bundle-id com.hakmicapital.resourceprobe --arch "${rid#osx-}" --formats app --output "$out/probe" --prefix probe
  for a in "$out/distil" "$out/relay" "$out/parent" "$out/probe"; do
    built="$(find "$a/artifacts" -maxdepth 3 -name '*.app' -type d | head -1)"
    bash "$REPO/tools/ui-driver/verify-macos-app.sh" "$built" "$shots/launch-$(basename "$a").png"
  done
  echo "--- resource probe: $(cat "${TMPDIR:-/tmp}/hcg-resource-probe.txt" 2>/dev/null || echo 'no report written')"
  echo "Screenshots and reports: $shots"
fi
