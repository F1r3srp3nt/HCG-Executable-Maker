#!/bin/bash
# A Simulator used only by this job: created with an explicitly chosen runtime and device type, and deleted afterwards.
#   tools/ci/simulator.sh create NAME     → prints the choice, writes HCG_SIM_UDID to $GITHUB_ENV (and $OUT/simulator-udid.txt)
#   tools/ci/simulator.sh delete [UDID]   → shuts down and deletes only that device
set -euo pipefail
case "${1:-}" in
create)
  name="$2"
  sdk="$(xcrun --sdk iphonesimulator --show-sdk-version)"
  # The iOS runtime that matches the selected Xcode's Simulator SDK (major.minor); refuse to guess when there is none.
  runtime="$(xcrun simctl list runtimes -j | python3 -c '
import json, sys
sdk = sys.argv[1]
want = ".".join(sdk.split(".")[:2])
rts = [r for r in json.load(sys.stdin)["runtimes"] if r.get("platform") == "iOS" and r.get("isAvailable")]
match = [r for r in rts if ".".join(r["version"].split(".")[:2]) == want]
if not match:
    sys.exit("No available iOS Simulator runtime for the iPhoneSimulator " + sdk + " SDK; available: " + ", ".join(r["version"] for r in rts))
r = sorted(match, key=lambda r: [int(x) for x in r["version"].split(".")])[-1]
print(r["identifier"], r["version"])' "$sdk")"
  rid="${runtime%% *}"; rver="${runtime#* }"
  devtype="$(xcrun simctl list runtimes -j | python3 -c '
import json, sys
r = next(r for r in json.load(sys.stdin)["runtimes"] if r["identifier"] == sys.argv[1])
phones = [d for d in r.get("supportedDeviceTypes", []) if d.get("productFamily") == "iPhone"]
if not phones: sys.exit("The runtime supports no iPhone device type.")
print(phones[-1]["identifier"], "|", phones[-1]["name"])' "$rid")"
  dtid="${devtype%% |*}"; dtname="${devtype#*| }"
  udid="$(xcrun simctl create "$name" "$dtid" "$rid")"
  echo "Simulator for this job: $name — $dtname, iOS $rver ($rid), UDID $udid (iPhoneSimulator SDK $sdk)"
  echo "Other Simulators already booted on this runner (not touched):"
  xcrun simctl list devices booted | sed 's/^/  /'
  [ -n "${GITHUB_ENV:-}" ] && echo "HCG_SIM_UDID=$udid" >> "$GITHUB_ENV"
  [ -n "${OUT:-}" ] && { mkdir -p "$OUT"; echo "$udid" > "$OUT/simulator-udid.txt"; }
  ;;
delete)
  udid="${2:-${HCG_SIM_UDID:-}}"
  [ -n "$udid" ] || { echo "No Simulator was created by this job."; exit 0; }
  xcrun simctl shutdown "$udid" 2>/dev/null || true
  xcrun simctl delete "$udid"
  if xcrun simctl list devices | grep -q "$udid"; then echo "Simulator $udid still exists"; exit 1; fi
  echo "Deleted the job's Simulator $udid."
  ;;
*) echo "usage: $0 create NAME | delete [UDID]"; exit 2 ;;
esac
