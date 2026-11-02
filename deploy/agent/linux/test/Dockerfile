# Throwaway "Linux PC" for the install-agent.sh dry run (deploy/agent/linux/test/dry-run.sh): Ubuntu 24.04 with systemd
# as PID 1, systemd-logind (lingering), a Docker engine (the docker group and socket the agent uses), ufw, ss and
# shellcheck. The .NET SDK base also runs the dev master the agent enrolls with. Never used for a real node.
ARG BASE=mcr.microsoft.com/dotnet/sdk:10.0
FROM ${BASE}
ENV DEBIAN_FRONTEND=noninteractive container=docker
RUN apt-get update -qq && \
    apt-get install -y -qq --no-install-recommends systemd systemd-sysv dbus dbus-user-session libpam-systemd \
        docker.io iptables ufw iproute2 shellcheck jq sudo curl ca-certificates >/dev/null && \
    rm -rf /var/lib/apt/lists/* && \
    systemctl mask getty.target systemd-firstboot.service systemd-resolved.service >/dev/null 2>&1 || true
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
