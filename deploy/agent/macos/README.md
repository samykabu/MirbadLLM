# mirbadllm-agent on a Mac (launchd)

An Apple Silicon Mac joins the fleet as a node: the agent runs as the LaunchDaemon `com.mirbadllm.agent` under the
hidden user `_mirbadllm`, the master dials it on TCP 9443 (mTLS), and models run as native processes (no Docker on a
Mac). Plan and decisions: `docs/macos/implementation-plan.md` (M1, M2, M9, Phase 1). `install-agent.sh` is the macOS
port of `deploy/agent/linux/install-agent.sh`, with the same verbs and arguments.

## Requirements

- An Apple Silicon Mac (M1 or newer) with macOS 13 or newer. Intel Macs are refused.
- An administrator account (`sudo`), and `curl` for the master's join command. The master serves a ready-built
  bundle, so the Mac needs no checkout, no .NET and no Homebrew.

## Install and join

On the master, **Add node** shows the macOS join command, the enroll URL, the CA hash and a one-time token. It
downloads `http://<master>:8080/agent/bundles/osx-arm64.tar.gz` into a temporary folder, checks it against the
SHA-256 in the command (so plain HTTP is fine), unpacks it and runs:

```bash
sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./mirbadllm-agent-*/install-agent.sh install --node-id mac-1 \
    --enroll-url https://<master>:8443 --ca-hash sha256:<hash>
```

The script asks for the token (or takes an exported `MIRBADLLM_JOIN_TOKEN`). It is read from the environment or the
prompt only, never from the command line (it would show in `ps`). Add `--address <this-mac-ip>` when the master
should dial a particular address, and `--allow-firewall` when the macOS firewall is on (see below).

From a checkout or an unpacked bundle, the same install is:

```bash
export MIRBADLLM_JOIN_TOKEN='<token>'   # or leave it unset and paste the token at the prompt
sudo --preserve-env=MIRBADLLM_JOIN_TOKEN deploy/agent/macos/install-agent.sh install --node-id mac-1 \
    --enroll-url https://<master>:8443 --ca-hash sha256:<hash> --address <this-mac-ip>
```

What the script sets up:

| What | Where |
|---|---|
| hidden system user and group `_mirbadllm` (uid/gid below 500, no shell, home `/var/empty`) | Directory Services (`dscl`) |
| binary (self-contained, single file, ad hoc signed); previous one kept for rollback | `/usr/local/lib/mirbadllm-agent/` (root:wheel, 0755) |
| configuration, from `agent.macos.example.json` with the node id and roots | `/Library/Application Support/mirbadllm-agent/agent.json` (root:_mirbadllm 0640) |
| identity (key 0600), fencing state, operation log | `/Library/Application Support/mirbadllm-agent/state/` (_mirbadllm, 0700) |
| LaunchDaemon (`RunAtLoad`, `KeepAlive`, `AbandonProcessGroup`) | `/Library/LaunchDaemons/com.mirbadllm.agent.plist` (root:wheel 0644) |
| log (stdout and stderr of the agent) | `/Library/Logs/mirbadllm-agent/agent.log` (readable by admins) |
| model and cache folders (`--models-root`, `--cache-root`), owned by `_mirbadllm` | default `/Users/Shared/mirbadllm/{models,cache}` |

It then enrolls (`mirbadllm-agent join`, run as `_mirbadllm`), installs and loads the LaunchDaemon
(`launchctl bootstrap system`) and waits until the agent listens on 9443. Approve the node on the master's Nodes page
after comparing the key fingerprint the join printed. Without the join parameters, everything is installed but the
LaunchDaemon is not: launchd would restart an unenrolled agent forever. The binary's code signature is checked
(`codesign --verify`) and renewed ad hoc when it does not verify; the quarantine flag is removed.

Status and logs:

```bash
sudo launchctl print system/com.mirbadllm.agent | head -30
tail -f /Library/Logs/mirbadllm-agent/agent.log
```

The log is not rotated; to empty it: `sudo sh -c ': > /Library/Logs/mirbadllm-agent/agent.log'`.

## llama.cpp (Metal)

`install` and `upgrade` also install the llama.cpp release pinned in `llama-cpp.lock.json` (next to the script and in
the bundle; plan M5, Phase 2). The script downloads `llama-<tag>-bin-macos-arm64.tar.gz` from the release tag with
`curl`, checks its size and SHA-256 against the lock file, and copies only `llama-server`, the dylibs it loads (under
the names it loads them by, e.g. `libggml-metal.0.dylib`; Metal's shaders are built into that dylib) and `LICENSE`
into:

| What | Where |
|---|---|
| one folder per version, no symlinks, nothing else (root:wheel, 0755; dylibs and `llama-server` 0755) | `/usr/local/lib/mirbadllm-agent/runtimes/llama.cpp/<tag>/` |
| `manifest.json` in that folder: SHA-256 and size of every file | written by the script |
| `Agent:Executables` entry `llama-server` with this version as `Default` (other versions are kept) | `agent.json` |

Every copied file is checked against the lock file, every Mach-O file's code signature must verify (the release is
ad hoc signed; the script does not re-sign it, as that would change the pinned bytes), and the quarantine flag is
removed. `llama-server` finds its dylibs through `@rpath` = `@loader_path`, so the folder works as it is, without
`DYLD_*` variables. The agent hashes the folder in the background at start and refuses the version (422
`executable_unverified`) when a file is missing or changed, or when an unlisted `.dylib`, `.so` or Mach-O file appears
in it. A version that still verifies is kept as it is on the next `install` or `upgrade`; a damaged one is replaced
(refused while a model runs from it).

```bash
sudo ./install-agent.sh install --node-id mac-1 ... --no-llama   # agent only; Agent:Executables is left as it is
sudo ./install-agent.sh update-llama                             # (or --update-llama) llama.cpp only, then restart the agent
sudo ./install-agent.sh update-llama --llama-source ~/Downloads  # take the pinned tarball from a folder, no download
```

`update-llama` repairs or re-registers llama.cpp and restarts a running agent so it reads `Agent:Executables` again;
running models keep running and are adopted again. `uninstall` deletes `/usr/local/lib/mirbadllm-agent` with the
runtimes. Without root, `./install-agent.sh stage-llama --dest DIR [--config scratch-agent.json]` builds and checks a
version folder at `DIR` the same way (CI and tests; it never touches the installation).

## MLX (mlx-lm)

MLX serves MLX checkpoints (folders with `config.json`, `*.safetensors` and the tokenizer, such as the
`mlx-community/*` repositories on Hugging Face) with `mlx_lm.server` (plan M6, Phase 3). It is opt-in, because it
takes about 520 MB:

```bash
sudo ./install-agent.sh install --node-id mac-1 ... --with-mlx   # agent, llama.cpp and MLX
sudo ./install-agent.sh update-mlx                               # (or --update-mlx) MLX only, then restart the agent
sudo ./install-agent.sh update-mlx --mlx-source ~/Downloads      # take the pinned uv tarball from a folder
```

`mlx-lm.lock.json` pins a standalone `uv` release (by tag, SHA-256 and size), the CPython version uv installs (uv
checks each Python build's SHA-256 itself) and `mlx-lm.lock.txt`, which pins mlx-lm and every package it needs with
their hashes (`uv pip sync --require-hashes`). The script builds the version folder in place:

| What | Where |
|---|---|
| CPython (its own copy, nothing from the system or Homebrew) | `…/runtimes/mlx-lm/<version>/python/cpython-<x.y.z>-macos-aarch64-none/` |
| the virtualenv with mlx-lm and compiled bytecode | `…/runtimes/mlx-lm/<version>/venv/` |
| `manifest.json` listing **every** file with its SHA-256 and size (`"allFiles": true`) | `…/runtimes/mlx-lm/<version>/` |
| `Agent:Executables` entry `mlx-lm-server` (`venv/bin/python`) with this version as `Default` | `agent.json` |

The folder is root:wheel and read-only for `_mirbadllm`. The agent re-hashes it like llama.cpp's and refuses any
file the manifest does not list, of any kind: an extra `.py` or `.pth` file would be code Python runs. Each Mach-O
file's arm64 code must carry a valid signature. The agent then reports `runtime.mlx.process` with the version, and
the Add model wizard offers MLX for this node.

The agent starts a model as `venv/bin/python -I -u -B -m mlx_lm.server --model <folder> --host 127.0.0.1 --port <port>
…`. In isolated mode no `PYTHON*` variable applies, and the process never downloads: `HF_HUB_OFFLINE=1`,
`TRANSFORMERS_OFFLINE=1`, and `HF_HOME` in the cache root. `mlx_lm.server` loads whatever a request's `model`,
`draft_model` or `adapters` field names. The agent's proxy therefore sets `model` to `default_model` (the folder the
server was started with) and removes the other two. A body it cannot rewrite safely, such as a repeated field name,
is refused with 400. The server has no API key: its port is bound to 127.0.0.1 and reached only through the agent's
mTLS proxy.

Without root, `./install-agent.sh stage-mlx --dest DIR [--config scratch-agent.json]` builds and checks a version folder
at `DIR` (CI and tests).

## Services: launchd jobs (hostd)

The node's **Services** tab can list the Mac's launchd jobs and start, stop and restart the ones you choose, for
example Ollama, LM Studio or ComfyUI holding memory a model needs (plan M10, Phase 4). The agent stays `_mirbadllm`.
A small root helper, `mirbadllm-hostd`, does the work as the LaunchDaemon `com.mirbadllm.hostd`:

```bash
sudo ./install-agent.sh install-hostd                          # LaunchAgents of the account that ran sudo
sudo ./install-agent.sh install-hostd --discover-user alice    # or name the accounts (repeatable)
sudo ./install-agent.sh install --node-id mac-1 ... --with-hostd   # with the agent
sudo ./install-agent.sh uninstall-hostd [--purge]              # --purge also deletes hostd.json and the whitelist
```

| What | Where |
|---|---|
| the binary (root:wheel 0755, in every bundle) | `/usr/local/lib/mirbadllm-hostd/mirbadllm-hostd` |
| `hostd.json`: the users whose LaunchAgents it lists, the seed of the whitelist (root:wheel 0644) | `/Library/Application Support/mirbadllm-hostd/` |
| the whitelist the Services page edits (root 0600) | `/Library/Application Support/mirbadllm-hostd/state/whitelist.json` |
| the socket, its only listener (directory root:`_mirbadllm` 0750, socket 0660) | `/var/run/mirbadllm-hostd/hostd.sock` |
| the log | `/Library/Logs/mirbadllm-hostd/hostd.log` |

`install-hostd` sets `Agent:ManageLaunchdJobs`, `Agent:HostdSocket` and `Agent:HostdExpected` in `agent.json` and
restarts the agent. The agent then reports `services.launchd`. `upgrade` upgrades an installed hostd with the agent.

On the Services tab, **Discovered** lists every job with a plist in `/Library/LaunchDaemons` (System) and in the
named users' `~/Library/LaunchAgents` and `/Library/LaunchAgents` (User, in that user's login session). Add a job to
**Managed** (admin password) to control it:

- **Start** loads the job if it is not loaded (`launchctl bootstrap`), then starts it (`kickstart`).
- **Stop** unloads it (`bootout`), so `KeepAlive` cannot restart it straight away.
- **Restart** is `kickstart -k`.
- **Autostart** is launchd's enabled/disabled override, which decides whether the job loads at boot or login.
  launchd does not load a disabled job, so turn autostart on before starting one.

Some jobs can never be managed, whatever the whitelist says. Discovery still shows them, marked as system services:

- Apple's own (`com.apple.*`) and mirbadllm's agent and hostd (`com.mirbadllm.*`).
- The GitHub Actions runners that share this Mac (`actions.runner.*`).
- Docker (`com.docker.*`), SSH, cron and time.

A root-owned `hostd-deny.json` next to `hostd.json` can add more patterns. hostd uses only plists that are regular
files, not links. A LaunchDaemon's plist must be owned by root, a LaunchAgent's by its user or root, and none may be
writable by group or others. There is no per-job GPU memory on a Mac (macOS does not account GPU memory per process).

## GPU telemetry

The agent reads the Apple GPU's utilisation from IOKit (`IOAccelerator` → `PerformanceStatistics` → "Device
Utilization %"), the same figure Activity Monitor's GPU history shows. It needs no root and no helper (plan M8). The
node's GPUs tab and Monitoring show it as the chip's GPU (for example "Apple M2 GPU"), sharing host memory. Power and
temperature show "—": only `powermetrics` reports them, and it needs root.

## Firewall (TCP 9443)

The master opens every connection; the agent accepts TCP 9443 and authenticates the master by its client
certificate. The script does not change the macOS application firewall. That firewall allows programs, not ports or
source addresses. When it is on, allow the agent:

```bash
sudo /usr/libexec/ApplicationFirewall/socketfilterfw --add /usr/local/lib/mirbadllm-agent/mirbadllm-agent
sudo /usr/libexec/ApplicationFirewall/socketfilterfw --unblockapp /usr/local/lib/mirbadllm-agent/mirbadllm-agent
```

or pass `--allow-firewall` to `install`. The script records it, allows the new binary again on every upgrade and
rollback (the firewall identifies a program by its signature), and `uninstall` removes it. To restrict 9443 to the
master's address, use a `pf` rule; nothing else needs to be open, because the agent reaches models on loopback ports.

## Updates from the master

Every install, upgrade and rekey also sets up the **update helper**. This lets the portal update
the agent: **Update now** on the node's page, after the admin password.

- The agent downloads its bundle from the master and checks its SHA-256.
- The root helper (the LaunchDaemon `com.mirbadllm.agent-updater`) checks the order
  the master signed against the master CA saved in `/Library/Application Support/mirbadllm-agent/update`, then runs the bundle's own
  `install-agent.sh upgrade` (which rolls back if the new agent does not come up).
- The agent never gets root, and cannot change which CA the helper trusts: only an install, upgrade or rekey you run
  saves it.
- After a CA rotation, upgrade or rekey each node once by hand.

## Upgrade, rollback, uninstall

An agent installed before the project's rename is upgraded in place and keeps its old service and folder names; see "Upgrading from a pre-rename install" in [docs/setup.md](../../../docs/setup.md).

```bash
sudo ./install-agent.sh upgrade              # new binary and plist; agent.json and identity kept
sudo ./install-agent.sh rollback             # back to the previous binary and plist
sudo ./install-agent.sh uninstall            # keeps agent.json, the identity, the log and model folders
sudo ./install-agent.sh uninstall --purge    # also deletes agent.json, the identity, the log and _mirbadllm
```

Run them from a newer bundle (or a checkout) for an upgrade. An upgrade reloads the agent (up to 30 s of drain; model
processes keep running and are adopted at the next start) and rolls back by itself when the new agent does not
listen within `MIRBADLLM_AGENT_HEALTH_TIMEOUT` seconds (default 180). Upgrade the master first. `uninstall` leaves
running model processes alone and lists them; `--purge` stops them before it deletes the user. Model and cache
folders are never removed; revoke the node on the master.

## Re-pair (rekey)

When the node's key or state directory is lost, or its certificate must be replaced, create a rekey token for it on
the master's Nodes page and run, on the Mac:

```bash
sudo MIRBADLLM_JOIN_TOKEN='<rekey token>' ./install-agent.sh rekey --node-id mac-1 \
    --node-uid <uid> --enroll-url https://<master>:8443 --ca-hash sha256:<hash>      # "--rekey" works as well
```

The script unloads the LaunchDaemon, runs `mirbadllm-agent join --rekey` as `_mirbadllm` (the node keeps its uid;
`--node-uid` is needed only when the state directory is gone), and loads it again on the new identity, waiting until
it listens. If the rekey fails, the identity is left as it was and the agent is started again on it.

## Memory on a Mac

CPU and GPU share one memory pool (`MemoryModel` `Unified`). Metal lets the GPU keep only about two thirds of RAM
resident (`recommendedMaxWorkingSetSize`): on an 8 GiB Mac that is about **5.3 GiB** for a model, its KV cache and
the runtime, so only small models fit (for example a 4B model at 4-bit). The MLX estimate counts the weights, a 16-bit
KV cache for the context × parallel requests, the prompt cache and 0.5 GiB for Python and Metal. `mlx_lm.server` does
not enforce the context size: a longer request still runs, beyond what admission counted. `MemoryReserveGiB` is 1 in the example so an
8 GiB Mac keeps room for models; raise it on a Mac that also serves as a desktop.

## Bundles

```bash
deploy/agent/macos/install-agent.sh package --published-dir <dir> [--rid osx-arm64] [--output agent.tar.gz]
```

`<dir>` is the output of `dotnet publish src/MirbadLLM.Agent/MirbadLLM.Agent.csproj -c Release -r osx-arm64
--self-contained true -p:PublishSingleFile=true -p:DebugType=none` (it cross-publishes on Linux and Windows; the result
is ad hoc signed already). Without `--published-dir`, a checkout publishes it with a local `dotnet` or the .NET SDK
image. A bundle is one folder `mirbadllm-agent-<revision>-osx-arm64` with the binary, `install-agent.sh`,
`com.mirbadllm.agent.plist`, `agent.macos.example.json`, `llama-cpp.lock.json`, `mlx-lm.lock.json`, `mlx-lm.lock.txt`,
`mirbadllm-hostd` with `com.mirbadllm.hostd.plist` and `hostd.macos.json` (when hostd was published next to the
agent, as the master's image does), this file and `BUILD-INFO` (revision, architecture, build time). `package` and `render-config` use only portable tools, so the master's image build runs them in its Linux
container; the master serves the bundle at `/agent/bundles/osx-arm64.tar.gz`.
