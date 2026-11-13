#!/usr/bin/env bash
# Runs one group of the test suite: test-group.sh <name> <dotnet test filter>
# On macOS a watchdog samples processes, resident memory and free disk every 5 s (logged every 30 s) and stops the run
# when one of them runs away, so the log names the cause instead of the runner freezing.
set -u
name="$1"; filter="$2"
echo "== test group ${name}: ${filter}"
if [ "${RUNNER_OS:-}" = "macOS" ]; then
  (
    # Limits are relative to the runner's own level when the group starts (a macos-latest runner idles at ~540
    # processes and ~12 GB resident — run 73), so only growth caused by the tests trips them.
    base_procs=$(ps -A | wc -l | tr -d ' ')
    base_rss=$(ps -A -o rss= | awk '{s+=$1} END {print int(s/1024)}')
    echo "watchdog[${name}]: baseline ${base_procs} processes, ${base_rss} MB resident"
    i=0
    while sleep 5; do
      i=$((i+1))
      procs=$(ps -A | wc -l | tr -d ' ')
      rss=$(ps -A -o rss= | awk '{s+=$1} END {print int(s/1024)}')
      free=$(df -m "${RUNNER_TEMP:-/tmp}" | awk 'NR==2 {print $4}')
      xcb=$(ps -A -o comm= | grep -c '/xcodebuild$' || true)
      [ $((i % 6)) -eq 0 ] && echo "watchdog[${name}]: ${procs} processes (${xcb} xcodebuild), ${rss} MB resident, ${free} MB free disk"
      if [ "$procs" -gt $((base_procs + 400)) ] || [ "$rss" -gt $((base_rss + 6000)) ] || [ "$free" -lt 4000 ] || [ "$xcb" -gt 12 ]; then
        echo "::error::Watchdog[${name}]: ${procs} processes, ${rss} MB resident, ${free} MB free disk — stopping the test run"
        echo "== most frequent commands"; ps -A -o comm= | sort | uniq -c | sort -rn | head -15
        echo "== largest processes";     ps -A -o rss=,pid=,ppid=,etime=,command= | sort -rn | head -15
        echo "== largest temporary folders"; du -xm -d 3 "${TMPDIR:-/tmp}" "${RUNNER_TEMP:-/tmp}" 2>/dev/null | sort -rn | head -15
        pkill -f testhost; pkill -f vstest.console; break
      fi
    done
  ) &
  watchdog=$!
fi
dotnet test HCG.ExecutableMaker.slnx -c Release --no-build \
  --logger "trx;LogFileName=${name}.trx" --logger "console;verbosity=normal" --results-directory TestResults \
  --filter "$filter" --blame-hang-timeout 5m --blame-hang-dump-type none --diag "TestResults/diag/${name}.log"
status=$?
[ -n "${watchdog:-}" ] && kill "$watchdog" 2>/dev/null
exit $status
