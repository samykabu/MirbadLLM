# Host services and the host agent

The host agent is optional. It lets the UI manage host daemons and non-model containers next to the models,
and it mounts the NAS share for backups. On Linux it is a small root service on a Unix socket; on macOS the
same program runs as a root LaunchDaemon ([macOS agent guide](../deploy/agent/macos/README.md)); Windows nodes
use the Service Control Manager directly and need no helper. This page covers the Linux host agent.

Edit `deploy/hostd/hostd.json` before the first install: `discoverUsers` names the users whose `--user` units are
discovered, and `services` seeds the whitelist. The shipped file is an example from a DGX Spark.

The Services tab of a node (admin only) manages host daemons and non-model containers. It lists
whitelisted services with start/stop/restart controls, and a discovered list of every systemd service
and Docker container on the box. The host agent (`mirbadllm-hostd`) enforces a compiled-in deny
list (sshd, systemd, D-Bus, Docker, networking, NVIDIA daemons, and so on) that can never be
managed, regardless of what the controller sends.

Install the agent:

```bash
sudo bash deploy/hostd/install-hostd.sh
journalctl -u mirbadllm-hostd -f
```

The script publishes `src/MirbadLLM.HostAgent` self-contained for linux-arm64, installs it to
`/usr/local/lib/mirbadllm-hostd/`, writes the seed config `/etc/mirbadllm/hostd.json` **only if it
does not exist**, and enables `mirbadllm-hostd.service` (with `StateDirectory=mirbadllm-hostd`). On its
first start the agent seeds the admin-managed whitelist `/var/lib/mirbadllm-hostd/whitelist.json`
(root, 0600) from hostd.json, keeping display names and protected flags; after that the page manages it.
`compose.yaml` mounts `/run/mirbadllm-hostd` read-only into the controller (`compose.multinode.yaml` mounts it into
the node agent instead; the master then reaches hostd through the agent). Without the agent the page
still lists containers and shows these install steps. The agent also mounts the NAS share chosen on the Backup
page ([Backup destination](backup-and-restore.md#backup-destination-keep-the-backups-on-a-nas)); reinstall it after pulling a version
that changed `deploy/hostd` or `src/MirbadLLM.HostAgent`, since CI does not.

Security model: the agent listens only on `/run/mirbadllm-hostd/hostd.sock` (directory
`root:docker 0750`, socket `0660`, no TCP), so only the docker group (already root-equivalent) can
reach it. A **deny list compiled into the agent** (sshd, `systemd-*`, D-Bus, Docker/containerd,
networking, polkit, getty, the NVIDIA driver daemons, cron, rsyslog, snapd, AppArmor, ufw, `user@*`, the
agent itself, …; extendable only by a root-owned `/etc/mirbadllm/hostd-deny.json`) can never be managed:
those units show up in Discovered as "System — not manageable" and every action or whitelist-add for them
is refused with 403 by the agent, whatever the controller sends. It acts only on whitelisted units
(unknown names get 404 before anything runs) and runs `/usr/bin/systemctl` with an argument vector (never a
shell; the unit name follows `--`). Adding a service to the whitelist, or unprotecting one, uses the signed-in admin session plus the
service name typed; with `MirbadLLM__AdminPassword` unset, whitelist changes are refused. Protected
services need their name typed to stop, restart or lose autostart, enforced in the UI, the controller API
(`confirm=<name>`) and again in the agent. MirbadLLM itself, model containers and download jobs are never
listed. Details: [architecture.md](architecture.md#services-page-and-host-agent-srcmirbadllmhostagent).
