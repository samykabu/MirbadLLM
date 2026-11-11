#!/usr/bin/env bash
# Installs, upgrades and removes mirbadllm-agent as a systemd service on a Linux PC: with NVIDIA GPUs, without a usable
# GPU (a CPU-only node), or with an AMD GPU through Vulkan (experimental). P2 node; design D7, §6.6. Not for the DGX:
# the DGX agent is a container (deploy/compose.multinode.yaml). README.md next to this file has the walk-through.
#
#   sudo ./install-agent.sh install --node-id pc-1 [--models-root DIR] [--cache-root DIR] [--firewall-from IP[/N]]...
#        [--accelerator auto|cuda|cpu|vulkan]   # auto: NVIDIA when nvidia-smi answers, else a CPU-only node
#   export MIRBADLLM_JOIN_TOKEN='<token>'   # or leave it unset and paste it at the prompt
#   sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./install-agent.sh install --node-id pc-1 \
#        --enroll-url https://<master>:8443 --ca-hash sha256:<hash> [--address 192.0.2.42]...
#   sudo MIRBADLLM_JOIN_TOKEN='<rekey token>' ./install-agent.sh rekey --node-id pc-1 --node-uid <uid> \
#        --enroll-url https://<master>:8443 --ca-hash sha256:<hash>   # re-pair ("--rekey" = this verb)
#   sudo ./install-agent.sh upgrade            # new binary + unit; agent.json and the identity are kept
#   sudo ./install-agent.sh rollback           # back to the binary and unit before the last install/upgrade
#   sudo ./install-agent.sh uninstall [--purge]
#   ./install-agent.sh package [--published-dir DIR] [--rid linux-x64|linux-arm64] [--output FILE]
#   ./install-agent.sh render-config --node-id pc-1 [--models-root DIR] [--cache-root DIR] [--accelerator cpu|vulkan]
#   ./install-agent.sh render-dropin [--models-root DIR] [--cache-root DIR]                  # print the unit drop-in
#
# The binary comes from --published-dir, from the bundle this script was unpacked with ("package"), or is published
# from this repository (src/MirbadLLM.Agent, self-contained, single file, this machine's architecture) inside the .NET
# SDK image, so nothing but the binary lands on the host.
#
# install (the default verb; re-running it is an upgrade):
# - creates the system user mirbadllm-agent (no login, no home) in the docker group and enables lingering for it
#   (its user manager runs native model processes later, design §6.6)
# - installs the binary to /usr/local/lib/mirbadllm-agent/mirbadllm-agent (root:root 0755); the previous one is kept
#   as mirbadllm-agent.previous for "rollback"
# - installs /etc/mirbadllm-agent/agent.json (root:mirbadllm-agent 0640) from deploy/agent/agent.linux.example.json
#   with the node id and the model/cache roots filled in, only if it does not exist yet. --accelerator decides its
#   "Accelerator" entry: none for NVIDIA (cuda), "cpu" for a machine without a usable GPU (llama.cpp runs on the CPU from
#   the CPU "server" image, containers get no GPU request, the budget counts against host RAM), "vulkan" for an AMD GPU
#   (llama.cpp's "server-vulkan" image with the GPU's DRM render node; experimental, not yet verified on AMD hardware,
#   never picked by auto). auto picks cuda when nvidia-smi lists a GPU and cpu otherwise. An existing agent.json keeps
#   its entry: edit it and restart the service after adding or removing a GPU.
# - installs mirbadllm-agent.service and the drop-in 10-install.conf (ReadWritePaths = the roots of agent.json)
# - with the join parameters (the command "Add node" shows; the token only from MIRBADLLM_JOIN_TOKEN or the prompt,
#   never argv): enrolls as mirbadllm-agent into /var/lib/mirbadllm-agent
# - when enrolled: enables and (re)starts the service, waits until it listens, and rolls the binary and unit back if it
#   does not come up (MIRBADLLM_AGENT_HEALTH_TIMEOUT seconds, default 180: the agent waits up to 2 min for Docker)
# - with --firewall-from: allows TCP 9443 from those sources in ufw or firewalld (recorded for uninstall)
# rekey (or --rekey; MN-114, design §9.4 step 5) re-pairs an installed node with a rekey token from the master's Nodes
# page, keeping its node uid: stops the service, runs "mirbadllm-agent join --rekey" as mirbadllm-agent (token from
# MIRBADLLM_JOIN_TOKEN or the prompt; --node-uid is needed when the state directory was lost), and starts the service
# on the new identity (or again on the old one when the rekey fails). Approve the node again if the master asks.
# uninstall stops and removes the service, the binary, the firewall rules and lingering, and keeps agent.json, the
# identity and every model/cache folder; --purge also deletes agent.json, the state directory and the user. Model
# containers keep running either way (the script lists them).
# Firewall: the master dials this agent on TCP 9443. Allow it from the master's address only, e.g.
#   ufw allow proto tcp from <master-ip> to any port 9443
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "${SCRIPT_DIR}/../../.." 2>/dev/null && pwd)" || REPO=""
SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
HEALTH_TIMEOUT="${MIRBADLLM_AGENT_HEALTH_TIMEOUT:-${SPARK_SERVE_AGENT_HEALTH_TIMEOUT:-180}}"
# The pre-rename update helper sets SPARK_SERVE_UPDATE_HELPER when it runs this script (legacy shim).
MIRBADLLM_UPDATE_HELPER="${MIRBADLLM_UPDATE_HELPER:-${SPARK_SERVE_UPDATE_HELPER:-0}}"
MIRBADLLM_JOIN_TOKEN="${MIRBADLLM_JOIN_TOKEN:-${SPARK_SERVE_JOIN_TOKEN:-}}"
unset SPARK_SERVE_JOIN_TOKEN
[[ -n "${MIRBADLLM_JOIN_TOKEN}" ]] || unset MIRBADLLM_JOIN_TOKEN
SYSTEMD_DIR="${MIRBADLLM_SYSTEMD_DIR:-/etc/systemd/system}" # tests point it elsewhere

UNIT=mirbadllm-agent.service
SERVICE_USER=mirbadllm-agent
PREFIX=/usr/local/lib/mirbadllm-agent
BINARY="${PREFIX}/mirbadllm-agent"
CONFIG_DIR=/etc/mirbadllm-agent
CONFIG="${CONFIG_DIR}/agent.json"
INSTALL_RECORD="${CONFIG_DIR}/install.record"
STATE_DIR=/var/lib/mirbadllm-agent
UNIT_PATH="/etc/systemd/system/${UNIT}"
DROPIN_DIR="/etc/systemd/system/${UNIT}.d"
DROPIN="${DROPIN_DIR}/10-install.conf"
DEFAULT_MODELS_ROOT=/srv/mirbadllm/models
DEFAULT_CACHE_ROOT=/srv/mirbadllm/cache
DEFAULT_PORT=9443
EXAMPLE_NODE_ID=pc-1
NODE_LABEL=mirbadllm.node
UPDATE_TRUST="${CONFIG_DIR}/update"
UPDATE_SPOOL="${STATE_DIR}/update"
UPDATE_HELPER="${PREFIX}/agent-update-helper.sh"
UPDATE_SERVICE_UNIT=/etc/systemd/system/mirbadllm-agent-update.service
UPDATE_PATH_UNIT=/etc/systemd/system/mirbadllm-agent-update.path

# An agent installed before the rename (SparkServe) keeps its names: install, upgrade (also one pushed from the master)
# and rollback work in place under its old unit, user, folders and update helper, so its identity, settings and running
# models stay where they are. Only the binary inside is new (docs/setup.md, "Upgrading from a pre-rename install").
LEGACY_AGENT_NAME=spark-serve-agent
LEGACY_INSTALL=0
if [[ ! -e "${SYSTEMD_DIR}/${UNIT}" && -e "${SYSTEMD_DIR}/${LEGACY_AGENT_NAME}.service" ]]; then
    LEGACY_INSTALL=1
    UNIT="${LEGACY_AGENT_NAME}.service"
    SERVICE_USER="${LEGACY_AGENT_NAME}"
    PREFIX="/usr/local/lib/${LEGACY_AGENT_NAME}"
    BINARY="${PREFIX}/${LEGACY_AGENT_NAME}"
    CONFIG_DIR="/etc/${LEGACY_AGENT_NAME}"
    CONFIG="${CONFIG_DIR}/agent.json"
    INSTALL_RECORD="${CONFIG_DIR}/install.record"
    STATE_DIR="/var/lib/${LEGACY_AGENT_NAME}"
    UNIT_PATH="/etc/systemd/system/${UNIT}"
    DROPIN_DIR="/etc/systemd/system/${UNIT}.d"
    DROPIN="${DROPIN_DIR}/10-install.conf"
    UPDATE_TRUST="${CONFIG_DIR}/update"
    UPDATE_SPOOL="${STATE_DIR}/update"
    UPDATE_HELPER="${PREFIX}/agent-update-helper.sh"
    UPDATE_SERVICE_UNIT="/etc/systemd/system/${LEGACY_AGENT_NAME}-update.service"
    UPDATE_PATH_UNIT="/etc/systemd/system/${LEGACY_AGENT_NAME}-update.path"
fi

step() { echo "==> $*"; }
warn() { echo "WARNING: $*" >&2; }
die() {
    echo "install-agent.sh: $1" >&2
    exit "${2:-1}"
}
usage() { sed -n '2,18p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

# ---- arguments ----

verb=install
if [[ $# -gt 0 && "$1" != -* ]]; then
    verb="$1"
    shift
fi

node_id=""
node_uid=""
enroll_url=""
ca_hash=""
models_root=""
cache_root=""
published_dir=""
rid=""
output=""
accelerator=auto
addresses=()
firewall_from=()
force_join=0
rekey=0
no_start=0
purge=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --node-id) node_id="${2:?--node-id needs a value}"; shift 2 ;;
        --node-uid) node_uid="${2:?--node-uid needs a value}"; shift 2 ;;
        --rekey) rekey=1; shift ;;
        --enroll-url) enroll_url="${2:?--enroll-url needs a value}"; shift 2 ;;
        --ca-hash) ca_hash="${2:?--ca-hash needs a value}"; shift 2 ;;
        --address) addresses+=("--address" "${2:?--address needs a value}"); shift 2 ;;
        --models-root) models_root="${2:?--models-root needs a value}"; shift 2 ;;
        --cache-root) cache_root="${2:?--cache-root needs a value}"; shift 2 ;;
        --published-dir) published_dir="${2:?--published-dir needs a value}"; shift 2 ;;
        --rid) rid="${2:?--rid needs a value}"; shift 2 ;;
        --output) output="${2:?--output needs a value}"; shift 2 ;;
        --accelerator) accelerator="${2:?--accelerator needs a value}"; shift 2 ;;
        --firewall-from) firewall_from+=("${2:?--firewall-from needs a value}"); shift 2 ;;
        --force-join) force_join=1; shift ;;
        --no-start) no_start=1; shift ;;
        --purge) purge=1; shift ;;
        --token) die "pass the join token in MIRBADLLM_JOIN_TOKEN (or at the prompt), not on the command line." 2 ;;
        -h | --help) usage; exit 0 ;;
        *) die "unknown argument '$1' (--help lists them)." 2 ;;
    esac
done

if ((rekey)); then
    [[ "${verb}" == install || "${verb}" == rekey ]] || die "--rekey is a verb of its own; it does not combine with '${verb}'." 2
    verb=rekey
fi
if [[ -n "${node_uid}" && "${verb}" != rekey ]]; then
    die "--node-uid applies only to rekey." 2
fi

case "${accelerator}" in
    auto | cuda | cpu | vulkan) ;;
    *) die "--accelerator must be auto, cuda, cpu or vulkan." 2 ;;
esac

valid_node_id() { [[ "$1" =~ ^[a-z0-9][a-z0-9-]{0,39}$ ]]; }
# AgentEnrollment.IsWellFormedUid: letters, digits and '-', at most 64.
valid_node_uid() { [[ "$1" =~ ^[A-Za-z0-9-]{1,64}$ ]]; }
# Absolute, plain characters only: the value goes into JSON and a unit file unescaped.
valid_root() { [[ "$1" =~ ^/[A-Za-z0-9._/-]*[A-Za-z0-9._-]$ && "$1" != *..* ]]; }
valid_source() { [[ "$1" =~ ^[0-9A-Fa-f:.]+(/[0-9]{1,3})?$ ]]; }

check_roots() {
    if [[ -n "${models_root}" ]] && ! valid_root "${models_root}"; then
        die "--models-root must be an absolute path of letters, digits, '.', '_', '-' and '/'." 2
    fi
    if [[ -n "${cache_root}" ]] && ! valid_root "${cache_root}"; then
        die "--cache-root must be an absolute path of letters, digits, '.', '_', '-' and '/'." 2
    fi
}

# ---- files of this checkout or bundle ----

# The bundle ("package") keeps everything next to the script; the repository keeps the example one level up.
example_config() {
    local f
    for f in "${SCRIPT_DIR}/agent.linux.example.json" "${SCRIPT_DIR}/../agent.linux.example.json"; do
        if [[ -f "${f}" ]]; then
            echo "${f}"
            return 0
        fi
    done
    die "agent.linux.example.json not found next to the script or in deploy/agent."
}

unit_source() {
    [[ -f "${SCRIPT_DIR}/mirbadllm-agent.service" ]] || die "mirbadllm-agent.service not found next to the script."
    echo "${SCRIPT_DIR}/mirbadllm-agent.service"
}

# The unit for this install: the shipped one, under the pre-rename names for an install made before the rename.
render_unit() {
    if [[ "${LEGACY_INSTALL}" == 1 ]]; then
        sed -e "s/mirbadllm-agent/${LEGACY_AGENT_NAME}/g" -e "s|/srv/mirbadllm/|/srv/spark-serve/|g" "$(unit_source)"
    else
        cat "$(unit_source)"
    fi
}

# agent.json for a node: the example with the node id and the roots substituted, each substitution checked. A fourth
# argument cpu or vulkan adds the "Accelerator" entry after the node id; cuda, auto or nothing adds none, so an NVIDIA
# node's file is the example's.
render_config() {
    local node="$1" models="$2" cache="$3" chosen="${4:-}" example text
    example="$(example_config)"
    text="$(sed -e "s|\"NodeId\": \"${EXAMPLE_NODE_ID}\"|\"NodeId\": \"${node}\"|" \
        -e "s|\"${DEFAULT_MODELS_ROOT}\"|\"${models}\"|g" \
        -e "s|\"${DEFAULT_CACHE_ROOT}\"|\"${cache}\"|g" "${example}")"
    grep -q "\"NodeId\": \"${node}\"" <<<"${text}" || die "${example} has no \"NodeId\": \"${EXAMPLE_NODE_ID}\" to fill in."
    grep -q "\"ModelsRoot\": \"${models}\"" <<<"${text}" || die "${example} has no ModelsRoot ${DEFAULT_MODELS_ROOT} to fill in."
    grep -q "\"CacheRoot\": \"${cache}\"" <<<"${text}" || die "${example} has no CacheRoot ${DEFAULT_CACHE_ROOT} to fill in."
    if [[ "${chosen}" == cpu || "${chosen}" == vulkan ]]; then
        text="$(sed -e "s|^\([[:space:]]*\)\"NodeId\": \"${node}\",|&\n\1\"Accelerator\": \"${chosen}\",|" <<<"${text}")"
        grep -q "\"Accelerator\": \"${chosen}\"" <<<"${text}" || die "${example} has no \"NodeId\" line to add \"Accelerator\" after."
    fi
    printf '%s\n' "${text}"
}

# What runs the models here when --accelerator is auto: cuda when nvidia-smi lists a GPU, else cpu. Vulkan is
# experimental and never picked.
detect_accelerator() {
    if command -v nvidia-smi >/dev/null && nvidia-smi -L >/dev/null 2>&1; then
        echo cuda
    else
        echo cpu
    fi
}

resolve_accelerator() {
    if [[ "${accelerator}" == auto ]]; then
        detect_accelerator
    else
        echo "${accelerator}"
    fi
}

# AMD cards whose driver exposes VRAM counters (amdgpu's mem_info_vram_total), one "cardN" per line.
amd_vram_cards() {
    local f
    for f in /sys/class/drm/card[0-9]*/device/mem_info_vram_total; do
        [[ -r "${f}" ]] || continue
        f="${f%/device/mem_info_vram_total}"
        echo "${f##*/}"
    done
}

render_dropin() {
    local models="$1" cache="$2"
    printf '%s\n' \
        "# Written by install-agent.sh: the model and cache roots of ${CONFIG} (rerun \"install-agent.sh upgrade\" after" \
        "# changing them there). The agent writes nowhere else but its StateDirectory." \
        "[Service]" \
        "ReadWritePaths=-${models} -${cache}"
}

# A string value of agent.json (the installed layout: one "Key": "value" per line).
config_string() {
    sed -n "s/^[[:space:]]*\"$1\":[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$2" | head -n 1
}

config_port() {
    local port
    port="$(sed -n 's/^[[:space:]]*"Port":[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$1" | head -n 1)"
    echo "${port:-${DEFAULT_PORT}}"
}

# ---- the binary ----

elf_rid() { # <file>: linux-x64 / linux-arm64 from the ELF header, empty otherwise
    local magic machine
    magic="$(od -An -c -N 4 "$1" 2>/dev/null | tr -d ' ')"
    [[ "${magic}" == '177ELF' ]] || return 0
    machine="$(od -An -t u2 -j 18 -N 2 "$1" | tr -d ' ')"
    case "${machine}" in
        62) echo linux-x64 ;;
        183) echo linux-arm64 ;;
    esac
}

host_rid() {
    case "$(uname -m)" in
        x86_64) echo linux-x64 ;;
        aarch64 | arm64) echo linux-arm64 ;;
        *) die "unsupported architecture $(uname -m)." ;;
    esac
}

WORK=""
cleanup() { if [[ -n "${WORK}" ]]; then rm -rf "${WORK}"; fi; }
trap cleanup EXIT
work_dir() {
    if [[ -z "${WORK}" ]]; then
        WORK="$(mktemp -d)"
    fi
}

# Sets NEW_BINARY (and NEW_BUILD_INFO when known) for <rid>.
obtain_binary() {
    local want="$1" src got
    NEW_BUILD_INFO=""
    if [[ -n "${published_dir}" ]]; then
        src="${published_dir}"
    elif [[ -f "${SCRIPT_DIR}/mirbadllm-agent" ]]; then
        src="${SCRIPT_DIR}"
    elif [[ -n "${REPO}" && -f "${REPO}/MirbadLLM.slnx" ]]; then
        publish_from_repo "${want}"
        src="${WORK}/out"
    else
        die "no agent binary: pass --published-dir, run from a bundle (\"package\") or from a mirbadllm checkout."
    fi

    NEW_BINARY="${src}/mirbadllm-agent"
    [[ -f "${NEW_BINARY}" ]] || die "${NEW_BINARY} does not exist."
    got="$(elf_rid "${NEW_BINARY}")"
    [[ "${got}" == "${want}" ]] || die "${NEW_BINARY} is not a ${want} executable (found '${got:-not ELF}')."
    if [[ -f "${src}/BUILD-INFO" ]]; then
        NEW_BUILD_INFO="${src}/BUILD-INFO"
    fi
}

publish_from_repo() {
    local want="$1" revision=""
    command -v docker >/dev/null || die "publishing the agent needs Docker (or pass --published-dir)."
    work_dir
    mkdir -p "${WORK}/out"
    if command -v git >/dev/null && git -C "${REPO}" rev-parse --git-dir >/dev/null 2>&1; then
        revision="$(git -C "${REPO}" rev-parse --short=12 HEAD)"
        if [[ -n "$(git -C "${REPO}" status --porcelain 2>/dev/null)" ]]; then
            revision="${revision}-dirty"
        fi
    fi
    step "Publishing the agent (${want}, self-contained, single file${revision:+, ${revision}}) with ${SDK_IMAGE}"
    # Only the agent's project closure is copied (as deploy/agent.Dockerfile does); the output is handed back to the
    # caller's uid.
    docker run --rm \
        -v "${REPO}:/repo:ro" \
        -v "${WORK}/out:/out" \
        -e RID="${want}" -e REVISION="${revision}" -e OWNER="$(id -u):$(id -g)" \
        "${SDK_IMAGE}" \
        bash -euc '
            mkdir -p /build/src
            cp /repo/global.json /repo/Directory.Build.props /repo/Directory.Packages.props /build/
            for p in MirbadLLM.Core MirbadLLM.Agent.Contracts MirbadLLM.Pki MirbadLLM.NodeRuntime MirbadLLM.Agent; do
                cp -r "/repo/src/$p" /build/src/
            done
            find /build/src -depth -type d \( -name bin -o -name obj \) -exec rm -rf {} +
            cd /build
            dotnet publish src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -c Release -r "$RID" \
                --self-contained true -p:PublishSingleFile=true -p:DebugType=none \
                ${REVISION:+-p:SourceRevisionId=$REVISION} -o /out --nologo -v quiet
            printf "revision=%s\nrid=%s\nbuilt=%s\n" "${REVISION:-unknown}" "$RID" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >/out/BUILD-INFO
            chown -R "$OWNER" /out
        '
    [[ -f "${WORK}/out/mirbadllm-agent" ]] || die "the publish did not produce mirbadllm-agent."
}

# ---- host checks ----

require_root() {
    [[ ${EUID} -eq 0 ]] || die "'${verb}' must run as root (sudo)."
}

check_host() {
    [[ -d /run/systemd/system ]] || die "systemd is not running as the init system on this host."
    getent group docker >/dev/null || die "no 'docker' group on this host: install Docker Engine first."
    # grep without -q reads all of ldconfig's output (no SIGPIPE under pipefail).
    if command -v ldconfig >/dev/null && ! ldconfig -p | grep 'libssl\.so\.[0-9]' >/dev/null; then
        die "OpenSSL (libssl) is missing; the agent's mTLS needs it (e.g. apt install libssl3)."
    fi
    local chosen="${1:-cuda}" docker_up=1
    if ! command -v docker >/dev/null || ! docker info >/dev/null 2>&1; then
        docker_up=0
        warn "the Docker engine does not answer: the agent waits up to 2 min for it at start and reports Degraded."
    fi
    case "${chosen}" in
        cpu)
            if [[ "${accelerator}" == auto ]]; then
                step "No NVIDIA GPU found (nvidia-smi is missing or lists none): this machine joins as a CPU-only node"
            else
                step "CPU-only node (--accelerator cpu)"
            fi
            echo "    llama.cpp runs on the CPU from the image ghcr.io/ggml-org/llama.cpp:server, model containers get no GPU request,"
            echo "    and a model's budget counts against host RAM. vLLM and SGLang need an NVIDIA GPU and are not offered here."
            echo "    With an NVIDIA card: install its driver and the NVIDIA Container Toolkit, remove \"Accelerator\" from ${CONFIG}"
            echo "    and restart ${UNIT}."
            ;;
        vulkan)
            warn "Vulkan nodes (AMD GPUs) are experimental: not yet verified on AMD hardware."
            if [[ -z "$(amd_vram_cards)" ]]; then
                warn "no GPU with VRAM counters found (/sys/class/drm/card*/device/mem_info_vram_total, the amdgpu driver): the node will report no GPU and refuse GPU placements. Intel GPUs are not supported on Linux yet."
            fi
            compgen -G '/dev/dri/renderD*' >/dev/null ||
                warn "no DRM render node (/dev/dri/renderD*): a model container cannot be given a GPU."
            ;;
        *)
            if ((docker_up)) && ! command -v nvidia-ctk >/dev/null && ! command -v nvidia-container-runtime-hook >/dev/null; then
                warn "the NVIDIA Container Toolkit is not installed: model containers cannot get GPUs (nvidia-container-toolkit)."
            fi
            if ! command -v nvidia-smi >/dev/null; then
                warn "nvidia-smi not found: install the NVIDIA driver; until then the node reports no GPUs."
            elif ! nvidia-smi -L >/dev/null 2>&1; then
                warn "nvidia-smi fails: the NVIDIA driver is not loaded; the node reports no GPUs."
            fi
            ;;
    esac
}

enrolled() { [[ -f "${STATE_DIR}/agent.crt" && -s "${STATE_DIR}/node-uid" ]]; }

# ---- firewall (ufw or firewalld; anything else is printed) ----

firewall_backend() {
    if command -v ufw >/dev/null && [[ "$(ufw status 2>/dev/null)" == "Status: active"* ]]; then
        echo ufw
    elif command -v firewall-cmd >/dev/null && [[ "$(firewall-cmd --state 2>/dev/null)" == running ]]; then
        echo firewalld
    fi
}

firewalld_rule() { # <source> <port>
    local family=ipv4
    if [[ "$1" == *:* ]]; then
        family=ipv6
    fi
    echo "rule family=${family} source address=$1 port port=$2 protocol=tcp accept"
}

firewall_allow() { # <source> <port>
    local backend
    backend="$(firewall_backend)"
    case "${backend}" in
        ufw)
            ufw allow proto tcp from "$1" to any port "$2" comment mirbadllm-agent >/dev/null
            ;;
        firewalld)
            firewall-cmd --quiet --permanent --add-rich-rule="$(firewalld_rule "$1" "$2")"
            firewall-cmd --quiet --reload
            ;;
        *)
            warn "no active ufw or firewalld: allow TCP $2 from $1 yourself (e.g. nft add rule inet filter input ip saddr $1 tcp dport $2 accept)."
            return 0
            ;;
    esac
    echo "firewall ${backend} $1 $2" >>"${INSTALL_RECORD}"
    step "Firewall (${backend}): TCP $2 allowed from $1"
}

firewall_remove_recorded() {
    [[ -f "${INSTALL_RECORD}" ]] || return 0
    local kind backend source port
    while read -r kind backend source port; do
        [[ "${kind}" == firewall ]] || continue
        case "${backend}" in
            ufw) ufw delete allow proto tcp from "${source}" to any port "${port}" >/dev/null 2>&1 || true ;;
            firewalld)
                firewall-cmd --quiet --permanent --remove-rich-rule="$(firewalld_rule "${source}" "${port}")" 2>/dev/null || true
                firewall-cmd --quiet --reload 2>/dev/null || true
                ;;
        esac
        step "Firewall (${backend}): removed TCP ${port} from ${source}"
    done <"${INSTALL_RECORD}"
}

# ---- service lifecycle ----

unit_prop() { systemctl show -p "$1" --value "${UNIT}"; }

listening() { [[ -n "$(ss -Hltn "sport = :$1" 2>/dev/null)" ]]; }

# Waits until the restarted unit is active and listens on <port>; fails on an exit, an automatic restart or the timeout.
wait_healthy() {
    local port="$1" restarts state deadline=$((SECONDS + HEALTH_TIMEOUT)) have_ss=1 active_since=-1
    restarts="$(unit_prop NRestarts)"
    command -v ss >/dev/null || have_ss=0
    while ((SECONDS < deadline)); do
        sleep 2
        state="$(unit_prop ActiveState)"
        if [[ "${state}" == failed || "$(unit_prop NRestarts)" != "${restarts}" ]]; then
            return 1
        fi
        [[ "${state}" == active ]] || continue
        if ((have_ss)); then
            if listening "${port}"; then
                return 0
            fi
        else
            # Without ss: up for 20 s without a restart.
            if ((active_since < 0)); then
                active_since=${SECONDS}
            elif ((SECONDS - active_since >= 20)); then
                return 0
            fi
        fi
    done
    return 1
}

show_status() {
    systemctl --no-pager --lines=20 status "${UNIT}" || true
}

# Saves the installed binary, unit and drop-in as *.previous (the state "rollback" returns to).
save_previous() {
    [[ -f "${BINARY}" ]] || return 0
    cp -p "${BINARY}" "${BINARY}.previous"
    if [[ -f "${UNIT_PATH}" ]]; then cp -p "${UNIT_PATH}" "${PREFIX}/${UNIT}.previous"; fi
    if [[ -f "${DROPIN}" ]]; then cp -p "${DROPIN}" "${PREFIX}/10-install.conf.previous"; fi
    if [[ -f "${PREFIX}/BUILD-INFO" ]]; then cp -p "${PREFIX}/BUILD-INFO" "${PREFIX}/BUILD-INFO.previous"; fi
}

restore_previous() {
    [[ -f "${BINARY}.previous" ]] || return 1
    install -m 0755 -o root -g root "${BINARY}.previous" "${BINARY}.new"
    mv -f "${BINARY}.new" "${BINARY}"
    if [[ -f "${PREFIX}/${UNIT}.previous" ]]; then install -m 0644 -o root -g root "${PREFIX}/${UNIT}.previous" "${UNIT_PATH}"; fi
    if [[ -f "${PREFIX}/10-install.conf.previous" ]]; then install -m 0644 -o root -g root "${PREFIX}/10-install.conf.previous" "${DROPIN}"; fi
    if [[ -f "${PREFIX}/BUILD-INFO.previous" ]]; then cp -p "${PREFIX}/BUILD-INFO.previous" "${PREFIX}/BUILD-INFO"; fi
    systemctl daemon-reload
}

# (Re)starts an enrolled agent and checks it; with <rollback>=1 a failed start puts the previous version back.
start_and_check() {
    local rollback="$1" port
    port="$(config_port "${CONFIG}")"
    systemctl enable "${UNIT}" >/dev/null 2>&1
    systemctl reset-failed "${UNIT}" 2>/dev/null || true
    step "Restarting ${UNIT}; waiting up to ${HEALTH_TIMEOUT} s for it to listen on ${port}"
    systemctl restart "${UNIT}"
    if wait_healthy "${port}"; then
        step "${UNIT} is running and listening on ${port}"
        return 0
    fi
    show_status
    if [[ "${rollback}" == 1 ]] && restore_previous; then
        warn "the new agent did not come up: rolled back to the previous binary and unit."
        systemctl reset-failed "${UNIT}" 2>/dev/null || true
        systemctl restart "${UNIT}"
        if wait_healthy "${port}"; then
            step "The previous agent is running again"
        else
            show_status
            warn "the previous agent does not come up either: journalctl -u ${UNIT}"
        fi
    fi
    die "${UNIT} did not come up (journalctl -u ${UNIT})."
}

# ---- verbs ----

# Sets token from MIRBADLLM_JOIN_TOKEN or, on a terminal, the prompt <label> (never from argv).
# ---- the update helper (pushed agent updates, docs/multinode/agent-updates.md U4/U5) ----
#
# A root oneshot service the agent cannot start itself: the path unit runs it when the agent writes an update order
# (the signature file, written last) into its spool. The helper checks the order against the trusted CA in
# ${UPDATE_TRUST}, copied here from the agent's state when an admin runs this script as root; an update the helper
# runs (MIRBADLLM_UPDATE_HELPER=1) keeps that copy as it is, so the agent can never choose what the helper trusts.

helper_source() {
    local f
    for f in "${SCRIPT_DIR}/agent-update-helper.sh" "${SCRIPT_DIR}/../agent-update-helper.sh"; do
        if [[ -f "${f}" ]]; then
            echo "${f}"
            return 0
        fi
    done
}

install_update_helper() {
    local src rid
    src="$(helper_source)"
    if [[ -z "${src}" ]]; then
        warn "agent-update-helper.sh is not next to this script: updates pushed from the master stay off on this node."
        return 0
    fi
    enrolled || return 0 # the trust copies need the node's uid and the master's CA
    if ! command -v openssl >/dev/null; then
        warn "openssl is not installed: updates pushed from the master stay off (install openssl, then run upgrade again)."
        return 0
    fi
    rid="$(elf_rid "${BINARY}")"
    step "Installing the update helper (${UPDATE_HELPER}, ${UPDATE_PATH_UNIT##*/})"
    install -m 0755 -o root -g root "${src}" "${UPDATE_HELPER}"
    install -d -m 0755 -o root -g root "${UPDATE_TRUST}"
    install -d -m 0700 -o "${SERVICE_USER}" -g "${SERVICE_USER}" "${UPDATE_SPOOL}"
    if [[ "${MIRBADLLM_UPDATE_HELPER:-0}" == 1 && -s "${UPDATE_TRUST}/ca.pem" && -s "${UPDATE_TRUST}/node-uid" ]]; then
        step "Keeping the trusted CA in ${UPDATE_TRUST} (this upgrade was pushed from the master)"
    else
        install -m 0644 -o root -g root "${STATE_DIR}/ca.crt" "${UPDATE_TRUST}/ca.pem"
        work_dir
        cat "${STATE_DIR}/node-uid" >"${WORK}/node-uid"
        install -m 0644 -o root -g root "${WORK}/node-uid" "${UPDATE_TRUST}/node-uid"
    fi
    printf '%s\n' "${rid}" >"${UPDATE_TRUST}/rid"
    cat >"${UPDATE_SERVICE_UNIT}" <<EOF
# mirbadllm agent update (root helper; docs/multinode/agent-updates.md). Started by ${UPDATE_PATH_UNIT##*/}.
[Unit]
Description=mirbadllm agent update from the master (root helper)

[Service]
Type=oneshot
ExecStart=${UPDATE_HELPER} ${UPDATE_SPOOL} ${UPDATE_TRUST} ${SERVICE_USER}
TimeoutStartSec=30min
EOF
    cat >"${UPDATE_PATH_UNIT}" <<EOF
# Runs ${UPDATE_SERVICE_UNIT##*/} when the agent hands over a signed update order.
[Unit]
Description=mirbadllm agent update orders

[Path]
PathExists=${UPDATE_SPOOL}/order.sig
Unit=${UPDATE_SERVICE_UNIT##*/}

[Install]
WantedBy=multi-user.target
EOF
    chmod 0644 "${UPDATE_SERVICE_UNIT}" "${UPDATE_PATH_UNIT}"
    systemctl daemon-reload
    systemctl enable --now "${UPDATE_PATH_UNIT##*/}" >/dev/null
    touch "${UPDATE_TRUST}/helper"
    chmod 0644 "${UPDATE_TRUST}/helper"
}

remove_update_helper() {
    systemctl disable --now "${UPDATE_PATH_UNIT##*/}" >/dev/null 2>&1 || true
    rm -f "${UPDATE_PATH_UNIT}" "${UPDATE_SERVICE_UNIT}" "${UPDATE_HELPER}"
    rm -rf "${UPDATE_TRUST}"
}

read_token() {
    token="${MIRBADLLM_JOIN_TOKEN:-}"
    if [[ -z "${token}" ]]; then
        # /dev/tty can exist without a controlling terminal (sudo in a pipeline, CI): opening it is the test.
        { true </dev/tty; } 2>/dev/null || die "joining needs the token: set MIRBADLLM_JOIN_TOKEN (sudo --preserve-env=MIRBADLLM_JOIN_TOKEN)." 2
        read -r -s -p "$1 for ${node_id}: " token </dev/tty
        echo >&2
        [[ -n "${token}" ]] || die "no join token given." 2
    fi
}

do_install() {
    local upgrade="$1" models cache join=0 token chosen
    require_root
    check_roots
    chosen="$(resolve_accelerator)"
    if [[ "${upgrade}" == 1 ]]; then
        [[ -f "${CONFIG}" && -f "${BINARY}" ]] || die "mirbadllm-agent is not installed here (run 'install' first)."
        [[ -z "${node_id}" ]] || die "'upgrade' takes the node id from ${CONFIG}; --node-id is for 'install'." 2
    fi
    if [[ -f "${CONFIG}" ]]; then
        local configured
        configured="$(config_string NodeId "${CONFIG}")"
        if [[ -n "${node_id}" && "${node_id}" != "${configured}" ]]; then
            die "${CONFIG} is for node '${configured}', not '${node_id}' (uninstall --purge first to change the node id)." 2
        fi
        node_id="${configured}"
        models="$(config_string ModelsRoot "${CONFIG}")"
        cache="$(config_string CacheRoot "${CONFIG}")"
        if [[ (-n "${models_root}" && "${models_root}" != "${models}") || (-n "${cache_root}" && "${cache_root}" != "${cache}") ]]; then
            warn "keeping the roots of the existing ${CONFIG} (${models}, ${cache}); edit it to move them."
        fi
        valid_root "${models}" || die "${CONFIG}: ModelsRoot '${models}' is not a plain absolute path."
        if [[ -z "${cache}" ]]; then cache="${models}"; fi
        valid_root "${cache}" || die "${CONFIG}: CacheRoot '${cache}' is not a plain absolute path."
    else
        models="${models_root:-${DEFAULT_MODELS_ROOT}}"
        cache="${cache_root:-${DEFAULT_CACHE_ROOT}}"
    fi
    valid_node_id "${node_id}" || die "--node-id is required: lower-case letters, digits and '-', at most 40 (design §3.1)." 2
    local source
    for source in ${firewall_from[@]+"${firewall_from[@]}"}; do
        valid_source "${source}" || die "--firewall-from '${source}' is not an IP address or CIDR." 2
    done

    if [[ -n "${enroll_url}" || -n "${ca_hash}" ]]; then
        [[ -n "${enroll_url}" && -n "${ca_hash}" ]] || die "joining needs --enroll-url and --ca-hash together." 2
        join=1
        if enrolled && [[ "${force_join}" != 1 ]]; then
            warn "node ${node_id} is enrolled already: join skipped (--force-join replaces the identity; the master then sees a new key)."
            join=0
        fi
    fi
    token=""
    if ((join)); then
        read_token "Join token"
    fi
    unset MIRBADLLM_JOIN_TOKEN

    if [[ -f "${CONFIG}" ]]; then
        # An existing agent.json decides; the host checks follow it, and a difference is said, not applied.
        local configured_accelerator
        configured_accelerator="$(config_string Accelerator "${CONFIG}")"
        configured_accelerator="${configured_accelerator:-cuda}"
        if [[ "${configured_accelerator}" != "${chosen}" ]]; then
            warn "${CONFIG} says Accelerator ${configured_accelerator}, this run would choose ${chosen}: the file is kept. To change it, edit \"Accelerator\" in the Agent section (cpu, vulkan, or no entry for NVIDIA) and restart ${UNIT}."
            chosen="${configured_accelerator}"
        fi
    fi
    check_host "${chosen}"
    obtain_binary "$(host_rid)"

    if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
        step "Creating the system user ${SERVICE_USER}"
        useradd --system --user-group --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "${SERVICE_USER}"
    fi
    usermod -aG docker "${SERVICE_USER}"
    if command -v loginctl >/dev/null && loginctl enable-linger "${SERVICE_USER}" 2>/dev/null; then
        step "Lingering enabled for ${SERVICE_USER} (its user manager runs without a login)"
    else
        warn "could not enable lingering for ${SERVICE_USER} (systemd-logind); only native model processes need it."
    fi

    install -d -m 0750 -o root -g "${SERVICE_USER}" "${CONFIG_DIR}"
    if [[ -f "${CONFIG}" ]]; then
        step "Keeping ${CONFIG} (node ${node_id})"
    else
        step "Installing ${CONFIG} for node ${node_id} (models ${models}, cache ${cache})"
        work_dir
        render_config "${node_id}" "${models}" "${cache}" "${chosen}" >"${WORK}/agent.json"
        install -m 0640 -o root -g "${SERVICE_USER}" "${WORK}/agent.json" "${CONFIG}"
    fi

    local dir
    for dir in "${models}" "${cache}"; do
        if [[ -d "${dir}" ]]; then
            runuser -u "${SERVICE_USER}" -- test -w "${dir}" ||
                warn "${dir} exists but ${SERVICE_USER} cannot write it (downloads and folder deletes will fail)."
        else
            install -d -m 0755 -o "${SERVICE_USER}" -g "${SERVICE_USER}" "${dir}"
        fi
    done
    install -d -m 0700 -o "${SERVICE_USER}" -g "${SERVICE_USER}" "${STATE_DIR}"

    step "Installing ${BINARY}"
    install -d -m 0755 -o root -g root "${PREFIX}"
    save_previous
    install -m 0755 -o root -g root "${NEW_BINARY}" "${BINARY}.new"
    mv -f "${BINARY}.new" "${BINARY}"
    if [[ -n "${NEW_BUILD_INFO}" ]]; then
        install -m 0644 -o root -g root "${NEW_BUILD_INFO}" "${PREFIX}/BUILD-INFO"
    fi

    step "Installing ${UNIT} and ${DROPIN}"
    work_dir
    render_unit >"${WORK}/${UNIT}"
    install -m 0644 -o root -g root "${WORK}/${UNIT}" "${UNIT_PATH}"
    install -d -m 0755 -o root -g root "${DROPIN_DIR}"
    work_dir
    render_dropin "${models}" "${cache}" >"${WORK}/10-install.conf"
    install -m 0644 -o root -g root "${WORK}/10-install.conf" "${DROPIN}"
    systemctl daemon-reload

    local port
    port="$(config_port "${CONFIG}")"
    for source in ${firewall_from[@]+"${firewall_from[@]}"}; do
        if [[ -f "${INSTALL_RECORD}" ]] && grep -qx "firewall [a-z]* ${source} ${port}" "${INSTALL_RECORD}"; then
            continue
        fi
        firewall_allow "${source}" "${port}"
    done

    if ((join)); then
        step "Enrolling node ${node_id} at ${enroll_url}"
        local force=()
        if [[ "${force_join}" == 1 ]]; then force=(--force); fi
        # The token reaches the agent through its environment only (runuser -u keeps the environment).
        MIRBADLLM_JOIN_TOKEN="${token}" runuser -u "${SERVICE_USER}" -- \
            "${BINARY}" join --enroll-url "${enroll_url}" --ca-hash "${ca_hash}" --node-id "${node_id}" \
            --state-dir "${STATE_DIR}" ${addresses[@]+"${addresses[@]}"} ${force[@]+"${force[@]}"} ||
            die "join failed; the service is installed but not started. Fix the cause and re-run with the join parameters."
        token=""
    fi

    install_update_helper

    if ! enrolled; then
        step "Installed but not started: node ${node_id} is not enrolled yet. Re-run with --enroll-url, --ca-hash and"
        echo "    the join token (\"Add node\" on the master); the unit is skipped until then."
    elif ((no_start)); then
        step "Installed; not started (--no-start): systemctl enable --now ${UNIT}"
    else
        local rollback=0
        if [[ -f "${BINARY}.previous" ]]; then rollback=1; fi
        start_and_check "${rollback}"
        if ((join)); then
            step "Done. Node ${node_id} waits on the master's Nodes page: approve it there (compare the key fingerprint)."
        else
            step "Done (node ${node_id})."
        fi
        echo "    Logs: journalctl -u ${UNIT}"
    fi
    if [[ ! -f "${INSTALL_RECORD}" ]] || ! grep -q '^firewall ' "${INSTALL_RECORD}"; then
        echo "==> Firewall: allow TCP ${port} from the master only (e.g. ufw allow proto tcp from <master-ip> to any port ${port}),"
        echo "    or re-run with --firewall-from <master-ip>."
    fi
}

# Re-pairs the installed node (join --rekey, MN-114): stop, replace the identity as the service user, start.
do_rekey() {
    local token="" configured
    require_root
    [[ -f "${CONFIG}" && -f "${BINARY}" ]] || die "mirbadllm-agent is not installed here (rekey re-pairs an installed node; run 'install' first)."
    configured="$(config_string NodeId "${CONFIG}")"
    if [[ -n "${node_id}" && "${node_id}" != "${configured}" ]]; then
        die "${CONFIG} is for node '${configured}', not '${node_id}'." 2
    fi
    node_id="${configured}"
    valid_node_id "${node_id}" || die "${CONFIG}: NodeId '${node_id}' is not a node id."
    [[ -n "${enroll_url}" && -n "${ca_hash}" ]] || die "rekey needs --enroll-url and --ca-hash (the rekey command on the master's Nodes page)." 2
    if [[ -n "${node_uid}" ]] && ! valid_node_uid "${node_uid}"; then
        die "--node-uid must be letters, digits and '-', at most 64 (as shown on the master's Nodes page)." 2
    fi
    [[ "${force_join}" != 1 ]] || die "--force-join does not apply to rekey (a re-pair keeps the node uid)." 2
    read_token "Rekey token"
    unset MIRBADLLM_JOIN_TOKEN

    # The state directory may be gone (the reason for a rekey); join --rekey then needs --node-uid.
    install -d -m 0700 -o "${SERVICE_USER}" -g "${SERVICE_USER}" "${STATE_DIR}"
    step "Stopping ${UNIT} while the identity of node ${node_id} is replaced"
    systemctl stop "${UNIT}"
    local uid_args=()
    if [[ -n "${node_uid}" ]]; then uid_args=(--node-uid "${node_uid}"); fi
    step "Re-pairing node ${node_id} at ${enroll_url}"
    # The token reaches the agent through its environment only (runuser -u keeps the environment).
    if ! MIRBADLLM_JOIN_TOKEN="${token}" runuser -u "${SERVICE_USER}" -- \
        "${BINARY}" join --rekey --enroll-url "${enroll_url}" --ca-hash "${ca_hash}" --node-id "${node_id}" \
        ${uid_args[@]+"${uid_args[@]}"} --state-dir "${STATE_DIR}" ${addresses[@]+"${addresses[@]}"}; then
        token=""
        if enrolled; then
            warn "join --rekey failed: starting ${UNIT} again on its previous identity."
            systemctl start "${UNIT}" || true
        fi
        die "join --rekey failed (the line above names the cause); the identity is unchanged. Fix it and re-run with a valid rekey token."
    fi
    token=""
    # The rekey may carry a new CA (after a CA rotation): the helper trusts it from now on.
    install_update_helper
    start_and_check 0
    step "Done: node ${node_id} re-paired (uid kept). If the master shows it pending, approve it after comparing the fingerprint above."
    echo "    Logs: journalctl -u ${UNIT}"
}

# Swaps <current> and <previous>; a missing current just takes the previous one.
swap_previous() {
    [[ -f "$2" ]] || return 0
    if [[ -f "$1" ]]; then
        mv -f "$1" "$1.swap"
        mv -f "$2" "$1"
        mv -f "$1.swap" "$2"
    else
        mv -f "$2" "$1"
    fi
}

do_rollback() {
    require_root
    [[ -f "${BINARY}.previous" ]] || die "no previous version in ${PREFIX}."
    step "Rolling back to the previous binary and unit (a second rollback returns to the newer ones)"
    swap_previous "${BINARY}" "${BINARY}.previous"
    swap_previous "${UNIT_PATH}" "${PREFIX}/${UNIT}.previous"
    swap_previous "${DROPIN}" "${PREFIX}/10-install.conf.previous"
    swap_previous "${PREFIX}/BUILD-INFO" "${PREFIX}/BUILD-INFO.previous"
    systemctl daemon-reload
    if enrolled && [[ "$(systemctl is-enabled "${UNIT}" 2>/dev/null)" == enabled ]]; then
        start_and_check 0
    fi
}

do_uninstall() {
    require_root
    local node=""
    if [[ -f "${CONFIG}" ]]; then node="$(config_string NodeId "${CONFIG}")"; fi
    remove_update_helper
    step "Stopping and removing ${UNIT}"
    systemctl disable --now "${UNIT}" >/dev/null 2>&1 || true
    rm -f "${UNIT_PATH}"
    rm -rf "${DROPIN_DIR}"
    systemctl daemon-reload
    systemctl reset-failed "${UNIT}" >/dev/null 2>&1 || true
    firewall_remove_recorded
    rm -f "${INSTALL_RECORD}"
    rm -rf "${PREFIX}"
    if id -u "${SERVICE_USER}" >/dev/null 2>&1; then
        loginctl disable-linger "${SERVICE_USER}" >/dev/null 2>&1 || true
        systemctl stop "user@$(id -u "${SERVICE_USER}").service" >/dev/null 2>&1 || true
    fi
    if ((purge)); then
        step "Purging ${CONFIG_DIR}, ${STATE_DIR} (the node identity) and the user ${SERVICE_USER}"
        rm -rf "${CONFIG_DIR}" "${STATE_DIR}"
        if id -u "${SERVICE_USER}" >/dev/null 2>&1; then
            userdel "${SERVICE_USER}"
        fi
        if getent group "${SERVICE_USER}" >/dev/null; then groupdel "${SERVICE_USER}"; fi
    else
        step "Kept ${CONFIG} and ${STATE_DIR} (identity): 'install' reuses them; 'uninstall --purge' deletes them"
    fi
    if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
        local containers
        containers="$(docker ps -a --filter "label=${NODE_LABEL}" --format '{{.Names}}' | tr '\n' ' ')"
        if [[ -n "${containers// /}" ]]; then
            step "Model containers of this node keep running: ${containers}"
            echo "    remove them with: docker rm -f \$(docker ps -aq --filter label=${NODE_LABEL})"
        fi
    fi
    step "Model and cache folders are never removed. Revoke node ${node:-<id>} on the master's Nodes page."
}

do_package() {
    local want version name stage
    if [[ -n "${rid}" ]]; then
        want="${rid}"
    elif [[ -n "${published_dir}" && -f "${published_dir}/mirbadllm-agent" ]]; then
        want="$(elf_rid "${published_dir}/mirbadllm-agent")"
    else
        want="$(host_rid)"
    fi
    [[ "${want}" == linux-x64 || "${want}" == linux-arm64 ]] || die "--rid must be linux-x64 or linux-arm64." 2
    obtain_binary "${want}"
    version=local
    if [[ -n "${NEW_BUILD_INFO}" ]]; then
        version="$(sed -n 's/^revision=//p' "${NEW_BUILD_INFO}")"
        version="${version:-local}"
    fi
    name="mirbadllm-agent-${version}-${want}"
    output="${output:-${PWD}/${name}.tar.gz}"
    work_dir
    stage="${WORK}/stage/${name}"
    mkdir -p "${stage}"
    install -m 0755 "${SCRIPT_DIR}/install-agent.sh" "${stage}/install-agent.sh"
    if [[ -n "$(helper_source)" ]]; then install -m 0755 "$(helper_source)" "${stage}/agent-update-helper.sh"; fi
    install -m 0644 "$(unit_source)" "${stage}/mirbadllm-agent.service"
    install -m 0644 "$(example_config)" "${stage}/agent.linux.example.json"
    if [[ -f "${SCRIPT_DIR}/README.md" ]]; then install -m 0644 "${SCRIPT_DIR}/README.md" "${stage}/README.md"; fi
    install -m 0755 "${NEW_BINARY}" "${stage}/mirbadllm-agent"
    if [[ -n "${NEW_BUILD_INFO}" ]]; then install -m 0644 "${NEW_BUILD_INFO}" "${stage}/BUILD-INFO"; fi
    # The folder's pre-rename name, a link: the update helper installed on a pre-rename node looks for it.
    ln -s "${name}" "${WORK}/stage/spark-serve-agent-${version}-${want}"
    tar -czf "${output}" -C "${WORK}/stage" "${name}" "spark-serve-agent-${version}-${want}"
    step "Wrote ${output}"
    echo "    on the PC: tar -xzf ${name}.tar.gz && sudo ./${name}/install-agent.sh install --node-id <id> ..."
}

case "${verb}" in
    install) do_install 0 ;;
    upgrade) do_install 1 ;;
    rekey) do_rekey ;;
    rollback) do_rollback ;;
    uninstall) do_uninstall ;;
    package) do_package ;;
    render-config)
        check_roots
        valid_node_id "${node_id}" || die "--node-id is required: lower-case letters, digits and '-', at most 40." 2
        # No hardware is probed here: auto prints the example's file (NVIDIA), as cuda does.
        render_config "${node_id}" "${models_root:-${DEFAULT_MODELS_ROOT}}" "${cache_root:-${DEFAULT_CACHE_ROOT}}" "${accelerator}"
        ;;
    render-dropin)
        check_roots
        render_dropin "${models_root:-${DEFAULT_MODELS_ROOT}}" "${cache_root:-${DEFAULT_CACHE_ROOT}}"
        ;;
    render-unit) render_unit ;; # the unit this install gets (tests)
    help) usage ;;
    *) die "unknown verb '${verb}' (install, upgrade, rekey, rollback, uninstall, package, render-config, render-dropin)." 2 ;;
esac
