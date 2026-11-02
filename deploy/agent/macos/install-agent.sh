#!/bin/bash
# Installs, upgrades and removes mirbadllm-agent as a LaunchDaemon on an Apple Silicon Mac (macOS 13 or newer;
# plan docs/macos/implementation-plan.md M1, M9, Phase 1). The macOS port of deploy/agent/linux/install-agent.sh:
# same verbs, arguments and token handling. README.md next to this file has the walk-through.
#
#   sudo ./install-agent.sh install --node-id mac-1 [--models-root DIR] [--cache-root DIR] [--allow-firewall]
#   export MIRBADLLM_JOIN_TOKEN='<token>'   # or leave it unset and paste it at the prompt
#   sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./install-agent.sh install --node-id mac-1 \
#        --enroll-url https://<master>:8443 --ca-hash sha256:<hash> [--address 192.0.2.176]...
#   sudo MIRBADLLM_JOIN_TOKEN='<rekey token>' ./install-agent.sh rekey --node-id mac-1 --node-uid <uid> \
#        --enroll-url https://<master>:8443 --ca-hash sha256:<hash>   # re-pair ("--rekey" = this verb)
#   sudo ./install-agent.sh upgrade            # new binary + plist; agent.json and the identity are kept
#   sudo ./install-agent.sh rollback           # back to the binary and plist before the last install/upgrade
#   sudo ./install-agent.sh uninstall [--purge]
#   sudo ./install-agent.sh update-llama       # ("--update-llama") re-provision llama.cpp only, restart the agent
#   sudo ./install-agent.sh update-mlx         # ("--update-mlx") install or re-provision MLX (mlx-lm) only, restart the agent
#   sudo ./install-agent.sh install-hostd [--discover-user NAME]...   # the root helper for launchd jobs (Services tab)
#   sudo ./install-agent.sh uninstall-hostd [--purge]
#   ./install-agent.sh package [--published-dir DIR] [--rid osx-arm64] [--output FILE]   # also runs on Linux
#   ./install-agent.sh render-config --node-id mac-1 [--models-root DIR] [--cache-root DIR]   # print agent.json
#   install/upgrade: [--no-llama] (no llama.cpp) [--with-mlx] (also MLX); install/upgrade/update-llama: [--llama-source DIR]
#   (pinned tarball, no download); install/upgrade/update-mlx: [--mlx-source DIR] (the pinned uv tarball, no download);
#   install/upgrade: [--with-hostd] (also install-hostd)
#
# The binary comes from --published-dir, from the bundle this script was unpacked with ("package"), or is published
# from this repository (src/MirbadLLM.Agent, osx-arm64, self-contained, single file) with a local "dotnet" or inside
# the .NET SDK image.
#
# install (the default verb; re-running it is an upgrade):
# - creates the hidden system user and group _mirbadllm (dscl; uid/gid below 500, no shell, home /var/empty)
# - installs the binary to /usr/local/lib/mirbadllm-agent/mirbadllm-agent (root:wheel 0755), checks its code
#   signature (re-signs ad hoc when it does not verify) and drops the quarantine flag; the previous binary is kept as
#   mirbadllm-agent.previous for "rollback"
# - installs "/Library/Application Support/mirbadllm-agent/agent.json" (root:_mirbadllm 0640) from
#   agent.macos.example.json with the node id and the model/cache roots filled in, only if it does not exist yet
# - with the join parameters (the command "Add node" shows; the token only from MIRBADLLM_JOIN_TOKEN or the prompt,
#   never argv): enrolls as _mirbadllm into "/Library/Application Support/mirbadllm-agent/state"
# - when enrolled: installs /Library/LaunchDaemons/com.mirbadllm.agent.plist, (re)loads it, waits until the agent
#   listens, and rolls the binary and plist back if it does not come up (MIRBADLLM_AGENT_HEALTH_TIMEOUT seconds,
#   default 180)
# - unless --no-llama: installs the llama.cpp release pinned in llama-cpp.lock.json (Metal; plan M5, Phase 2) into
#   /usr/local/lib/mirbadllm-agent/runtimes/llama.cpp/<version>/ (root:wheel; only llama-server and the dylibs it
#   loads, each checked against the lock file, plus manifest.json) and registers it in agent.json's Agent:Executables
#   as the default llama-server; an installed version that still verifies is kept, a damaged one is replaced
# - with --with-mlx (or on an upgrade when agent.json already registers MLX): installs the MLX runtime pinned in
#   mlx-lm.lock.json (plan M6, Phase 3) into /usr/local/lib/mirbadllm-agent/runtimes/mlx-lm/<version>/: its own
#   CPython (installed by the pinned uv), a virtualenv synced from the hash-locked mlx-lm.lock.txt with compiled
#   bytecode, and manifest.json listing every file ("allFiles"); registered as the default mlx-lm-server (~520 MB)
# - with --with-hostd (or on an upgrade when hostd is installed): installs mirbadllm-hostd (plan M10, Phase 4), the
#   root LaunchDaemon com.mirbadllm.hostd behind the Services tab: binary in /usr/local/lib/mirbadllm-hostd, its
#   hostd.json and whitelist in "/Library/Application Support/mirbadllm-hostd" (root-owned), socket
#   /var/run/mirbadllm-hostd/hostd.sock (root:_mirbadllm); sets Agent:ManageLaunchdJobs and Agent:HostdSocket
# - with --allow-firewall: allows the agent binary in the macOS application firewall (recorded for uninstall)
# rekey (or --rekey) re-pairs an installed node with a rekey token from the master's Nodes page, keeping its node uid:
# stops the daemon, runs "mirbadllm-agent join --rekey" as _mirbadllm (--node-uid is needed when the state
# directory was lost) and starts the daemon on the new identity (or again on the old one when the rekey fails).
# uninstall stops and removes the daemon, the binary and the firewall entry, and keeps agent.json, the identity, the
# logs and every model/cache folder; --purge also deletes agent.json, the state directory, the logs and the user.
# Model processes keep running on a plain uninstall (the script lists them); --purge stops them before deleting the user.
# Firewall: the master dials this agent on TCP 9443.
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
LAUNCHD_DIR="${MIRBADLLM_LAUNCHD_DIR:-/Library/LaunchDaemons}" # tests point it elsewhere

LABEL=com.mirbadllm.agent
PLIST_NAME="${LABEL}.plist"
PLIST="/Library/LaunchDaemons/${PLIST_NAME}"
SERVICE_USER=_mirbadllm
PREFIX=/usr/local/lib/mirbadllm-agent
BINARY="${PREFIX}/mirbadllm-agent"
CONFIG_DIR="/Library/Application Support/mirbadllm-agent"
CONFIG="${CONFIG_DIR}/agent.json"
INSTALL_RECORD="${CONFIG_DIR}/install.record"
STATE_DIR="${CONFIG_DIR}/state"
LOG_DIR=/Library/Logs/mirbadllm-agent
LOG_FILE="${LOG_DIR}/agent.log"
SOCKETFILTERFW=/usr/libexec/ApplicationFirewall/socketfilterfw
DEFAULT_MODELS_ROOT=/Users/Shared/mirbadllm/models
DEFAULT_CACHE_ROOT=/Users/Shared/mirbadllm/cache
DEFAULT_PORT=9443
EXAMPLE_NODE_ID=mac-1
EXAMPLE_CONFIG_NAME=agent.macos.example.json
MIN_MACOS_MAJOR=13
RID=osx-arm64
LLAMA_LOCK_NAME=llama-cpp.lock.json
LLAMA_EXECUTABLE=llama-server
RUNTIMES_DIR="${PREFIX}/runtimes/llama.cpp"
MLX_LOCK_NAME=mlx-lm.lock.json
MLX_EXECUTABLE=mlx-lm-server
MLX_DIR="${PREFIX}/runtimes/mlx-lm"
UPDATER_LABEL=com.mirbadllm.agent-updater
UPDATER_PLIST="/Library/LaunchDaemons/${UPDATER_LABEL}.plist"
UPDATE_TRUST="${CONFIG_DIR}/update"
UPDATE_SPOOL="${STATE_DIR}/update"
UPDATE_HELPER="${PREFIX}/agent-update-helper.sh"
HOSTD_LABEL=com.mirbadllm.hostd
HOSTD_PLIST_NAME="${HOSTD_LABEL}.plist"
HOSTD_PLIST="/Library/LaunchDaemons/${HOSTD_PLIST_NAME}"
HOSTD_PREFIX=/usr/local/lib/mirbadllm-hostd
HOSTD_BINARY="${HOSTD_PREFIX}/mirbadllm-hostd"
HOSTD_DIR="/Library/Application Support/mirbadllm-hostd"
HOSTD_CONFIG="${HOSTD_DIR}/hostd.json"
HOSTD_STATE="${HOSTD_DIR}/state"
HOSTD_SEED_NAME=hostd.macos.json
HOSTD_LOG_DIR=/Library/Logs/mirbadllm-hostd
HOSTD_LOG="${HOSTD_LOG_DIR}/hostd.log"
HOSTD_SOCKET=/var/run/mirbadllm-hostd/hostd.sock

# An agent installed before the rename (SparkServe) keeps its names: install, upgrade (also one pushed from the master),
# rollback and install-hostd work in place under its old labels, user, folders and update helper, so its identity,
# settings and running models stay where they are. Only the binaries inside are new (docs/setup.md, "Upgrading from a
# pre-rename install"). The shipped plists and seed are rewritten to those names (legacy_names).
LEGACY_INSTALL=0
if [[ ! -e "${LAUNCHD_DIR}/${PLIST_NAME}" && -e "${LAUNCHD_DIR}/com.spark-serve.agent.plist" ]]; then
    LEGACY_INSTALL=1
    LABEL=com.spark-serve.agent
    PLIST_NAME="${LABEL}.plist"
    PLIST="/Library/LaunchDaemons/${PLIST_NAME}"
    SERVICE_USER=_sparkserve
    PREFIX=/usr/local/lib/spark-serve-agent
    BINARY="${PREFIX}/spark-serve-agent"
    CONFIG_DIR="/Library/Application Support/spark-serve-agent"
    CONFIG="${CONFIG_DIR}/agent.json"
    INSTALL_RECORD="${CONFIG_DIR}/install.record"
    STATE_DIR="${CONFIG_DIR}/state"
    LOG_DIR=/Library/Logs/spark-serve-agent
    LOG_FILE="${LOG_DIR}/agent.log"
    RUNTIMES_DIR="${PREFIX}/runtimes/llama.cpp"
    MLX_DIR="${PREFIX}/runtimes/mlx-lm"
    UPDATER_LABEL=com.spark-serve.agent-updater
    UPDATER_PLIST="/Library/LaunchDaemons/${UPDATER_LABEL}.plist"
    UPDATE_TRUST="${CONFIG_DIR}/update"
    UPDATE_SPOOL="${STATE_DIR}/update"
    UPDATE_HELPER="${PREFIX}/agent-update-helper.sh"
    HOSTD_LABEL=com.spark-serve.hostd
    HOSTD_PLIST_NAME="${HOSTD_LABEL}.plist"
    HOSTD_PLIST="/Library/LaunchDaemons/${HOSTD_PLIST_NAME}"
    HOSTD_PREFIX=/usr/local/lib/spark-serve-hostd
    HOSTD_BINARY="${HOSTD_PREFIX}/spark-serve-hostd"
    HOSTD_DIR="/Library/Application Support/spark-serve-hostd"
    HOSTD_CONFIG="${HOSTD_DIR}/hostd.json"
    HOSTD_STATE="${HOSTD_DIR}/state"
    HOSTD_LOG_DIR=/Library/Logs/spark-serve-hostd
    HOSTD_LOG="${HOSTD_LOG_DIR}/hostd.log"
    HOSTD_SOCKET=/var/run/spark-serve-hostd/hostd.sock
fi

step() { echo "==> $*"; }
warn() { echo "WARNING: $*" >&2; }
die() {
    echo "install-agent.sh: $1" >&2
    exit "${2:-1}"
}
usage() { sed -n '2,23p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

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
addresses=()
force_join=0
rekey=0
no_start=0
purge=0
allow_firewall=0
no_llama=0
update_llama=0
llama_source=""
with_mlx=0
update_mlx=0
mlx_source=""
with_hostd=0
discover_users=()
dest=""
config_scratch=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-llama) no_llama=1; shift ;;
        --update-llama) update_llama=1; shift ;;
        --llama-source) llama_source="${2:?--llama-source needs a value}"; shift 2 ;;
        --with-mlx) with_mlx=1; shift ;;
        --update-mlx) update_mlx=1; shift ;;
        --mlx-source) mlx_source="${2:?--mlx-source needs a value}"; shift 2 ;;
        --with-hostd) with_hostd=1; shift ;;
        --discover-user) discover_users+=("${2:?--discover-user needs a value}"); shift 2 ;;
        # stage-llama/stage-mlx only (CI and tests; not in --help): the version folder to build, and a scratch agent.json to register it in.
        --dest) dest="${2:?--dest needs a value}"; shift 2 ;;
        --config) CONFIG="${2:?--config needs a value}"; config_scratch=1; shift 2 ;;
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
        --allow-firewall) allow_firewall=1; shift ;;
        --firewall-from) die "the macOS application firewall allows programs, not source addresses: use --allow-firewall (README.md: Firewall)." 2 ;;
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
if ((update_llama)); then
    [[ "${verb}" == install || "${verb}" == update-llama ]] || die "--update-llama is a verb of its own; it does not combine with '${verb}'." 2
    verb=update-llama
fi
if ((update_mlx)); then
    [[ "${verb}" == install || "${verb}" == update-mlx ]] || die "--update-mlx is a verb of its own; it does not combine with '${verb}'." 2
    verb=update-mlx
fi
if ((with_hostd)) && [[ "${verb}" != install && "${verb}" != upgrade ]]; then
    die "--with-hostd applies to install and upgrade (install-hostd installs hostd on its own)." 2
fi
if [[ ${#discover_users[@]} -gt 0 && "${verb}" != install && "${verb}" != upgrade && "${verb}" != install-hostd ]]; then
    die "--discover-user applies to install-hostd (and install/upgrade --with-hostd)." 2
fi
if ((with_mlx)) && [[ "${verb}" != install && "${verb}" != upgrade ]]; then
    die "--with-mlx applies to install and upgrade (update-mlx installs MLX on its own)." 2
fi
if [[ -n "${mlx_source}" && "${verb}" != install && "${verb}" != upgrade && "${verb}" != update-mlx && "${verb}" != stage-mlx ]]; then
    die "--mlx-source applies to install, upgrade and update-mlx." 2
fi
if ((no_llama)) && [[ "${verb}" != install && "${verb}" != upgrade ]]; then
    die "--no-llama applies to install and upgrade." 2
fi
if [[ -n "${llama_source}" && "${verb}" != install && "${verb}" != upgrade && "${verb}" != update-llama && "${verb}" != stage-llama ]]; then
    die "--llama-source applies to install, upgrade and update-llama." 2
fi
if [[ -n "${llama_source}" ]] && ((no_llama)); then
    die "--llama-source and --no-llama contradict each other." 2
fi
if [[ (-n "${dest}" || "${config_scratch}" == 1) && "${verb}" != stage-llama && "${verb}" != stage-mlx ]]; then
    die "--dest and --config apply only to stage-llama and stage-mlx." 2
fi
if [[ -n "${node_uid}" && "${verb}" != rekey ]]; then
    die "--node-uid applies only to rekey." 2
fi

valid_node_id() { [[ "$1" =~ ^[a-z0-9][a-z0-9-]{0,39}$ ]]; }
# AgentEnrollment.IsWellFormedUid: letters, digits and '-', at most 64.
valid_node_uid() { [[ "$1" =~ ^[A-Za-z0-9-]{1,64}$ ]]; }
# Absolute, plain characters only: the value goes into JSON unescaped.
valid_root() { [[ "$1" =~ ^/[A-Za-z0-9._/-]*[A-Za-z0-9._-]$ && "$1" != *..* ]]; }

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
    for f in "${SCRIPT_DIR}/${EXAMPLE_CONFIG_NAME}" "${SCRIPT_DIR}/../${EXAMPLE_CONFIG_NAME}"; do
        if [[ -f "${f}" ]]; then
            echo "${f}"
            return 0
        fi
    done
    die "${EXAMPLE_CONFIG_NAME} not found next to the script or in deploy/agent."
}

plist_source() {
    [[ -f "${SCRIPT_DIR}/com.mirbadllm.agent.plist" ]] || die "com.mirbadllm.agent.plist not found next to the script."
    echo "${SCRIPT_DIR}/com.mirbadllm.agent.plist"
}

# Prints a shipped file under this install's names (unchanged for a new install; the pre-rename ones otherwise).
legacy_names() {
    if [[ "${LEGACY_INSTALL}" == 0 ]]; then
        cat "$1"
    else
        sed -e 's/com\.mirbadllm\./com.spark-serve./g' -e 's/mirbadllm-agent/spark-serve-agent/g' \
            -e 's/mirbadllm-hostd/spark-serve-hostd/g' -e 's/_mirbadllm/_sparkserve/g' "$1"
    fi
}

# agent.json for a node: the example with the node id and the roots substituted, each substitution checked.
render_config() {
    local node="$1" models="$2" cache="$3" example text
    example="$(example_config)"
    text="$(sed -e "s|\"NodeId\": \"${EXAMPLE_NODE_ID}\"|\"NodeId\": \"${node}\"|" \
        -e "s|\"${DEFAULT_MODELS_ROOT}\"|\"${models}\"|g" \
        -e "s|\"${DEFAULT_CACHE_ROOT}\"|\"${cache}\"|g" "${example}")"
    grep -q "\"NodeId\": \"${node}\"" <<<"${text}" || die "${example} has no \"NodeId\": \"${EXAMPLE_NODE_ID}\" to fill in."
    grep -q "\"ModelsRoot\": \"${models}\"" <<<"${text}" || die "${example} has no ModelsRoot ${DEFAULT_MODELS_ROOT} to fill in."
    grep -q "\"CacheRoot\": \"${cache}\"" <<<"${text}" || die "${example} has no CacheRoot ${DEFAULT_CACHE_ROOT} to fill in."
    printf '%s\n' "${text}"
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

# <file>: osx-arm64 / osx-x64 from a 64-bit little-endian Mach-O header, empty otherwise (od works on Linux and macOS).
macho_rid() {
    local header
    header="$(od -An -t x1 -N 8 "$1" 2>/dev/null | tr -s ' \n' ' ')"
    case "${header}" in
        " cf fa ed fe 0c 00 00 01 ") echo osx-arm64 ;;
        " cf fa ed fe 07 00 00 01 ") echo osx-x64 ;;
    esac
}

WORK=""
LLAMA_STAGING=""
MLX_STAGING=""
cleanup() {
    if [[ -n "${WORK}" ]]; then rm -rf "${WORK}"; fi
    if [[ -n "${LLAMA_STAGING}" ]]; then rm -rf "${LLAMA_STAGING}"; fi
    if [[ -n "${MLX_STAGING}" ]]; then rm -rf "${MLX_STAGING}"; fi
}
trap cleanup EXIT
work_dir() {
    if [[ -z "${WORK}" ]]; then
        WORK="$(mktemp -d "${TMPDIR:-/tmp}/mirbadllm-agent.XXXXXX")"
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
    got="$(macho_rid "${NEW_BINARY}")"
    [[ "${got}" == "${want}" ]] || die "${NEW_BINARY} is not a ${want} executable (found '${got:-not Mach-O 64}')."
    if [[ -f "${src}/BUILD-INFO" ]]; then
        NEW_BUILD_INFO="${src}/BUILD-INFO"
    fi
}

publish_from_repo() {
    local want="$1" revision=""
    work_dir
    mkdir -p "${WORK}/out"
    if command -v git >/dev/null && git -C "${REPO}" rev-parse --git-dir >/dev/null 2>&1; then
        revision="$(git -C "${REPO}" rev-parse --short=12 HEAD)"
        if [[ -n "$(git -C "${REPO}" status --porcelain 2>/dev/null)" ]]; then
            revision="${revision}-dirty"
        fi
    fi
    if command -v dotnet >/dev/null; then
        step "Publishing the agent (${want}, self-contained, single file${revision:+, ${revision}}) with the local dotnet"
        dotnet publish "${REPO}/src/MirbadLLM.Agent/MirbadLLM.Agent.csproj" -c Release -r "${want}" \
            --self-contained true -p:PublishSingleFile=true -p:DebugType=none \
            ${revision:+-p:SourceRevisionId=${revision}} -o "${WORK}/out" --nologo -v quiet
    elif command -v docker >/dev/null; then
        step "Publishing the agent (${want}, self-contained, single file${revision:+, ${revision}}) with ${SDK_IMAGE}"
        # Only the agent's project closure is copied (as deploy/agent.Dockerfile does); the output is handed back to
        # the caller's uid.
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
                chown -R "$OWNER" /out
            '
    else
        die "publishing the agent needs dotnet or Docker (or pass --published-dir)."
    fi
    [[ -f "${WORK}/out/mirbadllm-agent" ]] || die "the publish did not produce mirbadllm-agent."
    printf 'revision=%s\nrid=%s\nbuilt=%s\n' "${revision:-unknown}" "${want}" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"${WORK}/out/BUILD-INFO"
}

# ---- host checks ----

require_root() {
    [[ ${EUID} -eq 0 ]] || die "'${verb}' must run as root (sudo)."
}

check_host() {
    [[ "$(uname -s)" == Darwin ]] || die "this installer is for macOS (deploy/agent/linux is the Linux one)."
    # A shell under Rosetta reports x86_64; hw.optional.arm64 tells the hardware.
    if [[ "$(uname -m)" != arm64 && "$(sysctl -n hw.optional.arm64 2>/dev/null || true)" != 1 ]]; then
        die "mirbadllm-agent needs an Apple Silicon Mac (found $(uname -m))."
    fi
    local version major
    version="$(sw_vers -productVersion 2>/dev/null || true)"
    major="${version%%.*}"
    [[ "${major}" =~ ^[0-9]+$ ]] || die "cannot read the macOS version (sw_vers -productVersion)."
    ((major >= MIN_MACOS_MAJOR)) || die "mirbadllm-agent needs macOS ${MIN_MACOS_MAJOR} or newer (found ${version})."
    command -v launchctl >/dev/null || die "launchctl not found."
    command -v dscl >/dev/null || die "dscl not found."
}

enrolled() { [[ -f "${STATE_DIR}/agent.crt" && -s "${STATE_DIR}/node-uid" ]]; }

user_exists() { dscl . -read "/Users/${SERVICE_USER}" UniqueID >/dev/null 2>&1; }
group_exists() { dscl . -read "/Groups/${SERVICE_USER}" PrimaryGroupID >/dev/null 2>&1; }

# The highest id in 300..499 that is neither a user's UniqueID nor a group's PrimaryGroupID (the system range is
# below 500; macOS hands out its own daemon ids upwards from the 200s, so searching from 499 down avoids them).
free_system_id() {
    local used id
    used=" $( { dscl . -list /Users UniqueID; dscl . -list /Groups PrimaryGroupID; } | awk '{print $NF}' | tr '\n' ' ') "
    id=499
    while ((id >= 300)); do
        if [[ "${used}" != *" ${id} "* ]]; then
            echo "${id}"
            return 0
        fi
        id=$((id - 1))
    done
    die "no free user/group id below 500 for ${SERVICE_USER}."
}

create_service_user() {
    local id gid
    if ! group_exists; then
        id="$(free_system_id)"
        step "Creating the hidden system group ${SERVICE_USER} (gid ${id})"
        dscl . -create "/Groups/${SERVICE_USER}"
        dscl . -create "/Groups/${SERVICE_USER}" PrimaryGroupID "${id}"
        dscl . -create "/Groups/${SERVICE_USER}" RealName "mirbadllm agent"
        dscl . -create "/Groups/${SERVICE_USER}" Password '*'
    fi
    gid="$(dscl . -read "/Groups/${SERVICE_USER}" PrimaryGroupID | awk '{print $2}')"
    if ! user_exists; then
        id="$(free_system_id)"
        # Same number for both when it is free (the usual macOS layout for daemon accounts).
        if ! dscl . -search /Users UniqueID "${gid}" | grep -q .; then id="${gid}"; fi
        step "Creating the hidden system user ${SERVICE_USER} (uid ${id}, no shell, home /var/empty)"
        dscl . -create "/Users/${SERVICE_USER}"
        dscl . -create "/Users/${SERVICE_USER}" UniqueID "${id}"
        dscl . -create "/Users/${SERVICE_USER}" PrimaryGroupID "${gid}"
        dscl . -create "/Users/${SERVICE_USER}" RealName "mirbadllm agent"
        dscl . -create "/Users/${SERVICE_USER}" NFSHomeDirectory /var/empty
        dscl . -create "/Users/${SERVICE_USER}" UserShell /usr/bin/false
        dscl . -create "/Users/${SERVICE_USER}" Password '*'
        dscl . -create "/Users/${SERVICE_USER}" IsHidden 1
    fi
    if ! dscl . -read "/Groups/${SERVICE_USER}" GroupMembership 2>/dev/null | grep -qw "${SERVICE_USER}"; then
        dscl . -append "/Groups/${SERVICE_USER}" GroupMembership "${SERVICE_USER}"
    fi
    dscacheutil -flushcache 2>/dev/null || true
    id -u "${SERVICE_USER}" >/dev/null 2>&1 || die "the user ${SERVICE_USER} was created but does not resolve (dscacheutil -flushcache)."
}

delete_service_user() {
    if user_exists; then dscl . -delete "/Users/${SERVICE_USER}"; fi
    if group_exists; then dscl . -delete "/Groups/${SERVICE_USER}"; fi
    dscacheutil -flushcache 2>/dev/null || true
}

# Runs <command...> as the service user, from the state directory (the caller's folder may be unreadable to it).
as_service_user() {
    (cd "${STATE_DIR}" && sudo -n -u "${SERVICE_USER}" -- "$@")
}

# ---- signing ----

# The bundle's binary is ad hoc signed by the cross-publish (spike S2); arm64 macOS refuses to run unsigned code.
check_signature() {
    xattr -d com.apple.quarantine "$1" 2>/dev/null || true
    if ! command -v codesign >/dev/null; then
        warn "codesign not found: cannot check the signature of $1."
        return 0
    fi
    if codesign --verify "$1" 2>/dev/null; then
        return 0
    fi
    step "Signing $1 ad hoc (its signature does not verify)"
    codesign --force --sign - "$1" || die "codesign could not sign $1 ad hoc."
    codesign --verify "$1" || die "$1 does not verify after the ad hoc signature."
}

# ---- firewall (the macOS application firewall; it allows programs, not ports or sources) ----

appfw_enabled() {
    [[ -x "${SOCKETFILTERFW}" ]] && "${SOCKETFILTERFW}" --getglobalstate 2>/dev/null | grep -q 'enabled'
}

appfw_recorded() { [[ -f "${INSTALL_RECORD}" ]] && grep -qx "appfw ${BINARY}" "${INSTALL_RECORD}"; }

# Adds (again: the firewall identifies a program by its signature, which a new binary changes) the binary.
appfw_allow() {
    [[ -x "${SOCKETFILTERFW}" ]] || { warn "${SOCKETFILTERFW} not found: cannot allow the agent in the application firewall."; return 0; }
    "${SOCKETFILTERFW}" --add "${BINARY}" >/dev/null
    "${SOCKETFILTERFW}" --unblockapp "${BINARY}" >/dev/null
    if ! appfw_recorded; then echo "appfw ${BINARY}" >>"${INSTALL_RECORD}"; fi
    step "Application firewall: ${BINARY} may accept incoming connections"
}

appfw_remove_recorded() {
    appfw_recorded || return 0
    if [[ -x "${SOCKETFILTERFW}" ]]; then
        "${SOCKETFILTERFW}" --remove "${BINARY}" >/dev/null 2>&1 || true
        step "Application firewall: removed ${BINARY}"
    fi
}

firewall_note() {
    local port="$1"
    if appfw_recorded; then
        return 0
    fi
    if appfw_enabled; then
        echo "==> Firewall: the macOS application firewall is on and the master dials TCP ${port} here. Allow the agent:"
        echo "    sudo ${SOCKETFILTERFW} --add ${BINARY} && sudo ${SOCKETFILTERFW} --unblockapp ${BINARY}"
        echo "    (or re-run with --allow-firewall)."
    else
        echo "==> Firewall: the master dials TCP ${port} here. The macOS application firewall is off; if you turn it on,"
        echo "    re-run with --allow-firewall (or: sudo ${SOCKETFILTERFW} --add ${BINARY})."
    fi
}

# ---- service lifecycle (launchd, system domain) ----

service_loaded() { launchctl print "system/${LABEL}" >/dev/null 2>&1; }

# The first "<name> = <value>" of the service's "launchctl print" (state, runs, pid, last exit code).
service_field() {
    launchctl print "system/${LABEL}" 2>/dev/null | sed -n "s/^[[:space:]]*$1 = \(.*\)\$/\1/p" | head -n 1
}

# Root's lsof sees every listener; nc covers a missing lsof (it reaches a wildcard or loopback listener).
listening() {
    if command -v lsof >/dev/null && lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; then
        return 0
    fi
    nc -z -G 2 127.0.0.1 "$1" >/dev/null 2>&1
}

# Unloads the daemon (launchd sends SIGTERM and waits up to ExitTimeOut); model processes keep running.
stop_service() {
    service_loaded || return 0
    launchctl bootout "system/${LABEL}" 2>/dev/null || true
    local deadline=$((SECONDS + 60))
    while service_loaded && ((SECONDS < deadline)); do
        sleep 1
    done
    ! service_loaded || warn "system/${LABEL} is still loaded after bootout."
}

# Loads the installed plist (RunAtLoad starts the agent); bootstrap can fail briefly right after a bootout.
load_service() {
    local tries=0
    launchctl enable "system/${LABEL}" 2>/dev/null || true
    until launchctl bootstrap system "${PLIST}" 2>/dev/null; do
        tries=$((tries + 1))
        if ((tries >= 10)); then
            launchctl bootstrap system "${PLIST}" || die "launchctl bootstrap system ${PLIST} failed."
            return 0
        fi
        sleep 1
    done
}

# Waits until the agent runs and listens on <port>; fails when it is unloaded, exits and is restarted by launchd
# (its run count passes <runs>), or on the timeout.
wait_healthy() {
    local port="$1" expected_runs="$2" state runs deadline=$((SECONDS + HEALTH_TIMEOUT))
    while ((SECONDS < deadline)); do
        sleep 2
        service_loaded || return 1
        runs="$(service_field runs)"
        if [[ "${runs}" =~ ^[0-9]+$ ]] && ((runs > expected_runs)); then
            return 1
        fi
        state="$(service_field state)"
        [[ "${state}" == running ]] || continue
        if listening "${port}"; then
            return 0
        fi
    done
    return 1
}

show_status() {
    launchctl print "system/${LABEL}" 2>/dev/null | sed -n '1,25p' || true
    if [[ -f "${LOG_FILE}" ]]; then
        echo "--- last lines of ${LOG_FILE}:"
        tail -n 20 "${LOG_FILE}" || true
    fi
}

# (Re)loads the plist and starts the agent: a fresh bootstrap, so a changed plist takes effect.
reload_service() {
    stop_service
    load_service
}

# Saves the installed binary and plist as *.previous (the state "rollback" returns to).
save_previous() {
    [[ -f "${BINARY}" ]] || return 0
    cp -p "${BINARY}" "${BINARY}.previous"
    if [[ -f "${PLIST}" ]]; then cp -p "${PLIST}" "${PREFIX}/${PLIST_NAME}.previous"; fi
    if [[ -f "${PREFIX}/BUILD-INFO" ]]; then cp -p "${PREFIX}/BUILD-INFO" "${PREFIX}/BUILD-INFO.previous"; fi
}

restore_previous() {
    [[ -f "${BINARY}.previous" ]] || return 1
    install -m 0755 -o root -g wheel "${BINARY}.previous" "${BINARY}.new"
    mv -f "${BINARY}.new" "${BINARY}"
    if [[ -f "${PREFIX}/${PLIST_NAME}.previous" ]]; then install -m 0644 -o root -g wheel "${PREFIX}/${PLIST_NAME}.previous" "${PLIST}"; fi
    if [[ -f "${PREFIX}/BUILD-INFO.previous" ]]; then cp -p "${PREFIX}/BUILD-INFO.previous" "${PREFIX}/BUILD-INFO"; fi
}

# Installs the plist from the script's folder (root:wheel 0644), checked by plutil.
install_plist() {
    local source
    work_dir
    source="${WORK}/${PLIST_NAME}"
    legacy_names "$(plist_source)" >"${source}"
    if command -v plutil >/dev/null; then
        plutil -lint -s "${source}" || die "${source} is not a valid property list (plutil -lint)."
    fi
    install -m 0644 -o root -g wheel "${source}" "${PLIST}.new"
    mv -f "${PLIST}.new" "${PLIST}"
}

# (Re)starts an enrolled agent and checks it; with <rollback>=1 a failed start puts the previous version back.
start_and_check() {
    local rollback="$1" port
    port="$(config_port "${CONFIG}")"
    step "(Re)loading ${LABEL}; waiting up to ${HEALTH_TIMEOUT} s for it to listen on ${port}"
    reload_service
    if wait_healthy "${port}" 1; then
        step "${LABEL} is running and listening on ${port}"
        return 0
    fi
    show_status
    if [[ "${rollback}" == 1 ]] && restore_previous; then
        warn "the new agent did not come up: rolled back to the previous binary and plist."
        if appfw_recorded; then appfw_allow; fi
        reload_service
        if wait_healthy "${port}" 1; then
            step "The previous agent is running again"
        else
            show_status
            warn "the previous agent does not come up either: see ${LOG_FILE}"
        fi
    fi
    die "${LABEL} did not come up (see ${LOG_FILE}; launchctl print system/${LABEL})."
}

# Creates <dir> owned by the service user (0755), or warns when an existing one is not writable for it.
ensure_root_dir() {
    local dir="$1"
    if [[ -d "${dir}" ]]; then
        as_service_user test -w "${dir}" ||
            warn "${dir} exists but ${SERVICE_USER} cannot write it (downloads and folder deletes will fail)."
    else
        mkdir -p "$(dirname "${dir}")"
        install -d -m 0755 -o "${SERVICE_USER}" -g "${SERVICE_USER}" "${dir}"
    fi
}

# Processes of the service user (model processes outlive the agent: AbandonProcessGroup).
service_processes() {
    id -u "${SERVICE_USER}" >/dev/null 2>&1 || return 0
    ps -U "${SERVICE_USER}" -o pid=,comm= 2>/dev/null || true
}

# ---- llama.cpp (Metal; plan M5, Phase 2) ----
#
# The pinned release (llama-cpp.lock.json next to the script): one tarball, pinned by SHA-256 and size, and the files a
# version folder holds, each pinned by SHA-256 and size. Only llama-server and the dylibs it loads are installed, under
# the names their install names use (@rpath/libggml.0.dylib; the archive's symlinks are resolved), so the folder needs
# no symlinks: llama-server's LC_RPATH is @loader_path, the version folder. The Metal shaders are embedded in
# libggml-metal (no default.metallib). The folder holds exactly these files plus manifest.json (ExecutableManifest),
# which the agent's ExecutableCatalog re-hashes and which makes any extra dylib or Mach-O file a mismatch. Lock and
# agent.json are read and edited with /usr/bin/plutil (macOS has no jq, and python3 may be a Command Line Tools stub).

# Parses the lock file into LLAMA_* globals and checks every value: schema 1, a bNNNN tag, one asset downloaded by tag,
# hex SHA-256s, positive sizes, plain unique relative paths, llama-server among them.
LLAMA_VERSION=""
read_llama_lock() {
    [[ -n "${LLAMA_VERSION}" ]] && return 0
    local lock="${SCRIPT_DIR}/${LLAMA_LOCK_NAME}" n i path sha bytes seen=" " lower
    [[ -f "${lock}" ]] || die "${LLAMA_LOCK_NAME} not found next to the script."
    command -v plutil >/dev/null || die "plutil not found (it reads ${LLAMA_LOCK_NAME})."
    lock_value() { plutil -extract "$1" raw -o - "${lock}" 2>/dev/null || true; }
    [[ "$(lock_value schemaVersion)" == 1 ]] || die "${lock}: unknown schemaVersion."
    LLAMA_VERSION="$(lock_value version)"
    [[ "${LLAMA_VERSION}" =~ ^b[0-9]+$ ]] || die "${lock}: version '${LLAMA_VERSION}' is not a bNNNN release tag."
    [[ "$(lock_value backend)" == metal ]] || die "${lock}: backend must be metal."
    [[ "$(lock_value assets)" == 1 ]] || die "${lock}: the macOS release is one asset; the lock file must list exactly one."
    LLAMA_ASSET="$(lock_value assets.0.name)"
    LLAMA_URL="$(lock_value assets.0.url)"
    LLAMA_SHA256="$(lock_value assets.0.sha256)"
    LLAMA_BYTES="$(lock_value assets.0.bytes)"
    LLAMA_PREFIX="$(lock_value assets.0.prefix)"
    [[ "${LLAMA_ASSET}" =~ ^[A-Za-z0-9._-]+\.tar\.gz$ ]] || die "${lock}: asset name '${LLAMA_ASSET}' is not a .tar.gz file name."
    # By tag only: every bNNNN release is a prerelease, so releases/latest never returns it.
    [[ "${LLAMA_URL}" == "https://github.com/ggml-org/llama.cpp/releases/download/${LLAMA_VERSION}/${LLAMA_ASSET}" ]] ||
        die "${lock}: ${LLAMA_ASSET} must be downloaded by tag from https://github.com/ggml-org/llama.cpp/releases/download/${LLAMA_VERSION}/, not ${LLAMA_URL}."
    [[ "${LLAMA_SHA256}" =~ ^[0-9a-f]{64}$ ]] || die "${lock}: ${LLAMA_ASSET} has no SHA-256."
    [[ "${LLAMA_BYTES}" =~ ^[1-9][0-9]*$ ]] || die "${lock}: ${LLAMA_ASSET} has no size."
    [[ "${LLAMA_PREFIX}" == "llama-${LLAMA_VERSION}/" ]] || die "${lock}: prefix must be llama-${LLAMA_VERSION}/ (the folder the tarball unpacks to)."
    n="$(lock_value files)"
    [[ "${n}" =~ ^[1-9][0-9]*$ ]] || die "${lock}: the lock file lists no files; every installed file must be pinned."
    # "path sha256 bytes" per line (paths have no spaces: checked below).
    LLAMA_FILES=""
    i=0
    while ((i < n)); do
        path="$(lock_value "files.${i}.path")"
        sha="$(lock_value "files.${i}.sha256")"
        bytes="$(lock_value "files.${i}.bytes")"
        valid_llama_path "${path}" || die "${lock}: bad file path '${path}'."
        [[ "${sha}" =~ ^[0-9a-f]{64}$ && "${bytes}" =~ ^[1-9][0-9]*$ ]] || die "${lock}: file ${path} has no SHA-256 or size."
        # Case-insensitive, like APFS and the agent's catalogue on macOS.
        lower="$(printf '%s' "${path}" | tr '[:upper:]' '[:lower:]')"
        [[ "${seen}" != *" ${lower} "* ]] || die "${lock}: file ${path} is listed twice."
        [[ "${lower}" != manifest.json ]] || die "${lock}: manifest.json is written by the installer, not unpacked."
        seen="${seen}${lower} "
        LLAMA_FILES="${LLAMA_FILES}${path} ${sha} ${bytes}"$'\n'
        i=$((i + 1))
    done
    [[ "${seen}" == *" ${LLAMA_EXECUTABLE} "* ]] || die "${lock}: ${LLAMA_EXECUTABLE} is not among the files."
    LLAMA_TARGET="${RUNTIMES_DIR}/${LLAMA_VERSION}"
}

# A '/'-separated path below the version folder: no root, no '.'/'..'/empty segment, plain characters only.
valid_llama_path() {
    [[ "$1" =~ ^[A-Za-z0-9._+-]+(/[A-Za-z0-9._+-]+)*$ ]] || return 1
    case "/$1/" in
        */./* | */../*) return 1 ;;
    esac
    return 0
}

file_bytes() { wc -c <"$1" | tr -d ' '; }

file_sha256() { shasum -a 256 "$1" | awk '{print $1}'; }

# <file>: true for a Mach-O (64-bit, either byte order) or universal binary.
is_macho() {
    case "$(od -An -t x1 -N 4 "$1" 2>/dev/null | tr -d ' \n')" in
        cffaedfe | feedfacf | cafebabe | bebafeca) return 0 ;;
    esac
    return 1
}

# Downloads (or, with --llama-source, copies) the asset into <dir> and checks its size and SHA-256.
fetch_llama_asset() {
    local dir="$1" file="$1/${LLAMA_ASSET}" got
    if [[ -n "${llama_source}" ]]; then
        step "Taking ${LLAMA_ASSET} from ${llama_source}"
        [[ -f "${llama_source}/${LLAMA_ASSET}" ]] || die "${llama_source}/${LLAMA_ASSET} does not exist."
        cp "${llama_source}/${LLAMA_ASSET}" "${file}"
    else
        step "Downloading ${LLAMA_URL}"
        command -v curl >/dev/null || die "curl not found."
        curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --connect-timeout 30 -o "${file}" "${LLAMA_URL}" ||
            die "downloading ${LLAMA_URL} failed."
    fi
    got="$(file_bytes "${file}")"
    [[ "${got}" == "${LLAMA_BYTES}" ]] || die "${LLAMA_ASSET}: ${got} bytes, the lock file pins ${LLAMA_BYTES}. Not installed."
    got="$(file_sha256 "${file}")"
    [[ "${got}" == "${LLAMA_SHA256}" ]] || die "${LLAMA_ASSET}: SHA-256 ${got}, the lock file pins ${LLAMA_SHA256}. Not installed."
    step "${LLAMA_ASSET} matches ${LLAMA_LOCK_NAME} (${LLAMA_BYTES} bytes, sha256 ${LLAMA_SHA256})"
}

# Problems of <folder> against the lock file, one per line (empty = it matches): every locked file a regular file with
# its size and SHA-256, nothing else in the folder (no symlink, no subfolder) except manifest.json when <manifest>=1.
llama_folder_problems() {
    local folder="$1" manifest="$2" path sha bytes entry name got
    while read -r path sha bytes; do
        [[ -n "${path}" ]] || continue
        if [[ -L "${folder}/${path}" || ! -f "${folder}/${path}" ]]; then
            echo "missing file ${path}"
            continue
        fi
        got="$(file_bytes "${folder}/${path}")"
        if [[ "${got}" != "${bytes}" ]]; then
            echo "${path} is ${got} bytes, expected ${bytes}"
            continue
        fi
        got="$(file_sha256 "${folder}/${path}")"
        [[ "${got}" == "${sha}" ]] || echo "${path} has SHA-256 ${got}, expected ${sha}"
    done <<<"${LLAMA_FILES}"
    while IFS= read -r entry; do
        [[ -n "${entry}" ]] || continue
        name="${entry#"${folder}"/}"
        if [[ "${manifest}" == 1 && "${name}" == manifest.json && -f "${entry}" && ! -L "${entry}" ]]; then continue; fi
        if [[ -d "${entry}" && ! -L "${entry}" ]]; then
            echo "unexpected folder ${name}"
        elif [[ -L "${entry}" || $'\n'"${LLAMA_FILES}" != *$'\n'"${name} "* ]]; then
            echo "unexpected file ${name}"
        fi
    done < <(find "${folder}" -mindepth 1 -maxdepth 1)
}

# manifest.json (ExecutableManifest, camelCase) from the same list the folder was verified against.
llama_manifest() {
    local path sha bytes first=1
    printf '{\n  "schemaVersion": 1,\n  "name": "%s",\n  "version": "%s",\n  "sources": [\n    "%s"\n  ],\n  "files": [' \
        "${LLAMA_EXECUTABLE}" "${LLAMA_VERSION}" "${LLAMA_ASSET}"
    while read -r path sha bytes; do
        [[ -n "${path}" ]] || continue
        if ((first)); then first=0; else printf ','; fi
        printf '\n    {\n      "path": "%s",\n      "sha256": "%s",\n      "bytes": %s\n    }' "${path}" "${sha}" "${bytes}"
    done <<<"${LLAMA_FILES}"
    printf '\n  ]\n}\n'
}

# Drops the quarantine flag and checks every Mach-O's code signature. The release's binaries carry an ad hoc
# (linker) signature that verifies; re-signing would change the pinned bytes, so a signature that does not verify
# stops the install instead (the bytes matched the lock file, so this means the lock file pins a broken build).
check_llama_signatures() {
    local folder="$1" path sha bytes
    xattr -dr com.apple.quarantine "${folder}" 2>/dev/null || true
    if ! command -v codesign >/dev/null; then
        warn "codesign not found: cannot check the signatures in ${folder}."
        return 0
    fi
    while read -r path sha bytes; do
        [[ -n "${path}" ]] || continue
        is_macho "${folder}/${path}" || continue
        codesign --verify --strict "${folder}/${path}" 2>/dev/null ||
            die "${folder}/${path}: its code signature does not verify (codesign --verify --strict); not installed."
    done <<<"${LLAMA_FILES}"
    step "Code signatures of the Mach-O files in ${folder} verify"
}

# Builds a complete version folder at <dest> (new or empty): download, check, unpack, copy only the locked files
# (symlinks resolved, no extended attributes), check them all, write manifest.json, check the signatures. No root
# needed ("stage-llama --dest DIR" runs exactly this).
stage_llama() {
    local dest="$1" download unpack path sha bytes member
    read_llama_lock
    command -v shasum >/dev/null || die "shasum not found."
    if [[ -e "${dest}" ]]; then
        [[ -d "${dest}" && -z "$(ls -A "${dest}")" ]] || die "${dest} exists and is not an empty folder."
    fi
    work_dir
    download="$(mktemp -d "${WORK}/llama-download.XXXXXX")"
    unpack="$(mktemp -d "${WORK}/llama-unpack.XXXXXX")"
    fetch_llama_asset "${download}"

    step "Unpacking llama.cpp ${LLAMA_VERSION}"
    # The tarball's SHA-256 matched; its member names are still checked before anything is written.
    while IFS= read -r member; do
        case "${member}" in
            /* | ../* | */../* | */..) die "${LLAMA_ASSET}: member '${member}' would unpack outside the folder." ;;
        esac
    done < <(tar -tzf "${download}/${LLAMA_ASSET}")
    tar -xzf "${download}/${LLAMA_ASSET}" -C "${unpack}" || die "unpacking ${LLAMA_ASSET} failed."

    mkdir -p "${dest}"
    while read -r path sha bytes; do
        [[ -n "${path}" ]] || continue
        [[ -f "${unpack}/${LLAMA_PREFIX}${path}" ]] || die "${LLAMA_ASSET} has no ${LLAMA_PREFIX}${path}."
        # cat follows the archive's symlinks (libggml.0.dylib -> libggml.0.26.0.dylib) and copies no xattrs.
        cat "${unpack}/${LLAMA_PREFIX}${path}" >"${dest}/${path}"
    done <<<"${LLAMA_FILES}"
    rm -rf "${unpack}" "${download}"

    local problems
    problems="$(llama_folder_problems "${dest}" 0)"
    [[ -z "${problems}" ]] || die "llama.cpp ${LLAMA_VERSION} does not match ${LLAMA_LOCK_NAME}: $(head -n 10 <<<"${problems}" | tr '\n' ';')"
    step "All $(grep -c . <<<"${LLAMA_FILES}") files match ${LLAMA_LOCK_NAME}"

    llama_manifest >"${dest}/manifest.json"
    [[ "$(plutil -extract files raw -o - "${dest}/manifest.json" 2>/dev/null)" == "$(grep -c . <<<"${LLAMA_FILES}")" ]] ||
        die "${dest}/manifest.json does not read back."
    while read -r path sha bytes; do
        [[ -n "${path}" ]] || continue
        if is_macho "${dest}/${path}"; then chmod 0755 "${dest}/${path}"; else chmod 0644 "${dest}/${path}"; fi
    done <<<"${LLAMA_FILES}"
    chmod 0644 "${dest}/manifest.json"
    chmod 0755 "${dest}"
    check_llama_signatures "${dest}"
}

# Processes running from <folder> (pid and command line). The list is taken first, so the filter never sees itself.
llama_folder_users() {
    local list line
    list="$(ps -axo pid=,command= 2>/dev/null || true)"
    while IFS= read -r line; do
        case "${line}" in
            *"$1/"*) echo "${line}" ;;
        esac
    done <<<"${list}"
}

# True when <folder> holds exactly the locked files and the manifest.json this lock file produces.
llama_installed_intact() {
    local folder="$1" problems
    [[ -d "${folder}" && -f "${folder}/manifest.json" ]] || return 1
    problems="$(llama_folder_problems "${folder}" 1)"
    if [[ -n "${problems}" ]]; then
        warn "${folder} does not verify ($(head -n 3 <<<"${problems}" | tr '\n' ';'))."
        return 1
    fi
    if [[ "$(llama_manifest)" != "$(cat "${folder}/manifest.json")" ]]; then
        warn "${folder}/manifest.json is not the one ${LLAMA_LOCK_NAME} produces."
        return 1
    fi
    return 0
}

# Installs the pinned version into ${RUNTIMES_DIR}/<version> (root:wheel, read-only for the agent) unless it is there
# and verifies, then registers it in agent.json. Root only.
provision_llama() {
    local users
    read_llama_lock
    step "llama.cpp ${LLAMA_VERSION} (Metal) in ${LLAMA_TARGET}"
    install -d -m 0755 -o root -g wheel "${PREFIX}" "${PREFIX}/runtimes" "${RUNTIMES_DIR}"
    if [[ -d "${LLAMA_TARGET}" ]]; then
        if llama_installed_intact "${LLAMA_TARGET}"; then
            step "llama.cpp ${LLAMA_VERSION} is installed and verified in ${LLAMA_TARGET}"
            register_llama
            return 0
        fi
        users="$(llama_folder_users "${LLAMA_TARGET}")"
        [[ -z "${users}" ]] || die "${LLAMA_TARGET} is in use (stop its models first): ${users}"
        step "Removing the damaged ${LLAMA_TARGET}; installing it again"
        rm -rf "${LLAMA_TARGET}"
    fi
    # Staged next to the target (same volume), so the last step is a rename; cleanup() removes it on failure.
    LLAMA_STAGING="$(mktemp -d "${RUNTIMES_DIR}/.staging-${LLAMA_VERSION}.XXXXXX")"
    stage_llama "${LLAMA_STAGING}"
    chown -R root:wheel "${LLAMA_STAGING}"
    mv "${LLAMA_STAGING}" "${LLAMA_TARGET}"
    LLAMA_STAGING=""
    step "Installed llama.cpp ${LLAMA_VERSION} to ${LLAMA_TARGET} with manifest.json"
    register_llama
}

register_llama() {
    register_executable "${LLAMA_EXECUTABLE}" "${LLAMA_VERSION}" "${LLAMA_TARGET}/${LLAMA_EXECUTABLE}" "${LLAMA_TARGET}/manifest.json" "llama.cpp"
}

# Agent:Executables in agent.json (ExecutableOptions): the <name> entry gets <version> with <path> and <manifest>
# (replacing an entry of the same version) and becomes the default; other versions and other executables stay. plutil
# edits a copy (it rewrites a JSON file compactly, with its own key order), and only the Executables value is spliced
# back into agent.json as one line, so the file keeps its layout (one key per line) and its "//" comment.
register_executable() {
    local exe_name="$1" exe_version="$2" exe_path="$3" exe_manifest="$4" label="$5"
    local edit spliced i idx="" j version entry expected got
    [[ -f "${CONFIG}" ]] || die "no ${CONFIG} yet: install the agent first."
    work_dir
    edit="${WORK}/agent.edit.json"
    spliced="${WORK}/agent.spliced.json"
    cp "${CONFIG}" "${edit}"
    plutil -extract Agent.NodeId raw -o - "${edit}" >/dev/null 2>&1 || die "${CONFIG} does not parse (plutil) or has no Agent.NodeId."
    if ! plutil -extract Agent.Executables json -o /dev/null "${edit}" 2>/dev/null; then
        plutil -insert Agent.Executables -json '[]' "${edit}" || die "cannot add Agent.Executables to ${CONFIG}."
    fi
    i=0
    while plutil -extract "Agent.Executables.${i}" json -o /dev/null "${edit}" 2>/dev/null; do
        if [[ "$(plutil -extract "Agent.Executables.${i}.Name" raw -o - "${edit}" 2>/dev/null)" == "${exe_name}" ]]; then
            idx="${i}"
            break
        fi
        i=$((i + 1))
    done
    if [[ -z "${idx}" ]]; then
        plutil -insert "Agent.Executables.${i}" -json "{\"Name\":\"${exe_name}\",\"Versions\":[]}" "${edit}" ||
            die "cannot add ${exe_name} to Agent.Executables."
        idx="${i}"
    fi
    entry="Agent.Executables.${idx}"
    if ! plutil -extract "${entry}.Versions" json -o /dev/null "${edit}" 2>/dev/null; then
        plutil -insert "${entry}.Versions" -json '[]' "${edit}"
    fi
    j=0
    while plutil -extract "${entry}.Versions.${j}" json -o /dev/null "${edit}" 2>/dev/null; do
        version="$(plutil -extract "${entry}.Versions.${j}.Version" raw -o - "${edit}" 2>/dev/null || true)"
        if [[ "${version}" == "${exe_version}" ]]; then
            plutil -remove "${entry}.Versions.${j}" "${edit}"
        else
            j=$((j + 1))
        fi
    done
    plutil -insert "${entry}.Versions.${j}" -json \
        "{\"Version\":\"${exe_version}\",\"Path\":\"${exe_path}\",\"Manifest\":\"${exe_manifest}\"}" "${edit}" ||
        die "cannot add ${label} ${exe_version} to ${entry}.Versions."
    plutil -replace "${entry}.Default" -string "${exe_version}" "${edit}"

    # plutil writes "/" as "\/"; the paths here hold no backslash, so that is the only escape to undo.
    expected="$(plutil -extract Agent.Executables json -o - "${edit}" | sed 's|\\/|/|g')"
    [[ "${expected}" == \[*\] ]] || die "cannot read Agent.Executables back from the edited copy of ${CONFIG}."
    local has=0
    if grep -q '^[[:space:]]*"Executables"[[:space:]]*:' "${CONFIG}"; then has=1; fi
    NEW_EXECUTABLES="${expected}" HAS_EXECUTABLES="${has}" awk '
        function depth(s,    i, c, d) { d = 0; for (i = 1; i <= length(s); i++) { c = substr(s, i, 1); if (c == "[") d++; else if (c == "]") d--; } return d }
        BEGIN { new = ENVIRON["NEW_EXECUTABLES"]; has = (ENVIRON["HAS_EXECUTABLES"] == 1); state = 0 }
        has && state == 0 && /^[[:space:]]*"Executables"[[:space:]]*:/ {
            indent = $0; sub(/[^[:space:]].*$/, "", indent)
            rest = $0; sub(/^[^:]*:/, "", rest); d = depth(rest); last = $0; state = 1
            while (d > 0 && (getline line) > 0) { d += depth(line); last = line }
            comma = (last ~ /,[[:space:]]*$/) ? "," : ""
            print indent "\"Executables\": " new comma
            state = 2; next
        }
        !has && state == 0 && /^[[:space:]]*"Agent"[[:space:]]*:[[:space:]]*\{[[:space:]]*$/ { print; agent = 1; next }
        agent == 1 && state == 0 {
            indent = $0; sub(/[^[:space:]].*$/, "", indent)
            print indent "\"Executables\": " new ","
            state = 2; agent = 0
        }
        { print }
        END { if (state != 2) exit 3 }
    ' "${CONFIG}" >"${spliced}" || die "cannot find where Agent.Executables goes in ${CONFIG}; add \"Executables\": [] under \"Agent\" and re-run."
    got="$(plutil -extract Agent.Executables json -o - "${spliced}" 2>/dev/null | sed 's|\\/|/|g' || true)"
    [[ -n "${got}" && "$(plutil -extract Agent.NodeId raw -o - "${spliced}" 2>/dev/null)" == "$(plutil -extract Agent.NodeId raw -o - "${CONFIG}")" ]] ||
        die "the edited ${CONFIG} does not parse; it was left unchanged."
    # The same text parsed the same way: {"Agent":{"Executables":<expected>}} must extract exactly as the spliced file.
    printf '{"Agent":{"Executables":%s}}\n' "${expected}" >"${WORK}/agent.expected.json"
    [[ "$(plutil -extract "${entry}.Default" raw -o - "${spliced}")" == "${exe_version}" &&
        "${got}" == "$(plutil -extract Agent.Executables json -o - "${WORK}/agent.expected.json" | sed 's|\\/|/|g')" ]] ||
        die "the edited ${CONFIG} does not hold the new Agent.Executables; it was left unchanged."
    if [[ "${config_scratch}" == 1 ]]; then
        cp "${spliced}" "${CONFIG}"
    else
        install -m 0640 -o root -g "${SERVICE_USER}" "${spliced}" "${CONFIG}.new"
        mv -f "${CONFIG}.new" "${CONFIG}"
    fi
    step "Registered ${label} ${exe_version} as the default ${exe_name} in ${CONFIG}"
}

# ---- the update helper (pushed agent updates, docs/multinode/agent-updates.md U4/U5) ----
#
# A root LaunchDaemon that launchd starts when the agent writes an update order (the signature file, written last) into
# its spool (WatchPaths). It checks the order against the trusted CA in ${UPDATE_TRUST}, copied from the agent's state
# when an admin runs this script as root; an update the helper runs (MIRBADLLM_UPDATE_HELPER=1) keeps that copy and
# does not reload the helper's own job (that would kill it in the middle of the upgrade).

helper_source() {
    local f
    for f in "${SCRIPT_DIR}/agent-update-helper.sh" "${SCRIPT_DIR}/../agent-update-helper.sh"; do
        if [[ -f "${f}" ]]; then
            echo "${f}"
            return 0
        fi
    done
}

updater_loaded() { launchctl print "system/${UPDATER_LABEL}" >/dev/null 2>&1; }

install_update_helper() {
    local src
    src="$(helper_source)"
    if [[ -z "${src}" ]]; then
        warn "agent-update-helper.sh is not next to this script: updates pushed from the master stay off on this node."
        return 0
    fi
    enrolled || return 0 # the trust copies need the node's uid and the master's CA
    step "Installing the update helper (${UPDATE_HELPER}, ${UPDATER_LABEL})"
    install -m 0755 -o root -g wheel "${src}" "${UPDATE_HELPER}"
    install -d -m 0755 -o root -g wheel "${UPDATE_TRUST}"
    install -d -m 0700 -o "${SERVICE_USER}" -g "${SERVICE_USER}" "${UPDATE_SPOOL}"
    if [[ "${MIRBADLLM_UPDATE_HELPER:-0}" == 1 && -s "${UPDATE_TRUST}/ca.pem" && -s "${UPDATE_TRUST}/node-uid" ]]; then
        step "Keeping the trusted CA in ${UPDATE_TRUST} (this upgrade was pushed from the master)"
    else
        install -m 0644 -o root -g wheel "${STATE_DIR}/ca.crt" "${UPDATE_TRUST}/ca.pem"
        install -m 0644 -o root -g wheel "${STATE_DIR}/node-uid" "${UPDATE_TRUST}/node-uid"
    fi
    printf '%s\n' "${RID}" >"${UPDATE_TRUST}/rid"
    chmod 0644 "${UPDATE_TRUST}/rid"
    cat >"${UPDATER_PLIST}.new" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<!-- mirbadllm agent update (root helper; docs/multinode/agent-updates.md). Written by install-agent.sh. -->
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${UPDATER_LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${UPDATE_HELPER}</string>
        <string>${UPDATE_SPOOL}</string>
        <string>${UPDATE_TRUST}</string>
        <string>${SERVICE_USER}</string>
    </array>
    <key>WatchPaths</key>
    <array>
        <string>${UPDATE_SPOOL}/order.sig</string>
    </array>
    <key>StandardOutPath</key>
    <string>${LOG_DIR}/update.log</string>
    <key>StandardErrorPath</key>
    <string>${LOG_DIR}/update.log</string>
</dict>
</plist>
EOF
    if command -v plutil >/dev/null; then
        plutil -lint -s "${UPDATER_PLIST}.new" || die "the update helper's plist does not lint."
    fi
    chown root:wheel "${UPDATER_PLIST}.new"
    chmod 0644 "${UPDATER_PLIST}.new"
    mv -f "${UPDATER_PLIST}.new" "${UPDATER_PLIST}"
    if [[ "${MIRBADLLM_UPDATE_HELPER:-0}" != 1 ]]; then
        if updater_loaded; then launchctl bootout "system/${UPDATER_LABEL}" 2>/dev/null || true; fi
        launchctl bootstrap system "${UPDATER_PLIST}" 2>/dev/null || warn "launchctl bootstrap ${UPDATER_PLIST} failed: pushed updates start after a reboot."
    fi
    touch "${UPDATE_TRUST}/helper"
    chmod 0644 "${UPDATE_TRUST}/helper"
}

remove_update_helper() {
    if updater_loaded; then launchctl bootout "system/${UPDATER_LABEL}" 2>/dev/null || true; fi
    rm -f "${UPDATER_PLIST}" "${UPDATE_HELPER}"
    rm -rf "${UPDATE_TRUST}"
}

# ---- hostd (the root helper for launchd jobs; plan M10, Phase 4) ----
#
# mirbadllm-hostd runs as the root LaunchDaemon com.mirbadllm.hostd and lists, starts, stops, restarts and
# enables/disables the whitelisted launchd jobs (Ollama, LM Studio, ComfyUI...) for the agent, which stays _mirbadllm.
# It listens only on ${HOSTD_SOCKET} (it makes the directory root:_mirbadllm 0750 itself at every start). Its files are
# root-owned and apart from the agent's: the binary in ${HOSTD_PREFIX}, hostd.json and the whitelist in ${HOSTD_DIR}.
# The agent reaches it once agent.json has Agent:ManageLaunchdJobs true and Agent:HostdSocket set (services.launchd).

hostd_installed() { [[ -f "${HOSTD_BINARY}" ]]; }

hostd_loaded() { launchctl print "system/${HOSTD_LABEL}" >/dev/null 2>&1; }

# Sets NEW_HOSTD: the bundle's (or --published-dir's) mirbadllm-hostd, or one published from this checkout.
obtain_hostd_binary() {
    local src="" got
    if [[ -n "${published_dir}" && -f "${published_dir}/mirbadllm-hostd" ]]; then
        src="${published_dir}"
    elif [[ -f "${SCRIPT_DIR}/mirbadllm-hostd" ]]; then
        src="${SCRIPT_DIR}"
    elif [[ -n "${REPO}" && -f "${REPO}/MirbadLLM.slnx" ]] && command -v dotnet >/dev/null; then
        work_dir
        mkdir -p "${WORK}/hostd"
        step "Publishing mirbadllm-hostd (${RID}, self-contained, single file) with the local dotnet"
        dotnet publish "${REPO}/src/MirbadLLM.HostAgent/MirbadLLM.HostAgent.csproj" -c Release -r "${RID}" \
            --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o "${WORK}/hostd" --nologo -v quiet
        src="${WORK}/hostd"
    else
        die "no mirbadllm-hostd binary: run from a bundle (\"package\" includes it), pass --published-dir, or run from a mirbadllm checkout with dotnet."
    fi
    NEW_HOSTD="${src}/mirbadllm-hostd"
    [[ -f "${NEW_HOSTD}" ]] || die "${NEW_HOSTD} does not exist."
    got="$(macho_rid "${NEW_HOSTD}")"
    [[ "${got}" == "${RID}" ]] || die "${NEW_HOSTD} is not a ${RID} executable (found '${got:-not Mach-O 64}')."
}

hostd_file_source() {
    [[ -f "${SCRIPT_DIR}/$1" ]] || die "$1 not found next to the script."
    echo "${SCRIPT_DIR}/$1"
}

# A login account hostd may discover LaunchAgents of: a valid name with a uid of 500 or more.
valid_discover_user() {
    local uid
    [[ "$1" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || return 1
    uid="$(id -u "$1" 2>/dev/null || true)"
    [[ "${uid}" =~ ^[0-9]+$ ]] && ((uid >= 500))
}

# hostd.json from the seed with discoverUsers filled in (each name checked; the substitution checked).
render_hostd_config() {
    local seed list="" user text
    seed="$(hostd_file_source "${HOSTD_SEED_NAME}")"
    for user in "$@"; do
        list="${list:+${list}, }\"${user}\""
    done
    text="$(legacy_names "${seed}" | sed -e "s|\"discoverUsers\": \[\]|\"discoverUsers\": [${list}]|")"
    grep -q "\"discoverUsers\": \[${list}\]" <<<"${text}" || die "${seed} has no \"discoverUsers\": [] to fill in."
    printf '%s\n' "${text}"
}

# Sets Agent.<key> to the JSON <value> (true, false or a plain "string") in agent.json: the "<key>": line is replaced,
# or one is added as the first line under "Agent", so the file keeps its layout and "//" comment. Checked with plutil.
config_set() {
    local key="$1" value="$2" edited got want
    [[ -f "${CONFIG}" ]] || die "no ${CONFIG} yet: install the agent first."
    work_dir
    edited="${WORK}/agent.set.json"
    if grep -q "^[[:space:]]*\"${key}\"[[:space:]]*:" "${CONFIG}"; then
        sed -E "s|^([[:space:]]*\"${key}\"[[:space:]]*:[[:space:]]*)[^,]*(,?)[[:space:]]*\$|\1${value}\2|" "${CONFIG}" >"${edited}"
    else
        KEY="${key}" VALUE="${value}" awk '
            done == 0 && agent == 1 {
                indent = $0; sub(/[^[:space:]].*$/, "", indent)
                print indent "\"" ENVIRON["KEY"] "\": " ENVIRON["VALUE"] ","
                done = 1; agent = 0
            }
            done == 0 && /^[[:space:]]*"Agent"[[:space:]]*:[[:space:]]*\{[[:space:]]*$/ { agent = 1 }
            { print }
            END { if (done != 1) exit 3 }
        ' "${CONFIG}" >"${edited}" || die "cannot find \"Agent\": { in ${CONFIG} to add ${key}."
    fi
    # raw, not json: plutil will not print a bare string as JSON. raw prints a string unquoted and a bool as true/false.
    got="$(plutil -extract "Agent.${key}" raw -o - "${edited}" 2>/dev/null || true)"
    want="${value#\"}"
    want="${want%\"}"
    [[ "${got}" == "${want}" && "$(plutil -extract Agent.NodeId raw -o - "${edited}" 2>/dev/null)" == "$(plutil -extract Agent.NodeId raw -o - "${CONFIG}")" ]] ||
        die "setting ${key} in ${CONFIG} did not work (read back '${got}'); the file was left unchanged."
    install -m 0640 -o root -g "${SERVICE_USER}" "${edited}" "${CONFIG}.new"
    mv -f "${CONFIG}.new" "${CONFIG}"
}

# Points the agent at hostd (1) or turns launchd jobs off again (0).
configure_agent_for_hostd() {
    if [[ "$1" == 1 ]]; then
        config_set HostdSocket "\"${HOSTD_SOCKET}\""
        config_set ManageLaunchdJobs true
        config_set HostdExpected true
        step "agent.json: Agent:ManageLaunchdJobs true, HostdSocket ${HOSTD_SOCKET} (services.launchd)"
    else
        config_set ManageLaunchdJobs false
        config_set HostdExpected false
        step "agent.json: Agent:ManageLaunchdJobs false"
    fi
}

# (Re)loads hostd and waits until its socket exists.
start_hostd() {
    local tries=0 deadline
    if hostd_loaded; then
        launchctl bootout "system/${HOSTD_LABEL}" 2>/dev/null || true
        deadline=$((SECONDS + 30))
        while hostd_loaded && ((SECONDS < deadline)); do sleep 1; done
    fi
    launchctl enable "system/${HOSTD_LABEL}" 2>/dev/null || true
    until launchctl bootstrap system "${HOSTD_PLIST}" 2>/dev/null; do
        tries=$((tries + 1))
        if ((tries >= 10)); then
            launchctl bootstrap system "${HOSTD_PLIST}" || die "launchctl bootstrap system ${HOSTD_PLIST} failed."
            break
        fi
        sleep 1
    done
    deadline=$((SECONDS + 30))
    while ((SECONDS < deadline)); do
        if [[ -S "${HOSTD_SOCKET}" ]]; then
            step "${HOSTD_LABEL} is running and listening on ${HOSTD_SOCKET}"
            return 0
        fi
        sleep 1
    done
    launchctl print "system/${HOSTD_LABEL}" 2>/dev/null | sed -n '1,20p' || true
    if [[ -f "${HOSTD_LOG}" ]]; then
        echo "--- last lines of ${HOSTD_LOG}:"
        tail -n 20 "${HOSTD_LOG}" || true
    fi
    die "${HOSTD_LABEL} did not open ${HOSTD_SOCKET} (see ${HOSTD_LOG})."
}

# Installs or upgrades hostd (binary, plist, hostd.json only when missing) and starts it. The users whose
# LaunchAgents it discovers: --discover-user, else the account that ran sudo.
install_hostd() {
    local users=() user seed_users=()
    obtain_hostd_binary
    group_exists || die "the group ${SERVICE_USER} does not exist (install the agent first)."
    if [[ ${#discover_users[@]} -gt 0 ]]; then
        seed_users=("${discover_users[@]}")
    elif [[ -n "${SUDO_USER:-}" && "${SUDO_USER}" != root ]]; then
        seed_users=("${SUDO_USER}")
    fi
    for user in ${seed_users[@]+"${seed_users[@]}"}; do
        valid_discover_user "${user}" || die "'${user}' is not a login account on this Mac (uid 500 or more)." 2
        users+=("${user}")
    done

    step "Installing ${HOSTD_BINARY}"
    install -d -m 0755 -o root -g wheel "${HOSTD_PREFIX}"
    if [[ -f "${HOSTD_BINARY}" ]]; then cp -p "${HOSTD_BINARY}" "${HOSTD_BINARY}.previous"; fi
    install -m 0755 -o root -g wheel "${NEW_HOSTD}" "${HOSTD_BINARY}.new"
    check_signature "${HOSTD_BINARY}.new"
    mv -f "${HOSTD_BINARY}.new" "${HOSTD_BINARY}"

    install -d -m 0755 -o root -g wheel "${HOSTD_DIR}"
    install -d -m 0700 -o root -g wheel "${HOSTD_STATE}"
    if [[ -f "${HOSTD_CONFIG}" ]]; then
        step "Keeping ${HOSTD_CONFIG}"
        [[ ${#discover_users[@]} -eq 0 ]] || warn "--discover-user only applies to a new ${HOSTD_CONFIG}; edit discoverUsers there."
    else
        step "Installing ${HOSTD_CONFIG} (LaunchAgents of: ${users[*]:-nobody})"
        work_dir
        render_hostd_config ${users[@]+"${users[@]}"} >"${WORK}/hostd.json"
        install -m 0644 -o root -g wheel "${WORK}/hostd.json" "${HOSTD_CONFIG}"
    fi

    install -d -m 0750 -o root -g admin "${HOSTD_LOG_DIR}"
    if [[ ! -f "${HOSTD_LOG}" ]]; then
        touch "${HOSTD_LOG}"
        chown root:admin "${HOSTD_LOG}"
        chmod 0640 "${HOSTD_LOG}"
    fi

    local plist
    work_dir
    plist="${WORK}/${HOSTD_PLIST_NAME}"
    legacy_names "$(hostd_file_source com.mirbadllm.hostd.plist)" >"${plist}"
    if command -v plutil >/dev/null; then
        plutil -lint -s "${plist}" || die "${plist} is not a valid property list (plutil -lint)."
    fi
    install -m 0644 -o root -g wheel "${plist}" "${HOSTD_PLIST}.new"
    mv -f "${HOSTD_PLIST}.new" "${HOSTD_PLIST}"
    start_hostd
}

# Stops and removes hostd; keeps hostd.json and the whitelist unless --purge.
remove_hostd() {
    if hostd_loaded; then
        step "Stopping ${HOSTD_LABEL}"
        launchctl bootout "system/${HOSTD_LABEL}" 2>/dev/null || true
    fi
    rm -f "${HOSTD_PLIST}"
    rm -rf "${HOSTD_PREFIX}" "$(dirname "${HOSTD_SOCKET}")"
    if ((purge)); then
        step "Purging ${HOSTD_DIR} (hostd.json, the whitelist) and ${HOSTD_LOG_DIR}"
        rm -rf "${HOSTD_DIR}" "${HOSTD_LOG_DIR}"
    elif [[ -d "${HOSTD_DIR}" ]]; then
        step "Kept ${HOSTD_DIR} (hostd.json, the whitelist): install-hostd reuses it; --purge deletes it"
    fi
}

do_install_hostd() {
    require_root
    [[ -f "${CONFIG}" && -f "${BINARY}" ]] || die "mirbadllm-agent is not installed here (run 'install' first)."
    check_host
    install_hostd
    configure_agent_for_hostd 1
    if service_loaded; then
        start_and_check 0
    else
        step "${LABEL} is not loaded: the agent reaches hostd at its next start."
    fi
    step "Done. The Services tab of this node lists its launchd jobs; add the ones to manage under Discovered."
}

do_uninstall_hostd() {
    require_root
    hostd_installed || [[ -f "${HOSTD_PLIST}" ]] || die "mirbadllm-hostd is not installed here."
    remove_hostd
    if [[ -f "${CONFIG}" ]]; then
        configure_agent_for_hostd 0
        if service_loaded; then start_and_check 0; fi
    fi
    step "hostd removed. Model processes and the launchd jobs it managed are left as they are."
}

# ---- MLX (mlx-lm; plan M6, Phase 3) ----
#
# The pinned runtime (mlx-lm.lock.json next to the script): a standalone uv release (pinned by SHA-256 and size), the
# CPython version uv installs (uv pins each Python build's SHA-256 itself), and mlx-lm.lock.txt, every package with its
# hashes (uv pip sync --require-hashes). The version folder holds its own Python (python/), a virtualenv (venv/) with
# compiled bytecode and manifest.json listing every file with "allFiles": true, which the agent's catalogue re-hashes
# and which makes any other file a mismatch (an unlisted .py or .pth would be code Python runs). The executable is
# venv/bin/python; the agent starts it as "python -I -u -B -m mlx_lm.server". The folder is built in place (a
# virtualenv records absolute paths) and removed again when anything fails.

MLX_VERSION=""
read_mlx_lock() {
    [[ -n "${MLX_VERSION}" ]] && return 0
    local lock="${SCRIPT_DIR}/${MLX_LOCK_NAME}" got
    [[ -f "${lock}" ]] || die "${MLX_LOCK_NAME} not found next to the script."
    command -v plutil >/dev/null || die "plutil not found (it reads ${MLX_LOCK_NAME})."
    mlx_value() { plutil -extract "$1" raw -o - "${lock}" 2>/dev/null || true; }
    [[ "$(mlx_value schemaVersion)" == 1 ]] || die "${lock}: unknown schemaVersion."
    MLX_VERSION="$(mlx_value version)"
    [[ "${MLX_VERSION}" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || die "${lock}: version '${MLX_VERSION}' is not an mlx-lm release number."
    MLX_PYTHON="$(mlx_value python)"
    [[ "${MLX_PYTHON}" =~ ^3\.[0-9]+\.[0-9]+$ ]] || die "${lock}: python '${MLX_PYTHON}' is not a CPython 3.x.y version."
    UV_VERSION="$(mlx_value uv.version)"
    UV_ASSET="$(mlx_value uv.name)"
    UV_URL="$(mlx_value uv.url)"
    UV_SHA256="$(mlx_value uv.sha256)"
    UV_BYTES="$(mlx_value uv.bytes)"
    UV_PREFIX="$(mlx_value uv.prefix)"
    [[ "${UV_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "${lock}: uv version '${UV_VERSION}' is not x.y.z."
    [[ "${UV_ASSET}" == uv-aarch64-apple-darwin.tar.gz ]] || die "${lock}: the uv asset must be uv-aarch64-apple-darwin.tar.gz."
    [[ "${UV_URL}" == "https://github.com/astral-sh/uv/releases/download/${UV_VERSION}/${UV_ASSET}" ]] ||
        die "${lock}: uv must be downloaded by tag from https://github.com/astral-sh/uv/releases/download/${UV_VERSION}/, not ${UV_URL}."
    [[ "${UV_SHA256}" =~ ^[0-9a-f]{64}$ ]] || die "${lock}: ${UV_ASSET} has no SHA-256."
    [[ "${UV_BYTES}" =~ ^[1-9][0-9]*$ ]] || die "${lock}: ${UV_ASSET} has no size."
    [[ "${UV_PREFIX}" == uv-aarch64-apple-darwin/ ]] || die "${lock}: prefix must be uv-aarch64-apple-darwin/ (the folder the tarball unpacks to)."
    MLX_REQUIREMENTS="$(mlx_value requirements)"
    [[ "${MLX_REQUIREMENTS}" =~ ^[A-Za-z0-9._-]+\.txt$ ]] || die "${lock}: requirements '${MLX_REQUIREMENTS}' is not a .txt file name."
    [[ -f "${SCRIPT_DIR}/${MLX_REQUIREMENTS}" ]] || die "${MLX_REQUIREMENTS} not found next to the script."
    got="$(file_sha256 "${SCRIPT_DIR}/${MLX_REQUIREMENTS}")"
    [[ "${got}" == "$(mlx_value requirementsSha256)" ]] ||
        die "${MLX_REQUIREMENTS}: SHA-256 ${got}, ${MLX_LOCK_NAME} pins $(mlx_value requirementsSha256)."
    grep -q "^mlx-lm==${MLX_VERSION//./\\.} " "${SCRIPT_DIR}/${MLX_REQUIREMENTS}" ||
        die "${MLX_REQUIREMENTS} does not pin mlx-lm==${MLX_VERSION}."
    # Every requirement carries a hash (uv refuses the sync otherwise, but say so before anything is downloaded).
    if grep -E '^[A-Za-z0-9]' "${SCRIPT_DIR}/${MLX_REQUIREMENTS}" | grep -vq ' \\$'; then
        die "${MLX_REQUIREMENTS}: every requirement must be followed by its --hash lines."
    fi
    MLX_TARGET="${MLX_DIR}/${MLX_VERSION}"
}

# Downloads uv (or, with --mlx-source, copies it) into <dir>, checks its size and SHA-256 and unpacks it; sets UV_BIN.
fetch_uv() {
    local dir="$1" file="$1/${UV_ASSET}" got member
    if [[ -n "${mlx_source}" ]]; then
        step "Taking ${UV_ASSET} from ${mlx_source}"
        [[ -f "${mlx_source}/${UV_ASSET}" ]] || die "${mlx_source}/${UV_ASSET} does not exist."
        cp "${mlx_source}/${UV_ASSET}" "${file}"
    else
        step "Downloading ${UV_URL}"
        command -v curl >/dev/null || die "curl not found."
        curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --connect-timeout 30 -o "${file}" "${UV_URL}" ||
            die "downloading ${UV_URL} failed."
    fi
    got="$(file_bytes "${file}")"
    [[ "${got}" == "${UV_BYTES}" ]] || die "${UV_ASSET}: ${got} bytes, the lock file pins ${UV_BYTES}. Not installed."
    got="$(file_sha256 "${file}")"
    [[ "${got}" == "${UV_SHA256}" ]] || die "${UV_ASSET}: SHA-256 ${got}, the lock file pins ${UV_SHA256}. Not installed."
    step "${UV_ASSET} matches ${MLX_LOCK_NAME} (uv ${UV_VERSION}, ${UV_BYTES} bytes)"
    while IFS= read -r member; do
        case "${member}" in
            /* | ../* | */../* | */..) die "${UV_ASSET}: member '${member}' would unpack outside the folder." ;;
        esac
    done < <(tar -tzf "${file}")
    tar -xzf "${file}" -C "${dir}" || die "unpacking ${UV_ASSET} failed."
    UV_BIN="${dir}/${UV_PREFIX}uv"
    [[ -f "${UV_BIN}" && ! -L "${UV_BIN}" ]] || die "${UV_ASSET} has no ${UV_PREFIX}uv."
    chmod 0755 "${UV_BIN}"
}

# Runs uv with a clean environment: nothing from the caller's uv, pip or Python configuration, its cache, home and
# temporary files in the work folder, Python downloads only when <downloads> is "manual".
run_uv() {
    local downloads="$1"
    shift
    env -i PATH=/usr/bin:/bin HOME="${WORK}/uv-home" TMPDIR="${WORK}/uv-tmp" \
        UV_CACHE_DIR="${WORK}/uv-cache" UV_PYTHON_INSTALL_DIR="${MLX_BUILD}/python" UV_PYTHON_BIN_DIR="${WORK}/uv-bin" \
        UV_NO_CONFIG=1 UV_NO_PROGRESS=1 UV_PYTHON_PREFERENCE=only-managed UV_PYTHON_DOWNLOADS="${downloads}" \
        "${UV_BIN}" "$@"
}

# manifest.json (ExecutableManifest, camelCase, "allFiles": true) of <folder>: every regular file and every link to a
# file (hashed through the link), sorted, with its SHA-256 and size. Paths are checked to be plain.
mlx_manifest() {
    local folder="$1" hashes sizes
    work_dir
    hashes="${WORK}/mlx-hashes.txt"
    sizes="${WORK}/mlx-sizes.txt"
    (cd "${folder}" && find . \( -type f -o -type l \) ! -path ./manifest.json -print0 | LC_ALL=C sort -z |
        xargs -0 shasum -a 256) >"${hashes}" || die "hashing ${folder} failed."
    (cd "${folder}" && find . \( -type f -o -type l \) ! -path ./manifest.json -print0 | LC_ALL=C sort -z |
        xargs -0 stat -L -f '%z %N') >"${sizes}" || die "reading the sizes in ${folder} failed."
    awk -v name="${MLX_EXECUTABLE}" -v version="${MLX_VERSION}" -v uv="${UV_ASSET}" -v req="${MLX_REQUIREMENTS}" '
        FNR == NR { size[substr($0, index($0, " ") + 1)] = $1; next }
        {
            sha = $1; path = substr($0, 67)
            if (path !~ /^\.\/[A-Za-z0-9._+\/-]+$/) { bad = path; exit 2 }
            if (!(path in size)) { bad = path; exit 3 }
            rel = substr(path, 3)
            entry[++n] = sprintf("    {\n      \"path\": \"%s\",\n      \"sha256\": \"%s\",\n      \"bytes\": %s\n    }", rel, sha, size[path])
        }
        END {
            if (bad != "") { print "bad path " bad > "/dev/stderr"; exit 2 }
            printf "{\n  \"schemaVersion\": 1,\n  \"name\": \"%s\",\n  \"version\": \"%s\",\n  \"sources\": [\n    \"%s\",\n    \"%s\"\n  ],\n  \"allFiles\": true,\n  \"files\": [", name, version, uv, req
            for (i = 1; i <= n; i++) printf "%s\n%s", (i > 1 ? "," : ""), entry[i]
            printf "\n  ]\n}\n"
        }
    ' "${sizes}" "${hashes}" || die "${folder} holds a file whose path is not plain letters, digits and ._+-/ (the catalogue would refuse it)."
}

# Problems of the links in <folder>, one per line: a link to a folder (the agent's catalogue follows folder links and
# would see every file under it twice), a dangling link, or one that points outside the folder.
mlx_link_problems() {
    local folder="$1" link target
    while IFS= read -r link; do
        [[ -n "${link}" ]] || continue
        if [[ -d "${link}" ]]; then echo "${link#"${folder}"/} links to a folder"; continue; fi
        if [[ ! -e "${link}" ]]; then echo "${link#"${folder}"/} is a dangling link"; continue; fi
        target="$(cd "$(dirname "${link}")" && cd "$(dirname "$(readlink "${link}")")" && pwd -P)" || { echo "${link#"${folder}"/} cannot be resolved"; continue; }
        case "${target}/" in
            "$(cd "${folder}" && pwd -P)"/*) ;;
            *) echo "${link#"${folder}"/} points outside the folder (${target})" ;;
        esac
    done < <(find "${folder}" -type l)
}

# Checks the code signature of the arm64 slice of every Mach-O file (a wheel's universal binary may carry an unsigned
# x86_64 slice that arm64 never loads).
check_mlx_signatures() {
    local folder="$1" file n=0
    xattr -dr com.apple.quarantine "${folder}" 2>/dev/null || true
    if ! command -v codesign >/dev/null; then
        warn "codesign not found: cannot check the signatures in ${folder}."
        return 0
    fi
    while IFS= read -r file; do
        is_macho "${file}" || continue
        n=$((n + 1))
        codesign --verify --strict --arch arm64 "${file}" 2>/dev/null ||
            die "${file}: the code signature of its arm64 code does not verify; not installed."
    done < <(find "${folder}" -type f \( -name '*.so' -o -name '*.dylib' -o -path '*/bin/*' \))
    step "Code signatures of the ${n} Mach-O files in ${folder} verify (arm64)"
}

# Builds a complete version folder at <dest> (absent or empty; the final path, since a virtualenv records absolute
# paths): uv, the pinned Python, the virtualenv synced from the hash-locked requirements with bytecode, links fixed,
# a smoke import, manifest.json, signatures. No root needed ("stage-mlx --dest DIR" runs exactly this).
stage_mlx() {
    local dest="$1" pyhome py minor
    read_mlx_lock
    command -v shasum >/dev/null || die "shasum not found."
    if [[ -e "${dest}" ]]; then
        [[ -d "${dest}" && -z "$(ls -A "${dest}")" ]] || die "${dest} exists and is not an empty folder."
    fi
    work_dir
    mkdir -p "${WORK}/uv-download" "${WORK}/uv-home" "${WORK}/uv-tmp" "${WORK}/uv-cache" "${WORK}/uv-bin" "${dest}"
    MLX_BUILD="$(cd "${dest}" && pwd -P)"
    fetch_uv "${WORK}/uv-download"

    minor="${MLX_PYTHON%.*}"
    step "Installing CPython ${MLX_PYTHON} into ${MLX_BUILD}/python (uv checks the download's SHA-256)"
    run_uv manual python install --no-bin "${MLX_PYTHON}" || die "uv could not install CPython ${MLX_PYTHON}."
    pyhome="${MLX_BUILD}/python/cpython-${MLX_PYTHON}-macos-aarch64-none"
    py="${pyhome}/bin/python${minor}"
    [[ -x "${py}" && ! -L "${py}" ]] || die "uv did not install ${py}."

    step "Creating the virtualenv and installing mlx-lm ${MLX_VERSION} from ${MLX_REQUIREMENTS} (hashes required)"
    run_uv never venv --python "${py}" "${MLX_BUILD}/venv" || die "uv could not create the virtualenv."
    run_uv never pip sync --python "${MLX_BUILD}/venv/bin/python" --require-hashes --compile-bytecode --link-mode copy \
        "${SCRIPT_DIR}/${MLX_REQUIREMENTS}" || die "installing ${MLX_REQUIREMENTS} failed."

    # uv's lock and scratch files are not part of the runtime; the minor-version folder link uv adds
    # (cpython-3.12-... -> cpython-3.12.15-...) would make the agent's catalogue list every Python file twice, so the
    # virtualenv points at the full version folder instead.
    rm -rf "${MLX_BUILD}/python/.lock" "${MLX_BUILD}/python/.temp" "${MLX_BUILD}/python/.gitignore" "${MLX_BUILD}/venv/.lock"
    rm -f "${MLX_BUILD}/python/cpython-${minor}-macos-aarch64-none"
    ln -sfn "${py}" "${MLX_BUILD}/venv/bin/python"
    sed -i '' "s|^home = .*|home = ${pyhome}/bin|" "${MLX_BUILD}/venv/pyvenv.cfg"
    local problems
    problems="$(mlx_link_problems "${MLX_BUILD}")"
    [[ -z "${problems}" ]] || die "the MLX folder has links the agent cannot verify: $(head -n 5 <<<"${problems}" | tr '\n' ';')"

    step "Checking that mlx-lm ${MLX_VERSION} imports in isolated mode"
    env -i PATH=/usr/bin:/bin HOME="${WORK}/uv-home" HF_HUB_OFFLINE=1 \
        "${MLX_BUILD}/venv/bin/python" -I -B -c "import mlx_lm, mlx.core; assert mlx_lm.__version__ == '${MLX_VERSION}', mlx_lm.__version__" ||
        die "mlx-lm ${MLX_VERSION} does not import from ${MLX_BUILD}/venv."

    chmod -R u+rwX,go+rX,go-w "${MLX_BUILD}"
    check_mlx_signatures "${MLX_BUILD}"
    mlx_manifest "${MLX_BUILD}" >"${WORK}/mlx-manifest.json"
    mv "${WORK}/mlx-manifest.json" "${MLX_BUILD}/manifest.json"
    chmod 0644 "${MLX_BUILD}/manifest.json"
    step "manifest.json lists $(plutil -extract files raw -o - "${MLX_BUILD}/manifest.json") files ($(du -sh "${MLX_BUILD}" | awk '{print $1}'))"
}

# True when <folder> holds exactly what its manifest.json lists and that manifest is for this lock's version.
mlx_installed_intact() {
    local folder="$1" problems
    [[ -d "${folder}" && -f "${folder}/manifest.json" ]] || return 1
    [[ "$(plutil -extract version raw -o - "${folder}/manifest.json" 2>/dev/null)" == "${MLX_VERSION}" ]] || return 1
    problems="$(mlx_link_problems "${folder}")"
    if [[ -n "${problems}" ]]; then
        warn "${folder} does not verify ($(head -n 3 <<<"${problems}" | tr '\n' ';'))."
        return 1
    fi
    if [[ "$(mlx_manifest "${folder}")" != "$(cat "${folder}/manifest.json")" ]]; then
        warn "${folder} no longer matches its manifest.json."
        return 1
    fi
    return 0
}

# True when agent.json already registers an mlx-lm-server (an upgrade then keeps MLX current without --with-mlx).
mlx_registered() {
    local i=0 name
    [[ -f "${CONFIG}" ]] || return 1
    while plutil -extract "Agent.Executables.${i}" json -o /dev/null "${CONFIG}" 2>/dev/null; do
        name="$(plutil -extract "Agent.Executables.${i}.Name" raw -o - "${CONFIG}" 2>/dev/null || true)"
        [[ "${name}" == "${MLX_EXECUTABLE}" ]] && return 0
        i=$((i + 1))
    done
    return 1
}

# Installs the pinned version into ${MLX_DIR}/<version> (root:wheel, read-only for the agent) unless it is there and
# verifies, then registers it in agent.json. Root only. About 520 MB on disk.
provision_mlx() {
    local users
    read_mlx_lock
    step "MLX: mlx-lm ${MLX_VERSION} (Python ${MLX_PYTHON}) in ${MLX_TARGET}"
    install -d -m 0755 -o root -g wheel "${PREFIX}" "${PREFIX}/runtimes" "${MLX_DIR}"
    if [[ -d "${MLX_TARGET}" ]]; then
        if mlx_installed_intact "${MLX_TARGET}"; then
            step "mlx-lm ${MLX_VERSION} is installed and verified in ${MLX_TARGET}"
            register_executable "${MLX_EXECUTABLE}" "${MLX_VERSION}" "${MLX_TARGET}/venv/bin/python" "${MLX_TARGET}/manifest.json" "mlx-lm"
            return 0
        fi
        users="$(llama_folder_users "${MLX_TARGET}")"
        [[ -z "${users}" ]] || die "${MLX_TARGET} is in use (stop its models first): ${users}"
        step "Removing the damaged ${MLX_TARGET}; installing it again"
        rm -rf "${MLX_TARGET}"
    fi
    # Built in place (a virtualenv records absolute paths); cleanup() removes it if anything fails.
    MLX_STAGING="${MLX_TARGET}"
    stage_mlx "${MLX_TARGET}"
    chown -R root:wheel "${MLX_TARGET}"
    MLX_STAGING=""
    step "Installed mlx-lm ${MLX_VERSION} to ${MLX_TARGET} with manifest.json"
    register_executable "${MLX_EXECUTABLE}" "${MLX_VERSION}" "${MLX_TARGET}/venv/bin/python" "${MLX_TARGET}/manifest.json" "mlx-lm"
}

# ---- verbs ----

# Sets token from MIRBADLLM_JOIN_TOKEN or, on a terminal, the prompt <label> (never from argv).
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
    local upgrade="$1" models cache join=0 token
    require_root
    check_roots
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

    check_host
    obtain_binary "${RID}"
    # A missing or bad lock file stops the install before anything changes.
    if ((no_llama == 0)); then read_llama_lock; fi
    if ((with_mlx == 0)) && [[ "${upgrade}" == 1 ]] && mlx_registered; then with_mlx=1; fi
    if ((with_mlx)); then read_mlx_lock; fi
    # An installed hostd is upgraded with the agent, so the two speak the same version.
    if ((with_hostd == 0)) && [[ "${upgrade}" == 1 ]] && hostd_installed; then with_hostd=1; fi
    if ((with_hostd)); then obtain_hostd_binary; fi

    create_service_user

    install -d -m 0750 -o root -g "${SERVICE_USER}" "${CONFIG_DIR}"
    if [[ -f "${CONFIG}" ]]; then
        step "Keeping ${CONFIG} (node ${node_id})"
    else
        step "Installing ${CONFIG} for node ${node_id} (models ${models}, cache ${cache})"
        work_dir
        render_config "${node_id}" "${models}" "${cache}" >"${WORK}/agent.json"
        install -m 0640 -o root -g "${SERVICE_USER}" "${WORK}/agent.json" "${CONFIG}"
    fi
    install -d -m 0700 -o "${SERVICE_USER}" -g "${SERVICE_USER}" "${STATE_DIR}"

    local dir
    for dir in "${models}" "${cache}"; do
        ensure_root_dir "${dir}"
    done

    # The log is opened by launchd for the job; admins read it without sudo.
    install -d -m 0750 -o "${SERVICE_USER}" -g admin "${LOG_DIR}"
    if [[ ! -f "${LOG_FILE}" ]]; then
        touch "${LOG_FILE}"
        chown "${SERVICE_USER}:admin" "${LOG_FILE}"
        chmod 0640 "${LOG_FILE}"
    fi

    # Before the binary is swapped and the agent (re)started, so a failed download changes nothing that runs and the
    # agent's catalogue sees the version at its next start.
    if ((no_llama)); then
        step "Skipping llama.cpp (--no-llama); Agent:Executables in ${CONFIG} is left as it is"
    else
        provision_llama
    fi
    if ((with_mlx)); then
        provision_mlx
    fi

    step "Installing ${BINARY}"
    install -d -m 0755 -o root -g wheel "${PREFIX}"
    save_previous
    install -m 0755 -o root -g wheel "${NEW_BINARY}" "${BINARY}.new"
    check_signature "${BINARY}.new"
    mv -f "${BINARY}.new" "${BINARY}"
    if [[ -n "${NEW_BUILD_INFO}" ]]; then
        install -m 0644 -o root -g wheel "${NEW_BUILD_INFO}" "${PREFIX}/BUILD-INFO"
    fi
    # The plist is checked now and installed into /Library/LaunchDaemons only once the node is enrolled: launchd
    # loads every plist there at boot, and an unenrolled agent would exit and be restarted every 10 s.
    if command -v plutil >/dev/null; then
        plutil -lint -s "$(plist_source)" || die "$(plist_source) is not a valid property list (plutil -lint)."
    fi

    local port
    port="$(config_port "${CONFIG}")"
    if ((allow_firewall)) || appfw_recorded; then
        appfw_allow
    fi

    if ((join)); then
        step "Enrolling node ${node_id} at ${enroll_url}"
        local force=()
        if [[ "${force_join}" == 1 ]]; then force=(--force); fi
        # The token reaches the agent through its environment only (sudo keeps just this variable; root may pass it).
        if ! (cd "${STATE_DIR}" && MIRBADLLM_JOIN_TOKEN="${token}" sudo -n -u "${SERVICE_USER}" \
            --preserve-env=MIRBADLLM_JOIN_TOKEN -- \
            "${BINARY}" join --enroll-url "${enroll_url}" --ca-hash "${ca_hash}" --node-id "${node_id}" \
            --state-dir "${STATE_DIR}" ${addresses[@]+"${addresses[@]}"} ${force[@]+"${force[@]}"}); then
            die "join failed; the agent is installed but not started. Fix the cause and re-run with the join parameters."
        fi
        token=""
    fi

    if ((with_hostd)); then
        install_hostd
        configure_agent_for_hostd 1
    fi
    install_update_helper

    if ! enrolled; then
        step "Installed but not started: node ${node_id} is not enrolled yet. Re-run with --enroll-url, --ca-hash and"
        echo "    the join token (\"Add node\" on the master); ${PLIST} is installed with the enrollment."
    elif ((no_start)); then
        install_plist
        step "Installed; not started (--no-start). It starts at the next boot, or now: sudo launchctl bootstrap system ${PLIST}"
    else
        local rollback=0
        if [[ -f "${BINARY}.previous" ]]; then rollback=1; fi
        install_plist
        start_and_check "${rollback}"
        if ((join)); then
            step "Done. Node ${node_id} waits on the master's Nodes page: approve it there (compare the key fingerprint)."
        else
            step "Done (node ${node_id})."
        fi
        echo "    Logs: ${LOG_FILE}"
    fi
    firewall_note "${port}"
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
    check_host

    # The state directory may be gone (the reason for a rekey); join --rekey then needs --node-uid.
    install -d -m 0700 -o "${SERVICE_USER}" -g "${SERVICE_USER}" "${STATE_DIR}"
    step "Stopping ${LABEL} while the identity of node ${node_id} is replaced"
    stop_service
    local uid_args=()
    if [[ -n "${node_uid}" ]]; then uid_args=(--node-uid "${node_uid}"); fi
    step "Re-pairing node ${node_id} at ${enroll_url}"
    # The token reaches the agent through its environment only (sudo keeps just this variable).
    if ! (cd "${STATE_DIR}" && MIRBADLLM_JOIN_TOKEN="${token}" sudo -n -u "${SERVICE_USER}" \
        --preserve-env=MIRBADLLM_JOIN_TOKEN -- \
        "${BINARY}" join --rekey --enroll-url "${enroll_url}" --ca-hash "${ca_hash}" --node-id "${node_id}" \
        ${uid_args[@]+"${uid_args[@]}"} --state-dir "${STATE_DIR}" ${addresses[@]+"${addresses[@]}"}); then
        token=""
        if enrolled && [[ -f "${PLIST}" ]]; then
            warn "join --rekey failed: starting ${LABEL} again on its previous identity."
            launchctl enable "system/${LABEL}" 2>/dev/null || true
            launchctl bootstrap system "${PLIST}" || true
        fi
        die "join --rekey failed (the line above names the cause); the identity is unchanged. Fix it and re-run with a valid rekey token."
    fi
    token=""
    if [[ ! -f "${PLIST}" ]]; then install_plist; fi
    # The rekey may carry a new CA (after a CA rotation): the helper trusts it from now on.
    install_update_helper
    start_and_check 0
    step "Done: node ${node_id} re-paired (uid kept). If the master shows it pending, approve it after comparing the fingerprint above."
    echo "    Logs: ${LOG_FILE}"
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
    step "Rolling back to the previous binary and plist (a second rollback returns to the newer ones)"
    swap_previous "${BINARY}" "${BINARY}.previous"
    swap_previous "${PLIST}" "${PREFIX}/${PLIST_NAME}.previous"
    swap_previous "${PREFIX}/BUILD-INFO" "${PREFIX}/BUILD-INFO.previous"
    if appfw_recorded; then appfw_allow; fi
    if enrolled && [[ -f "${PLIST}" ]]; then
        start_and_check 0
    fi
}

do_uninstall() {
    require_root
    local node="" processes
    if [[ -f "${CONFIG}" ]]; then node="$(config_string NodeId "${CONFIG}")"; fi
    if hostd_installed || [[ -f "${HOSTD_PLIST}" ]]; then remove_hostd; fi
    remove_update_helper
    step "Stopping and removing ${LABEL}"
    stop_service
    rm -f "${PLIST}"
    appfw_remove_recorded
    rm -f "${INSTALL_RECORD}"
    rm -rf "${PREFIX}"
    processes="$(service_processes)"
    if ((purge)); then
        if [[ -n "${processes}" ]]; then
            step "Stopping the model processes of ${SERVICE_USER} before the user is deleted:"
            printf '%s\n' "${processes}" | sed 's/^/    /'
            pkill -TERM -U "${SERVICE_USER}" 2>/dev/null || true
            local deadline=$((SECONDS + 30))
            while [[ -n "$(service_processes)" ]] && ((SECONDS < deadline)); do sleep 1; done
            pkill -KILL -U "${SERVICE_USER}" 2>/dev/null || true
        fi
        step "Purging ${CONFIG_DIR} (with ${STATE_DIR}, the node identity), ${LOG_DIR} and the user ${SERVICE_USER}"
        rm -rf "${CONFIG_DIR}" "${LOG_DIR}"
        delete_service_user
    else
        step "Kept ${CONFIG}, ${STATE_DIR} (identity) and ${LOG_DIR}: 'install' reuses them; 'uninstall --purge' deletes them"
        if [[ -n "${processes}" ]]; then
            step "Processes of ${SERVICE_USER} (models) keep running:"
            printf '%s\n' "${processes}" | sed 's/^/    /'
            echo "    stop them with: sudo pkill -U ${SERVICE_USER}"
        fi
    fi
    step "Model and cache folders are never removed. Revoke node ${node:-<id>} on the master's Nodes page."
}

# Re-provisions llama.cpp only (a damaged version folder is replaced, an intact one kept) and registers it; restarts a
# running agent so its catalogue re-reads Agent:Executables (models keep running and are adopted again).
do_update_llama() {
    require_root
    [[ -f "${CONFIG}" && -f "${BINARY}" ]] || die "mirbadllm-agent is not installed here (run 'install' first)."
    check_host
    group_exists || die "the group ${SERVICE_USER} does not exist (run 'install' first)."
    provision_llama
    if service_loaded; then
        start_and_check 0
    else
        step "${LABEL} is not loaded: the agent picks llama.cpp ${LLAMA_VERSION} up at its next start."
    fi
}

# Installs or re-provisions MLX only (a damaged version folder is replaced, an intact one kept) and registers it;
# restarts a running agent so its catalogue re-reads Agent:Executables and reports runtime.mlx.process.
do_update_mlx() {
    require_root
    [[ -f "${CONFIG}" && -f "${BINARY}" ]] || die "mirbadllm-agent is not installed here (run 'install' first)."
    check_host
    group_exists || die "the group ${SERVICE_USER} does not exist (run 'install' first)."
    provision_mlx
    if service_loaded; then
        start_and_check 0
    else
        step "${LABEL} is not loaded: the agent picks mlx-lm ${MLX_VERSION} up at its next start."
    fi
}

# Builds a verified MLX version folder at --dest without root (CI, tests); with --config also registers it there.
do_stage_mlx() {
    [[ "$(uname -s)" == Darwin ]] || die "stage-mlx needs macOS (shasum, codesign, plutil)."
    [[ -n "${dest}" ]] || die "stage-mlx needs --dest DIR (the version folder to build)." 2
    [[ "${dest}" == /* ]] || dest="${PWD}/${dest}"
    read_mlx_lock
    MLX_TARGET="${dest%/}"
    MLX_STAGING="${MLX_TARGET}"
    stage_mlx "${MLX_TARGET}"
    MLX_STAGING=""
    step "Staged mlx-lm ${MLX_VERSION} in ${MLX_TARGET}"
    if ((config_scratch)); then
        register_executable "${MLX_EXECUTABLE}" "${MLX_VERSION}" "${MLX_TARGET}/venv/bin/python" "${MLX_TARGET}/manifest.json" "mlx-lm"
    fi
}

# Builds a verified version folder at --dest without root and without touching the installation (CI, tests); with
# --config also registers it in that scratch agent.json.
do_stage_llama() {
    [[ "$(uname -s)" == Darwin ]] || die "stage-llama needs macOS (shasum, codesign, plutil)."
    [[ -n "${dest}" ]] || die "stage-llama needs --dest DIR (the version folder to build)." 2
    [[ "${dest}" == /* ]] || dest="${PWD}/${dest}"
    read_llama_lock
    LLAMA_TARGET="${dest%/}"
    stage_llama "${LLAMA_TARGET}"
    step "Staged llama.cpp ${LLAMA_VERSION} in ${LLAMA_TARGET}"
    if ((config_scratch)); then register_llama; fi
}

sha256_of() {
    if command -v sha256sum >/dev/null; then
        sha256sum "$1" | awk '{print $1}'
    elif command -v shasum >/dev/null; then
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

# Runs on macOS and on Linux (the master image builds the bundle in a Linux container): POSIX tools only.
do_package() {
    local want version name stage
    if [[ -n "${rid}" ]]; then
        want="${rid}"
    elif [[ -n "${published_dir}" && -f "${published_dir}/mirbadllm-agent" ]]; then
        want="$(macho_rid "${published_dir}/mirbadllm-agent")"
        want="${want:-${RID}}" # not Mach-O: obtain_binary names the problem
    else
        want="${RID}"
    fi
    [[ "${want}" == "${RID}" ]] || die "--rid must be ${RID} (Apple Silicon only)." 2
    obtain_binary "${want}"
    version=local
    if [[ -n "${NEW_BUILD_INFO}" ]]; then
        version="$(sed -n 's/^revision=//p' "${NEW_BUILD_INFO}")"
        version="${version:-local}"
    fi
    [[ "${version}" =~ ^[A-Za-z0-9._-]+$ ]] || die "BUILD-INFO revision '${version}' is not letters, digits, '.', '_' or '-'."
    name="mirbadllm-agent-${version}-${want}"
    output="${output:-${PWD}/${name}.tar.gz}"
    work_dir
    stage="${WORK}/stage/${name}"
    mkdir -p "${stage}"
    install -m 0755 "${SCRIPT_DIR}/install-agent.sh" "${stage}/install-agent.sh"
    install -m 0644 "$(plist_source)" "${stage}/com.mirbadllm.agent.plist"
    install -m 0644 "$(example_config)" "${stage}/${EXAMPLE_CONFIG_NAME}"
    [[ -f "${SCRIPT_DIR}/${LLAMA_LOCK_NAME}" ]] || die "${LLAMA_LOCK_NAME} not found next to the script."
    install -m 0644 "${SCRIPT_DIR}/${LLAMA_LOCK_NAME}" "${stage}/${LLAMA_LOCK_NAME}"
    local mlx_file
    for mlx_file in "${MLX_LOCK_NAME}" mlx-lm.lock.txt; do
        [[ -f "${SCRIPT_DIR}/${mlx_file}" ]] || die "${mlx_file} not found next to the script."
        install -m 0644 "${SCRIPT_DIR}/${mlx_file}" "${stage}/${mlx_file}"
    done
    if [[ -f "${SCRIPT_DIR}/README.md" ]]; then install -m 0644 "${SCRIPT_DIR}/README.md" "${stage}/README.md"; fi
    install -m 0644 "$(hostd_file_source com.mirbadllm.hostd.plist)" "${stage}/com.mirbadllm.hostd.plist"
    if [[ -n "$(helper_source)" ]]; then install -m 0755 "$(helper_source)" "${stage}/agent-update-helper.sh"; fi
    install -m 0644 "$(hostd_file_source "${HOSTD_SEED_NAME}")" "${stage}/${HOSTD_SEED_NAME}"
    # hostd travels with the agent when it was published next to it (the master image's bundle stage does).
    local hostd_src=""
    if [[ -n "${published_dir}" && -f "${published_dir}/mirbadllm-hostd" ]]; then
        hostd_src="${published_dir}/mirbadllm-hostd"
    elif [[ -f "${SCRIPT_DIR}/mirbadllm-hostd" ]]; then
        hostd_src="${SCRIPT_DIR}/mirbadllm-hostd"
    fi
    if [[ -n "${hostd_src}" ]]; then
        [[ "$(macho_rid "${hostd_src}")" == "${want}" ]] || die "${hostd_src} is not a ${want} executable."
        install -m 0755 "${hostd_src}" "${stage}/mirbadllm-hostd"
    else
        warn "no mirbadllm-hostd next to the agent: the bundle has no host agent (install-hostd then needs a checkout)."
    fi
    install -m 0755 "${NEW_BINARY}" "${stage}/mirbadllm-agent"
    if [[ -n "${NEW_BUILD_INFO}" ]]; then
        install -m 0644 "${NEW_BUILD_INFO}" "${stage}/BUILD-INFO"
    else
        printf 'revision=%s\nrid=%s\nbuilt=%s\n' "${version}" "${want}" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"${stage}/BUILD-INFO"
        chmod 0644 "${stage}/BUILD-INFO"
    fi
    # bsdtar (macOS) would add AppleDouble files and extended attributes that GNU tar warns about.
    local tar_opts=()
    if tar --version 2>/dev/null | grep -q bsdtar; then tar_opts=(--no-xattrs --no-mac-metadata); fi
    # The folder's pre-rename name, a link: the update helper installed on a pre-rename node looks for it.
    ln -s "${name}" "${WORK}/stage/spark-serve-agent-${version}-${want}"
    COPYFILE_DISABLE=1 tar ${tar_opts[@]+"${tar_opts[@]}"} -czf "${output}" -C "${WORK}/stage" "${name}" "spark-serve-agent-${version}-${want}"
    step "Wrote ${output}"
    local sum
    sum="$(sha256_of "${output}")"
    if [[ -n "${sum}" ]]; then echo "    sha256 ${sum}"; fi
    echo "    on the Mac: tar -xzf ${name}.tar.gz && sudo ./${name}/install-agent.sh install --node-id <id> ..."
}

case "${verb}" in
    install) do_install 0 ;;
    upgrade) do_install 1 ;;
    rekey) do_rekey ;;
    rollback) do_rollback ;;
    uninstall) do_uninstall ;;
    update-llama) do_update_llama ;;
    update-mlx) do_update_mlx ;;
    install-hostd) do_install_hostd ;;
    uninstall-hostd) do_uninstall_hostd ;;
    stage-llama) do_stage_llama ;;
    stage-mlx) do_stage_mlx ;;
    package) do_package ;;
    render-config)
        check_roots
        valid_node_id "${node_id}" || die "--node-id is required: lower-case letters, digits and '-', at most 40." 2
        render_config "${node_id}" "${models_root:-${DEFAULT_MODELS_ROOT}}" "${cache_root:-${DEFAULT_CACHE_ROOT}}"
        ;;
    help) usage ;;
    *) die "unknown verb '${verb}' (install, upgrade, rekey, rollback, uninstall, update-llama, update-mlx, install-hostd, uninstall-hostd, package, render-config)." 2 ;;
esac
