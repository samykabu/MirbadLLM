#!/usr/bin/env bash
# Deploys the P1 split (master + DGX agent, deploy/compose.multinode.yaml) on the DGX Spark from the checked-out commit
# (design §11.8 "CI for topology (a)"; MN-041). ci.yml's deploy job runs it on every push to main (self-hosted DGX
# runner); deploy/ci-deploy.sh (the single controller) is kept for the way back.
#
#   DRY_RUN=1 bash deploy/ci-deploy-multinode.sh                             # print the steps, change nothing
#   DRY_RUN=1 MIRBADLLM_RENAME_CUTOVER=1 bash deploy/ci-deploy-multinode.sh  # the same for the rename cut-over below
#   bash deploy/ci-deploy-multinode.sh                                       # redeploy master, then agent
#
# Order: build both images → master (health: /healthz and /login answer 200) → agent (health: its container healthcheck,
# i.e. the mTLS listener is open after enrollment, the Docker wait and reconciliation). A master that does not become
# healthy is rolled back alone; an agent that does not is rolled back together with the master (they deploy as a pair,
# D53). Model containers keep running throughout; the agent adopts them.
#
# Rename cut-over (the first deploy on a host deployed before the rename, SparkServe): the containers spark-serve and
# spark-serve-agent run. The new ones bind the same ports and the master the same data folder (which it locks), so the
# old ones are stopped first, not removed: their volumes are copied into the new ones, mirbadllm and mirbadllm-agent
# start on the same ports, folders and env file, the model containers on the old network join the new one, and only
# when both are healthy are the old containers removed. If either fails, the new ones and the copied volumes are removed
# and the old containers start again. The old volumes and network stay until removed by hand.
# Bind mounts, the hostd socket folder and the env file fall back to their pre-rename names when only those exist, and
# SPARK_SERVE_* keys of the env file are read as MIRBADLLM_* ones (a MIRBADLLM_* key wins). Logs never print values,
# addresses or host names: the Actions log is public. The runner and its limits are not touched (W3 review MN-094).
set -euo pipefail

DRY_RUN="${DRY_RUN:-0}"
COMPOSE_FILE="deploy/compose.multinode.yaml"
MASTER=mirbadllm
AGENT=mirbadllm-agent
NETWORK=mirbadllm
# The names of a deployment made before the rename.
LEGACY_MASTER=spark-serve
LEGACY_AGENT=spark-serve-agent
LEGACY_NETWORK=spark-serve
LEGACY_VOLUMES="spark-serve-agent-state:mirbadllm-agent-state spark-serve-bootstrap:mirbadllm-bootstrap"
# Must exceed the agent's 30 s ShutdownTimeout (compose sets stop_grace_period: 45s for both services).
STOP_TIMEOUT=45
KEEP=5
sha="${GITHUB_SHA:-$(git rev-parse HEAD 2>/dev/null || echo 000000000000dryrun)}"
master_tag="mirbadllm/controller:${sha:0:12}"
agent_tag="mirbadllm/agent:${sha:0:12}"

dry() { [ "$DRY_RUN" = 1 ]; }
# Runs a command, or prints it in dry-run mode.
run() {
  if dry; then
    printf '+'; printf ' %q' "$@"; printf '\n'
  else
    "$@"
  fi
}
step() { echo "==> $*"; }
compose() { run env MIRBADLLM_IMAGE="$1" MIRBADLLM_AGENT_IMAGE="$2" MIRBADLLM_REVISION="${sha:0:12}" docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "${@:3}"; }
have_docker() { command -v docker >/dev/null 2>&1; }
# Read-only, so it runs in dry-run mode too when Docker is there.
inspect() { have_docker || return 0; docker inspect --type container "$@" 2>/dev/null || true; }
# Container logs for the (public) Actions log: addresses and this host's names masked.
masked_logs() {
  local host
  host="$(hostname 2>/dev/null || echo localhost)"
  docker logs --tail 60 "$1" 2>&1 | sed -E -e 's/([0-9]{1,3}\.){3}[0-9]{1,3}/<ip>/g' -e "s/${host%%.*}[A-Za-z0-9.-]*/<host>/g" || true
}

# ---- the env file and the pre-rename fallbacks ----

ENV_FILE="${MIRBADLLM_ENV_FILE:-${SPARK_SERVE_ENV_FILE:-}}"
if [ -z "$ENV_FILE" ]; then
  ENV_FILE="$HOME/mirbadllm/repo/deploy/.env"
  if [ ! -e "$ENV_FILE" ] && [ -e "$HOME/spark-serve/repo/deploy/.env" ]; then ENV_FILE="$HOME/spark-serve/repo/deploy/.env"; fi
fi

# The value of KEY in the env file (empty when absent); one pair of surrounding quotes is removed, as compose does.
env_value() {
  [ -r "$ENV_FILE" ] || return 0
  local line
  line="$(grep -E "^$1=" "$ENV_FILE" | tail -n 1 || true)"
  line="${line#*=}"
  case "$line" in
    \"*\") line="${line#\"}"; line="${line%\"}" ;;
    \'*\') line="${line#\'}"; line="${line%\'}" ;;
  esac
  printf '%s' "$line"
}
# True when KEY is set in the environment or the env file.
is_set() { [ -n "${!1:-}" ] || { [ -r "$ENV_FILE" ] && grep -qE "^$1=" "$ENV_FILE"; }; }

# SPARK_SERVE_<X> (env file or environment) → MIRBADLLM_<X> when that is set in neither. Names only in the log.
legacy_keys=""
for key in $( { [ -r "$ENV_FILE" ] && grep -oE '^SPARK_SERVE_[A-Z0-9_]+' "$ENV_FILE"; compgen -e | grep -E '^SPARK_SERVE_[A-Z0-9_]+$'; } | sort -u); do
  case "$key" in SPARK_SERVE_ENV_FILE | SPARK_SERVE_RENAME_CUTOVER) continue ;; esac
  new="MIRBADLLM_${key#SPARK_SERVE_}"
  legacy_keys="$legacy_keys $key"
  is_set "$new" && continue
  if [ -n "${!key:-}" ]; then export "$new=${!key}"; else export "$new=$(env_value "$key")"; fi
done
[ -z "$legacy_keys" ] || echo "::warning::The env file uses pre-rename keys (read as MIRBADLLM_*; rename them):$legacy_keys"

# Bind sources: the pre-rename folder when only it exists (the defaults of compose.multinode.yaml otherwise).
for pair in DATA:data BACKUPS:backups MODELS:models CACHE:cache; do
  var="MIRBADLLM_${pair%%:*}"
  is_set "$var" && continue
  if [ ! -e "/srv/mirbadllm/${pair#*:}" ] && [ -e "/srv/spark-serve/${pair#*:}" ]; then
    export "$var=/srv/spark-serve/${pair#*:}"
    step "Using the pre-rename folder /srv/spark-serve/${pair#*:} ($var)"
  fi
done
if ! is_set MIRBADLLM_HOSTD_RUN && [ ! -e /run/mirbadllm-hostd ] && [ -e /run/spark-serve-hostd ]; then
  export MIRBADLLM_HOSTD_RUN=/run/spark-serve-hostd
  step "Using the pre-rename host agent's socket folder /run/spark-serve-hostd"
fi
HOSTD_RUN="${MIRBADLLM_HOSTD_RUN:-$(env_value MIRBADLLM_HOSTD_RUN)}"
HOSTD_RUN="${HOSTD_RUN:-/run/mirbadllm-hostd}"
PORT="${MIRBADLLM_PORT:-$(env_value MIRBADLLM_PORT)}"
PORT="${PORT:-8080}"

if ! dry; then
  [ -r "$ENV_FILE" ] || { echo "::error::The env file is missing or unreadable (MIRBADLLM_ENV_FILE)."; exit 1; }
  grep -q '^DOCKER_GID=' "$ENV_FILE" || { echo "::error::The env file does not set DOCKER_GID"; exit 1; }
fi
[ -S "$HOSTD_RUN/hostd.sock" ] || dry ||
  echo "::warning::The host agent is not answering on $HOSTD_RUN/hostd.sock: the node will be Degraded (deploy/hostd/install-hostd.sh)"

# ---- what runs now ----

prev_master="$(inspect "$MASTER" --format '{{.Config.Image}}')"
prev_agent="$(inspect "$AGENT" --format '{{.Config.Image}}')"
legacy_master="$(inspect "$LEGACY_MASTER" --format '{{.Config.Image}}')"
legacy_agent="$(inspect "$LEGACY_AGENT" --format '{{.Config.Image}}')"
rename=0
if [ -n "$legacy_master$legacy_agent" ] && [ -z "$prev_master$prev_agent" ]; then
  if [ "$(inspect "$LEGACY_MASTER" --format '{{index .Config.Labels "com.docker.compose.service"}}')" = controller ]; then
    echo "::error::$LEGACY_MASTER is the single controller. Deploy the master + agent split with the release before the rename first."
    exit 1
  fi
  rename=1
elif dry && [ "${MIRBADLLM_RENAME_CUTOVER:-0}" = 1 ]; then
  # Shows the cut-over steps on a machine without the pre-rename containers.
  rename=1
  legacy_master="${legacy_master:-spark-serve/controller:<running>}"
  legacy_agent="${legacy_agent:-spark-serve/agent:<running>}"
fi
if [ "$rename" = 1 ]; then
  step "Rename cut-over: $LEGACY_MASTER ($legacy_master) and $LEGACY_AGENT ($legacy_agent) become $MASTER and $AGENT"
else
  step "Previous master: ${prev_master:-none}; previous agent: ${prev_agent:-none}"
fi
step "New images: $master_tag, $agent_tag"; dry && step "DRY_RUN=1: printing the steps only"

master_healthy() {
  dry && { echo "+ wait up to 90 s for http://127.0.0.1:$PORT/healthz and /login to answer 200"; return 0; }
  for _ in $(seq 1 45); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/healthz")" = 200 ] &&
       [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/login")" = 200 ]; then
      return 0
    fi
    sleep 2
  done
  return 1
}

# The agent's healthcheck passes once its listener is open; a first start can wait for the bootstrap bundle and Docker.
agent_healthy() {
  local name="${1:-$AGENT}"
  dry && { echo "+ wait up to 240 s for $name's healthcheck (mTLS listener open) to report healthy"; return 0; }
  for _ in $(seq 1 80); do
    case "$(docker inspect "$name" --format '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{end}}' 2>/dev/null)" in
      running/healthy) return 0 ;;
      exited/*|dead/*) return 1 ;;
    esac
    sleep 3
  done
  return 1
}

# Back to the pre-rename containers: the new ones and the copied volumes go (the next run copies afresh).
rename_rollback() {
  echo "::error::$1 Rolling back to $LEGACY_MASTER and $LEGACY_AGENT."
  if ! dry; then echo "Last logs of $2:"; masked_logs "$2"; fi
  compose "$master_tag" "$agent_tag" rm -sf master agent
  for pair in $LEGACY_VOLUMES; do run docker volume rm -f "${pair#*:}"; done
  run docker start "$LEGACY_MASTER"
  master_healthy && echo "$LEGACY_MASTER is healthy again" || echo "::error::$LEGACY_MASTER is not healthy after the rollback either"
  run docker start "$LEGACY_AGENT"
  agent_healthy "$LEGACY_AGENT" && echo "$LEGACY_AGENT is healthy again" || echo "::error::$LEGACY_AGENT is not healthy after the rollback either"
  exit 1
}

step "Building both images"
compose "$master_tag" "$agent_tag" build --pull master agent

if [ "$rename" = 1 ]; then
  step "Stopping $LEGACY_AGENT, then $LEGACY_MASTER (kept for the way back); model containers keep running"
  run docker stop -t "$STOP_TIMEOUT" "$LEGACY_AGENT"
  run docker stop -t "$STOP_TIMEOUT" "$LEGACY_MASTER"
  for pair in $LEGACY_VOLUMES; do
    old="${pair%%:*}" new="${pair#*:}"
    if dry || { docker volume inspect "$old" >/dev/null 2>&1 && ! docker volume inspect "$new" >/dev/null 2>&1; }; then
      step "Copying the volume $old into $new"
      run docker volume create "$new"
      run docker run --rm --user 0 --entrypoint bash -v "$old:/from:ro" -v "$new:/to" "$agent_tag" -c 'cp -a /from/. /to/'
    fi
  done

  step "Starting the master"
  compose "$master_tag" "$agent_tag" up -d --no-build --no-deps master
  master_healthy || rename_rollback "$master_tag did not become healthy." "$MASTER"
  step "Master healthy on $master_tag"

  # The agent reaches models at http://ss-<id>:8000 on its own network: the running ones are on the old one.
  step "Connecting the containers of the $LEGACY_NETWORK network to $NETWORK"
  if dry; then
    echo "+ docker network connect $NETWORK <each container on $LEGACY_NETWORK except $LEGACY_MASTER and $LEGACY_AGENT>"
  else
    for c in $(docker network inspect "$LEGACY_NETWORK" --format '{{range .Containers}}{{.Name}} {{end}}' 2>/dev/null || true); do
      case "$c" in "$LEGACY_MASTER" | "$LEGACY_AGENT") continue ;; esac
      docker network connect "$NETWORK" "$c" 2>/dev/null || true # already connected is fine
    done
  fi

  step "Starting the agent"
  compose "$master_tag" "$agent_tag" up -d --no-build --no-deps agent
  agent_healthy || rename_rollback "$agent_tag did not become healthy." "$AGENT"
  step "Agent healthy on $agent_tag"

  step "Removing the pre-rename containers $LEGACY_MASTER and $LEGACY_AGENT"
  run docker rm "$LEGACY_MASTER" "$LEGACY_AGENT"
  echo "::notice::Renamed. The pre-rename volumes (spark-serve-agent-state, spark-serve-bootstrap) and network ($LEGACY_NETWORK) are kept; remove them by hand once satisfied."
else
  step "Deploying the master"
  compose "$master_tag" "${prev_agent:-$agent_tag}" up -d --no-build --no-deps master
  if ! master_healthy; then
    echo "::error::$master_tag did not become healthy; last master logs:"
    dry || masked_logs "$MASTER"
    if [ -n "$prev_master" ] && [ "$prev_master" != "$master_tag" ]; then
      echo "Rolling the master back to $prev_master"
      compose "$prev_master" "${prev_agent:-$agent_tag}" up -d --no-build --no-deps master
      master_healthy && echo "Master rolled back to $prev_master" || echo "::error::rollback to $prev_master is not healthy either"
    fi
    exit 1
  fi
  step "Master healthy on $master_tag"

  step "Deploying the agent"
  compose "$master_tag" "$agent_tag" up -d --no-build --no-deps agent
  if ! agent_healthy; then
    echo "::error::$agent_tag did not become healthy; last agent logs:"
    dry || masked_logs "$AGENT"
    if [ -n "$prev_agent" ] && [ "$prev_agent" != "$agent_tag" ]; then
      echo "Rolling the agent back to $prev_agent and the master to ${prev_master:-?} (they deploy as a pair)"
      compose "$master_tag" "$prev_agent" up -d --no-build --no-deps agent
      agent_healthy && echo "Agent rolled back to $prev_agent" || echo "::error::agent rollback to $prev_agent is not healthy either"
      if [ -n "$prev_master" ] && [ "$prev_master" != "$master_tag" ]; then
        compose "$prev_master" "$prev_agent" up -d --no-build --no-deps master
        master_healthy && echo "Master rolled back to $prev_master" || echo "::error::rollback to $prev_master is not healthy either"
      fi
    fi
    exit 1
  fi
  step "Agent healthy on $agent_tag"
fi

# The node itself (enrollment on a first start, synchronization) shows in the master's log; report, never fail on it.
if ! dry; then
  for _ in $(seq 1 30); do
    if docker logs --since 5m "$MASTER" 2>&1 | grep -Eq 'Node dgx: .*-> (Online|Degraded)'; then
      docker logs --since 5m "$MASTER" 2>&1 | grep -E 'Node dgx: .*-> ' | tail -n 1 | sed -E 's/([0-9]{1,3}\.){3}[0-9]{1,3}/<ip>/g'
      break
    fi
    sleep 2
  done
else
  echo "+ report the master's last 'Node dgx: … -> Online|Degraded' log line (warning only)"
fi

# The host agent runs as root outside compose; CI never reinstalls it.
if [ -n "${BEFORE_SHA:-}" ] && git cat-file -e "${BEFORE_SHA}^{commit}" 2>/dev/null; then
  changed="$(git diff --name-only "$BEFORE_SHA" "$sha" -- deploy/hostd src/MirbadLLM.HostAgent 2>&1)" || changed="(git diff failed: $changed)"
  if [ -n "$changed" ]; then
    echo "::warning::The host agent changed since ${BEFORE_SHA:0:7}; reinstall it by hand (deploy/hostd/install-hostd.sh): $(echo "$changed" | tr '\n' ' ')"
  fi
fi

# Keep the newest $KEEP images of each repository (running ones are never removed: docker refuses).
for repo in mirbadllm/controller mirbadllm/agent; do
  if dry; then
    echo "+ keep the newest $KEEP $repo images"
  else
    docker images "$repo" --format '{{.CreatedAt}}|{{.Repository}}:{{.Tag}}' | sort -r |
      tail -n +$((KEEP + 1)) | cut -d'|' -f2 | xargs -r docker rmi >/dev/null 2>&1 || true
  fi
done
step "Done: master $master_tag, agent $agent_tag"
