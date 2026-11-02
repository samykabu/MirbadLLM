#!/usr/bin/env bash
# Stops the two-agent multinode dev setup started by start.sh (MN-042). State stays in artifacts/dev-multinode, so the
# next start.sh reuses the CA and both enrollments (use start.sh --reset for a clean slate).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ROOT="${DEV_MULTINODE_ROOT:-$REPO/artifacts/dev-multinode}"
PIDS="$ROOT/pids"
if [[ ! -f "$PIDS" ]]; then echo "Nothing to stop (no pids file)."; exit 0; fi

# Agents first (SIGTERM: they drain and close their operation logs), then the master.
stop_one() {
  local name="$1" pid="$2"
  if kill -0 "$pid" 2>/dev/null; then
    kill "$pid"
    for _ in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
    if kill -0 "$pid" 2>/dev/null; then kill -9 "$pid"; fi
    echo "stopped $name (pid $pid)"
  else
    echo "$name (pid $pid) is not running"
  fi
}
while read -r name pid; do
  if [[ -n "$name" && "$name" != master ]]; then stop_one "$name" "$pid"; fi
done <"$PIDS"
while read -r name pid; do
  if [[ "$name" == master ]]; then stop_one "$name" "$pid"; fi
done <"$PIDS"
rm -f "$PIDS"
