# MirbadLLM multi-node: Operator guide (P3/P4)

What Phase 3 and Phase 4 add to the [P2 operator guide](operations-p2.md): a **Windows PC as a node**, models that
run as **native `llama-server` processes** instead of containers, **GGUF** models from Hugging Face, the GGUF
admission estimate, per-node admin APIs (`?node=`), Windows host services through the SCM, and the runbook that moves
OpenViking's embedding model off the DGX. Read [operations.md](operations.md) (P1) and
[operations-p2.md](operations-p2.md) first; this guide covers only what is new or different.

Every statement here was checked against the code on `feature/multinode-p3` at `2c9ccc1` (the Wave 14 review,
MN-234/D131, is the last merge in it). Where the [design addendum](design-p3p4.md) says something else, this guide
follows the code, and section 14 lists what the design describes but P3/P4 does not ship.

**Status:** P2 is live on the DGX from `main`. P3/P4 is developed on `feature/multinode-p3` and **not deployed**. The
owner's live acceptance checklist is plan-p3p4.md §4.2 (MN-241); the OpenViking cut-over (section 10)
is MN-242 and needs the owner's approval.

Packaging details of the Windows agent are in [`deploy/agent/windows/README.md`](../../deploy/agent/windows/README.md).
This guide does not repeat them.

---

## 1. What P3/P4 changes

| Area | P2 | P3/P4 |
|---|---|---|
| Nodes | DGX + Linux PCs | Plus a **Windows PC**: a real Windows service, no Docker, no hostd |
| Runtimes | vLLM in a container | Plus **llama.cpp as a native process** (`llama-server.exe`), one process per model |
| Models | checkpoint folders for vLLM | Plus **GGUF** files, downloaded by the agent's own downloader (no helper container) |
| Memory | measured per container | On Windows: a **GGUF estimate** before the start, and "measured at start" afterwards |
| Model ports | container ports | loopback ports with a **per-instance API key** |
| Admin APIs | the bootstrap node only | optional **`?node=<id>`** on downloads, folders, catalog and GGUF reads |
| Host services | systemd through hostd | **SCM** on Windows, per-service whitelist, no admin rights for the agent |
| Degraded reasons | P2's | plus `gpu_external_use_high` and `executable_unverified` |

**On the DGX nothing changes.** As long as `dgx` is the only node reporting a runtime, its launch bodies, legacy
config hashes, execution fingerprints, capabilities, admission, `/status` and `/v1` behaviour are byte-identical to
P2. The protocol stays version 1; every new field is additive. A Windows node that is only registered, or approved
but offline, changes nothing on the DGX either.

**What does not move.** OpenViking's query planner (`guoxuter/ov_intent_analysis_sft:v7_q8`) and
`Qwen3-VL-Embedding-2B` stay on the DGX exactly as they are (owner decisions O7/D113 and O3/D109). Only
`Qwen3-Embedding-4B` moves.

---

## 2. How a Windows node is put together (the composition gate)

One switch decides the whole node: **`Agent:Docker:Enabled`** (new in P3).

- **Docker on** — the DGX and every Linux agent — is P2's composition, registration for registration. Nothing in the
  gate runs.
- **Docker off** registers the process half instead: the llama.cpp process driver, the executable catalogue, the
  process record store, the GPU start meter and the per-instance key protector. It does so **only if an
  `IProcessHost` and an `IInstanceKeyProtector` are available**. On Windows the composition registers both itself;
  off Windows a test or the dev setup (MN-236) injects them. That is deliberately a check for the two services and
  not an OS check, so the Linux CI leg exercises the same composition instead of skipping it (MN-234, D131 (1)).

There is **no second switch.** An `Agent:Runtime: process|container` setting would be a second source of truth for
the same decision.

What follows from the gate, in the order an operator meets it:

- With Docker off there is no Docker client, no container driver, no Docker probe and no two-minute Docker wait, and
  the node reports none of `runtime.vllm.container`, `downloads.hf-container` or `services.docker`. It never goes
  Degraded with `docker_unreachable`.
- **`Agent:CacheRoot` is mandatory** on a node with Docker off: a native launch's argv may name it as `{cache}`. It
  is checked at composition, not in the options validator, because the frozen contract tests bind a Docker-off
  `agent.json` without one. See section 11.1 for the refusal.
- **`Agent:Executables` holds one entry per name.** Two entries for `llama-server` would each derive their own
  default version, so the node's default — and every launch fingerprint through it — would follow JSON ordering.
  Refused in validation; section 11.1.
- **The native downloader follows the Docker switch, not process delivery.** A download needs a disk and an HTTPS
  client, not a runtime driver, and on a node without Docker it is the only downloader there is. So a Docker-off node
  always reports `downloads.hf-native`, even one that can start no model.
- **A Docker-off node that supplies neither service composes no runtime driver and still starts.** It enrolls,
  reports, serves telemetry, downloads and host services, and refuses every launch. Only a hand-edited Linux
  `agent.json` reaches that state; section 11.1 has the log line and the refusal.
- **Two P2 contributors read every driver**, not only the container one (`GpuSelectionContributor`,
  `LaunchFeaturesContributor`). Without that a process node reported neither `gpus.uuids` nor `launch.env`, and the
  master's placement validator refuses every GPU placement on a node without `gpus.uuids` — no llama.cpp model could
  be placed on the PC at all. The widening is inert with Docker on (there is no process driver to see), and
  `images.digest-pinned` stays container-only.
- **A placement with no GPU sends `HostBudgetGiB = null`, and that is correct.** The agent then charges the whole
  budget to host RAM, which is what a CPU-only GGUF needs; on the unified DGX the field is ignored.
- **Approval stays the gate for anything sent to a node.** The fit preview answers a verdict for a node that is
  still waiting for approval (the Nodes page shows "waiting for approval"), but it does not *read* from it: the
  preview checks the node's approval before any GGUF read.

### 2.1 `gpu-reserves.json`

`gpu_external_use_high` (section 5.3) judges a GPU against the reserve the **master** set for it, and the master
sends reserves only with a launch. A restarted agent would therefore judge nothing until the next launch, so it
keeps the last reserves it learned in `{state}\gpu-reserves.json` and loads them at start:

```json
{ "gpuReservesGiB": { "GPU-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx": 2.5 } }
```

GPU UUIDs and GiB figures only, nothing secret, nothing worth backing up. It is written only when the reserves
change and the in-memory copy is updated only after the write succeeded (so one transient `IOException` does not
disable the feature for good). A missing, damaged or foreign file is ignored with a warning and the reserves are
re-learned from the next launch. Every launch teaches them, a refused launch included: the hook runs before the
admission lock.

---

## 3. Before the Windows PC joins

| Check | Why |
|---|---|
| With an NVIDIA GPU: driver ≥ the lock file's `minDriver` (`615.00` for `b11476`, CUDA 13.4) | The installer refuses an older driver for the CUDA build. `nvidia-smi --query-gpu=driver_version --format=csv,noheader` |
| Without an NVIDIA GPU: nothing more | The installer finds no NVIDIA driver and installs the CPU build; the PC joins as a CPU-only node (section 4.6) |
| **Microsoft Visual C++ 2015–2022 Redistributable (x64)** | The llama.cpp binaries import `msvcp140.dll`, `vcruntime140.dll` and `vcruntime140_1.dll`, and the release zips ship none of them. The installer checks `System32` and stops naming the redistributable; it never downloads it |
| The PC can reach the master on TCP 8443 (enroll) and 8080 (agent bundle) | As for a Linux PC (operations-p2 section 2) |
| The master can reach the PC on TCP 9443 | The master dials every agent. The installer opens 9443 for the service, Private profile, master address only |
| A local NTFS volume with ≥ 40 GB free for `D:\mirbadllm` | `ModelsRoot` must be local NTFS: the service account has no network credentials and mmap over SMB is slow. The agent refuses a network drive |
| Clock in sync (`w32tm /query /status`) | The agent is Degraded above 30 s skew and refuses mutations above 5 min. The installer warns when the Windows Time service is not syncing |
| Power plan: never sleep on AC; Windows Update active hours | A sleeping or rebooting PC is an Offline node and OpenViking gets errors meanwhile. The installer warns |
| An **elevated Windows PowerShell 5.1** window | The installer is written for 5.1 (the default shell) and also runs under PowerShell 7 |

No Docker Desktop, no WSL and no checkout are needed.

---

## 4. Adding a Windows PC

### 4.1 Create the join command (master)

**Nodes → Add node**, exactly as for a Linux PC (operations-p2 section 3.1), then pick the **Windows** tab. Both
token modes are offered:

- **Paste the token at a prompt** (default): the line carries no secret and ends in `-PromptToken`; the installer
  reads the token with `Read-Host -AsSecureString`.
- **Token in the command**: the line begins with `$env:MIRBADLLM_JOIN_TOKEN='…'`.

There is never a `-JoinToken` parameter: every user on the PC can read process command lines through
`Win32_Process`. The installer clears the variable in a `finally`, so it is gone even when a step fails.

PSReadLine keeps any line containing "token" out of the history file (MemoryOnly), so the command is not saved to
disk — but it stays in the window's Up-arrow history and scrollback until the window closes, and
`Start-Transcript` would record it. Close the elevated window when you are done.

### 4.2 Install and join (PC)

Paste the line into the elevated 5.1 window. It is one line (wrapped here):

```powershell
$env:MIRBADLLM_JOIN_TOKEN='<token>'; & { $ProgressPreference = 'SilentlyContinue'; $d = Join-Path $env:TEMP ([guid]::NewGuid()); New-Item -ItemType Directory $d | Out-Null;
  try { Invoke-WebRequest -UseBasicParsing 'http://<master>:8080/agent/bundles/win-x64.zip' -OutFile "$d\agent.zip";
  if ((Get-FileHash "$d\agent.zip" -Algorithm SHA256).Hash -ne '<SHA256>') { throw 'agent bundle hash mismatch' };
  Expand-Archive "$d\agent.zip" $d; & (Get-ChildItem "$d\mirbadllm-agent-*\install-agent.ps1").FullName install -NodeId win-1
  -EnrollUrl https://<master>:8443 -CaHash sha256:<H> -LlamaCpp }
  finally { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; Remove-Item Env:MIRBADLLM_JOIN_TOKEN -ErrorAction SilentlyContinue } }
```

The SHA-256 in the command is what makes the plain-HTTP download safe (the command itself comes from the
authenticated UI). A wrong hash stops with `agent bundle hash mismatch` before anything runs.

What `install -LlamaCpp` does, in order:

1. Copies `mirbadllm-agent.exe` to `C:\Program Files\mirbadllm-agent\app\{version}\`.
2. Creates the service `mirbadllm-agent` under the virtual account `NT SERVICE\mirbadllm-agent`, startup type
   **Automatic (Delayed Start)**, with restart on failure. The account has no password, no interactive logon and no
   network credentials, and it joins no Docker group.
3. Writes `%ProgramData%\mirbadllm-agent\agent.json` once, from `agent.windows.example.json`, with `NodeId`,
   `StateDirectory`, `ModelsRoot`, `CacheRoot` and an empty `Executables` list. A second `install` keeps the file.
4. Protects `%ProgramData%\mirbadllm-agent` (section 9) and grants the account modify on the models and cache roots.
5. Sets the service's `PreshutdownTimeout` to `Agent:ShutdownTimeout` + 10 s and registers the Event Log source
   `mirbadllm-agent` in the Application log (the virtual account cannot create a source itself).
6. Enrolls the node and prints the **key fingerprint**.
7. With `-LlamaCpp`, installs the pinned llama.cpp (section 4.4).
8. Opens TCP 9443 inbound, bound to the **service** (so it survives the `binPath` switches of an upgrade), remote
   address = the master, **Private profile only**. If the Windows Firewall is off — it is off on all three profiles
   of the owner's PC — the rule has no effect and the installer says so in red. Turning the firewall on is the
   owner's call.
9. Starts the service (`-NoStart` skips it).

`-DryRun` runs every check, including the llama.cpp download and its verification into a temporary folder, and
prints each change instead of making it.

### 4.3 Approve (master)

As for a Linux PC (operations-p2 section 3.3): compare the fingerprint the PC printed, then **Approve**. An
approved Windows node reports, on the Nodes page and in `GET /agent/v1/capabilities`:

- OS badge **Windows**, memory model **discrete**, both GPUs with their UUIDs;
- features `runtime.llamacpp.process`, `downloads.hf-native`, `gguf.inspect`, `admission.gpu-budgets`,
  `services.scm`, `gpus.uuids`, `launch.env`, `admission.discrete`;
- **not** `runtime.vllm.container`, `downloads.hf-container`, `services.docker`, `services.systemd`,
  `backup-mount`, `images.digest-pinned`, `downloads.ollama`;
- the installed llama.cpp versions under the llama.cpp runtime, each with its CUDA variant, whether it is verified
  and which one is the node's default.

### 4.4 llama.cpp versions on a node

The binaries are **official pinned llama.cpp release builds, `win-cuda-13.4-x64`**, verified by SHA-256 against
`deploy/agent/windows/llama-cpp.lock.json` and installed by the installer. We do not build llama.cpp (owner
decision O2, D108). Currently pinned: **`b11476`**, commit `988190680`, `minDriver` `615.00`. The 13.4 build is the
one with native `sm_120`/`sm_121` kernels for the RTX 50 cards; the 12.4 build would JIT-compile at every first
start and is not offered.

```powershell
.\install-agent.ps1 -LlamaCpp                                   # the version pinned in llama-cpp.lock.json
.\install-agent.ps1 -LlamaCpp b11476 -LlamaCppSource D:\downloads  # zips already downloaded
.\install-agent.ps1 -RemoveLlamaCpp b11476
```

- Releases are looked up **by tag**, never through `releases/latest`: every recent `bNNNN` release is flagged
  prerelease, so `latest` never returns one.
- `llama-server.exe` is a 9 KB launcher; the server is `llama-server-impl.dll`, loaded in the same process (so its
  PID and image path are what adoption checks). Verifying only the exe would leave the code unverified, so the
  installer requires **exactly** the lock file's 55 unpacked files — each with the right size and hash, nothing
  missing, nothing extra — before it moves the folder into place and writes its `manifest.json`.
- The version folder is **read-only for the service account**; only Administrators can write it.
- **Versions live side by side**, one folder each, listed in `Agent:Executables` with one default:
  `C:\Program Files\mirbadllm-agent\runtimes\llama.cpp\{bNNNN}\`.
- **Upgrading llama.cpp** = install the new version, change the node default (or pin `executableVersion` on one
  model), then restart the model. The version is part of the execution fingerprint, so the UI shows "restart
  pending" until you do. **Rollback** = switch the default back.
- `-RemoveLlamaCpp <version>` is refused while a process runs from that folder, and while that version is the
  default of other installed versions.
- At every agent start the catalogue re-hashes every file in each manifest (≈ 712 MiB, in the background, so it
  never counts against the service start). Between full hashes it watches size and mtime. A mismatch — or any
  `.exe`/`.dll` in the folder that the manifest does not list, because a planted DLL would load from the
  application folder first — marks that version unusable: health reason `executable_unverified`, the capability
  reads `available: false`, and launches naming it answer 422 `executable_unverified`.

### 4.5 Upgrade, rollback, rekey, uninstall

```powershell
.\install-agent.ps1 upgrade -Bundle .\win-x64.zip -BundleSha256 <hex>
.\install-agent.ps1 rollback
$env:MIRBADLLM_JOIN_TOKEN='<rekey token>'; .\install-agent.ps1 rekey -EnrollUrl https://<master>:8443 -CaHash sha256:<hash>
.\install-agent.ps1 uninstall
```

**A model's `llama-server` process is never restarted by an agent upgrade.** Each instance has its own job object
without kill-on-close, so the process outlives the agent's stop, upgrade or crash and the new agent adopts it by
PID, creation time and image path. The master's rekey dialog builds the Windows rekey line; running models keep
running through a rekey too.

`uninstall` stops only the runtime processes the service account started from the runtimes folder, removes the
service, its Event Log source, the firewall rule and `C:\Program Files\mirbadllm-agent`, and **keeps**
`%ProgramData%\mirbadllm-agent` (identity, configuration, state). The virtual account's profile stays loaded
until a reboot, so the script prints the command that removes it afterwards instead of failing.

---

### 4.6 CPU-only and Vulkan nodes

`install-agent.ps1 -LlamaCpp` installs one of three builds of the pinned release, chosen by `-Accelerator`
(`auto`, `cuda`, `cpu`, `vulkan`), and writes the choice to `agent.json` as `Agent:Accelerator`. The install guide
([`deploy/agent/windows/README.md`](../../deploy/agent/windows/README.md)) lists the assets; operations-p2 section 5.6
has the rules that hold on every operating system, including where a Vulkan node's VRAM figures come from and what
happens when one cannot be read.

- **`auto`** (the default, and what the **Add node** command uses) installs the CUDA build when an NVIDIA driver
  answers and the CPU build otherwise. It never installs the Vulkan build.
- **A CPU-only node** is unified: a model takes no GPU, its budget counts against host RAM, and one model starts at a
  time. Sections 5.3 and 11.3 (per-GPU memory, external VRAM use) do not apply to it.
- **A Vulkan node** is discrete, like an NVIDIA PC: per-GPU budgets, one start per GPU, and "measured at start"
  (section 5.3) from the Windows GPU memory counters instead of `nvidia-smi`. It is experimental and has not run on
  an AMD or Intel graphics card.
- To change the build, run `install-agent.ps1 -LlamaCpp -Accelerator <value>` again with the node's models stopped.
  The version folder is replaced and the service is restarted.

## 5. Windows node differences

### 5.1 What the node has instead

| Subsystem | On a Windows node |
|---|---|
| Docker | Off. No container driver, no probe, no wait, no `docker_unreachable` |
| Host memory | `GlobalMemoryStatusEx` (`ullTotalPhys`, `ullAvailPhys`) |
| GPUs | `nvidia-smi.exe` for inventory, totals and used/free. Memory model **discrete** |
| GPU processes | `nvidia-smi --query-compute-apps=gpu_uuid,pid,process_name`; **memory is `[N/A]` under WDDM**. PIDs are matched to models through `processes.json`; other programs are listed by name with no memory |
| hostd | None. `services.systemd` and `backup-mount` are not reported |
| Host services | The **SCM** (`services.scm`): list services; start, stop, restart and change the start type of whitelisted ones only. Default whitelist: empty |
| Model ports | loopback, from `PortRangeStart` (the Windows example uses **8101**, away from 8000) |

**Ports.** Desktop programs hold ports, so before creating a process the agent skips every port a process already
listens on (any address) and test-binds `127.0.0.1:{port}` exclusively. The check runs inside the reservation lock,
so two concurrent launches cannot both walk onto the same free port. The owner's own `llama-server.exe` on port 8000
is simply skipped, never touched.

### 5.2 Host services through the SCM

The Services page shows Managed/Discovered tabs as for Linux, with Windows services in place of units. The agent
needs no admin rights: the installer grants it rights per service.

```powershell
.\install-agent.ps1 -AllowService ComfyUI_Server
.\install-agent.ps1 -AllowService ComfyUI_Server -AllowServiceAutostart   # read section 9 first
```

Each name is matched **exactly** — no wildcard reaches `Get-Service`, both by parameter validation and by a
re-check at grant time. Drivers are refused. A compiled deny list is enforced on the canonical service name for
every path, whitelisted or not: `WinDefend`, `WdNisSvc`, `EventLog`, `RpcSs`, `RpcEptMapper`, `DcomLaunch`, `SamSs`,
`Dnscache`, `LanmanServer`, `TermService`, `wuauserv`, `mpssvc`, `BFE`, `nvlddmkm`,
`NVDisplay.ContainerLocalSystem` — and the agent's own service, which is always refused. The whitelist is
`{state}\scm-whitelist.json`, read on every call, so a service added while the agent runs
counts at once; an unreadable or malformed file fails **closed** (nothing is listed or acted on).

`-AllowService` takes the **Windows service name** (`ComfyUI_Server`). The Services page and the API show the
whitelist name derived from it — lower case, anything outside `[a-z0-9-]` turned into `-`, runs collapsed, trimmed
to 64 characters: `comfyui-server`. Two services whose names derive to the same whitelist name cannot both be
added.

Without `services.scm` — the feature off, or an older agent — the Services page keeps its existing text, "This
node's agent does not manage services".

### 5.3 Per-GPU memory, "measured at start", and external VRAM use

Under WDDM `nvidia-smi` reports **no per-process GPU memory**, so the usual "memory by model" measurement is not
available on Windows. Two figures take its place:

- the model's **declared budget** (the GGUF estimate, section 7.4), and
- a **measured** figure: the agent samples the placed GPU's `memory.used` right before it creates the process and
  right after Ready, and stores the delta on the instance. The UI labels it **"measured at start"**. It is
  approximate: anything else allocating at that moment lands in it, and a cold start can even measure *below* the
  real buffers because the desktop released memory meanwhile.

**Free GPU memory always comes from `nvidia-smi`, never from CUDA or llama.cpp.** Under WDDM CUDA reports far more
free memory than the card has (spike S1 saw 15 172 MiB "free" on a card `nvidia-smi` showed 3–4 GiB used on), so
admission, the start meter and the external-use judgement all read
`nvidia-smi --query-gpu=memory.used,memory.total`. `--fit` is always **off** for the same reason: llama.cpp must
never silently shrink values to fit a free-memory figure that admission did not agree with.

**External VRAM use.** Per GPU, `external = memory.used − Σ measured model deltas`. When that stays above the GPU's
reserve for two consecutive samples — the owner opened a game, ComfyUI or Blender — the node goes **Degraded** with
`gpu_external_use_high` and the exact numbers:

```
GPU 1 (NVIDIA GeForce RTX 5060 Ti): 4.1 GiB used by programs other than mirbadllm's models, above its
2.5 GiB reserve (11.7 GiB used, 7.6 GiB measured at start for models).
```

Running models keep serving; new starts may be refused on admission's own figures. The reason clears after two
samples at or below the reserve. A GPU is **not judged** — its state and counters stay as they were — when
`memory.used` is missing, `processes.json` cannot be read, or an instance on that GPU has no measurement yet (while
it starts, its memory would otherwise count as external). A GPU the master's reserves no longer cover is dropped
from the judgement and a raised reason clears. A model on several GPUs is split by its `--tensor-split` weights
when its argv has them, else evenly.

---

## 6. Per-node admin APIs (`?node=`)

The P2 routes that acted on the bootstrap node only now take an optional **`?node=<id>`**:

| Route |
|---|
| `POST /api/downloads`, `GET /api/downloads`, `GET /api/downloads/{folder}`, `DELETE /api/downloads/{folder}` |
| `GET /api/catalog` |
| `GET /api/model-folders`, `GET /api/model-folders/{folder}` |
| `GET /api/model-folders/{folder}/gguf?file=…` (new in P3) |

`GET /api/downloads/inspect` takes **no** `?node=`: it reads Hugging Face, not a node. `/api/services*` still acts
on the node the page selects.

Resolution and refusals (the matrix operations-p2 section 6.1 already sketches, with the exact messages):

| Situation | Answer |
|---|---|
| `?node=` absent, one fleet member | that member acts, exactly as in P2 |
| `?node=` absent, two or more fleet members | 400 `node_required` — `More than one node exists; name the node with ?node=<id>.` |
| not a valid node id | 400 `invalid_request` — `?node= is not a valid node id.` |
| unknown or revoked node | 404 `not_found` — `Node 'x' does not exist.` |
| enrolled but **not approved** | 409 `invalid_state` — `Node 'x' is not approved yet; approve it first.` Approval is the trust gate: before it nothing is sent to the node, and a download would hand it the Hugging Face token and write to its disk |
| approved but not serving | 503 from the read itself, e.g. `Node win-1 is not serving (state: Offline); its GGUF files cannot be read now.` — **except `GET /api/catalog`**, see below |
| the node does not report `gguf.inspect` | 501 `not_supported` — `Node 'win-1' cannot read GGUF headers (feature 'gguf.inspect').` |
| `source: "ollama"` on `POST /api/downloads` | 501 `not_supported` (the puller is deferred; section 14) |

`POST /api/downloads` echoes the scope in its `Location` header (`/api/downloads/{folder}?node=win-1`) whenever the
node was named explicitly. The model editor, a move and a restore with a target all use these routes on the node you
chose, so you rarely pass `?node=` by hand.

**`GET /api/catalog?node=` is answered by the master, not by the node** (MN-239 item E). It is the fixed vLLM option
lists plus the node's **images from its last capability report**, so an approved node that is Offline or unreachable
answers **200** with the images it last reported — deliberately, so the editor can still show them — and never 503.
The `?node=` refusals above (400, 404, 409) still apply, because they are decided before any report is read. Only the
routes that genuinely talk to the agent — `/api/downloads*`, `/api/model-folders*`, `/api/model-folders/{f}/gguf` —
can answer 503 for an unreachable node. If a node's image list looks stale, that is why: look at its last contact
time on the node page, not at the catalog.

---

## 7. GGUF models on a Windows node

### 7.1 Where GGUFs come from

**Hugging Face GGUF repos only.** The editor's **Source → Hugging Face** flow groups a repo's `.gguf` files into
variants (split parts kept together, `mmproj-*` listed separately), you pick one, and the master records the source
as the repo id, the revision resolved to a commit SHA, and the variant's files as allow-patterns.

The download runs on the node through its **native downloader** (`downloads.hf-native`): immutable commit SHA, a
manifest, range resume, LFS SHA-256 verification, a disk pre-check and an atomic finalize. There is no helper
container, so nothing needs an image on the PC.

- The Hugging Face token, when a repo needs one, lives in the `POST /downloads` body and in the agent's memory for
  that download only. It is redacted from the operation log and never given to `llama-server`.
- A download whose agent restarted without its token ends **Failed** with an error starting `Paused (needs token)`.
  Start it again with the token: it resumes from the bytes in `{folder}.partial`, which the disk pre-check counts as
  already present.
- Folder names compare case-insensitively on Windows, so `Foo` and `foo` are one folder and one download.
- **Quants.** Q8_0 is the default for embedding models (near-lossless, half of F16). For chat models the editor
  suggests Q4_K_M or Q5_K_M when VRAM is tight and shows the estimate for each.

**There is no Ollama-registry source in P3/P4.** The contract is frozen and the design keeps the pull, but the
puller is deferred because the planner stays on the DGX (O4 + O7, D110/D113); the admin API answers 501
`not_supported` for it. For a one-off there is a manual fallback on the PC:

```powershell
.\install-agent.ps1 -ImportGguf C:\Users\me\.ollama\models\blobs\sha256-<hex> -Folder planner
```

It copies the file into `{ModelsRoot}\planner\model.gguf` and verifies its SHA-256 (taken from the blob's name, or
from `-Sha256`). It is a fallback, not a supported path: a model added that way has no sources, so it cannot be
moved or restored.

### 7.2 Folders and path confinement

| Path | Content |
|---|---|
| `D:\mirbadllm\models\{folder}\` | the GGUF files (`{folder}.partial\` while downloading) |
| `D:\mirbadllm\cache\cuda\`, `…\tmp\` | the CUDA JIT cache and the child's `TEMP` |
| `C:\ProgramData\mirbadllm-agent\state\logs\ss-{modelId}.log` | one log per model, with `.1` and `.2` |

Folder names keep the `^[A-Za-z0-9._-]{1,200}$` rule. Confinement refuses drive letters, UNC paths, alternate data
streams, reserved device names (`CON`, `NUL`, `COM1`…), trailing dots and spaces, and compares paths
case-insensitively after resolving junctions and symlinks. Only the builder's own flags may carry a path (`-m`,
`--mmproj`, `--chat-template-file`); an `ExtraArg` with a path-valued flag is refused.

### 7.3 Reading a GGUF header

`GET /api/model-folders/{folder}/gguf?file=<name>&node=win-1` returns the header summary the editor shows:
architecture, GGUF version, parameter count, file type, block count, context length, embedding length, head counts,
key/value lengths, vocab size, the pooling type and whether attention is causal.

A folder with **several** model GGUFs and no `?file=` answers 400 `invalid_request`:

```
'planner' has several model GGUFs; choose one with ?file=: a.gguf, b.gguf.
```

The same candidates come back as a structured field, so the editor's picker offers them correctly even when a file
name contains a comma. A malformed or hostile GGUF is 422 `gguf_invalid`: the reader bounds every length and count
it reads from the file.

### 7.4 The admission estimate

llama.cpp cannot be asked how much memory a model will take before it takes it, so the master computes an estimate
from the GGUF header and the settings. All values in GiB:

```
weights      = Σ tensor data over all split parts            × gpuLayers / blockCount (partial offload)
kv           = attnLayers × ctxTotal × headCountKv × (keyLength × bytes(ctk) + valueLength × bytes(ctv))
               attnLayers = blockCount, or blockCount / fullAttentionInterval for a hybrid model
               ctxTotal   = ContextSize × Parallel
ssmState     = hybrid models only: (blockCount − attnLayers) × Parallel × (convState + ssmStateSize)
compute      = max(256 MiB, ub × (embeddingLength + vocab) × 4 B × 1.2)
cudaContext  = 0.45 GiB per GPU
budget       = 1.10 × (weights + kv + ssmState + compute) + cudaContext × GPUs
```

`budget` is what the editor proposes as the model's memory budget and what admission uses. The editor shows the
breakdown (weights / KV / compute / CUDA context) and updates it as you type, with one line per GPU:

```
GPU 1: needs 8.9 GiB, 10.9 GiB free after the 2.5 GiB reserve · fits
```

Before the download only the file size is known, so the picker says "needs **at least** X GiB" instead.

Read it as an estimate, deliberately on the safe side. Spike S1 measured it 17–18 % **above** the real buffers on
the RTX 5060 Ti (the compute term is `ub × (n_embd + n_vocab) × 4 B × 1.076` in practice, and the CUDA context is
nearer 0.1 GiB than 0.45 under WDDM). It was not recalibrated: the estimate *is* the admission budget, the
measurement at start is noisy under desktop load, and a 16 GB card with a 2.5 GiB reserve still admits 8.9 GiB. Two
safeguards keep the gap honest — `--fit off`, so llama.cpp never shrinks anything to make it fit, and the measured
delta shown next to the budget with the existing "uses X GiB more than its budget" flag.

Worked example, the model that moves (Qwen3-Embedding-4B Q8_0: 36 layers, 8 KV heads, head dim 128, n_embd 2560,
vocab 151 665, f16 KV, `ContextSize` 8192, `Parallel` 2, so `ub` = 2048):

| Term | Value |
|---|---|
| weights | 3.99 GiB |
| kv | 2.25 GiB |
| compute | 1.41 GiB |
| CUDA context | 0.45 GiB |
| **budget** | **≈ 8.9 GiB** (measured at start ≈ 7.6 GiB) |

At the model card's `-ub 8192` the same formula gives 13.5 GiB, which would not be admitted next to the desktop on
the 16 GB card — which is why the micro-batch default is `min(ContextSize, 2048)` for a causal embedding model
(section 7.5).

**Two fit verdicts can disagree.** The editor shows the placement line against the model's *memory budget* and the
GGUF picker's per-GPU "needs X GiB … fits" against *free memory*. If you set a budget by hand that is below the
estimate, the two read differently. Trust the estimate for "will it start", and set the budget to it.

### 7.5 llama.cpp settings

The editor's llama.cpp section writes typed settings; the master's builder is the only thing that knows
`llama-server` flags, and the argv order is fixed so the execution fingerprint is stable.

| Setting | Flag | Notes |
|---|---|---|
| Model file | `-m` | The first part of a split GGUF; llama.cpp loads the other parts itself |
| Served name | `--alias` | Exactly as entered, `/` and `:` included |
| Context per request | `-c ContextSize × Parallel` | **`ContextSize` is per slot.** An input above the slot size gets 400 `exceed_context_size_error` |
| Parallel slots | `-np` | Always explicit (`-np -1` would make memory unpredictable) |
| GPU layers | `-ngl` | 999 = all |
| Flash attention | `-fa on\|off\|auto` | |
| KV types | `-ctk` / `-ctv` | `f16`, `q8_0`, `q4_0`, … |
| Batch / micro-batch | `-b` / `-ub` | Embedding models: `-b = -ub` (below). Chat models: as set, else llama.cpp's defaults |
| Embedding mode + pooling | `--embedding --pooling {none\|mean\|cls\|last\|rank}` | Pre-filled from the GGUF's `pooling_type` |
| Emulate `dimensions` | — | Router-side; section 8.2 |
| Per-GPU budgets | `--split-mode`, `--tensor-split`, `--device` | Below |
| Chat template | `--chat-template builtin:{name}` or `--chat-template-file` | For a GGUF whose embedded template must be overridden |
| Reasoning | `--reasoning on\|off\|auto` | Default auto |
| Threads | `-t` | Default: llama.cpp's choice |
| llama.cpp version | `executableVersion` | The node default, or a version pinned on the model |
| Extra args | appended last | Refusal list shown in the editor |
| always | `--host 127.0.0.1 --port {port} --metrics --no-ui --cache-ram 0 --fit off` | `--fit` would shrink unset values to fit free memory; `--cache-ram 0` because llama-server otherwise keeps up to 8 GiB of host-RAM prompt cache that admission does not count |

**Embedding micro-batch.** "An input must fit one micro-batch" holds only for **non-causal** (BERT-style) embedding
models. A causal model with last-token pooling — Qwen3-Embedding is one — processes a long input over several
micro-batches against the KV cache, and llama-server forces `n_batch = n_ubatch` when embeddings are on. So the
builder always emits `-b = -ub`: your `MicroBatchSize` when set, else `min(ContextSize, 2048)` for a causal model
and `ContextSize` for a non-causal one. At `b11476`, 8 102-token inputs embedded at `-ub 2048` gave vectors
identical to `-ub 8192` (median cosine 1.0) while the compute buffer dropped from 5.1 to 1.3 GiB. The editor reads
`causalAttention` from the GGUF header and sets `MicroBatchSize = ContextSize` for a non-causal file.

**GPUs.** GPUs are pinned by **UUID** (`CUDA_VISIBLE_DEVICES`), and the builder emits `--device CUDA0[,CUDA1]`
against the renumbered visible devices, never a physical index. One GPU: `--split-mode none --device CUDA0`. Two
unequal GPUs are fine for llama.cpp (vLLM's "tensor parallelism needs equal GPUs" rule does not apply): give a
per-GPU budget in placement order and the builder derives `--tensor-split` from it, defaulting to each card's total
minus its reserve. Admission then checks each GPU against its own budget — and refuses a **partial** budget map, so
name every GPU of the start. `row` and `tensor` split modes are not offered in the editor.

**Extra args are refused when they would lie to admission.** `llama-server` takes the last value of a repeated
flag, so an `ExtraArg` would silently override the typed setting the budget was computed from. Refused: `--host`,
`--port`, `--api-key*`, `--alias`, `-m`, `--fit`, `--log-file`, `--device`, the UI switches, `--cache-ram`/`-cram`,
any flag that takes a path, and every typed setting — `-c`, `-np`, `-ngl`, `-b`, `-ub`, `-ctk`, `-ctv`, `-fa`,
`--embedding`, `--pooling`, `-ts`, `-mg` and their long forms. `-sm`/`--split-mode` stays allowed (`row` has no
typed setting), as do sampling flags. A refused arg is 400 at save and `llamacpp_settings_invalid` in placement.

There is **no legacy config hash** for a llama.cpp model: restart-pending compares execution fingerprints alone,
and the fingerprint includes the canonical argv and the executable version.

### 7.6 Starting, Ready, crashes and restarts

| Stage | What happens |
|---|---|
| reserved | one start per GPU on the node; the port is picked under the lock |
| created | a pending start is written to `processes.json`, then `CreateProcessW` with an explicit environment, stdout and stderr on the log file (not a pipe), stdin on `NUL`, and only those handles inherited |
| started | the process is assigned to its own job object and resumed; the record replaces the pending start |
| Ready | `/health` answers 200 **and** `GET /v1/models` with the instance's key lists the expected alias |

The second Ready check is what catches a foreign process that grabbed the port — it would not know the key either.
Before Ready is declared, the lines this start wrote are checked for a failed GPU backend: llama-server keeps answering
`/health` 200 after Metal runs out of memory in the warm-up (a context too large for the Mac), so the model goes
Failed with `Cause: error: Insufficient Memory (...kIOGPUCommandBufferCallbackErrorOutOfMemory)` and the process is
stopped. Lower the context size or the parallel slots, or free memory on the node, and start it again.
`llama-server` answers **503 `Loading model`** on every endpoint while it loads; `/health` is the only endpoint open
without the key. A 503 before Ready never reaches a `/v1` client: the master answers `model_not_ready` while the
state is not Ready.

- **Start timeout** `Agent:Process:StartTimeout`, default **10 min** (a large GGUF from a cold disk). S1 measured
  3.3 s for the 4.3 GB embedding GGUF from the OS file cache.
- **An exit before Ready is Failed at once** — a configuration or memory problem retries the same way. The exit
  code and the last 400 log lines come back with the failure.
- **An exit after Ready is restarted** on the same port, at most **2 times in 10 minutes**
  (`Agent:Process:RestartBudget` / `RestartWindow`); the third exit is Failed with the root cause. A restart is not
  a new operation: it shows as the instance's restart count and in the agent log.
- **Stop** terminates the job object (the whole process tree) and deletes the record. `llama-server` has no graceful
  stop that works for a windowless child and holds no state worth flushing.
- **Logs** are `{state}\logs\ss-{modelId}.log`, 50 MiB per file, `.1` and `.2` kept. They rotate before a start and,
  while the process runs, by copy-and-truncate at each health poll; the few lines written between the copy and the
  truncation are lost, and the process is never restarted for a rotation.
- **After an agent restart** every record is re-checked by PID, creation time and image path, the process is put in
  a new job and the instance reads Starting until the probe passes again. After a **reboot** every record fails that
  check, so the models read Stopped and the master's per-node auto-start starts them again.
- **Orphans.** At start, after adoption, the agent lists processes whose image lies under its runtimes folder and
  whose owner is the service account, and kills one **only** when it is provably its own: the arguments it reads back
  hash to a record's or a pending start's digest, and the process was created after that entry. Anything else — a
  process outside the runtimes folder, another account's, or with a command line it does not recognise — is logged
  as foreign and never touched. The owner's own `D:\AI\llama.cpp\llama-server.exe` is in that category.

---

## 8. Telemetry, the router and jobs

### 8.1 llama.cpp KPIs

With `--metrics`, `llama-server` exposes `llamacpp:*` families; the agent's scrape sends the instance's API key,
because `/metrics` needs it once `LLAMA_API_KEY` is set.

| KPI | llama.cpp source |
|---|---|
| Running requests | `llamacpp:requests_processing` |
| Waiting requests | `llamacpp:requests_deferred` |
| Prompt tokens/s | Δ`llamacpp:prompt_tokens_total` |
| Generation tokens/s | Δ`llamacpp:tokens_predicted_total` |
| Prefix-cache hit rate | Δ`llamacpp:prompt_tokens_cached_total` ÷ Δ`prompt_tokens_total` |
| Spec-decode acceptance | `llamacpp:spec_decode_num_accepted_tokens_total` ÷ `…_draft_tokens_total` |
| Requests done | none on the model; counted by the master's router stats |
| **KV cache usage** | **none** — current llama.cpp removed the gauge. The page reads "—" |
| TTFT / ITL histograms | none ("—"); the benchmark measures them client-side, as it always did |

A KPI a runtime does not report is NaN, and the fleet tiles sum only the finite values while a chart treats a
non-finite point as a gap — so one llama.cpp model never blanks the Dashboard's requests/min or breaks the KV chart.

### 8.2 `/v1` compatibility

The router stays a byte pass-through keyed by served name, with one opt-in exception.

- **Chat, streaming, tool calling, reasoning** pass through. `llama-server` detects tool calling from the Jinja
  template and parses Qwen3/3.5 XML tool calls; reasoning comes back as `reasoning_content`. Test per model in
  acceptance.
- `llama-server` **does not validate the request `model` field** and echoes whatever it gets. Harmless: the router
  has already routed by served name, and the echoed string is the client's own.
- Embeddings are L2-normalised by default, and `encoding_format: base64` works.
- **`dimensions`.** `llama-server` ignores the field and returns all 2560 values, where the DGX's vLLM refuses it
  with 400. The router emulates it, but **only** when the request body carries `dimensions` *and* the model is a
  llama.cpp embedding model with "emulate dimensions" on (the default for embedding models). Then it removes the
  field, forwards the rest, truncates each returned embedding to the first N values and L2-renormalises — the
  correct Matryoshka procedure. A `dimensions` above the native size, below 1, or not an integer is refused 400
  `invalid_request_error`, as vLLM does. A request **without** the field is forwarded and answered byte for byte.
  **OpenViking never sends it** (section 10), so OpenViking is not on this path.

### 8.3 Benchmarks and the small tune

Benchmarks and the small tune work on a llama.cpp model as on a vLLM one, through the master → agent proxy.

- **Suites:** the existing chat suite for chat models (TTFT, ITL, tok/s at 1/2/4/8, capped at `Parallel`), the
  existing embeddings suite for embedding models.
- **A llama.cpp run is always scored on a workload preset.** Its chat levels stop at the slots (≤ 8), so the classic
  goals' N=16 comparison has no measurement and every candidate — the baseline included — would come back "No N=16
  measurement". `POST /models/{id}/tune` maps a classic goal (`Balanced`, `ManyAgents`, `SingleUser`) to the
  matching preset for a llama.cpp model; a vLLM model keeps the classic goal exactly as before.
- **Tune dimensions:** `Parallel` {1, 2, 4, 8}, flash attention {on, off}, KV types {f16, q8_0} (K and V together),
  batch × micro-batch — for a causal embedding model `ub = b` ∈ {1024, 2048, 4096} bounded by `ContextSize`; a
  non-causal embedding model keeps `ub = b = ContextSize` and is not varied. Never context size, GPU layers, quant
  or split. At most 12 candidates; each candidate's estimate goes through admission first, and one that does not fit
  is skipped with its numbers.
- **Provenance** carries `runtime: llamacpp`, `delivery: process`, the node uid, the GPU UUIDs and the llama.cpp
  version. Results from different llama.cpp versions are labelled and compared only when the hardware and runtime
  fields match.
- With two or more fleet members a job on the Windows PC pauses only that node (P2's node-scoped maintenance).

---

## 9. Security

The rules of operations.md section 10 and design-p3p4 §A.8 still hold. What is new or easy to get wrong on a Windows
node:

**Native processes run as the service account.** Every `llama-server` is a child of the service under
`NT SERVICE\mirbadllm-agent` — never LocalSystem, never the owner's account. It can read the models root and
nothing in the owner's profile.

**No secrets on command lines.** The model's API key goes through `LLAMA_API_KEY` in the explicit environment block;
the join and rekey token through `MIRBADLLM_JOIN_TOKEN`; the Hugging Face token never leaves the agent's memory.
argv is logged and returned by `/launch-config`; environment **values** are not. A spec that names any of the eight
variables the agent sets itself (`CUDA_VISIBLE_DEVICES`, `CUDA_CACHE_PATH`, `CUDA_CACHE_MAXSIZE`, `LLAMA_API_KEY`,
`PATH`, `SystemRoot`, `TEMP`, `TMP`) is refused 422 `spec_rejected`, and so is an `ExtraArg` that tries to set the
API key.

**The per-instance API key, and why its ACL is what protects it.** On a desktop PC any local process can reach
`127.0.0.1:81xx`, so each instance gets its own random 256-bit key. The agent proxy strips the client's
`Authorization` header and sets `Bearer <that key>` upstream; the telemetry scrape and the health probe send it too.
`/health` stays open, as llama.cpp designed it.

The key is stored in `processes.json` so adoption works after an agent restart, protected with DPAPI —
`CryptProtectData` with **`CRYPTPROTECT_LOCAL_MACHINE`** (machine scope, so the virtual account needs no loaded
profile) and the **service account's SID as the entropy**. Read the threat model correctly:

> A service SID is **derived from the service name**: anyone can compute `NT SERVICE\mirbadllm-agent`'s SID with
> `sc.exe showsid mirbadllm-agent`. The entropy therefore stops a blob from being unprotected under a *different*
> account's machine key — it does **not** stop another local account that can read the file. What keeps another
> account out is the **ACL on `%ProgramData%\mirbadllm-agent`**: only SYSTEM, Administrators and the service
> account can read it. An administrator can read the keys, by design. If you loosen that ACL, you have handed every
> local model's API key away, entropy or no entropy.

The installer protects that root (`icacls … /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
'*<service SID>:(OI)(CI)M'`) and then resets everything **below** it to inherit (`icacls "<root>\*" /reset /T /C`).
Never run `/inheritance:r … /T` over the tree yourself: the `(OI)(CI)` grants do not apply to files, so every
existing file is left with an empty DACL — the service then gets "Access is denied" on its own `agent.json` and key,
and even an administrator cannot replace or delete them. Check the ACL with
`icacls C:\ProgramData\mirbadllm-agent\state\agent.key`: `(I)` entries for the account, Administrators and SYSTEM
only.

**Executables.** Only logical names from `agent.json` are launchable; the files are verified by SHA-256 against
`manifest.json`; the version folders are read-only for the account; there is no shell anywhere (`CreateProcessW`
with an explicit application name and a command line built by one tested function). Between full hashes the change
detection is size + mtime, and `manifest.json` lives in the folder it describes — so the **admin-only ACL on the
version folder is what stops a same-size tamper with a restored mtime**. A full re-hash runs at every agent start.

**Service rights: `-AllowService` vs `-AllowServiceAutostart`.** This is the one place where a small flag is a real
privilege decision.

| Flag | ACE granted (`sc sdset`) | What it allows |
|---|---|---|
| `-AllowService <name>` | `CCLCLORPWPRC` | query config, query status, interrogate, **start**, **stop**, read control |
| `-AllowServiceAutostart` | `CCLCLORPWPRCDC` | the above **plus `SERVICE_CHANGE_CONFIG` (`DC`)** |

`SERVICE_CHANGE_CONFIG` is not "may change the start type". It is **may change that service's configuration**, its
program path and the account it runs as included. Whoever controls the agent can then make that service run code of
their choosing as that service's account — and on a desktop PC that account is often LocalSystem. The installer
prints this in red:

```
WARNING: NT SERVICE\mirbadllm-agent may now change the configuration of <service>, its program path and account
included: whoever controls the agent can run code as that service's account. Grant it only for services you would
let the agent's account run.
```

Grant it only for a service you would be willing to let the agent's account *be*. The runtime asks for `DC` only
when a start type really changes, and names the flag when it is missing, so a whitelist without autostart still
lets you start and stop from the Services page.

#### Revoking a grant (there is no `-DenyService`)

**Taking a service off the whitelist does not take the right away.** `-AllowService` does two independent things —
it appends one allow ACE for the agent's service SID to *that service's* security descriptor (`sc sdset`), and it
adds an entry to the agent's `scm-whitelist.json`. Deleting the whitelist entry stops the agent from acting on the
service, but the ACE stays, `-AllowServiceAutostart`'s `DC` grant included, and `uninstall` does not undo it either.
Both halves have to go, in this order. Run it **as Administrator on the node**, with `<service>` the Windows service
name (`ComfyUI_Server`), not the whitelist name.

```powershell
# 1. The identity the ACE names: the agent's virtual service account SID.
$sid = (& sc.exe showsid mirbadllm-agent | Select-String 'SERVICE SID:\s*(S-1-5-80-[0-9-]+)').Matches[0].Groups[1].Value

# 2. The service's current descriptor, as one line (sdshow wraps and pads).
$sddl = ((& sc.exe sdshow <service>) | Where-Object { $_.Trim() }) -join ''
if ($LASTEXITCODE -ne 0) { throw "sc sdshow failed: $sddl" }
$sddl                                   # keep this. It is the rollback.

# 3. Drop exactly the installer's ACE for that SID. Every other ACE, the owner, the group, the DACL flags and the
#    SACL are left byte for byte as they were — this is the inverse of the installer's Add-ServiceSddlAce.
$stripped = $sddl -replace ('\(A;;[A-Z]*;;;' + [regex]::Escape($sid) + '\)'), ''
if ($stripped -ceq $sddl) { throw "No ACE for $sid on <service>: nothing to revoke." }

# 4. Write it back, then prove it is gone.
& sc.exe sdset <service> $stripped
if ($LASTEXITCODE -ne 0) { throw "sc sdset failed; the descriptor is unchanged." }
((& sc.exe sdshow <service>) -join '') -notmatch [regex]::Escape($sid)   # must be True
```

Then remove the service's object from the `services` array of
`C:\ProgramData\mirbadllm-agent\state\scm-whitelist.json` (the agent re-reads that file on every call, so the
change takes effect at once, with no restart), and refresh the Services page: the service disappears from it.

Notes:

- **Order matters only for what the operator sees.** Revoke the ACE first and the agent's next call on a still-
  whitelisted service fails with an access error; remove the whitelist entry first and the service simply vanishes
  from the Services page while the ACE is still there. Do both and the state is the one the node had before
  `-AllowService` ran.
- **Rollback** is `sc.exe sdset <service> '<the SDDL from step 2>'`.
- **`-AllowServiceAutostart` needs no separate revoke.** Both rights live in the one ACE (`CCLCLORPWPRCDC`), so
  step 3 removes `DC` with it. To *downgrade* autostart to start/stop instead, re-run
  `install-agent.ps1 -AllowService <service>` **without** `-AllowServiceAutostart`: the installer replaces its own
  ACE rather than adding a second one.
- **A re-created service starts clean.** `sc delete` + `sc create` gives the service a fresh default descriptor, so
  the grant is gone with it; the whitelist entry is not.

**TLS key files.** On Windows the agent's certificates are loaded with `DefaultKeySet`, which persists the private
key under
`C:\Windows\ServiceProfiles\mirbadllm-agent\AppData\Roaming\Microsoft\Crypto\Keys`. A graceful stop leaves none,
but **every unclean exit** (crash, kill, power loss, a failed update) leaves one file per live certificate, for
good. The agent therefore sweeps that folder before it loads any credential — and only when it is really running as
`NT SERVICE\mirbadllm-agent`, checked from its own token, so a console or dev run never touches a real user's
keys.

**Residual risk, unchanged from the design:** a malicious GGUF that exploits a llama.cpp parser bug runs code as the
service account, which can read the models and the agent's state, its private key included. Same class as
"malicious model repo". Mitigation: official, pinned sources and a current llama.cpp.

---

## 10. OpenViking migration runbook (the embedding model)

**Scope.** Only `Qwen3-Embedding-4B` moves, onto **GPU 1 (RTX 5060 Ti 16 GB) by UUID** and nowhere else (O8, D114).
**The query planner `guoxuter/ov_intent_analysis_sft:v7_q8` stays on the DGX exactly as it is** (O7, D113): its
runtime, its served name and OpenViking's whole `query_planner` section are never touched in P3/P4.
`Qwen3-VL-Embedding-2B` stays on the DGX under vLLM until spike S3 passes (O3, D109).

Steps 0–5 are reversible and invisible to OpenViking. **Step 6 is the cut-over and needs the owner's approval**; the
date is the owner's (O9, D115). This is MN-242's runbook.

**0. Inventory (read-only).** Read OpenViking's `ov.conf` on the box — embedding provider, base URL, model name,
`dimension` — and record the vector DB's current dimension and metric. Change nothing. Do **not** read or change
`query_planner`. What spike S2 read on 2026-10-08:

```
provider:  "openai"
model:     "Qwen3-Embedding-4B"
api_base:  "http://192.0.2.191:8080/v1"      # mirbadllm's /v1 on the DGX already
dimension: 1024
input:     "text"
vector DB: local, default cosine metric
```

Also record the exact memory budget of `ss-qwen3-embedding-4b` on the DGX: that budget, and nothing more, is what
this move frees. (The ~30 GB figure includes the planner and the VL model, which both stay.)

**1. Join and approve the PC** as `win-1` (sections 3–4). Check its GPUs and that it reports
`runtime.llamacpp.process`, `downloads.hf-native` and `gguf.inspect`. `downloads.ollama` is not needed.

**2. Create the model with a temporary served name.** `qwen3-embedding-4b-win`, placed on `win-1` GPU 1, source
Hugging Face `Qwen/Qwen3-Embedding-4B-GGUF` at commit `f4602530db1d980e16da9d7d3a70294cf5c190be`, file
`Qwen3-Embedding-4B-Q8_0.gguf`. Settings: embedding on, pooling `last`, `ContextSize` 8192, `Parallel` 2,
`-b = -ub = 2048` (the default), `-fa auto`, f16 KV, emulate dimensions on, GPU 1 reserve 2.5 GiB. Estimate ≈ 8.9 GiB.
Download, then start. **The DGX models keep serving under their real names throughout.**

Q8_0 rather than F16: F16's ~8 GiB of weights would leave too little margin on a 16 GB card carrying the desktop,
and S2 showed Q8_0 is close enough (median cosine 0.99947).

**3. Parity check through mirbadllm's `/v1`, the way OpenViking embeds.** Request **without** `dimensions`,
truncate both sides to 1024, L2-normalise, cosine per item, on S2's fixed text set (`tools/spikes/s2_texts.py`, 195
items; `tools/spikes/s2_parity.py` is the harness) against the DGX model.

> **Pass bar (S2, D123): median cosine ≥ 0.998, minimum ≥ 0.995, and top-10 retrieval overlap mean ≥ 0.95.**

S2 measured median 0.99947, p5 0.99920, min 0.99848 and overlap 0.976 directly against `llama-server`, so the bar
leaves room for run-to-run noise while still catching a wrong pooling, a missing EOS token, a wrong quant or a
broken truncation — each of which drops the cosine far below 0.99. **A miss stops the cut-over and goes back to the
owner.**

**4. Benchmark it on `win-1`** (section 8.3, embeddings suite) and watch GPU 1's headroom with the desktop in normal
use.

**5. Vectors: keep them.** O6 is already decided by S2 (D112/D123): the existing index is kept and nothing is
re-indexed. Only a failure in step 3 reopens that, with OpenViking's own rebuild before or right after the
cut-over.

**6. Cut-over (owner-approved).**

  a. **Stop the DGX copy** `ss-qwen3-embedding-4b`. This frees its DGX memory. `ss-ov-intent-analysis` and
     `ss-qwen3-vl-embedding-2b` keep running on the DGX, untouched.

  b. **Rename the `win-1` model to the real served name.** Served names are unique, so unplace the DGX definition
     first (keep it — do not delete it — for the rollback window).

  c. **OpenViking configuration — the `embedding` section only.** Back up `ov.conf` as
     `ov.conf.bak-<timestamp>` before any edit. If step 0 read what S2 read, **nothing changes and OpenViking is not
     restarted**: the rename in 6b moves its traffic to `win-1` on its own. Keep **`provider: "openai"`** and
     **`dimension: 1024`** exactly as they are: with that provider OpenViking never sends `dimensions`, truncates
     client-side and searches by cosine, which is precisely what step 3 measured. A provider value that *does* send
     `dimensions` would route through the router's emulation instead — renormalised, still cosine-comparable, but
     not the thing that was measured. `api_base` must be mirbadllm's `/v1`
     (`http://192.0.2.191:8080/v1` or `https://mirbad.example.com/v1`), `api_key` a mirbadllm key with embeddings
     scope, `model` the served name. If a key does need editing: edit those keys and `docker restart openviking`.
     **The `query_planner` section is never touched.**

  d. **Smoke test.** OpenViking `health`; one `remember` and one `find` from a client; then watch `/v1` stats for
     the embedding model (on `win-1`) **and** the planner (still on the DGX).

**7. Rollback — any time in the first week.** Rename the `win-1` model back to `qwen3-embedding-4b-win` and start
the DGX copy again under the real name. If 6c changed `ov.conf`, restore `ov.conf.bak-…` and restart OpenViking.
The vectors are identical in both directions, so a rollback needs no re-index. Keep the DGX definition unplaced
rather than deleted until the owner closes the rollback window.

**What can still differ afterwards:** nothing for the embeddings that step 3 did not measure. Sampling defaults
would have mattered for the planner, which is not moving.

**Served names are unique across every definition, stopped ones included** (`JsonModelRegistry`: "ServedName … is
already used by another model"). Stopping the DGX copy is not enough to free the name: rename it away first (the
cut-over below used `Qwen3-Embedding-4B-dgx`), then give the PC model the real name. The rollback is the same swap in
the other order. Turn the DGX copy's `AutoStart` off while it waits, or a DGX reboot brings it back under its parked
name and holds 17 GiB for nothing.

### 10.1 Cut-over log

| Date | What | Who |
|---|---|---|
| 2026-10-15 | **Cut over.** Parity on S2's 195 texts, devpc (llama.cpp `b11476`, Q8_0, RTX 5060 Ti) against the DGX (vLLM): median cosine **0.99948**, min **0.99848**, top-10 overlap mean **0.963**, top-1 agreement 97.1 % — the bar holds, vectors kept (O6). `ov.conf` already read `model: "Qwen3-Embedding-4B"` on mirbadllm's `/v1`, so it was **not** edited and OpenViking was **not** restarted (backup taken anyway: `~/.openviking/ov.conf.bak-mn242-20261015`). DGX `qwen3-embedding-4b` stopped, renamed to served name `Qwen3-Embedding-4B-dgx`, `AutoStart` off, definition kept; `qwen3-embedding-4b-devpc` renamed to `Qwen3-Embedding-4B`, `AutoStart` on. Smoke: OpenViking healthy, one `remember`, one `find`; the DGX's available memory went from 21 to 42 GiB. Planner untouched on the DGX. | owner + Claude (MN-242) |
| 2026-10-22 | **Rollback window closes.** Until then, rollback = rename `Qwen3-Embedding-4B` on devpc away, rename the DGX copy back to `Qwen3-Embedding-4B`, start it. After it, delete or keep the DGX definition at the owner's choice. | owner |

---

## 11. Troubleshooting

### 11.1 The three start-up refusals

These are the ones an operator actually hits on a Windows node. All three are decided before anything serves, and
all three are self-inflicted configuration, not failure.

**(1) `Agent:CacheRoot` missing on a node with Docker off.** The agent exits with exit code 1 and:

```
Agent:CacheRoot is required on a node that runs native processes (Agent:Docker:Enabled = false): a launch's argv
may name it as {cache}.
```

- **Where you read it.** On a console run it is on **stderr**, prefixed `mirbadllm-agent: `. **Under the SCM
  nobody reads stderr**, so the same reason is written to the **Windows Event Log** (Application log, source
  `mirbadllm-agent`) as event **2628**, `The agent cannot start: {Reason}`. `Get-Service mirbadllm-agent` just
  shows Stopped. Look in the Event Log, not at a console:
  ```powershell
  Get-WinEvent -LogName Application -FilterXPath "*[System[Provider[@Name='mirbadllm-agent']]]" -MaxEvents 20 |
    Format-List TimeCreated, Id, LevelDisplayName, Message
  ```
- **The fix.** Add `"CacheRoot": "D:\\mirbadllm\\cache"` to the `Agent` section of
  `%ProgramData%\mirbadllm-agent\agent.json` and start the service.
- **Why you should not see this.** `install-agent.ps1` always writes `CacheRoot` (and `CacheHostPath`) when it
  creates `agent.json`, and MN-236's generated dev `agent.json` does too. **Only a hand-edited `agent.json` loses
  it.** The check deliberately turns what used to be an unhandled `ArgumentException` — which under the SCM looked
  exactly like error 1053 with nothing in the Event Log — into a start-up error with a readable reason.

**(2) Two `Agent:Executables` entries with the same name.** The agent refuses to start with:

```
Agent:Executables lists 'llama-server' twice; put every version of an executable in one entry.
```

- **The fix.** One entry per logical name, with every version in its `Versions` list and one `Default`:
  ```json
  "Executables": [
    { "Name": "llama-server", "Default": "b11476",
      "Versions": [
        { "Version": "b11476", "Cuda": "13.4",
          "Path": "C:\\Program Files\\mirbadllm-agent\\runtimes\\llama.cpp\\b11476\\llama-server.exe",
          "Manifest": "C:\\Program Files\\mirbadllm-agent\\runtimes\\llama.cpp\\b11476\\manifest.json" }
      ] }
  ]
  ```
- **Why it is refused rather than merged.** Each entry derives **its own** default version (`Default`, or the single
  version when there is one). Two entries for `llama-server` would both carry a default, and the lookup for "the
  node's default `llama-server`" takes the first one it finds — so the node's default version, and every launch
  fingerprint through it, would be decided by the order of the objects in the JSON file. Changing the file's
  formatting could silently restart your models on a different build.
- The same validation also rejects an entry without a `Name`, with no versions, with a version missing `Version`,
  `Path` or `Manifest`, with the same name+version twice, or with a `Default` that is not one of its versions.
  `install-agent.ps1 -LlamaCpp` always writes a well-formed entry.

**(3) A node with Docker off and no process delivery.** The agent **starts**, but logs one warning at start
(event **2684**):

```
Agent:Docker:Enabled is false and this node has no process delivery (no IProcessHost and IInstanceKeyProtector,
which only Windows registers by itself): the agent runs but can start no model, and reports no runtime at all
```

Its capability report then carries an **empty runtime list** (no reason, no Degraded state), and every launch,
whatever its kind, is refused 422 `spec_rejected` with the message (the same
`Launch spec rejected: …` shape the driver's own validators use; the `details.reason` is the fragment without the
prefix):

```
Launch spec rejected: this node has no runtime driver for delivery 'process'.
```

- **On Windows this cannot happen.** The composition registers the Windows process host and the key protector
  itself, and removing either guard does not even compile or turns a composition test red. If you see this line,
  you are on a **Linux** agent whose `Agent:Docker:Enabled` was turned off by mistake (or a dev setup that forgot to
  inject the two seams).
- **The fix on Linux:** set `Agent:Docker:Enabled` back to `true` and restart the agent.
- The node is deliberately **not** refused a start in this state: it still enrolls, reports, serves telemetry, runs
  downloads (`downloads.hf-native` follows the Docker switch) and host services — and refusing it would also refuse
  the Linux CI leg that proves the Windows guards. The warning exists so the state is visible as something other
  than a mysteriously empty runtime list.

### 11.2 Service error 1053

`Error 1053: The service did not respond to the start request in a timely fashion` is the SCM giving up after
~30 seconds because the process never told it that it had started. It was the symptom before P4: the agent simply
did not connect to the Service Control Manager. The fix is in the agent, not in your configuration — it hosts
itself as a Windows service and reports RUNNING when the host starts.

So on a P4 agent, **1053 means one of these**:

| Cause | Check | Fix |
|---|---|---|
| The service runs an agent build from **before** P4 | `Get-Service mirbadllm-agent` Stopped, nothing from source `mirbadllm-agent` in the Application log | `install-agent.ps1 upgrade` to a P3/P4 bundle |
| **`Agent:NodeId` in `agent.json` is not a node id** — a capitalised machine name such as `DevelopmentPC` | also **nothing at all** from source `mirbadllm-agent` in the Application log, so it looks exactly like the row above. Tell them apart by the file: `(Get-Content C:\ProgramData\mirbadllm-agent\agent.json \| ConvertFrom-Json).Agent.NodeId` against lower-case letters, digits and `-`, at most 40. Run from a console the agent prints `Agent:NodeId 'DevelopmentPC' is not a node id …` and exits code 2 | `install-agent.ps1 install -NodeId <lower-case id>` — since MN-239 the installer refuses such a value instead of writing it (its old `-NodeId` check was case-insensitive while the agent's is not), and an explicitly passed `-NodeId` now updates an existing `agent.json`. Use the **same** id the master's "Add node" command used; if the node already enrolled under a different one, `rekey` after fixing the file |
| The process died before it could report RUNNING | Application log, source `mirbadllm-agent`, event **2628** `The agent cannot start: …` | read the reason — usually section 11.1 (1) |
| Slow work before the host starts | the start takes tens of seconds every time | Only the code before the host starts counts against the 30 s, and on Windows that is file reads plus process adoption by handle. A Docker wait would blow it, which is why Docker is off. Report it: nothing in the shipped configuration does slow work there |

What **cannot** cause 1053: a slow hosted service, the executable catalogue's first 712 MiB of hashing, the Ready
probes of adopted processes, or the TLS key sweep. All of those run after RUNNING is reported (a deliberate
40-second blocking start in spike S4 still reached RUNNING in 0.5 s). If one of them hangs, port 9443 stays closed
and the node reads Offline — but the service shows Running and the SCM is content.

Related but different: `1053` also appears in the agent's **own** SCM client, as `ERROR_SERVICE_REQUEST_TIMEOUT`,
when a host service you started or stopped from the Services page did not finish in time. That is about the other
service, not about the agent.

### 11.3 WDDM memory and external VRAM use

Section 5.3 explains the mechanism. The symptoms:

| Symptom | Cause | What to do |
|---|---|---|
| "Memory by model" shows nothing measured on a Windows node | WDDM: `nvidia-smi --query-compute-apps` reports `[N/A]` for every process, by design | Read the budget and the "measured at start" figure instead. Nothing is broken |
| A model's "measured at start" is far below its budget | The estimate is 17–18 % conservative, and a cold start can measure low because the desktop released memory at the same moment | Normal. Only "uses X GiB **more** than its budget" is worth acting on |
| Node Degraded `gpu_external_use_high` | Something other than mirbadllm holds more VRAM on that GPU than its reserve — a game, ComfyUI, Blender, the owner's own `llama-server` on port 8000 | Close it, or raise that GPU's reserve in node Settings. Running models keep serving; the reason clears after two samples back under the reserve |
| A start is refused with exact numbers while the headroom bar looks fine | Admission reads live `nvidia-smi` free memory minus the reserve; the bar is a sample | Free VRAM, lower the budget, or use **Start anyway?** (which skips memory admission only, never the start rule) |
| `gpu_external_use_high` never raises, or never clears | A GPU is not judged when `memory.used` is missing, `processes.json` is unreadable, or an instance on it has no measurement yet; a GPU the master's reserves no longer cover is dropped entirely | Check the GPU still has a reserve in node Settings; check the state directory is readable by the service account |
| After an agent restart, no external-use judgement at all | `{state}\gpu-reserves.json` was lost or unreadable (event 2682 in the agent log) | Nothing: the reserves are re-learned from the next launch, successful or refused |
| Free memory looks enormous in a llama.cpp log | Under WDDM CUDA reports far more free VRAM than the card has | Ignore it. The agent never takes a free-memory figure from CUDA or llama.cpp |

### 11.4 Other symptoms

| Symptom | Likely cause | Fix |
|---|---|---|
| Node Degraded `executable_unverified` | A file under `runtimes\llama.cpp\{version}\` changed, or an unlisted `.exe`/`.dll` appeared there | Re-run `install-agent.ps1 -LlamaCpp <version>` (it re-installs a folder that fails verification). Investigate first: that folder is admin-only |
| Launch answers 422 `executable_unverified` | The model names a version that is not verified | As above, or pin a verified version on the model |
| Launch answers 422 `executable_missing` | The model names a logical name or version `agent.json` does not list | Install that llama.cpp version, or clear the model's pinned version to use the node default |
| Model Failed before Ready, no restart | Configuration or memory. The exit code and the last 400 log lines come with the failure | Read them, then `{state}\logs\ss-{modelId}.log`. An exit before Ready is never retried |
| Model Failed after 2 restarts | Crash loop after Ready | The third exit carries the root cause; check the log tail and the GPU's free memory |
| Model stuck Starting for minutes | A large GGUF from a cold disk; `StartTimeout` is 10 min | Wait, or watch the log. `llama-server` answers 503 `Loading model` on everything until it is ready |
| A model's port is not the one you expected | The agent skips every port a process already listens on, then test-binds | Normal. `PortRangeStart` 8101 keeps it away from 8000 |
| The installer stops naming `msvcp140.dll` | No Visual C++ 2015–2022 x64 redistributable | Install it from Microsoft. The installer never downloads it |
| The installer refuses the driver | Below the lock file's `minDriver` (`615.00`) | Update the NVIDIA driver, or pass `-Accelerator cpu` to run models on the CPU |
| A Vulkan node is Degraded with `gpu_probe_failing`, and a GPU shows "—" for Used and Free | The agent cannot read the GPU memory counters, or Windows lists none for that card | Check that `NT SERVICE\mirbadllm-agent` is in **Performance Monitor Users** (the installer adds it) and restart the service. Until then starts on that GPU are refused |
| Red warning: the Windows Firewall is off | It is off on all three profiles | The 9443 rule then has no effect and 9443 is reachable from any network the PC joins. Owner's call |
| Node Online but no llama.cpp runtime listed | `Agent:Executables` is empty (`install-agent.ps1 install` without `-LlamaCpp`) | `install-agent.ps1 -LlamaCpp` |
| Editor's runtime control is disabled | The selected node reports no llama.cpp runtime | Pick the Windows node; check its capabilities |
| Download Failed, error starts `Paused (needs token)` | The agent restarted without the Hugging Face token | Start the download again with the token; it resumes from `{folder}.partial` |
| `GET …/gguf` answers 400 listing candidates | Several model GGUFs in the folder | Pass `?file=<name>` (the editor's picker does) |
| `GET …/gguf` answers 422 `gguf_invalid` | The file is not a valid GGUF, or breaks a reader bound | Re-download; check the LFS hash |
| Admin call answers 400 `node_required` | Two or more fleet members and no `?node=` | Section 6 |
| Admin call answers 409 `invalid_state` | The node is enrolled but not approved | Approve it first |
| Node Degraded `clock_skew` | Windows Time is not syncing | `w32tm /query /status`; start `w32time` |
| `/v1` request answers 503 for ~15 s after a service restart | The master could not reach the agent while it restarted; the model itself kept running | Nothing. The model keeps its PID and is adopted |

### 11.5 Agent log lines

The agent writes to the Windows **Event Log** (Application log, source `mirbadllm-agent`) when it runs under the
SCM: Information and above from the agent, Warning and above from the framework. The same lines go to the console on
a console run.

| Id | Line (start) | Meaning |
|---|---|---|
| 2628 | `The agent cannot start: {Reason}` | A start-up refusal, section 11.1 (1) |
| 2684 | `Agent:Docker:Enabled is false and this node has no process delivery …` | Section 11.1 (3) |
| 2141 | `Executable {Name} {Version} is unverified: {Problem}. Launches naming it get 422 executable_unverified.` | Section 4.4 |
| 2680 | `Remembered the reserves of {Count} GPU(s) in {Path}` | Debug; the GPU reserves were persisted |
| 2681 | `The learned GPU reserves could not be written to {Path}; …` | The next restart may not judge external use until a launch |
| 2682 | `{Path} could not be read; the GPU reserves are re-learned from the next launch` | `gpu-reserves.json` is missing, damaged or foreign |
| 2683 | `A launch's GPU reserves could not be remembered; gpu_external_use_high keeps the reserves it had` | The learning hook threw; the launch itself is unaffected |
| — | `GPU {Gpu}: {N} GiB used outside mirbadllm's models, above the {R} GiB reserve (gpu_external_use_high).` | The reason was raised |
| — | `GPU {Gpu}: external use back to {N} GiB, within the {R} GiB reserve.` | It cleared |

> **Every id names one line.** The five P3 lines above were first shipped on 2640–2644, which the P2 memory-model
> lines (operations-p2 section 5.1) already used, so the number alone did not identify a line. MN-239 moved them to
> 2680–2684 — 2680, 2681, 2682 and 2683 are the four GPU-reserve lines, 2684 is the no-process-delivery warning — and
> `P3AgentCompositionTests.EveryLogEventIdOfTheAgentProcess_IsUsedByExactlyOneMessage` keeps every id in the agent and
> the node runtime unique. **An agent older than MN-239 logs these five as 2640–2644**; if you are reading the Event
> Log of a node installed before then, match the text.

---

## 12. Configuration reference (new in P3/P4)

### 12.1 Agent (`agent.json`, `Agent` section)

The template is [`deploy/agent/agent.windows.example.json`](../../deploy/agent/agent.windows.example.json).

| Field | Type | Default | Purpose |
|---|---|---|---|
| `Docker:Enabled` | bool | `true` | **The composition gate** (section 2). `false` on Windows |
| `CacheRoot` | path | unset | **Mandatory with Docker off**; `{cache}` in a launch's argv, and the home of `cuda\` and `tmp\` |
| `Executables[]` | array | empty | One entry per logical name: `Name`, optional `Default`, and `Versions[]` of `{Version, Path, Manifest, Cuda?}`. Empty = no process runtime |
| `Accelerator` | string | unset | `cpu` or `vulkan` on a node without a usable NVIDIA GPU (section 4.6); unset or `cuda` on an NVIDIA node. Written by `install-agent.ps1 -LlamaCpp -Accelerator`. It may not be combined with an `Agent:MemoryModel` that contradicts it |
| `Process:StartTimeout` | TimeSpan | `00:10:00` | How long a process may stay Starting before it is Failed |
| `Process:RestartBudget` | int | `2` | Restarts allowed after Ready within the window |
| `Process:RestartWindow` | TimeSpan | `00:10:00` | Window of the restart budget |
| `Process:LogMaxBytes` | long | `52428800` | Size at which `logs\ss-{id}.log` rotates |
| `Process:LogFilesKept` | int | `2` | Rotated files kept (`.1`, `.2`) |
| `Process:HealthPollInterval` | TimeSpan | `00:00:02` | `/health` poll while Starting; also the log-rotation check |

`StartTimeout`, `RestartWindow` and `HealthPollInterval` must be positive; `RestartBudget` and `LogFilesKept` must
not be negative and `LogMaxBytes` must be at least 1.

Earlier fields that matter on a Windows node: `PortRangeStart` (8101 in the example, away from 8000),
`MemoryReserveGiB` (the host RAM reserve; 4 in the example), `UseContainerNetwork` false, `HostdExpected`,
`ManageSystemdUnits`, `ManageContainers` and `ManageBackupMount` all false, `ShutdownTimeout` (the installer sets
the service's `PreshutdownTimeout` to this + 10 s). `DockerEndpoint` and the image settings are unused while Docker
is off, and `ApprovedImages`/`AllowTagPulls` do not apply to a GGUF.

**Ignored on a node whose host services go through the SCM** (section 2 and 5.2: a process node with a Service
Control Manager — every Windows node): `ManageSystemdUnits`, `ManageBackupMount` and `HostdExpected`. The composition
forces all three off there, whatever `agent.json` says, because such a node has no systemd, no hostd socket and no
backup mount; the example sets them to false so the file matches what the node does. Until MN-239 they were read
straight out of the file, so turning `ManageSystemdUnits` on made a Windows node report `services.systemd` and
`gpu-processes` beside `services.scm`, replace the SCM's `hostServices` block with manager `systemd`, and raise
`hostd_missing` with its Linux wording about an SCM that was answering fine. `ManageContainers` still applies in
principle; Docker being off already turns `services.docker` off (section 2).

### 12.2 Files on a Windows node

| Path | Content |
|---|---|
| `C:\Program Files\mirbadllm-agent\app\{version}\` | Agent binaries, one folder per version (last 3 kept) |
| `C:\Program Files\mirbadllm-agent\runtimes\llama.cpp\{bNNNN}\` | llama.cpp + cudart DLLs and `manifest.json`; read-only for the account |
| `%ProgramData%\mirbadllm-agent\agent.json` | Configuration |
| `%ProgramData%\mirbadllm-agent\state\` | Identity and CA, the operation log, fencing, `processes.json`, `gpu-reserves.json`, `scm-whitelist.json`, `logs\` |
| `D:\mirbadllm\models\` | `ModelsRoot`: `{folder}\…gguf`, `{folder}.partial\` |
| `D:\mirbadllm\cache\` | `CacheRoot`: `cuda\` (CUDA JIT cache, capped at 4 GiB), `tmp\` |

`%ProgramData%\mirbadllm-agent` is readable only by SYSTEM, Administrators and the service account (section 9).
`uninstall` keeps it.

---

## 13. Deployment

P3/P4 reaches the DGX the same way P2 did: when the owner merges `feature/multinode-p3` into `main`, which runs
`deploy/ci-deploy-multinode.sh` on the self-hosted runner. What is new on the DGX itself is only the master and
agent images; the DGX agent keeps Docker on, so its composition, launches, hashes, capabilities, admission and
`/v1` behaviour are unchanged, and `deploy/.env` needs no change. The master image gains one Dockerfile stage that
cross-compiles the `win-x64` agent bundle from Linux, so no Windows CI runner is involved.

**Downgrading the master after a llama.cpp model exists is not supported.** `models.json` gains optional `llamaCpp`
fields and an `ollama` source kind, which a P2 master would refuse.

The owner's live acceptance checklist — join, approve, model, `/v1`, parity, robustness (service restart, process
kill, reboot, external VRAM, upgrade, rollback, rekey, clock skew), benchmark, tune and the Services page — is
plan-p3p4.md §4.2 and is run on the owner's PC (MN-241). It is not repeated here.

---

## 14. What is not in P3/P4

### Deferred, with the contract already frozen

| Design | State in the code |
|---|---|
| **Ollama-registry source** (`ModelSource.Kind = ollama`, `OllamaSource`, `downloads.ollama`, `unsupported_manifest`) | Contract frozen; the puller, the editor source and the admin API are **not built** (O4 + O7, D110/D113). `POST /api/downloads` with `source: "ollama"` answers 501 `not_supported`. The manual fallback is `install-agent.ps1 -ImportGguf` |
| Moving the **query planner** off the DGX | Not done, by owner decision (O7, D113). It would need the Ollama puller and a planner parity run |
| Moving **`Qwen3-VL-Embedding-2B`** | Waits for spike S3 (O3, D109). There is no official GGUF; community GGUFs with an mmproj exist, and llama.cpp image embeddings have open issues including one on Windows CUDA |
| llama.cpp **containers on the DGX** (own arm64 image, D12) and **native user units on Linux** | Moved to a later phase ("P3b"). The driver is already split into an OS-neutral core and an OS host, so a Linux host is additive |
| `--mmproj` **vision chat** | The editor can store it; nothing else uses it |
| A "Windows Firewall is off" **reason on the node page** | Needs a new capability field. The installer warns instead |
| `-DenyService` / an automatic revoke of a service grant | Not built. Section 9 has the manual `sc sdset` revert |
| `/metrics` `node` label, `NodeRecord.MaxConcurrentStarts` on the wire, agent self-update and `agent_outdated` | Unchanged from operations-p2 section 16 |
| macOS | Later |

### Unproven, covered by tests only

- The **two-GPU path**: the reverse-order two-UUID `--list-devices` check and `--tensor-split` across both cards
  were not run on the owner's PC (both open a context on the display card, which the owner ruled out). The code is
  tested; nobody has run it on the hardware.
- Loading the planner's `qwen35` GGUF on the pinned build. The architecture has been supported since `b7990`, but
  that specific file has not been loaded.
- `llama-fit-params` as a second opinion on the estimate. It ships in the pinned folder and is not used.
- **Vulkan nodes** (section 4.6). The GPU readings were compared with `nvidia-smi` on NVIDIA cards and the device
  selection with the pinned build's `--list-devices`. No model was loaded through Vulkan, nothing ran on an AMD or
  Intel graphics card, and the counters were not read under the service account.
- **CPU-only nodes** on a machine of their own. The CPU build's files were verified against the lock file and it
  starts and lists no device; no model was served from it, and its speed was not measured.
- **Tuning on a CPU-only node.** The tuner's memory check still uses the GPU estimate, which adds 0.45 GiB of CUDA
  context, so its plan skips a candidate unless the model's budget has that much room above the CPU estimate.

---

## 15. See also

- [operations.md](operations.md) – P1 operator guide (architecture, enrollment, mTLS, backup v2, fencing)
- [operations-p2.md](operations-p2.md) – P2 operator guide (Linux PCs, discrete GPUs, moves, jobs, takeover, audit,
  CA rotation); its sections 4, 5, 6–12 and 15 apply to a Windows node unchanged
- [`deploy/agent/windows/README.md`](../../deploy/agent/windows/README.md) – Windows agent packaging and
  `install-agent.ps1`
- [design-p3p4.md](design-p3p4.md) – the P3/P4 design addendum (§A.1 driver, §A.2 GGUF, §A.3 router, §A.4 Windows
  agent, §A.5 migration, §A.8 security)
- [design.md](design.md) – architecture and decisions (D107–D131 for P3/P4)

---

*Last updated: 2026-10-09 (MN-238, first written; checked against `feature/multinode-p3` at `2c9ccc1`)*
