#!/usr/bin/env bash
# Starts the two-agent multinode dev setup on this machine (MN-042, MN-122): one master and two agents with the fake
# runtime (no GPU, no Docker models): "dgx" unified like the DGX, "dev-b" a discrete PC with two fake 16 GiB GPUs.
# Linux or macOS (on Windows use start.ps1); needs the .NET 10 SDK and curl.
#
#   tools/dev-multinode/start.sh [--reset] [--no-build] [--no-smoke]
#
# 1. builds MirbadLLM.slnx (skip with --no-build);
# 2. starts the master (Development) on http://127.0.0.1:$MASTER_PORT with its enroll listener on $ENROLL_PORT, the
#    bootstrap bundle for "dgx" and the development join-token helper for "dev-b";
# 3. starts agent "dgx" in bootstrap mode on 127.0.0.1:$DGX_PORT (it enrolls from the bundle, pre-approved);
# 4. runs "mirbadllm-agent join" for "dev-b" with the helper's token, approves it through the node API
#    (POST /api/nodes/dev-b/approve, as the Nodes page does) and starts it on 127.0.0.1:$DEVB_PORT with the fake runtime
#    in discrete mode (discrete:2x16, placeholder GPU UUIDs ...de00 and ...de01);
# 5. unless --no-smoke: checks that dev-b reports a discrete node with both GPUs and the gpus.uuids feature, adds a fake
#    model on each node (fake-dgx; fake-devb placed on dev-b's GPU 1 by UUID), starts both and sends /v1 chat requests
#    (buffered and streamed) to each through the master.
# Everything lives under artifacts/dev-multinode (logs in logs/). Stop with tools/dev-multinode/stop.sh.
# Ports: MASTER_PORT (5080), ENROLL_PORT (8443), DGX_PORT (9443), DEVB_PORT (9444); folder: DEV_MULTINODE_ROOT.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ROOT="${DEV_MULTINODE_ROOT:-$REPO/artifacts/dev-multinode}"
MASTER_PORT="${MASTER_PORT:-5080}"
ENROLL_PORT="${ENROLL_PORT:-8443}"
DGX_PORT="${DGX_PORT:-9443}"
DEVB_PORT="${DEVB_PORT:-9444}"
MASTER="http://127.0.0.1:$MASTER_PORT"
MASTER_DLL="$REPO/artifacts/bin/MirbadLLM.Web/debug/MirbadLLM.Web.dll"
AGENT_DLL="$REPO/artifacts/bin/MirbadLLM.Agent/debug/mirbadllm-agent.dll"
# dev-b's fake GPUs: placeholder UUIDs (not real cards), index order.
DEVB_GPU0="GPU-00000000-0000-0000-0000-00000000de00"
DEVB_GPU1="GPU-00000000-0000-0000-0000-00000000de01"
RESET=0 BUILD=1 SMOKE=1
for arg in "$@"; do
  case "$arg" in
    --reset) RESET=1 ;;
    --no-build) BUILD=0 ;;
    --no-smoke) SMOKE=0 ;;
    -h|--help) sed -n '2,21p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

wait_until() { # <seconds> <what> <command...>
  local seconds="$1" what="$2"; shift 2
  local deadline=$(( $(date +%s) + seconds ))
  until "$@" >/dev/null 2>&1; do
    if (( $(date +%s) > deadline )); then echo "Timed out after ${seconds} s waiting for $what. Logs: $ROOT/logs" >&2; exit 1; fi
    sleep 0.5
  done
}

start_dotnet() { # <name> <working dir> <environment> <args...>
  local name="$1" dir="$2" environment="$3"; shift 3
  ( cd "$dir" && env ASPNETCORE_ENVIRONMENT="$environment" DOTNET_ENVIRONMENT="$environment" \
      nohup dotnet "$@" >"$ROOT/logs/$name.log" 2>"$ROOT/logs/$name.err.log" </dev/null & echo "$name $!" >>"$ROOT/pids" )
  echo "    $name pid $(awk -v n="$name" '$1==n {p=$2} END {print p}' "$ROOT/pids") (log: logs/$name.log)"
}

write_agent_config() { # <node id> <port> <first fake-model port> <bootstrap dir or empty> <DevFakeRuntime JSON>
  local node="$1" port="$2" first="$3" bootstrap="$4" fake="$5" dir="$ROOT/$1"
  mkdir -p "$dir/models/fake-model" "$dir/cache" "$dir/state"
  printf '%s\n' '{"architectures":["LlamaForCausalLM"],"model_type":"llama"}' >"$dir/models/fake-model/config.json"
  local extra=""
  if [[ -n "$bootstrap" ]]; then
    extra=$(printf ',\n    "BootstrapDirectory": "%s",\n    "BootstrapWait": "00:02:00",\n    "Addresses": [ "127.0.0.1" ]' "$bootstrap")
  fi
  cat >"$dir/agent.json" <<EOF
{
  "Agent": {
    "NodeId": "$node",
    "StateDirectory": "$dir/state",
    "ListenAddress": "127.0.0.1",
    "Port": $port,
    "ModelsRoot": "$dir/models",
    "ModelsHostPath": "$dir/models",
    "CacheRoot": "$dir/cache",
    "CacheHostPath": "$dir/cache",
    "UseContainerNetwork": false,
    "DockerStartupTimeout": "00:00:05",
    "HostdSocket": "$dir/no-hostd.sock",
    "HostdExpected": false,
    "ManageSystemdUnits": false,
    "ManageContainers": false,
    "ManageBackupMount": false,
    "AllowTagPulls": true,
    "PortRangeStart": $first,
    "PortRangeEnd": $(( first + 49 )),
    "ShutdownTimeout": "00:00:10"$extra
  },
  "DevFakeRuntime": $fake,
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning", "System.Net.Http.HttpClient": "Warning" } }
}
EOF
}

json_field() { sed -n "s/.*\"$1\": *\"\([^\"]*\)\".*/\1/p" "$2" | head -n 1; }

if [[ -f "$ROOT/pids" ]]; then
  echo "A dev setup is already running (or was not stopped): run tools/dev-multinode/stop.sh first." >&2
  exit 1
fi
if (( RESET )) && [[ -d "$ROOT" ]]; then step "Reset: removing $ROOT"; rm -rf "$ROOT"; fi
mkdir -p "$ROOT/logs" "$ROOT/bootstrap" "$ROOT/join"

if (( BUILD )); then
  step "Building MirbadLLM.slnx"
  # From the repo root, so its global.json picks the SDK (not whatever SDK the caller's folder resolves to).
  ( cd "$REPO" && dotnet build "$REPO/MirbadLLM.slnx" -v q -nologo )
fi

write_agent_config dgx "$DGX_PORT" 18100 "$ROOT/bootstrap" \
  '{ "Enabled": true, "Memory": "unified:64", "StartDelay": "00:00:02", "TokenDelay": "00:00:00.02" }'
write_agent_config dev-b "$DEVB_PORT" 18150 "" \
  "{ \"Enabled\": true, \"Memory\": \"discrete:2x16\", \"HostMemoryGiB\": 32, \"GpuUuids\": [ \"$DEVB_GPU0\", \"$DEVB_GPU1\" ], \"StartDelay\": \"00:00:02\", \"TokenDelay\": \"00:00:00.02\" }"

step "Master on $MASTER (enroll listener https://127.0.0.1:$ENROLL_PORT)"
start_dotnet master "$REPO/src/MirbadLLM.Web" Development "$MASTER_DLL" \
  --urls "$MASTER" \
  "--MirbadLLM:DataPath=$ROOT/master-data" \
  "--MirbadLLM:Backup:Directory=$ROOT/backups" \
  "--MirbadLLM:HostAgentSocket=$ROOT/no-hostd.sock" \
  "--MirbadLLM:Enrollment:Port=$ENROLL_PORT" \
  "--MirbadLLM:Enrollment:BootstrapDirectory=$ROOT/bootstrap" \
  "--MirbadLLM:Enrollment:BootstrapEnrollEndpoint=https://127.0.0.1:$ENROLL_PORT" \
  "--MirbadLLM:Enrollment:BootstrapAgentAddress=https://127.0.0.1:$DGX_PORT" \
  "--MirbadLLM:DevJoinTokens:Nodes:0=dev-b" \
  "--MirbadLLM:DevJoinTokens:OutputDirectory=$ROOT/join" \
  "--MirbadLLM:DevJoinTokens:AutoApprove=false"
wait_until 90 "the master /healthz" curl -fsS "$MASTER/healthz"

step "Agent dgx (bootstrap mode) on https://127.0.0.1:$DGX_PORT"
start_dotnet agent-dgx "$ROOT/dgx" Development "$AGENT_DLL" --config "$ROOT/dgx/agent.json"

if [[ ! -f "$ROOT/dev-b/state/agent.crt" ]]; then
  step "Joining dev-b with the dev helper's token"
  JOIN="$ROOT/join/dev-b.join.json"
  wait_until 90 "the dev join token for dev-b (master log: logs/master.log)" test -f "$JOIN"
  MIRBADLLM_JOIN_TOKEN="$(json_field token "$JOIN")" dotnet "$AGENT_DLL" join \
    --enroll-url "$(json_field enrollUrl "$JOIN")" --ca-hash "$(json_field caHash "$JOIN")" \
    --node-id dev-b --state-dir "$ROOT/dev-b/state" --address "https://127.0.0.1:$DEVB_PORT"
else
  step "dev-b is enrolled already (state kept); skipping join"
fi

# Approve dev-b through the node API (MN-102), as the Nodes page does; 409 = approved already (state kept).
approval=$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$MASTER/api/nodes/dev-b/approve")
case "$approval" in
  200) echo "    approved dev-b (POST /api/nodes/dev-b/approve)" ;;
  409) echo "    dev-b is approved already" ;;
  *) echo "Approving dev-b answered $approval (master log: logs/master.log)" >&2; exit 1 ;;
esac

step "Agent dev-b on https://127.0.0.1:$DEVB_PORT"
start_dotnet agent-dev-b "$ROOT/dev-b" Development "$AGENT_DLL" --config "$ROOT/dev-b/agent.json"

if (( SMOKE )); then
  step "Smoke: dev-b is discrete with two GPUs"
  # The master stores each node's capability report when it connects; placement validation reads it.
  wait_until 120 "dev-b's discrete capability report" bash -c "curl -fsS '$MASTER/api/nodes/dev-b' | grep -q '\"model\":\"discrete\"'"
  node_json=$(curl -fsS "$MASTER/api/nodes/dev-b")
  for gpu in "$DEVB_GPU0" "$DEVB_GPU1"; do
    grep -q "\"$gpu\"" <<<"$node_json" || { echo "dev-b does not report GPU $gpu" >&2; exit 1; }
    echo "    dev-b GPU: $gpu"
  done
  grep -q '"gpus.uuids"' <<<"$node_json" || { echo "dev-b does not report the gpus.uuids feature" >&2; exit 1; }
  echo "    dev-b reports gpus.uuids"

  step "Smoke: a fake model on each node (fake-devb on dev-b GPU 1), /v1 chat through both"
  for pair in "fake-dgx:dgx:" "fake-devb:dev-b:$DEVB_GPU1"; do
    IFS=: read -r model node gpu <<<"$pair"
    placement="{\"nodeId\":\"$node\"}"
    [[ -n "$gpu" ]] && placement="{\"nodeId\":\"$node\",\"gpuUuids\":[\"$gpu\"]}"
    curl -fsS -o /dev/null -X POST "$MASTER/api/models" -H 'Content-Type: application/json' \
      -d "{\"id\":\"$model\",\"servedName\":\"$model\",\"modelPath\":\"fake-model\",\"memoryBudgetGiB\":4,\"maxModelLen\":8192,\"maxNumSeqs\":4,\"placement\":$placement}"
    # 503/409 while the node is not Online yet or another start holds the node's start slot: retry.
    wait_until 120 "a start of $model on $node" curl -fsS -m 60 -X POST "$MASTER/api/models/$model/start"
    echo "    started $model on $node"
  done
  echo "    fake-devb placement: $(curl -fsS "$MASTER/api/models/fake-devb" | sed -n 's/.*"placement":\({[^}]*}\).*/\1/p')"
  wait_until 90 "both fake models in /v1/models" bash -c "curl -fsS '$MASTER/v1/models' | grep -q '\"fake-dgx\"' && curl -fsS '$MASTER/v1/models' | grep -q '\"fake-devb\"'"
  for model in fake-dgx fake-devb; do
    answer=$(curl -fsS -X POST "$MASTER/v1/chat/completions" -H 'Content-Type: application/json' \
      -d "{\"model\":\"$model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"max_tokens\":8}")
    echo "    $model (buffered): $(printf '%s' "$answer" | sed -n 's/.*"content":"\([^"]*\)".*/\1/p')"
    stream=$(curl -fsSN -X POST "$MASTER/v1/chat/completions" -H 'Content-Type: application/json' \
      -d "{\"model\":\"$model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"max_tokens\":8,\"stream\":true}")
    grep -q 'data: \[DONE\]' <<<"$stream" || { echo "$model stream did not end with [DONE]" >&2; exit 1; }
    echo "    $model (streamed): $(grep -c '^data: ' <<<"$stream") SSE events, ended with [DONE]"
  done
fi

step "Up"
echo "    UI:       $MASTER/ (Nodes: $MASTER/nodes)"
echo "    /v1:      $MASTER/v1/models"
echo "    logs:     $ROOT/logs"
echo "    stop:     tools/dev-multinode/stop.sh"
