# mirbadllm-agent on a Linux PC (systemd)

A Linux PC joins the fleet as a node: the agent runs as the systemd service `mirbadllm-agent`, the master dials it
on TCP 9443 (mTLS) and it starts model containers through the local Docker engine (design D7, §2.2, §6.6). The PC can
have NVIDIA GPUs, no usable GPU at all (a CPU-only node), or an AMD GPU used through Vulkan (experimental); see
[Nodes without an NVIDIA GPU](#nodes-without-an-nvidia-gpu). The DGX does not use this: its agent is a container
(`deploy/compose.multinode.yaml`).

## Requirements

- systemd 247 or newer (Ubuntu 22.04+, Debian 12+), x86_64 or aarch64, OpenSSL (`libssl3`).
- Docker Engine (the `docker` group must exist).
- For an NVIDIA node: the NVIDIA Container Toolkit and the NVIDIA driver (`nvidia-smi -L` lists the GPUs). When
  `nvidia-smi` lists no GPU, the script installs the machine as a CPU-only node and says so; it no longer treats a
  missing driver as a problem to warn about.
- `curl` and `sudo` for the master's join command. The master serves ready-built bundles, so the PC needs no
  checkout and no Docker build. Without them: Docker and a mirbadllm checkout (the script builds the binary), a
  bundle made with `package`, or `--published-dir`.

## Install and join

On the master, **Add node** shows the join command, the enroll URL, the CA hash and a one-time token. The master image
carries a bundle (see [Bundles](#bundles)) for each architecture, so the command is a single paste on a clean PC with
`curl` and `sudo`, run in any folder. It is one line (wrapped here):

```bash
(set -e; d="$(mktemp -d)"; trap 'rm -rf "$d"' EXIT; cd "$d"; case "$(uname -m)" in
  x86_64|amd64) u='http://<master>:8080/agent/bundles/linux-x64.tar.gz' h=<sha256> ;;
  aarch64|arm64) u='http://<master>:8080/agent/bundles/linux-arm64.tar.gz' h=<sha256> ;;
  *) echo "the master has no mirbadllm agent for $(uname -m)" >&2; exit 1 ;; esac;
  curl -fsSL -o agent.tar.gz "$u"; echo "$h  agent.tar.gz" | sha256sum -c -; tar -xzf agent.tar.gz;
  sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./mirbadllm-agent-*/install-agent.sh install --node-id pc-1 \
    --enroll-url https://<master>:8443 --ca-hash sha256:<hash>)
```

It downloads the bundle for this PC's architecture into a temporary folder, checks it against the SHA-256 the master
put in the command (`sha256sum -c`, so plain HTTP is fine), unpacks it, runs the bundle's `install-agent.sh install`
and removes the folder. The script asks for the token (or takes an exported `MIRBADLLM_JOIN_TOKEN`). Append
`--address <this-pc-ip> --firewall-from <master-ip>` before the closing `)`.

From a checkout or an unpacked bundle, the same install is:

```bash
export MIRBADLLM_JOIN_TOKEN='<token>'   # or leave it unset and paste the token at the prompt
sudo --preserve-env=MIRBADLLM_JOIN_TOKEN deploy/agent/linux/install-agent.sh install --node-id pc-1 \
    --enroll-url https://<master>:8443 --ca-hash sha256:<hash> \
    --address <this-pc-ip> --firewall-from <master-ip>
```

The token is read from the environment or the prompt only, never from the command line (it would show in `ps`).
The script:

| What | Where |
|---|---|
| system user `mirbadllm-agent` (no login, no home), in `docker`, lingering on | `/etc/passwd`, `/var/lib/systemd/linger/` |
| binary (self-contained, single file); previous one kept for rollback | `/usr/local/lib/mirbadllm-agent/` (root, 0755) |
| configuration, from `agent.linux.example.json` with the node id and roots | `/etc/mirbadllm-agent/agent.json` (root:mirbadllm-agent 0640) |
| identity (key 0600), fencing state, operation log | `/var/lib/mirbadllm-agent/` (0700) |
| unit, and the drop-in with the model/cache roots (`ReadWritePaths`) | `/etc/systemd/system/mirbadllm-agent.service{,.d/10-install.conf}` |
| model and cache folders (`--models-root`, `--cache-root`) | default `/srv/mirbadllm/{models,cache}` |

It then enrolls (`mirbadllm-agent join`, as the service user), starts the service and waits until it listens on
9443. Approve the node on the master's Nodes page after comparing the key fingerprint the join printed. Without the
join parameters the service is installed but not started: the unit is skipped until `agent.crt` and `node-uid` exist.

Logs: `journalctl -u mirbadllm-agent`. Status: `systemctl status mirbadllm-agent`.

## Nodes without an NVIDIA GPU

`--accelerator` decides how the node runs models. The script writes the choice to `agent.json` as `Agent:Accelerator`
when it creates the file, and the agent reports it to the master.

| `--accelerator` | When | What the node does |
|---|---|---|
| `auto` (default) | | `cuda` when `nvidia-smi -L` lists a GPU, otherwise `cpu`. It never picks `vulkan`. |
| `cuda` | NVIDIA GPUs | As before: no `Accelerator` entry in `agent.json`, GPUs through the NVIDIA Container Toolkit. |
| `cpu` | No usable GPU | llama.cpp on the CPU. Containers are created without a GPU request. |
| `vulkan` | An AMD GPU | llama.cpp through Vulkan, one GPU per model. Experimental. |

An existing `agent.json` keeps its entry. After you add or remove a GPU, edit `"Accelerator"` in the `Agent` section
(`"cpu"`, `"vulkan"`, or no entry for NVIDIA) and run `sudo systemctl restart mirbadllm-agent`.

### CPU-only nodes

A CPU-only node runs llama.cpp and nothing else: vLLM and SGLang need an NVIDIA GPU, so the node does not list them
and the master refuses to place such a model there. Three things differ from an NVIDIA node:

- **The image.** Use the CPU image `ghcr.io/ggml-org/llama.cpp:server`, not `server-cuda`. A PC node only starts
  images you approved by digest, so read the digest of the tag you pulled
  (`docker pull ghcr.io/ggml-org/llama.cpp:server`, then
  `docker inspect --format '{{index .RepoDigests 0}}' ghcr.io/ggml-org/llama.cpp:server`), add
  `ghcr.io/ggml-org/llama.cpp@sha256:<digest>` to `ApprovedImages` in `agent.json`, restart the agent, and set the
  node's **Default llama.cpp image** and **Approved llama.cpp image digest** on the master's Nodes page.
- **Memory.** A model's whole budget is counted against host RAM, minus the node's memory reserve, and one model
  starts at a time. The **Add model** wizard asks for no GPU. **GPU layers** has no effect: a llama.cpp build
  without a GPU backend prints a warning and ignores it.
- **Downloads.** The agent downloads from Hugging Face itself, so the node needs no vLLM image for the download
  helper. `DownloadHelperImage` is unused.

The node's GPU figures stay empty, because there is no GPU to read. The speed of llama.cpp on a CPU has not been
measured for this project; benchmark the model on the node before you rely on it.

### Vulkan nodes (experimental)

This path has not been run on AMD hardware. It is built and tested against sysfs trees and Docker requests written by
the tests, and it is off unless you pass `--accelerator vulkan`. What it does:

- **GPUs.** The agent lists the cards whose driver publishes VRAM counters in sysfs
  (`/sys/class/drm/card*/device/mem_info_vram_total` and `mem_info_vram_used`, the `amdgpu` driver). A card is
  identified by its PCI address. Intel GPUs are not listed on Linux: their drivers publish no system-wide VRAM figure
  there, and a node offers only GPUs whose free memory it can read.
- **Admission.** Free VRAM is the driver's total minus the driver's used figure, which covers every process. When
  the used figure cannot be read, the GPU shows "—", the node is Degraded with the reason, and a start on that GPU is
  refused unless you force it.
- **Containers.** Use the image `ghcr.io/ggml-org/llama.cpp:server-vulkan`, approved by digest as described above.
  The agent maps the card's DRM render node (`/dev/dri/renderD*`) into the container; the NVIDIA runtime is not
  involved. A model gets one GPU: the agent cannot fix the order in which the container lists several cards, so it
  refuses a placement on two.
- **Telemetry.** VRAM total, used and free only. Utilization, power and temperature are not read and show "—".

## Firewall (TCP 9443)

The master opens every connection; the agent accepts TCP 9443 and authenticates the master by its client
certificate. Allow 9443 from the master only: the DGX's address, or the cluster node subnet when the master runs in
Kubernetes (pod egress leaves with a node IP).

```bash
sudo ufw allow proto tcp from 192.0.2.191 to any port 9443                         # ufw
sudo firewall-cmd --permanent --add-rich-rule='rule family=ipv4 source address=192.0.2.191 port port=9443 protocol=tcp accept' \
  && sudo firewall-cmd --reload                                                       # firewalld
sudo nft add rule inet filter input ip saddr 192.0.2.191 tcp dport 9443 accept      # plain nftables
```

`--firewall-from <ip|cidr>` (repeatable) adds the rule in ufw or firewalld when one is active, and `uninstall`
removes it again. Nothing else needs to be open: models are reached by the agent on loopback ports.

## Updates from the master

Every install, upgrade and rekey also sets up the **update helper** (it needs `openssl`). This lets the portal update
the agent: **Update now** on the node's page, after the admin password.

- The agent downloads its bundle from the master and checks its SHA-256.
- The root helper (`mirbadllm-agent-update.path`, which starts `mirbadllm-agent-update.service`) checks the order
  the master signed against the master CA saved in `/etc/mirbadllm-agent/update`, then runs the bundle's own
  `install-agent.sh upgrade` (which rolls back if the new agent does not come up).
- The agent never gets root, and cannot change which CA the helper trusts: only an install, upgrade or rekey you run
  saves it.
- After a CA rotation, upgrade or rekey each node once by hand.

## Upgrade, rollback, uninstall

An agent installed before the project's rename is upgraded in place and keeps its old service and folder names; see "Upgrading from a pre-rename install" in [docs/setup.md](../../../docs/setup.md).

```bash
git pull && sudo deploy/agent/linux/install-agent.sh upgrade     # new binary and unit; agent.json and identity kept
sudo deploy/agent/linux/install-agent.sh rollback                # back to the previous binary and unit
sudo deploy/agent/linux/install-agent.sh uninstall               # keeps agent.json, the identity, model folders
sudo deploy/agent/linux/install-agent.sh uninstall --purge       # also deletes agent.json, the identity, the user
```

An upgrade restarts the agent (about 30 s of drain; model containers keep running and are adopted) and rolls back by
itself when the new agent does not listen within `MIRBADLLM_AGENT_HEALTH_TIMEOUT` seconds (default 180). Upgrade
the master first (design §3.6). After changing `ModelsRoot`/`CacheRoot` in `agent.json`, run `upgrade` to rewrite the
drop-in. `uninstall` never removes model folders or containers; revoke the node on the master.

## Re-pair (rekey)

When the node's key or state directory is lost, or its certificate must be replaced, create a rekey token for it on
the master's Nodes page and run, on the PC:

```bash
sudo MIRBADLLM_JOIN_TOKEN='<rekey token>' deploy/agent/linux/install-agent.sh rekey --node-id pc-1 \
    --node-uid <uid> --enroll-url https://<master>:8443 --ca-hash sha256:<hash>      # "--rekey" works as well
```

The script stops the service, runs `mirbadllm-agent join --rekey` as `mirbadllm-agent` (the node keeps its uid;
`--node-uid` is needed only when the state directory is gone), and starts the service on the new identity, waiting
until it listens. If the rekey fails, the identity is left as it was and the service is started again on it. Approve
the node on the Nodes page if it shows as pending, after comparing the fingerprint the rekey printed. The master's
rekey command downloads and checks the bundle as above and ends with
`./mirbadllm-agent-*/install-agent.sh rekey --node-id … --node-uid … --enroll-url … --ca-hash …`.

## Bundles

```bash
deploy/agent/linux/install-agent.sh package [--rid linux-arm64] [--output agent.tar.gz]   # needs Docker
tar -xzf agent.tar.gz && sudo ./mirbadllm-agent-*/install-agent.sh install --node-id pc-1 ...
```

A bundle holds the binary, `install-agent.sh`, the unit, `agent.linux.example.json`, this file and `BUILD-INFO`
(revision, architecture, build time), so the PC needs no checkout.

The master image builds both bundles (`linux-x64`, `linux-arm64`) with this script's `package` verb at image build
time (`deploy/Dockerfile`, stage `agent-bundles`; the bundle is named after the deployed commit). The master serves
them anonymously on its main port:

```bash
curl -s http://<master>:8080/agent/bundles                                       # rid, url, sha256, size, version
curl -fsSL -o agent.tar.gz http://<master>:8080/agent/bundles/linux-x64.tar.gz   # or linux-arm64
```

A master without bundles (a dev run, not the image) answers 404, and its join command expects this script in the
current folder.

## Testing

`deploy/agent/linux/test/dry-run.sh` runs the whole life cycle (install, join with a dev master, upgrade, automatic
rollback, uninstall, bundle install, purge) in a throwaway privileged container with systemd, logind, Docker and ufw;
it also runs `shellcheck` and `systemd-analyze verify`. It touches nothing on the host but Docker objects.
`tests/MirbadLLM.UnitTests/Deploy/LinuxAgentPackagingTests.cs` checks the unit and the script against the agent's code.
