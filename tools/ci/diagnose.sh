#!/bin/bash
# Snapshot for a stalled or failed iOS porting stage, taken while the processes still exist:
# process tree, stacks (sample) and open files/pipes (lsof) of the build and Simulator tools, the Simulator's state and
# log, recent crash/hang reports, memory/swap/disk, and the end of the newest build logs. Every command is bounded.
#   tools/ci/diagnose.sh LABEL        (env: OUT, DIAG_DIR, HCG_SIM_UDID)
set -uo pipefail
label="${1:-snapshot}"
dir="${DIAG_DIR:-$OUT/diagnostics}/$label-$(date -u +%H%M%S)"
mkdir -p "$dir"
bounded() { local secs="$1"; shift; perl -e 'alarm shift; exec @ARGV' "$secs" "$@"; }
echo "DIAGNOSE $label → $dir"
ps -axo pid,ppid,pgid,lstart,etime,stat,%cpu,rss,command > "$dir/ps.txt" 2>&1
{ sysctl vm.swapusage; memory_pressure -Q; vm_stat; df -h; uptime; } > "$dir/resources.txt" 2>&1
pids="$(ps -axo pid=,comm= | awk '{ n = $2; sub(".*/", "", n)
  if (n ~ /^(xcodebuild|SWBBuildService|XCBBuildService|swift-frontend|swift-driver|clang|ld|simctl|actool|ibtool|dotnet|CoreSimulatorService|launchd_sim|SimulatorTrampoline)$/) print $1 }' | head -40)"
for p in $pids; do
  cmd="$(ps -o command= -p "$p" 2>/dev/null | cut -c1-160)"
  [ -n "$cmd" ] || continue
  echo "== $p: $cmd" >> "$dir/processes.txt"
  bounded 30 sample "$p" 3 -file "$dir/sample-$p.txt" > /dev/null 2>&1 || true
  bounded 20 lsof -p "$p" > "$dir/lsof-$p.txt" 2>&1 || true
done
bounded 60 xcrun simctl list devices > "$dir/simctl-devices.txt" 2>&1 || true
if [ -n "${HCG_SIM_UDID:-}" ]; then
  bounded 30 xcrun simctl spawn "$HCG_SIM_UDID" launchctl list > "$dir/simulator-launchctl.txt" 2>&1 || true
  tail -n 400 "$HOME/Library/Logs/CoreSimulator/$HCG_SIM_UDID/system.log" > "$dir/simulator-system.log" 2>/dev/null || true
fi
ls -lt "$HOME/Library/Logs/DiagnosticReports" 2>/dev/null | head -30 > "$dir/diagnostic-reports.txt"
find "$HOME/Library/Logs/DiagnosticReports" -mmin -60 -type f \( -name '*.ips' -o -name '*.hang' -o -name '*.spin' \) -exec cp {} "$dir/" \; 2>/dev/null || true
logs=()
while IFS= read -r b; do logs+=("$b"); done < <(find "${OUT:-.}" -path '*hcg-data/builds/*' -name build.log 2>/dev/null)
if [ "${#logs[@]}" -gt 0 ]; then
  for b in $(ls -t "${logs[@]}" | head -2); do tail -n 120 "$b" > "$dir/build-$(basename "$(dirname "$b")").log.tail"; done
fi
# A short summary in the step log itself.
echo "DIAGNOSE resources: $(sysctl -n vm.swapusage 2>/dev/null) · $(df -h / | awk 'NR==2 {print $4" free"}')"
echo "DIAGNOSE build and Simulator processes:"; sed 's/^/DIAGNOSE   /' "$dir/processes.txt" 2>/dev/null | head -30
for s in "$dir"/sample-*.txt; do
  [ -f "$s" ] || continue
  echo "DIAGNOSE stack excerpt $(basename "$s"):"; grep -m 25 -E '^ +[0-9]+ ' "$s" | sed 's/^/DIAGNOSE   /' | cut -c1-180
done
for b in "$dir"/build-*.tail; do [ -f "$b" ] && { echo "DIAGNOSE last build log lines ($(basename "$b")):"; tail -n 15 "$b" | sed 's/^/DIAGNOSE   /'; }; done
exit 0
