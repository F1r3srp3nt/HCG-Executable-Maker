#!/usr/bin/env bash
# Runs one group of the test suite: test-group.sh <name> <dotnet test filter>
# On macOS a watchdog samples processes, resident memory and free disk every 5 s (logged every 30 s) and stops the run
# when one of them runs away, so the log names the cause instead of the runner freezing.
set -u
name="$1"; filter="$2"
echo "== test group ${name}: ${filter}"
if [ "${RUNNER_OS:-}" = "macOS" ]; then
  (
    i=0
    while sleep 5; do
      i=$((i+1))
      procs=$(ps -A | wc -l | tr -d ' ')
      rss=$(ps -A -o rss= | awk '{s+=$1} END {print int(s/1024)}')
      free=$(df -m "${RUNNER_TEMP:-/tmp}" | awk 'NR==2 {print $4}')
      [ $((i % 6)) -eq 0 ] && echo "watchdog[${name}]: ${procs} processes, ${rss} MB resident, ${free} MB free disk"
      if [ "$procs" -gt 700 ] || [ "$rss" -gt 9000 ] || [ "$free" -lt 4000 ]; then
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
