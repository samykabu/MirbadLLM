#!/usr/bin/env bash
# CI deploy on the DGX Spark (run by .github/workflows/ci.yml from the checked-out commit).
# Builds mirbadllm/controller:<sha>, recreates the controller, waits for it to answer, and puts the
# previous image back if it doesn't. Secrets stay in the operator's env file; this script only reads it.
set -euo pipefail

ENV_FILE="${MIRBADLLM_ENV_FILE:-$HOME/mirbadllm/repo/deploy/.env}"
PORT="${MIRBADLLM_PORT:-8080}"
KEEP=5
sha="${GITHUB_SHA:-$(git rev-parse HEAD)}"
tag="mirbadllm/controller:${sha:0:12}"
compose=(docker compose -f deploy/compose.yaml --env-file "$ENV_FILE")

[ -r "$ENV_FILE" ] || { echo "::error::env file $ENV_FILE is missing or unreadable"; exit 1; }

prev="$(docker inspect mirbadllm --format '{{.Config.Image}}' 2>/dev/null || true)"
echo "Previous image: ${prev:-none}; new image: $tag"

MIRBADLLM_IMAGE="$tag" "${compose[@]}" build --pull controller
MIRBADLLM_IMAGE="$tag" "${compose[@]}" up -d --no-build controller

healthy() {
  for _ in $(seq 1 45); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/healthz")" = 200 ] &&
       [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/login")" = 200 ]; then
      return 0
    fi
    sleep 2
  done
  return 1
}

if ! healthy; then
  echo "::error::$tag did not become healthy; last controller logs:"
  docker logs --tail 60 mirbadllm 2>&1 || true
  if [ -n "$prev" ] && [ "$prev" != "$tag" ]; then
    echo "Rolling back to $prev"
    MIRBADLLM_IMAGE="$prev" "${compose[@]}" up -d --no-build controller
    healthy && echo "Rolled back to $prev" || echo "::error::rollback to $prev is not healthy either"
  fi
  exit 1
fi
echo "Controller healthy on $tag"

# The host agent runs as root outside compose; CI never reinstalls it.
if [ -n "${BEFORE_SHA:-}" ] && git cat-file -e "${BEFORE_SHA}^{commit}" 2>/dev/null; then
  changed="$(git diff --name-only "$BEFORE_SHA" "$sha" -- deploy/hostd src/MirbadLLM.HostAgent 2>&1)" || changed="(git diff failed: $changed)"
  if [ -n "$changed" ]; then
    echo "::warning::The host agent changed since ${BEFORE_SHA:0:7}; reinstall it by hand (deploy/hostd/install-hostd.sh): $(echo "$changed" | tr '
' ' ')"
  fi
fi

# Keep the newest $KEEP controller images (the running one is never removed: docker refuses).
docker images mirbadllm/controller --format '{{.CreatedAt}}|{{.Repository}}:{{.Tag}}' | sort -r |
  tail -n +$((KEEP + 1)) | cut -d'|' -f2 | xargs -r docker rmi >/dev/null 2>&1 || true
