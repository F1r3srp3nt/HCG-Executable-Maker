#!/bin/bash
# End of an iOS porting job: stop what this job started (its process groups, recorded by run-stage.sh) and delete its
# own Simulator. Nothing else on the runner is touched.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
groups="${DIAG_DIR:-$OUT/diagnostics}/process-groups.txt"
if [ -f "$groups" ]; then
  while read -r pgid label; do
    left="$(ps -axo pid=,pgid=,command= | awk -v g="$pgid" '$2 == g' | cut -c1-160)"
    if [ -n "$left" ]; then
      echo "Still running from step '$label' (process group $pgid) — stopping:"; echo "$left" | sed 's/^/  /'
      kill -TERM -- "-$pgid" 2>/dev/null; sleep 5; kill -KILL -- "-$pgid" 2>/dev/null
    fi
  done < "$groups"
fi
"$here/simulator.sh" delete || true
