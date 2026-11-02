#!/usr/bin/env bash
# Builds and installs mirbadllm-hostd (the host agent behind the Services page) on the DGX Spark.
#
#   sudo deploy/hostd/install-hostd.sh        # from a checkout of this repo, on the DGX
#
# - publishes src/MirbadLLM.HostAgent self-contained, single-file, linux-arm64 inside the .NET SDK image
#   (nothing is installed on the host besides the binary)
# - installs it to /usr/local/lib/mirbadllm-hostd/mirbadllm-hostd (root:root 0755)
# - installs the seed config to /etc/mirbadllm/hostd.json only if that file does not exist yet
# - installs + enables + (re)starts mirbadllm-hostd.service (StateDirectory=mirbadllm-hostd)
# Re-running it upgrades the binary and the unit and keeps your config and whitelist.
#
# Migration from the fixed allowlist: /etc/mirbadllm/hostd.json stays where it is and becomes the seed.
# On the first start of this version the agent writes /var/lib/mirbadllm-hostd/whitelist.json (root, 0600)
# from its entries (display names, descriptions, protected flags and risks kept). From then on the Services
# page manages the whitelist; entries added to hostd.json later are merged in once.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
RID="${RID:-linux-arm64}"
SYSTEMD_DIR="${MIRBADLLM_SYSTEMD_DIR:-/etc/systemd/system}" # tests point it elsewhere
# A host agent installed before the rename (SparkServe) keeps its names: re-running this script upgrades it in place
# under its old unit, folders and socket (which the agent container mounts), and keeps its backup mount. Only the binary
# inside is new (docs/setup.md, "Upgrading from a pre-rename install").
NAME=mirbadllm
if [[ ! -e "${SYSTEMD_DIR}/mirbadllm-hostd.service" && -e "${SYSTEMD_DIR}/spark-serve-hostd.service" ]]; then
    NAME=spark-serve
fi
PREFIX="/usr/local/lib/${NAME}-hostd"
CONFIG_DIR="/etc/${NAME}"
UNIT="${NAME}-hostd.service"
SOCKET="/run/${NAME}-hostd/hostd.sock"
STATE_DIR="/var/lib/${NAME}-hostd"
DENY_FILE=${CONFIG_DIR}/hostd-deny.json
# The backup destination's share credentials (Backup & restore page); root-only.
SECRETS_DIR="/etc/${NAME}-hostd"

# The unit for this install: the shipped one, under the pre-rename names for a host agent installed before the rename.
render_unit() {
    if [[ "${NAME}" == mirbadllm ]]; then
        cat "${REPO}/deploy/hostd/mirbadllm-hostd.service"
    else
        sed -e 's|/etc/mirbadllm/|/etc/spark-serve/|g' -e 's|/srv/mirbadllm/|/srv/spark-serve/|g' \
            -e 's/mirbadllm-hostd/spark-serve-hostd/g' "${REPO}/deploy/hostd/mirbadllm-hostd.service"
    fi
}
if [[ "${1:-}" == render-unit ]]; then # prints the unit this host would get (tests)
    render_unit
    exit 0
fi

if [[ ${EUID} -ne 0 ]]; then
    echo "install-hostd.sh must run as root (sudo)." >&2
    exit 1
fi

if ! getent group docker >/dev/null; then
    echo "No 'docker' group on this host; the socket is shared with that group." >&2
    exit 1
fi

OUT="$(mktemp -d)"
trap 'rm -rf "${OUT}"' EXIT

echo "==> Publishing the host agent (${RID}, self-contained, single file) with ${SDK_IMAGE}"
docker run --rm \
    -v "${REPO}:/repo:ro" \
    -v "${OUT}:/out" \
    "${SDK_IMAGE}" \
    bash -euc "
        mkdir -p /build
        cp -r /repo/global.json /repo/Directory.Build.props /repo/Directory.Packages.props /build/
        mkdir -p /build/src && cp -r /repo/src/MirbadLLM.HostAgent /build/src/
        cd /build
        dotnet publish src/MirbadLLM.HostAgent/MirbadLLM.HostAgent.csproj -c Release -r ${RID} \
            --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o /out --nologo -v quiet
    "

if [[ ! -x "${OUT}/mirbadllm-hostd" ]]; then
    echo "Publish did not produce ${OUT}/mirbadllm-hostd." >&2
    exit 1
fi

echo "==> Installing ${PREFIX}/${NAME}-hostd"
install -d -m 0755 -o root -g root "${PREFIX}"
install -m 0755 -o root -g root "${OUT}/mirbadllm-hostd" "${PREFIX}/${NAME}-hostd"

install -d -m 0755 -o root -g root "${CONFIG_DIR}"
if [[ -e "${CONFIG_DIR}/hostd.json" ]]; then
    echo "==> Keeping existing ${CONFIG_DIR}/hostd.json"
else
    echo "==> Installing default allowlist ${CONFIG_DIR}/hostd.json"
    install -m 0644 -o root -g root "${REPO}/deploy/hostd/hostd.json" "${CONFIG_DIR}/hostd.json"
fi

# Holds backup-share.cred once an SMB backup destination is saved on the Backup & restore page. The unit
# grants the agent write access to this directory only (ReadWritePaths); it must exist before the agent starts.
install -d -m 0700 -o root -g root "${SECRETS_DIR}"

# Optional deny-list extension: it can only add denials, but the agent refuses to start if it is not
# root-owned or is writable by group/others, so fix that here rather than failing the restart.
if [[ -e "${DENY_FILE}" ]]; then
    chown root:root "${DENY_FILE}"
    chmod go-w "${DENY_FILE}"
    echo "==> Using extra deny patterns from ${DENY_FILE}"
fi

if [[ -e "${STATE_DIR}/whitelist.json" ]]; then
    echo "==> Keeping the whitelist ${STATE_DIR}/whitelist.json"
else
    echo "==> No whitelist yet: the agent seeds ${STATE_DIR}/whitelist.json from ${CONFIG_DIR}/hostd.json on start"
fi

# The socket directory exists from boot on, before Docker starts the controller that bind-mounts it.
echo "d /run/${NAME}-hostd 0750 root docker -" > "/etc/tmpfiles.d/${NAME}-hostd.conf"
chmod 0644 "/etc/tmpfiles.d/${NAME}-hostd.conf"
systemd-tmpfiles --create "/etc/tmpfiles.d/${NAME}-hostd.conf"

echo "==> Installing and starting ${UNIT}"
render_unit >"${OUT}/${UNIT}"
install -m 0644 -o root -g root "${OUT}/${UNIT}" "${SYSTEMD_DIR}/${UNIT}"
systemctl daemon-reload
systemctl enable "${UNIT}" >/dev/null
systemctl restart "${UNIT}"

for _ in $(seq 1 30); do
    [[ -S "${SOCKET}" ]] && break
    sleep 0.5
done

if [[ ! -S "${SOCKET}" ]]; then
    echo "The agent did not create ${SOCKET}. Check: journalctl -u ${UNIT} -n 50" >&2
    exit 1
fi

stat -c '==> %n %U:%G %a' "/run/${NAME}-hostd" "${SOCKET}"
if [[ -e "${STATE_DIR}/whitelist.json" ]]; then
    stat -c '==> %n %U:%G %a' "${STATE_DIR}" "${STATE_DIR}/whitelist.json"
    if command -v python3 >/dev/null; then
        python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print("==> whitelist:", ", ".join(s["name"] for s in d.get("services", [])))' "${STATE_DIR}/whitelist.json"
    fi
fi
if command -v curl >/dev/null; then
    curl -fsS --unix-socket "${SOCKET}" http://hostd/healthz && echo
fi
echo "==> Done. Logs: journalctl -u ${UNIT}"
