#!/usr/bin/env bash
# The entrypoint of the demo image (deploy/Dockerfile, target "demo"; docs/setup.md, "Demo"): a master and two node
# agents in one container, on the pieces of the development fleet (tools/dev-multinode): the agents' DevFakeRuntime and
# the master's development join-token helper. No GPU, no Docker socket, no model files.
#
#   docker run --rm -p 127.0.0.1:8080:8080 ghcr.io/samykabu/mirbadllm-demo
#
# Everything on screen is fake: the nodes demo-dgx (128 GiB unified) and demo-pc (two 24 GiB GPUs) are processes in this
# container, the models are small servers that answer with made-up text, and the traffic is this script calling /v1 in a
# loop, so the benchmark and token figures mean nothing. The agents, mTLS, admission, the router and the UI are the real
# code. Downloads, image pulls and host services do not work here. The state is in /tmp and goes with the container.
#
# Not a deployment: the master runs in the Development environment (both development switches refuse to start outside
# it) with no admin password and no API key, so anyone who reaches the published port has full control of it. Publish
# the port on loopback, as above, and publish only 8080: the agents listen on the container's loopback, and the enroll
# listener (8443) has no use outside it.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT=/tmp/mirbadllm-demo
PORT=8080
# demo-pc's fake GPUs carry the fake runtime's placeholder UUIDs (DevFakeRuntimeOptions.PlaceholderUuid), in index order.
GPU0="GPU-00000000-0000-0000-0000-00000000de00"
GPU1="GPU-00000000-0000-0000-0000-00000000de01"
# id : node : GPU of a discrete node : memory budget in GiB
MODELS="fake-chat-large:demo-dgx::48 fake-coder:demo-dgx::24 fake-chat-small:demo-pc:$GPU0:12 fake-assistant:demo-pc:$GPU1:16"

step() { echo "==> $*"; }

fail() {
  echo "The demo did not start: $*" >&2
  for log in "$ROOT"/logs/*.log; do echo "--- last lines of $log" >&2; tail -n 25 "$log" >&2 || true; done
  exit 1
}

# http <method> <path> [json]: the response (headers and body) on stdout; fails unless the status is 2xx.
# Plain bash on /dev/tcp, because the image has no curl. HTTP/1.0, so the master closes the connection after the answer.
http() {
  local body="${3:-}" status=""
  {
    printf '%s %s HTTP/1.0\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: %d\r\n\r\n%s' \
      "$1" "$2" "${#body}" "$body" >&3 || return 1
    IFS= read -r status <&3 || return 1
    cat <&3
  } 3<>"/dev/tcp/127.0.0.1/$PORT"
  [[ "$status" == "HTTP/1."?" 2"* ]]
}

wait_until() { # <seconds> <what> <command...>
  local deadline=$(( $(date +%s) + $1 )) what="$2"; shift 2
  until "$@" >/dev/null 2>&1; do
    (( $(date +%s) < deadline )) || fail "timed out waiting for $what"
    sleep 0.5
  done
}

# A string field of a JSON file. System.Text.Json writes "+" as \u002B, which base64 values contain.
json_field() { sed -n "s/.*\"$1\": *\"\([^\"]*\)\".*/\1/p" "$2" | head -n 1 | sed 's/\\u002[Bb]/+/g'; }

start() { # <name> <command...>: in the background, its output in logs/<name>.log
  local name="$1"; shift
  "$@" >"$ROOT/logs/$name.log" 2>&1 &
}

# The agent of this architecture, from the bundle the master serves to Linux nodes (self-contained, one file).
case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64) rid=linux-arm64 ;;
  *) echo "The demo has no agent for $(uname -m)." >&2; exit 1 ;;
esac

trap 'kill $(jobs -p) 2>/dev/null; exit 0' TERM INT
rm -rf "$ROOT"
mkdir -p "$ROOT/logs" "$ROOT/bootstrap" "$ROOT/join" "$ROOT/agent"
tar -xzf "/app/agent-bundles/$rid.tar.gz" -C "$ROOT/agent" --strip-components=1 --wildcards '*/mirbadllm-agent'
AGENT="$ROOT/agent/mirbadllm-agent"
for node in demo-dgx demo-pc; do
  mkdir -p "$ROOT/$node/models/fake-model" "$ROOT/$node/cache" "$ROOT/$node/state"
  printf '%s\n' '{"architectures":["LlamaForCausalLM"],"model_type":"llama"}' >"$ROOT/$node/models/fake-model/config.json"
done

echo "MirbadLLM demo: fake nodes, fake models and generated traffic. No login: keep the port on loopback."
export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development

step "Starting the master"
( cd /app && exec env \
    ASPNETCORE_HTTP_PORTS="$PORT" \
    MirbadLLM__DataPath="$ROOT/master" \
    MirbadLLM__Backup__Directory="$ROOT/backups" \
    MirbadLLM__Backup__HostName=demo \
    MirbadLLM__HostAgentSocket="$ROOT/no-hostd.sock" \
    MirbadLLM__Nodes__BootstrapNodeId=demo-dgx \
    MirbadLLM__Enrollment__Port=8443 \
    MirbadLLM__Enrollment__BootstrapDirectory="$ROOT/bootstrap" \
    MirbadLLM__Enrollment__BootstrapEnrollEndpoint=https://127.0.0.1:8443 \
    MirbadLLM__Enrollment__BootstrapAgentAddress=https://127.0.0.1:9443 \
    MirbadLLM__DevJoinTokens__Nodes__0=demo-pc \
    MirbadLLM__DevJoinTokens__OutputDirectory="$ROOT/join" \
    dotnet MirbadLLM.Web.dll ) >"$ROOT/logs/master.log" 2>&1 &
wait_until 90 "the master" http GET /healthz
# A new master seeds one model definition with a real model's name; the demo shows only its own fake ones.
http DELETE /api/models/ornith-9b-fp8 >/dev/null 2>&1 || true

step "Starting node demo-dgx (128 GiB unified memory)"
start agent-demo-dgx "$AGENT" --config "$HERE/agent.demo-dgx.json"

step "Joining and starting node demo-pc (two 24 GiB GPUs)"
JOIN="$ROOT/join/demo-pc.join.json"
wait_until 90 "the join token of demo-pc" test -f "$JOIN"
MIRBADLLM_JOIN_TOKEN="$(json_field token "$JOIN")" "$AGENT" join \
  --enroll-url "$(json_field enrollUrl "$JOIN")" --ca-hash "$(json_field caHash "$JOIN")" \
  --node-id demo-pc --state-dir "$ROOT/demo-pc/state" --address https://127.0.0.1:9444 >"$ROOT/logs/join-demo-pc.log" 2>&1 ||
  fail "demo-pc could not join"
start agent-demo-pc "$AGENT" --config "$HERE/agent.demo-pc.json"

step "Adding and starting the fake models"
for entry in $MODELS; do
  IFS=: read -r model node gpu budget <<<"$entry"
  placement="{\"nodeId\":\"$node\"}"
  [[ -z "$gpu" ]] || placement="{\"nodeId\":\"$node\",\"gpuUuids\":[\"$gpu\"]}"
  # Refused until the node is approved and has reported its memory and GPUs: retry.
  wait_until 120 "node $node to accept $model" http POST /api/models \
    "{\"id\":\"$model\",\"servedName\":\"$model\",\"modelPath\":\"fake-model\",\"memoryBudgetGiB\":$budget,\"maxModelLen\":8192,\"maxNumSeqs\":4,\"placement\":$placement}"
  # 503 or 409 while the node is not Online yet or another start holds its start slot: retry.
  wait_until 120 "a start of $model on $node" http POST "/api/models/$model/start"
  echo "    $model on $node"
done
served() { local list; list="$(http GET /v1/models)" && for entry in $MODELS; do [[ "$list" == *"\"${entry%%:*}\""* ]] || return 1; done; }
wait_until 90 "the fake models in /v1/models" served

# Generated traffic: two callers per model, each asking for a streamed reply of random length, then pausing.
traffic() {
  while :; do
    http POST /v1/chat/completions \
      "{\"model\":\"$1\",\"messages\":[{\"role\":\"user\",\"content\":\"A demo request\"}],\"max_tokens\":$(( RANDOM % 200 + 40 )),\"stream\":true}" \
      >/dev/null 2>&1 || true
    sleep $(( RANDOM % 4 + 1 ))
  done
}
for entry in $MODELS; do traffic "${entry%%:*}" & traffic "${entry%%:*}" & done

version="$(http GET /api/releases | sed -n 's/.*"controller":"\([^"]*\)".*/\1/p' | head -n 1)"
touch "$ROOT/ready"
step "Up: MirbadLLM ${version:-} with a fake fleet. Open the port you published, for example http://127.0.0.1:8080/"

# The container lives as long as the master and both agents do.
wait -n || true
fail "a demo process exited"
