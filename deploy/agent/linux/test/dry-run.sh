#!/usr/bin/env bash
# Container dry run of install-agent.sh (MN-103): a throwaway "Linux PC" (Dockerfile here: Ubuntu 24.04, systemd as
# PID 1, logind, a Docker engine, ufw) where the real script installs, enrolls with a dev master, upgrades, rolls back,
# uninstalls, reinstalls from a bundle and purges, checked after every step. Changes nothing on the host but Docker
# objects (image mirbadllm-agent-dryrun, container mirbadllm-agent-dryrun, volumes mirbadllm-agent-dryrun-*).
#
#   dotnet build MirbadLLM.slnx                     # the dev master runs from artifacts/bin/MirbadLLM.Web/debug
#   deploy/agent/linux/test/dry-run.sh [--keep] [--clean]
#
# --keep leaves the container running for inspection (docker exec -it mirbadllm-agent-dryrun bash); --clean also
# removes the volumes that cache the nested Docker's images. BASE_IMAGE / SDK_IMAGE override the .NET SDK image
# (default: mcr.microsoft.com/dotnet/sdk:10.0). Needs a privileged container (systemd and the nested Docker engine).
set -euo pipefail

NAME=mirbadllm-agent-dryrun
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---------------------------------------------------------------- host side ----
host_main() {
    local keep=0 clean=0 repo mount context
    for arg in "$@"; do
        case "${arg}" in
            --keep) keep=1 ;;
            --clean) clean=1 ;;
            *) echo "unknown argument ${arg}" >&2; exit 2 ;;
        esac
    done
    repo="$(cd "${HERE}/../../../.." && pwd)"
    [[ -f "${repo}/artifacts/bin/MirbadLLM.Web/debug/MirbadLLM.Web.dll" ]] ||
        { echo "build first: dotnet build MirbadLLM.slnx (the dev master runs from artifacts/bin)" >&2; exit 1; }
    # Docker Desktop on Windows (Git Bash) wants D:/... paths and no MSYS path conversion.
    mount="$(cd "${repo}" && (pwd -W 2>/dev/null || pwd))"
    context="$(cd "${HERE}" && (pwd -W 2>/dev/null || pwd))"
    export MSYS_NO_PATHCONV=1

    echo "==> Building ${NAME}"
    docker build -q -t "${NAME}" ${BASE_IMAGE:+--build-arg "BASE=${BASE_IMAGE}"} "${context}" >/dev/null
    docker rm -f "${NAME}" >/dev/null 2>&1 || true
    # /var/lib/docker and /var/lib/containerd on volumes: the nested engine cannot use overlayfs on overlayfs.
    docker run -d --privileged --cgroupns=private --name "${NAME}" --hostname pc-dryrun \
        -v "${NAME}-docker:/var/lib/docker" -v "${NAME}-containerd:/var/lib/containerd" \
        -v "${mount}:/src:ro" ${SDK_IMAGE:+-e "SDK_IMAGE=${SDK_IMAGE}"} "${NAME}" >/dev/null
    local rc=0
    docker exec ${SDK_IMAGE:+-e "SDK_IMAGE=${SDK_IMAGE}"} "${NAME}" bash /src/deploy/agent/linux/test/dry-run.sh --inside || rc=$?
    if ((keep)); then
        echo "==> Kept: docker exec -it ${NAME} bash   (remove: docker rm -f ${NAME})"
    else
        docker rm -f "${NAME}" >/dev/null
    fi
    if ((clean)); then
        docker volume rm "${NAME}-docker" "${NAME}-containerd" >/dev/null
    fi
    if ((rc == 0)); then echo "==> DRY RUN PASSED"; else echo "==> DRY RUN FAILED (${rc})" >&2; fi
    exit "${rc}"
}

# ----------------------------------------------------------- container side ----
SCRIPT=/src/deploy/agent/linux/install-agent.sh
MASTER=http://127.0.0.1:5080
JOIN_FILE=/var/tmp/dev-master/join/pc-1.join.json
BUILD=/var/tmp/agent-build
UNIT=mirbadllm-agent.service
USER_NAME=mirbadllm-agent

pass() { echo "  ok   $*"; }
fail() {
    echo "  FAIL $*" >&2
    systemctl --no-pager status "${UNIT}" >&2 || true
    journalctl -u "${UNIT}" --no-pager -n 30 >&2 || true
    exit 1
}
check() { # <description> <command...>
    local what="$1"
    shift
    if "$@" >/dev/null 2>&1; then pass "${what}"; else fail "${what}"; fi
}
check_not() {
    local what="$1"
    shift
    if "$@" >/dev/null 2>&1; then fail "${what}"; else pass "${what}"; fi
}
expect_exit() { # <code> <description> <command...>
    local want="$1" what="$2" got=0
    shift 2
    "$@" >/tmp/out.txt 2>&1 || got=$?
    [[ "${got}" == "${want}" ]] || { cat /tmp/out.txt >&2; fail "${what} (exit ${got}, expected ${want})"; }
    pass "${what} (exit ${want})"
}
run_step() { echo "==> $*"; }
mode_of() { stat -c '%a %U:%G' "$1"; }
listening() { [[ -n "$(ss -Hltn 'sport = :9443')" ]]; }
binary_sum() { sha256sum "$1" | cut -d' ' -f1; }
# grep without -q in pipelines: an early exit would SIGPIPE the producer and fail the pipeline (pipefail).
ufw_has() { ufw status | grep "9443/tcp.*ALLOW.*$1" >/dev/null; }
master_saw_online() { journalctl -u dev-master --no-pager | grep 'Node pc-1: .*-> Online' >/dev/null; }

inside_main() {
    cd /tmp
    run_step "Static checks"
    check "shellcheck install-agent.sh dry-run.sh" shellcheck -x "${SCRIPT}" /src/deploy/agent/linux/test/dry-run.sh
    check "bash -n install-agent.sh" bash -n "${SCRIPT}"

    run_step "Dev master (Development, dev join-token helper for pc-1)"
    mkdir -p /var/tmp/dev-master
    # The Windows build's static web asset manifest names Windows paths: the UI is not needed here.
    systemd-run --quiet --unit=dev-master -E ASPNETCORE_ENVIRONMENT=Development -E DOTNET_ENVIRONMENT=Development \
        -E ASPNETCORE_STATICWEBASSETS=/nonexistent.json --working-directory=/src/src/MirbadLLM.Web \
        dotnet /src/artifacts/bin/MirbadLLM.Web/debug/MirbadLLM.Web.dll --urls "${MASTER}" \
        --MirbadLLM:DataPath=/var/tmp/dev-master/data --MirbadLLM:Backup:Directory=/var/tmp/dev-master/backups \
        --MirbadLLM:HostAgentSocket=/var/tmp/dev-master/no-hostd.sock --MirbadLLM:Enrollment:BootstrapEnabled=false \
        --MirbadLLM:Enrollment:BootstrapAgentAddress=https://127.0.0.1:9999 \
        --MirbadLLM:DevJoinTokens:Nodes:0=pc-1 --MirbadLLM:DevJoinTokens:OutputDirectory=/var/tmp/dev-master/join
    for _ in $(seq 1 90); do
        if curl -fsS "${MASTER}/healthz" >/dev/null 2>&1 && [[ -f "${JOIN_FILE}" ]]; then break; fi
        sleep 1
    done
    check "master healthy and join token for pc-1 written" test -f "${JOIN_FILE}"
    ufw --force enable >/dev/null
    check "ufw active" bash -c 'ufw status | grep -q "Status: active"'

    run_step "Argument and permission checks (nothing installed)"
    expect_exit 1 "refused without root" runuser -u nobody -- bash "${SCRIPT}" install --node-id pc-1
    expect_exit 2 "refused: token on the command line" bash "${SCRIPT}" install --node-id pc-1 --token x
    expect_exit 2 "refused: bad node id" bash "${SCRIPT}" install --node-id PC_1
    expect_exit 2 "refused: bad models root" bash "${SCRIPT}" install --node-id pc-1 --models-root 'rel/models'
    expect_exit 2 "refused: enroll url without ca hash" bash "${SCRIPT}" install --node-id pc-1 --enroll-url https://x:8443
    expect_exit 1 "refused: upgrade before install" bash "${SCRIPT}" upgrade
    check_not "no user created by the refusals" id -u "${USER_NAME}"
    check "render-config fills the node id and roots" bash -c \
        "bash '${SCRIPT}' render-config --node-id pc-9 --models-root /data/m --cache-root /data/c | jq -e '.Agent.NodeId == \"pc-9\" and .Agent.ModelsRoot == \"/data/m\" and .Agent.ModelsHostPath == \"/data/m\" and .Agent.CacheRoot == \"/data/c\"'"

    run_step "install (not enrolled; publishes the agent from /src with ${SDK_IMAGE:-the default SDK image})"
    "${SCRIPT}" install --node-id pc-1 --firewall-from 192.0.2.191 --firewall-from 10.42.0.0/16 2>&1 |
        grep -v '^  Determining\|Restored ' || fail "install"
    local uid
    uid="$(id -u "${USER_NAME}")"
    check "system user without login" bash -c "getent passwd ${USER_NAME} | grep -q ':/nonexistent:/usr/sbin/nologin$'"
    check "user in the docker group" bash -c "id -nG ${USER_NAME} | tr ' ' '\n' | grep -qx docker"
    check "lingering enabled" test -f "/var/lib/systemd/linger/${USER_NAME}"
    check "the user manager runs (user@${uid})" systemctl is-active "user@${uid}.service"
    check "binary root:root 0755" test "$(mode_of /usr/local/lib/mirbadllm-agent/mirbadllm-agent)" = "755 root:root"
    check "agent.json root:${USER_NAME} 0640" test "$(mode_of /etc/mirbadllm-agent/agent.json)" = "640 root:${USER_NAME}"
    check "agent.json is for pc-1" jq -e '.Agent.NodeId == "pc-1"' /etc/mirbadllm-agent/agent.json
    check "state dir 0700 ${USER_NAME}" test "$(mode_of /var/lib/mirbadllm-agent)" = "700 ${USER_NAME}:${USER_NAME}"
    check "models root owned by the agent" test "$(mode_of /srv/mirbadllm/models)" = "755 ${USER_NAME}:${USER_NAME}"
    check "drop-in lists the roots" grep -qx 'ReadWritePaths=-/srv/mirbadllm/models -/srv/mirbadllm/cache' \
        "/etc/systemd/system/${UNIT}.d/10-install.conf"
    check "systemd-analyze verify is clean" bash -c "[[ -z \"\$(systemd-analyze verify ${UNIT} 2>&1)\" ]]"
    check "ufw allows 9443 from the master" ufw_has 192.0.2.191
    check "ufw allows 9443 from the cluster subnet" ufw_has 10.42.0.0/16
    check_not "not started while not enrolled" systemctl is-active "${UNIT}"
    systemctl start "${UNIT}"
    check "a start is skipped by the condition, no crash loop" \
        test "$(systemctl show -p ConditionResult --value "${UNIT}")/$(systemctl show -p NRestarts --value "${UNIT}")" = "no/0"
    mkdir -p "${BUILD}"
    cp /usr/local/lib/mirbadllm-agent/mirbadllm-agent /usr/local/lib/mirbadllm-agent/BUILD-INFO "${BUILD}/"
    check "BUILD-INFO records the revision" grep -q '^revision=' "${BUILD}/BUILD-INFO"

    run_step "install with the join parameters (the documented sudo --preserve-env form)"
    MIRBADLLM_JOIN_TOKEN="$(jq -r .token "${JOIN_FILE}")" sudo --preserve-env=MIRBADLLM_JOIN_TOKEN \
        "${SCRIPT}" install --published-dir "${BUILD}" --enroll-url "$(jq -r .enrollUrl "${JOIN_FILE}")" \
        --ca-hash "$(jq -r .caHash "${JOIN_FILE}")" --address https://127.0.0.1:9443 || fail "install with join"
    check "enrolled: agent.crt and node-uid" test -s /var/lib/mirbadllm-agent/node-uid -a -f /var/lib/mirbadllm-agent/agent.crt
    check "agent.key 0600 ${USER_NAME}" test "$(mode_of /var/lib/mirbadllm-agent/agent.key)" = "600 ${USER_NAME}:${USER_NAME}"
    check "service enabled and active" systemctl is-active "${UNIT}"
    check "listening on 9443" listening
    for _ in $(seq 1 60); do
        if master_saw_online; then break; fi
        sleep 1
    done
    check "the master reaches the agent over mTLS (pc-1 Online)" master_saw_online
    check "no Information-level HttpClient lines in the journal" bash -c \
        "! journalctl -u ${UNIT} --no-pager | grep -q 'System.Net.Http.HttpClient'"
    echo "  info $(systemd-analyze security "${UNIT}" --no-pager | tail -n 1)"
    check "the agent ran inside its sandbox (NoNewPrivs, seccomp)" bash -c \
        "grep -q '^NoNewPrivs:[[:space:]]*1' /proc/\$(systemctl show -p MainPID --value ${UNIT})/status && grep -q '^Seccomp:[[:space:]]*2' /proc/\$(systemctl show -p MainPID --value ${UNIT})/status"
    local good
    good="$(binary_sum /usr/local/lib/mirbadllm-agent/mirbadllm-agent)"

    run_step "install again with the join parameters: enrolled already, join skipped"
    MIRBADLLM_JOIN_TOKEN=unused "${SCRIPT}" install --published-dir "${BUILD}" \
        --enroll-url https://127.0.0.1:8443 --ca-hash "$(jq -r .caHash "${JOIN_FILE}")" >/tmp/out.txt 2>&1 || fail "re-install"
    check "join skipped with a warning" grep -q 'enrolled already: join skipped' /tmp/out.txt
    check "still listening" listening

    run_step "upgrade (same build)"
    "${SCRIPT}" upgrade --published-dir "${BUILD}" || fail "upgrade"
    check "previous binary kept" test -f /usr/local/lib/mirbadllm-agent/mirbadllm-agent.previous
    check "listening after upgrade" listening
    expect_exit 2 "upgrade refuses --node-id" bash "${SCRIPT}" upgrade --node-id pc-2 --published-dir "${BUILD}"

    run_step "upgrade to a binary that exits at once: automatic rollback"
    mkdir -p /var/tmp/broken
    cp /bin/false /var/tmp/broken/mirbadllm-agent
    MIRBADLLM_AGENT_HEALTH_TIMEOUT=60 expect_exit 1 "the broken upgrade fails" "${SCRIPT}" upgrade --published-dir /var/tmp/broken
    check "rolled back message" grep -q 'rolled back to the previous binary' /tmp/out.txt
    check "the good binary is back" test "$(binary_sum /usr/local/lib/mirbadllm-agent/mirbadllm-agent)" = "${good}"
    check "running again after the rollback" systemctl is-active "${UNIT}"
    check "listening after the rollback" listening

    run_step "upgrade refuses a binary for another architecture"
    printf '\177ELF\002\001\001\000\000\000\000\000\000\000\000\000\002\000\267\000' >/var/tmp/broken/mirbadllm-agent
    expect_exit 1 "aarch64 binary refused on x86_64" "${SCRIPT}" upgrade --published-dir /var/tmp/broken
    check "service untouched" listening

    run_step "rollback verb (swaps current and previous)"
    "${SCRIPT}" rollback || fail "rollback"
    check "listening after rollback" listening

    run_step "uninstall (keeps agent.json and the identity)"
    "${SCRIPT}" uninstall || fail "uninstall"
    check_not "unit removed" test -e "/etc/systemd/system/${UNIT}"
    check_not "drop-in removed" test -e "/etc/systemd/system/${UNIT}.d"
    check_not "binary removed" test -e /usr/local/lib/mirbadllm-agent
    check_not "not listening" listening
    check_not "lingering disabled" test -f "/var/lib/systemd/linger/${USER_NAME}"
    check_not "ufw rule for the master removed" ufw_has 192.0.2.191
    check_not "ufw rule for the subnet removed" ufw_has 10.42.0.0/16
    check "agent.json kept" test -f /etc/mirbadllm-agent/agent.json
    check "identity kept" test -f /var/lib/mirbadllm-agent/agent.crt
    check "models root kept" test -d /srv/mirbadllm/models

    run_step "package (as a normal user) and install from the bundle: the kept identity starts without a join"
    chmod 0755 "${BUILD}"
    runuser -u nobody -- bash "${SCRIPT}" package --published-dir "${BUILD}" --output /tmp/agent-bundle.tar.gz || fail "package"
    rm -rf /tmp/bundle && mkdir -p /tmp/bundle && tar -xzf /tmp/agent-bundle.tar.gz -C /tmp/bundle
    local dir
    dir="$(find /tmp/bundle -mindepth 1 -maxdepth 1 -type d)"
    check "bundle holds script, unit, example, README, binary" bash -c \
        "cd '${dir}' && test -x install-agent.sh -a -f ${UNIT} -a -f agent.linux.example.json -a -f README.md -a -x mirbadllm-agent -a -f BUILD-INFO"
    "${dir}/install-agent.sh" install || fail "install from the bundle"
    check "running from the bundle install" systemctl is-active "${UNIT}"
    check "listening from the bundle install" listening
    check "the bundle's binary is installed" test "$(binary_sum /usr/local/lib/mirbadllm-agent/mirbadllm-agent)" = "${good}"

    run_step "uninstall --purge"
    "${SCRIPT}" uninstall --purge || fail "uninstall --purge"
    check_not "agent.json removed" test -e /etc/mirbadllm-agent
    check_not "state removed" test -e /var/lib/mirbadllm-agent
    check_not "user removed" id -u "${USER_NAME}"
    check_not "group removed" getent group "${USER_NAME}"
    check "models root still kept" test -d /srv/mirbadllm/models
    systemctl stop dev-master
}

if [[ "${1:-}" == --inside ]]; then
    inside_main
else
    host_main "$@"
fi
