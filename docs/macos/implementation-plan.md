# macOS nodes: llama.cpp (Metal) and MLX — implementation plan

Status: plan, 2026-10-16. Owner decision: Apple Silicon Macs join the fleet as worker nodes and serve models with
**both** llama.cpp (Metal, GGUF) and **MLX** (`mlx_lm.server`, MLX safetensors). vLLM stays Linux/NVIDIA only.

| Phase | State (2026-10-17) |
|---|---|
| 0 | Merged (#147): Mac CI leg, TLS on macOS, spikes S1–S5 (§7). |
| 1 | Merged (#149–#151): `mac-1` enrolled and Online with unified memory and Metal's working set. |
| 2 | Merged (#152). Checked end to end on `mac-1` (2026-10-17): Qwen3-0.6B Q8 GGUF added, started, answered through the router, both plain and streamed. |
| 3 | Merged (#153). Checked end to end on `mac-1`: Qwen3-0.6B 4-bit MLX model started and answered through the router, both plain and streamed. A pushed agent update left its process running, but adoption failed (empty readiness alias); fixed in Phase 5. |
| 4 | Merged (#155). Checked: the agent's samples show the Apple GPU at 77-82% while a model generates; the agent lists 14 launchd jobs through hostd (deny list applied). |
| 5 | In progress (this PR): a new node's reserve sized to its memory; MLX adoption after a restart or update; MLX responses report the served name; README and design docs. Pushed agent updates checked on `mac-1` (MLX model running) and the Windows PC. |

Phase 3 decisions taken while building it (beyond §3):
- The executable is the virtualenv's `python`, started as `python -I -u -B -m mlx_lm.server`: isolated mode ignores
  every `PYTHON*` variable, and the recorded argv matches what `KERN_PROCARGS2` reads back, so the orphan sweep works.
  CPython is installed **inside** the version folder (`uv python install` with `UV_PYTHON_INSTALL_DIR`), so the process
  image is under the folder too.
- `mlx_lm.server` parses flags with argparse, which accepts abbreviations (`--hos` = `--host`). Flags are allow-listed
  by exact spelling (`MlxFlags.Owned` + `AllowedInExtraArgs`), on the master at save time and on the agent at launch.
- The manifest lists every file (`"allFiles": true`, ~12 100 files, 518 MB). An unlisted `.py`/`.pth` would be code
  Python runs. uv's minor-version folder link is removed, because .NET's enumeration follows folder links.
- `/v1/models` lists the model folder's real path, which is the readiness check's "alias". It fails unless
  `HF_HOME/hub` exists, so the driver creates it.
- The proxy sets `model` to `default_model`, removes `draft_model`/`adapters`, and refuses (400) a body .NET cannot
  parse as one object with unique names. Python's `json` would read such a body differently (last duplicate wins,
  `NaN`).

Phase 4 decisions taken while building it:
- The Mac reports **no** GPU in its capabilities: a `GpuCapability` would bring a GPU picker into placement and
  `--device` arguments into llama.cpp launches, and Metal needs neither. The Apple GPU appears in telemetry only (one
  `GpuSample` with utilisation and the chip's name, no UUID, no memory, so the node stays unified), and the GPUs tab
  shows it from there.
- hostd drives launchd as root over the same socket API as on Linux. A unit is a label. **Stop** is `bootout` (a
  `KeepAlive` job would come straight back from a kill), **start** bootstraps the job's plist when it is not loaded,
  then kicks it, and **autostart** is launchd's enabled/disabled override. launchd will not load a disabled job, and
  hostd says so rather than passing on launchd's "Input/output error".
- The deny list protects what shares this Mac: Apple's jobs, mirbadllm's own, the GitHub Actions runners of other
  organisations, Docker and SSH. RustDesk is not denied. As on the DGX it can be whitelisted as a protected entry.
- No `gpu-processes` and no backup mount on a Mac: macOS has no per-process GPU accounting, and backups live on the
  master.

## 1. What exists today

The groundwork is partly in place (file references are to `main` at `fcf9d54`):

| Already there | Where |
|---|---|
| OS family `"macos"` reported by the agent, `arm64` mapped | `Agent/Hosting/AgentCapabilityReporter.cs:58-61, 83-94` |
| `MemoryModel.Unified` (documented for Apple Silicon); no GPU → Unified fallback | `Core/NodeCapabilities.cs:17-26`, `Agent/AgentMemoryModel.cs:24-59` |
| `RuntimeKind.Mlx` enum value, labels, fingerprint, fit weight | `Core/ModelDefinition.cs:221`, `PlacementValidator.cs:509`, `PlacementFit.cs:492` |
| `IProcessHost` seam meant for more hosts than Windows | `NodeRuntime/Abstractions/IProcessHost.cs:74-124` |
| `HostServiceManagers.Launchd`, `JoinCommands.Macos`, the UI's macOS tab | `Agent.Contracts/HostServices.cs:441`, `Core/NodeApi.cs:90`, `JoinCommandView.razor:101-210` |
| Agent reverse proxy: model ports bind to 127.0.0.1 only | design §7.2 (so MLX needs no API key of its own) |
| macOS-aware case-insensitive path checks (some) | `PathConfinement.cs:14`, `ModelRootPaths.cs:30` |

Missing: everything that runs a model on a Mac — a process host, a memory probe, a key protector, CUDA-free launch,
Mach-O executables, a macOS install/bundle, the MLX runtime end to end, Apple GPU telemetry, launchd host services and
macOS CI.

## 2. The Mac on hand (measured 2026-10-16)

| | |
|---|---|
| Host | `<mac-host>`, Mac mini (Mac14,3), **Apple M2**, 8 CPU (4P+4E), 10-core GPU, Metal 4 |
| Memory | **8 GiB** unified, no swap configured, `iogpu.wired_limit_mb = 0` (macOS default GPU limit) |
| Disk | 228 GiB volume, **~20 GiB free** |
| OS | macOS 27.0.1 (26A434), Xcode installed (`codesign`, `xcode-select`) |
| Tools | `git`, `curl`, `jq`, system Python 3.9.6; **no** .NET, Homebrew, `uv`, MLX, llama.cpp |
| Runners | Shared with other GitHub runners; none registered to this repository yet |

Consequences for the plan:
- 8 GiB unified with a default GPU working-set limit of roughly two thirds of RAM leaves **~5 GiB for a model**:
  small models only (e.g. a 4B model at 4-bit ≈ 2.5 GiB plus KV cache, small embedding models). Admission must use the
  GPU working-set limit, not total RAM (§4.3).
- ~20 GiB free disk holds a handful of small checkpoints; the disk-space check before downloads matters here.
- The same Mac runs other runners: the mirbadllm runner gets its own instance with concurrency 1
  and Mac CI is gated by a variable, like Windows CI.

## 3. Decisions (proposed; confirm before Phase 1)

| # | Decision | Why |
|---|---|---|
| M1 | Agent runs as a **LaunchDaemon** under a dedicated hidden user `_mirbadllm`, like the Linux systemd service | Starts at boot without a login; no root at runtime |
| M2 | Native models run as **detached child processes** (`posix_spawn` + `setsid`), recorded in `{state}/processes.json` with PID, start time and operation id, re-adopted at agent start — the Windows design (§6.6), not per-model launchd jobs | One supervisor model for Windows and macOS; a non-root daemon cannot bootstrap jobs into the system domain; the agent plist sets `AbandonProcessGroup` so agent restarts leave models running |
| M3 | Per-instance keys stored in a file readable only by `_mirbadllm` (mode 0600), not the Keychain | A daemon user has no unlocked login keychain |
| M4 | Model ports bind `127.0.0.1`; the master reaches models only through the agent's mTLS proxy | Already decided (§7.2); MLX has no API-key option and needs none |
| M5 | llama.cpp: official `llama-bNNNN-bin-macos-arm64.zip`, pinned by version + SHA-256 in `deploy/agent/macos/llama-cpp.lock.json` | Same pinning as Windows; Metal is built in |
| M6 | MLX: `mlx-lm` in a per-version virtualenv created with a pinned standalone **`uv`** and a hash-locked requirements file; logical executable `mlx-lm-server` | Reproducible, no Homebrew, no system Python changes |
| M7 | Admission on a Mac budgets against the **GPU working-set limit** (`iogpu.wired_limit_mb` when set, else the macOS default fraction of RAM), with the host reserve on top | A model over that limit fails at load, not at admission |
| M8 | Apple GPU telemetry from IOKit (`IOAccelerator` → `PerformanceStatistics` "Device Utilization %"), no `sudo`; power and temperature report "—" | `powermetrics` needs root; honest numbers rule |
| M9 | The macOS agent bundle is cross-published (`osx-arm64`) in the existing image build; the installer ad-hoc signs it (`codesign --force -s -`) if it is not already signed | Arm64 macOS refuses unsigned code; curl downloads carry no quarantine flag, so no notarization is needed for a LAN install |
| M10 | macOS host services (Ollama, LM Studio, ComfyUI) go through a small root helper — a macOS build of **hostd** as a LaunchDaemon on a Unix socket — not through the agent | The agent stays non-root, as on Linux |

## 4. Phases

Each phase ends with a merged PR, the full unit suite green, and its exit criteria shown on the real Mac.

### Phase 0 — Foundations and spikes

Work:
1. **Runner.** Register a runner instance on the Mac for this repository, own directory
   `~/actions-runners/runner-mirbadllm`, labels `self-hosted, macOS, ARM64, mirbadllm-mac`, installed as its own
   launchd service. Install the **.NET 10 SDK** per user (`dotnet-install.sh` into `~/.dotnet`) and `uv` once on the
   host — the runner's image, not per-job downloads.
2. **CI job.** `test-macos` in `ci.yml` on `[self-hosted, macOS, mirbadllm-mac]`, gated by `vars.MAC_CI == '1'`
   (as `test-windows`), `concurrency: mac` so it never runs twice at once.
3. **Spikes**, each a short throwaway program run on the Mac, answers recorded in this file:
   - **S1 Metal from a LaunchDaemon user.** Does `llama-server` with Metal (and `mlx_lm.server`) work when started by
     a LaunchDaemon running as `_mirbadllm`, with nobody logged in? If not: fall back to a LaunchAgent in an
     auto-login user's session (and record the trade-off).
   - **S2 Signing.** Does a `dotnet publish -r osx-arm64` single-file built on the DGX run on the Mac as is, or only
     after `codesign -s -`?
   - **S3 TLS.** Does the agent's PFX loading (`TlsCredentials.cs:25`, `EphemeralKeySet` off Windows) work on macOS?
     (`EphemeralKeySet` is not supported there; expect a change to a persisted/ephemeral-free path.)
   - **S4 mlx-lm server.** Pin a version; record its endpoints (`/v1/chat/completions`, `/v1/completions`,
     `/v1/models`, health), concurrency behaviour, streaming usage fields, start time and peak memory for a 4-bit
     ~1–4B model.
   - **S5 GPU limit.** Read the Metal `recommendedMaxWorkingSetSize` once (tiny Swift snippet) and compare with the
     fraction M7 will use, so admission matches what Metal actually allows.

Exit: the Mac runner picks up `test-macos` and runs the existing unit suite green; S1–S5 answered in §7.

### Phase 1 — The Mac joins the fleet (agent core, no models yet)

Agent:
- `MacMemoryProbe : IMemoryProbe` — `hw.memsize` and `host_statistics64` (free + inactive + purgeable + speculative as
  available) via `libSystem` P/Invoke; registered on `OperatingSystem.IsMacOS()` next to
  `AgentServiceCollectionExtensions.cs:141-144`.
- GPU working-set limit (M7) reported in the capabilities so the master's fit uses it.
- `Program.DefaultConfigPath` (`Program.cs:317`) for macOS: `/Library/Application Support/MirbadLLM/agent.json`.
- S3 fix for TLS credential loading on macOS.
- Case-insensitivity made consistent with `PathConfinement` in `ExecutableCatalog.cs:50`,
  `ProcessRuntimeDriver.cs:290/321/616`, `NativeDownloadService.cs:57` (APFS is case-insensitive by default).
- `NativeDownloadService` (Docker off) already runs off-Windows; verify disk-space and range downloads on APFS.

Packaging and install:
- `osx-arm64` added to `AgentBundleCatalog.Rids` (`Web/Api/AgentBundleEndpoints.cs:21-60`) and to the multi-RID
  publish in `deploy/Dockerfile:47-90` (tar.gz like Linux).
- `deploy/agent/macos/install-agent.sh`: checks arm64 and macOS version; creates `_mirbadllm`
  (`sysadminctl`/`dscl`, hidden, no shell); lays out `/usr/local/lib/mirbadllm-agent` (binaries),
  `/Library/Application Support/MirbadLLM` (config, state, keys), `/Library/Logs/MirbadLLM`; ad-hoc signs (M9);
  enrolls with the join token; writes `/Library/LaunchDaemons/com.mirbadllm.agent.plist` (`KeepAlive`, `RunAtLoad`,
  `AbandonProcessGroup`, `UserName=_mirbadllm`, log paths) and `launchctl bootstrap system`; supports `--rekey`,
  `--uninstall`, `package` like the Linux script.
- `deploy/agent/agent.macos.example.json` (Docker off, MemoryModel Unified, models root, cache root, executables).
- Master: `MacosInstallerLine` builds `JoinCommands.Macos` (uname → `osx-arm64`); the Add node dialog's macOS tab
  shows it.
- CI lint: `bash -n` and `plutil -lint` on the new script and plist (`deploy-artifacts`).

Tests: unit tests for the probe's parsing, the installer line, the bundle catalog; Mac-only integration tests (skipped
off macOS) for the probe and config path.

Exit: from the Add node dialog, the macOS command enrolls the Mac; it shows **Online** on Nodes with its unified
memory headroom, survives a reboot, and appears in the join/revoke/rekey flows.

### Phase 2 — llama.cpp on the Mac (Metal)

Agent:
- `MacProcessHost : IProcessHost` (M2): `posix_spawn` with `POSIX_SPAWN_SETSID`, stdout/stderr redirected to the
  per-model rotating log through spawn file actions, fixed environment; `OpenAsync` re-adopts by PID + start time
  (`proc_pidinfo`) and verifies the command line (`sysctl KERN_PROCARGS2`); `Kill` sends SIGTERM then SIGKILL after the
  grace period; `ListCandidates` finds stray `llama-server`/`mlx_lm.server` children of a previous agent.
- `FileInstanceKeyProtector : IInstanceKeyProtector` (M3).
- `AddMacSeams` registration next to `AddWindowsSeams` (`AgentServiceCollectionExtensions.cs:465-474`), so
  `AddProcessRuntime` wires the driver on macOS.
- `ProcessRuntimeDriver.BuildEnvironment` (`:803-836`) made backend-aware: no `SystemRoot`/`CUDA_*` on macOS, `PATH`
  with the executable folder plus `/usr/bin:/bin`, `DYLD_*` untouched (the release zip loads its dylibs with
  `@rpath`).
- NVIDIA-only process-node add-ons (`AgentServiceCollectionExtensions.cs:487-505`: start meter, GPU process probe,
  external-GPU-use contributor, GPU reserve, GPU budgets) registered only when NVIDIA GPUs exist; unified fit otherwise.
- `ExecutableCatalog` (`:460`): Mach-O and `.dylib` aware; the manifest's `Cuda` field generalised to a **backend**
  (`cuda-13.4`, `metal`), kept readable for existing Windows manifests.
- `LlamaCppRuntimeContributor` reports `runtime.llamacpp.process` with backend `metal` and its versions.
- Installer: downloads and verifies the pinned macOS llama.cpp zip (M5) into the executables root; `--update-llama`.

Master:
- `LlamaCppArgsBuilder.Devices` (`:17-30, 298`): on a Metal node no `--device CUDA…`; GPU offload via `-ngl` as today;
  "None" (CPU only) becomes `-ngl 0 --device none` instead of `CUDA_VISIBLE_DEVICES=-1`.
- `ProcessLaunchValidator` accepts the Metal device form.
- Placement on a Mac uses the unified path (`PlacementValidator.CheckUnified`, `PlacementFit`) with the working-set
  limit (M7); the GPU picker is hidden for Metal nodes; the editor's llama.cpp estimate uses the same limit.
- Move rules: a llama.cpp model may move between Windows and Mac (GGUF is portable); the move dialog's runtime check
  already greys out nodes without the runtime.

Tests: unit tests for args (Metal, CPU only), environment building per backend, catalog Mach-O checks, the process
records; Mac integration tests: spawn → health → adopt after an agent restart → kill, with a tiny GGUF (≈100 MB).

Exit: on the Mac, a small GGUF model (e.g. Qwen3-0.6B/1.7B Q4) is added through the wizard, started, chatted with in
the Playground through the router, survives an agent restart (adopted, not restarted), fails cleanly on a crash
loop, and its logs show in the Logs tab.

### Phase 3 — The MLX runtime

Core and master:
- `MlxSettings` on `ModelDefinition` (model folder, served name, max tokens default, chat-template arguments,
  `trust_remote_code` off by default, optional draft model, optional adapter path, extra args); persisted, backed up
  and restored like `LlamaCppSettings`.
- `MlxArgsBuilder` → `mlx_lm.server --model <folder> --host 127.0.0.1 --port <port> …` from S4's pinned option set;
  `ProcessLaunchValidator` rules for refused flags (host, port, model, owned flags), like llama.cpp's.
- `PlacementValidator`: MLX needs `runtime.mlx.process` and a Metal (unified) node; vLLM never on a Mac; clear refusal
  text in the move and placement dialogs.
- Memory estimate: MLX weights from the folder's safetensors size, KV cache from `config.json` (reuse
  `ModelFolderInspector`), plus a measured runtime overhead from S4; checked against M7.
- Health: the readiness probe S4 found; streaming and non-streaming chat through the agent proxy; per-request usage
  numbers taken from the response where MLX reports them, "—" where it does not (no Prometheus endpoint).
- Execution fingerprint and restart-needed rules include the MLX settings.

Agent:
- MLX provisioning (M6): installer step `--with-mlx` creates `{exec}/mlx/<mlx-lm version>/` with `uv venv` +
  `uv pip sync` from `deploy/agent/macos/mlx-lm.lock.txt` (hashes), registers `mlx-lm-server` in the executable
  catalogue with its version; `--update-mlx`.
- `MlxRuntimeContributor` reports `runtime.mlx.process` with versions.
- `ProcessRuntimeDriver` launches MLX through the same `MacProcessHost` (environment: `PYTHONUNBUFFERED=1`,
  `HF_HUB_OFFLINE=1` so it never downloads at start, a private `HF_HOME` under the cache root).
- Checkpoints: the native download service fetches `mlx-community/*` repos (safetensors + tokenizer + config); the
  folder inspector recognises MLX checkpoints (`config.json` with `quantization` and MLX weight names).

UI:
- Runtime picker: vLLM / llama.cpp / **MLX**, showing only kinds the node reports.
- Add model wizard: MLX checkpoint step (folder or Hugging Face download, with an MLX-repo hint), an **MLX settings**
  step in place of Memory/Tuning, review rows for MLX.
- Settings (edit) page: `MlxSection` like `LlamaCppSection`; model header and lists show the MLX runtime label.
- Monitoring: MLX models show throughput/latency from the router where available, "—" for engine metrics.

Tests: unit tests for args, validator refusals, estimate, settings round-trip through backup/restore, wizard steps
for MLX; Mac integration test: start a tiny MLX model (e.g. a 0.5–1B 4-bit) → chat → adopt → stop.

Exit: an MLX model is added, started, chatted with through the router, survives an agent restart, shows in
Monitoring, is backed up and restored, and is refused on the DGX and Windows nodes with a clear reason.

### Phase 4 — Telemetry and host services

- `AppleGpuTelemetryProbe`: IOKit `IOAccelerator` "Device Utilization %" and "In use system memory" (M8); GPU card on
  the node page and Monitoring with utilisation; power/temperature "—".
- macOS **hostd** (M10): HostAgent built for `osx-arm64`, installed as a root LaunchDaemon with a Unix socket owned
  by `_mirbadllm`; `launchctl` manager for a whitelist of LaunchAgents/Daemons (Ollama, LM Studio, ComfyUI), reports
  `services.launchd`; the Services tab works on the Mac with the same admin re-authentication.
- Backup destination on a Mac node is out of scope (backups live on the master).

Exit: the Mac's node page shows GPU utilisation while a model generates; Ollama (if installed) can be stopped and
started from the Services tab to free memory.

### Phase 5 — Hardening and documentation

- Benchmarks and tuning: llama.cpp benchmark on the Mac; MLX benchmark (tuning disabled for MLX at first).
- Failure paths: Metal out-of-memory at load → Failed with the cause; disk full during download; agent upgrade with
  models running; reboot with AutoStart models.
- Docs: README (macOS node install), `docs/multinode/design.md` §6.6 updated to the chosen supervisor, DESIGN.md if
  the UI changed, PRODUCT.md runtimes.
- CI: `test-macos` turned on for every PR once stable (`MAC_CI=1`); deploy lint covers the macOS script and plist.

Exit: a clean Mac (no prior state) goes from join command to serving one llama.cpp and one MLX model in under 15
minutes following the README alone.

## 5. Order and size

| Phase | Depends on | Relative size |
|---|---|---|
| 0 Foundations and spikes | — | S |
| 1 Mac joins the fleet | 0 (S1–S3) | M |
| 2 llama.cpp on the Mac | 1 | L |
| 3 MLX runtime | 2 (process host), 0 (S4) | L |
| 4 Telemetry and host services | 1 | M |
| 5 Hardening and docs | 2, 3 | M |

Phase 4 can run in parallel with Phase 3 once Phase 1 is merged.

## 6. Risks

| Risk | Effect | Mitigation |
|---|---|---|
| Metal unavailable to a LaunchDaemon user with nobody logged in (S1) | Models fail to load on a headless Mac | Decide in Phase 0; fallback LaunchAgent + auto-login |
| 8 GiB / ~20 GiB disk on this Mac | Only small models; CI fixtures must be tiny | Admission by working-set limit; ≤100 MB GGUF and ≤600 MB MLX test models; clean caches in CI |
| Shared Mac (other orgs' runners) | Memory pressure during builds affects models | Concurrency 1; MAC_CI gate; tests stop models they start |
| `mlx_lm.server` API drift between versions | Broken health or args after an update | Pin the version (M6); S4 records the contract; update through `--update-mlx` with a smoke test |
| Unsigned cross-built binaries (S2) | Agent refuses to start | Installer ad-hoc signs |
| No Prometheus metrics from MLX | Thinner Monitoring for MLX models | Router-side numbers; "—" elsewhere |

## 7. Spike results

(Filled in during Phase 0.)

| Spike | Result (2026-10-16, Mac mini M2 8 GiB, macOS 27.0.1) |
|---|---|
| S1 Metal from LaunchDaemon user | **Works.** A system LaunchDaemon with `UserName=nobody` (uid -2) created the M2 device, compiled a compute kernel from source and ran it over 1M elements with the right result (`status=completed`), in the system domain outside any login session. Not tested: no console login at all (the owner's account stays logged in for the other runners). M1 stands. |
| S2 Signing of cross-built bundle | **No action needed.** `dotnet publish -r osx-arm64 --self-contained -p:PublishSingleFile=true` on a non-Mac host produces a Mach-O arm64 file that is already ad-hoc signed (`codesign -dv`: `flags=0x2(adhoc)`) and runs on the Mac (`--help`, exit 0). 113 MB. The installer keeps a `codesign --verify` check with an ad-hoc re-sign fallback. |
| S3 TLS credential loading | **Broken on macOS, fixed in Phase 0.** `X509CertificateLoader.LoadPkcs12(..., EphemeralKeySet)` throws `PlatformNotSupportedException` on macOS; it failed 838 of the 877 unit tests that failed on the Mac (the rest were the TLS handshakes behind it). The default flags work but create a temporary keychain on every load (**361 ms** each on the M2), which made the suite take 25 min there. `TlsCredentials.Create` now binds the certificate to an in-memory copy of its ECDSA key on macOS (**3.7 ms**; server- and client-authenticated TLS handshakes succeed), keeps ephemeral PKCS#12 on Linux and the PKCS#12 round trip on Windows. |
| S4 mlx-lm server contract | mlx 0.32.4, mlx-lm 0.32.2 in a `uv` venv (Python 3.12, 377 MB), Metal available. Server flags include `--model`, `--host` (default 127.0.0.1), `--port`, `--adapter-path`, `--draft-model`/`--num-draft-tokens`, `--trust-remote-code`, `--chat-template(-args)`, `--max-tokens`, sampling defaults, `--decode-concurrency`/`--prompt-concurrency` (batched decoding), `--prompt-cache-size/-bytes`, `--kv-bits`/`--kv-group-size`. No API-key option (M4 covers it). Measured with `mlx-community/Qwen3-0.6B-4bit` (335 MB): `GET /health` → `200 {"status": "ok"}` 0.9 s after start (model loaded lazily, first request 0.3 s); `GET /v1/models` lists the folder path as the id; **no `/metrics`** (404); chat responses carry OpenAI `usage`, streaming ends with a usage chunk when `stream_options.include_usage` is set; 149 tok/s for one request, **254–262 tok/s aggregate for 4 concurrent** (batched at `--decode-concurrency` 1 and 4 alike); server RSS ~620 MiB; exits on SIGTERM. **Contract trap:** the request's `model` field selects the model — anything but `default_model` (or the loaded path) makes the server try to load that repo (refused offline, 404). The agent proxy must rewrite `model` to `default_model` for MLX, and the agent must always launch with `HF_HUB_OFFLINE=1` so a client can never make a node download. |
| S5 GPU working-set limit | Metal `recommendedMaxWorkingSetSize` = **5,726,633,984 bytes (5.33 GiB) = exactly 2/3 of 8 GiB**, `hasUnifiedMemory=true`, `iogpu.wired_limit_mb=0`. M7 uses the measured value when the agent can read it (a 20-line Swift helper or a Metal P/Invoke), else 2/3 of RAM. |
