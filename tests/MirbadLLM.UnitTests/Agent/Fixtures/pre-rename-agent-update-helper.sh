#!/bin/bash
# spark-serve agent update helper (docs/multinode/agent-updates.md U4/U5). Runs as root, started by the system when the
# agent writes an update order into its spool: on Linux the systemd path unit spark-serve-agent-update.path, on macOS the
# LaunchDaemon com.spark-serve.agent-updater (WatchPaths). install-agent.sh installs it next to the agent binary.
#
#   agent-update-helper.sh <spool> <trust-dir> <agent-user>
#
# The spool is the agent's (writable by the agent, so nothing in it is trusted as is): bundle.tar.gz, order,
# order.sig, operation. Everything is copied into a root-only folder first, then checked there:
# - the order's signature (ECDSA P-256, SHA-256) against each certificate in <trust-dir>/ca.pem, the master CA the
#   installer copied when an admin ran it as root (never refreshed by an update this helper runs);
# - the order is for this node (<trust-dir>/node-uid) and this bundle (<trust-dir>/rid), and not expired;
# - the bundle has the order's size and SHA-256, and holds the folder spark-serve-agent-<version>-<rid>.
# Then it runs that bundle's own "install-agent.sh upgrade" (which keeps identity and settings and rolls back when the
# new agent does not come up) and writes <spool>/result for the agent: operation, exit code, version, and the
# installer's last lines.
set -uo pipefail
PATH=/usr/sbin:/usr/bin:/sbin:/bin

# The upgrade reinstalls this very file while bash still reads it: run from a private copy.
if [[ -z "${SPARK_SERVE_HELPER_COPY:-}" ]]; then
    copy="$(mktemp "${TMPDIR:-/tmp}/spark-serve-update-helper.XXXXXX")" || exit 1
    cp "$0" "${copy}" && chmod 0700 "${copy}"
    SPARK_SERVE_HELPER_COPY="${copy}" exec bash "${copy}" "$@"
fi
trap 'rm -f "${SPARK_SERVE_HELPER_COPY}"' EXIT

SPOOL="${1:?spool}"
TRUST="${2:?trust dir}"
AGENT_USER="${3:?agent user}"
WORK=""
version=""
op=""

lock="${TRUST}/.running"
# A lock left by a helper that was killed (power loss) expires after two hours.
if [[ -d "${lock}" ]] && [[ -n "$(find "${lock}" -maxdepth 0 -mmin +120 2>/dev/null)" ]]; then rmdir "${lock}" 2>/dev/null || true; fi
mkdir "${lock}" 2>/dev/null || exit 0 # an update is already running
cleanup() { if [[ -n "${WORK}" ]]; then rm -rf "${WORK}"; fi; rmdir "${lock}" 2>/dev/null || true; rm -f "${SPARK_SERVE_HELPER_COPY}"; }
trap cleanup EXIT

[[ -f "${SPOOL}/order.sig" ]] || exit 0
if [[ ! -f "${SPOOL}/order" ]]; then rm -f "${SPOOL}/order.sig"; exit 0; fi # a signature without its order: consume it
WORK="$(mktemp -d "${TMPDIR:-/tmp}/spark-serve-update.XXXXXX")" || exit 1
chmod 0700 "${WORK}"
op="$(head -c 200 "${SPOOL}/operation" 2>/dev/null | tr -dc 'A-Za-z0-9._:-')"

# Writes the result for the agent and consumes the order (so the path unit does not fire for it again).
report() {
    local rc="$1"
    shift
    {
        echo "operation=${op}"
        echo "exit=${rc}"
        echo "version=${version}"
        echo "log:"
        printf '%s\n' "$@"
        if [[ -f "${WORK}/install.log" ]]; then tail -n 40 "${WORK}/install.log"; fi
    } >"${SPOOL}/result.part"
    chown "${AGENT_USER}" "${SPOOL}/result.part" 2>/dev/null || true
    mv -f "${SPOOL}/result.part" "${SPOOL}/result"
    rm -f "${SPOOL}/order.sig"
    exit 0
}

sha256_of() {
    if command -v sha256sum >/dev/null; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi
}

cp "${SPOOL}/order" "${WORK}/order" && cp "${SPOOL}/order.sig" "${WORK}/order.sig" || report 2 "The order could not be read."
openssl base64 -d -A -in "${WORK}/order.sig" -out "${WORK}/sig" 2>/dev/null || report 2 "The order's signature is not base64."

# Each certificate of the trusted CA file in turn (two during a CA rotation).
[[ -s "${TRUST}/ca.pem" ]] || report 2 "No trusted CA at ${TRUST}/ca.pem: re-run the installer's upgrade as root."
awk -v dir="${WORK}" '/-----BEGIN CERTIFICATE-----/{n++} n{print > (dir "/ca" n ".pem")}' "${TRUST}/ca.pem"
verified=0
for cert in "${WORK}"/ca*.pem; do
    openssl x509 -pubkey -noout -in "${cert}" >"${WORK}/pub.pem" 2>/dev/null || continue
    if openssl dgst -sha256 -verify "${WORK}/pub.pem" -signature "${WORK}/sig" "${WORK}/order" >/dev/null 2>&1; then
        verified=1
        break
    fi
done
((verified)) || report 3 "The order is not signed by this node's master: refused."

# The order: a header line, then key=value lines (AgentUpdateOrder.Format).
[[ "$(head -n 1 "${WORK}/order")" == "spark-serve-agent-update 1" ]] || report 3 "The order has an unknown format."
node_uid="" rid="" sha="" size="" expires=""
while IFS='=' read -r key value; do
    case "${key}" in
        node-uid) node_uid="${value}" ;;
        rid) rid="${value}" ;;
        version) version="${value}" ;;
        sha256) sha="${value}" ;;
        size) size="${value}" ;;
        expires) expires="${value}" ;;
    esac
done < <(tail -n +2 "${WORK}/order")
[[ "${node_uid}" == "$(cat "${TRUST}/node-uid" 2>/dev/null)" ]] || report 3 "The order is for another node."
[[ "${rid}" == "$(cat "${TRUST}/rid" 2>/dev/null)" ]] || report 3 "The order is for ${rid}, not this node's bundle."
[[ "${version}" =~ ^[A-Za-z0-9._-]{1,64}$ ]] || report 3 "The order names an invalid version."
[[ "${sha}" =~ ^[0-9a-f]{64}$ && "${size}" =~ ^[0-9]{1,12}$ && "${expires}" =~ ^[0-9]{1,12}$ ]] || report 3 "The order is incomplete."
((expires > $(date +%s))) || report 3 "The order expired; start the update again from the master."

bundle="${SPOOL}/bundle.tar.gz"
[[ -f "${bundle}" && ! -L "${bundle}" ]] || report 4 "The bundle is missing."
cp "${bundle}" "${WORK}/bundle.tar.gz" || report 4 "The bundle could not be read."
[[ "$(wc -c <"${WORK}/bundle.tar.gz" | tr -d ' ')" == "${size}" ]] || report 4 "The bundle's size does not match the order."
[[ "$(sha256_of "${WORK}/bundle.tar.gz")" == "${sha}" ]] || report 4 "The bundle's SHA-256 does not match the order."

mkdir "${WORK}/x"
tar -xzf "${WORK}/bundle.tar.gz" -C "${WORK}/x" || report 4 "The bundle could not be unpacked."
folder="${WORK}/x/spark-serve-agent-${version}-${rid}"
[[ -f "${folder}/install-agent.sh" && ! -L "${folder}/install-agent.sh" ]] || report 4 "The bundle has no spark-serve-agent-${version}-${rid}/install-agent.sh."

# The bundle's own upgrade. SPARK_SERVE_UPDATE_HELPER tells it to keep the trusted CA as it is.
SPARK_SERVE_UPDATE_HELPER=1 bash "${folder}/install-agent.sh" upgrade >"${WORK}/install.log" 2>&1
rc=$?
if ((rc == 0)); then
    report 0 "Installed ${version}."
else
    report "${rc}" "The installer failed (it rolls back to the previous agent when the new one does not come up)."
fi
