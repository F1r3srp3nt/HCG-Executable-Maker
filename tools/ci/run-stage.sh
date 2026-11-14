#!/bin/bash
# Runs one CI step's command with: start/end timestamps and exit code, a hard time limit, a heartbeat every 30 s
# (active stage, memory, swap, disk, the step's process tree), a resource guard, and diagnostics BEFORE anything is
# killed. The command runs in its own process group with its output in a file that is followed here, so a background
# process it leaves behind can never hold this step's output (or the runner) open.
#
#   tools/ci/run-stage.sh LABEL MAX_SECONDS -- command args…
#
# Exit: the command's own exit code; 124 when the time limit was reached; 125 when the resource guard stopped it.
# Env: OUT (job output folder); DIAG_DIR (default $OUT/diagnostics).
set -uo pipefail
label="$1"; max="$2"; shift 2
[ "${1:-}" = "--" ] && shift
here="$(cd "$(dirname "$0")" && pwd)"
diag="${DIAG_DIR:-$OUT/diagnostics}"; mkdir -p "$diag"
slug="$(printf '%s' "$label" | tr -c 'A-Za-z0-9' '-' | tr -s '-')"
log="$diag/$slug.output.log"
export HCG_STAGE_FILE="$diag/$slug.stage"; : > "$HCG_STAGE_FILE"
: > "$log"
utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
start=$(date +%s)
echo "STEP $label start $(utc) (limit ${max} s)"

perl -e 'setpgrp(0, 0); exec @ARGV or die "exec: $!"' "$@" > "$log" 2>&1 < /dev/null &
pgid=$!
echo "$pgid $label" >> "$diag/process-groups.txt"
tail -n +1 -f "$log" &
tailpid=$!

resources() {
  local swap disk mem
  swap="$(sysctl -n vm.swapusage 2>/dev/null | sed -E 's/.*used = ([0-9.]+[MG]).*/\1/')"
  disk="$(df -h / | awk 'NR==2 {print $4}')"
  mem="$(memory_pressure -Q 2>/dev/null | awk -F': ' '/free percentage/ {print $2}')"
  echo "memory free ${mem:-?} · swap used ${swap:-?} · disk free ${disk:-?}"
}
group_tree() { ps -axo pid=,ppid=,pgid=,etime=,stat=,rss=,command= | awk -v g="$pgid" '$3 == g' | cut -c1-200; }

status=""; next=30; elapsed=0
while kill -0 "$pgid" 2>/dev/null; do
  sleep 5
  elapsed=$(( $(date +%s) - start ))
  if [ "$elapsed" -ge "$next" ]; then
    echo "♥ $(utc) $label running ${elapsed} s · stage: $(cat "$HCG_STAGE_FILE" 2>/dev/null | tr -d '\n' || true) · $(resources)"
    group_tree | sed 's/^/♥   /'
    next=$((next + 30))
  fi
  # Resource guard: stop with diagnostics before the runner itself runs out (a lost runner uploads no log at all).
  free_kb="$(df -k / | awk 'NR==2 {print $4}')"
  swap_mb="$(sysctl -n vm.swapusage 2>/dev/null | sed -E 's/.*used = ([0-9.]+)M.*/\1/' | cut -d. -f1)"
  if [ "${free_kb:-9999999}" -lt 2097152 ]; then status="stopped by the resource guard (disk free below 2 GB)"; code=125; break; fi
  if [ -n "$swap_mb" ] && [ "$swap_mb" -gt 8192 ] 2>/dev/null; then status="stopped by the resource guard (swap above 8 GB)"; code=125; break; fi
  if [ "$elapsed" -ge "$max" ]; then status="timed out after ${elapsed} s"; code=124; break; fi
done

if [ -n "$status" ]; then
  echo "STEP $label $status at $(utc) — collecting diagnostics while the processes still exist"
  "$here/diagnose.sh" "$slug-limit" || true
  echo "STEP $label stopping process group $pgid (TERM, then KILL after 10 s)"
  kill -TERM -- "-$pgid" 2>/dev/null; sleep 10; kill -KILL -- "-$pgid" 2>/dev/null
  wait "$pgid" 2>/dev/null
else
  wait "$pgid"; code=$?
fi
sleep 1; kill "$tailpid" 2>/dev/null
if [ "$code" -gt 128 ] && [ "$code" -lt 160 ] && [ -z "$status" ]; then
  # Killed by a signal: say which, and show processes of the group left STOPPED (they can bring SIGHUP to the group).
  echo "STEP $label: the command was terminated by signal $((code - 128)) (SIG$(kill -l $((code - 128)) 2>/dev/null || echo ?))"
  ps -axo pid=,ppid=,pgid=,stat=,etime=,command= 2>/dev/null | awk -v g="$pgid" '$3 == g && $4 ~ /^T/ { print "STEP   stopped: " $0 }' | cut -c1-220
fi
echo "STEP $label end $(utc) exit=$code after $(( $(date +%s) - start )) s"
exit "$code"
