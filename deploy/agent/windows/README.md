# mirbadllm-agent on Windows

`install-agent.ps1` installs the node agent on a Windows 11 PC (P4, design-p3p4 §A.4) and the pinned llama.cpp
runtime (§A.1.2). The PC can have an NVIDIA GPU, no usable GPU at all (a CPU-only node), or an AMD or Intel GPU used
through Vulkan (experimental); see [Which llama.cpp build](#which-llamacpp-build). It runs in an **elevated Windows
PowerShell 5.1** window (the default shell) or in PowerShell 7. Docker Desktop is not needed: models run as native
`llama-server` processes under the service account.

## Before you start

- For an NVIDIA GPU: a driver at or above the lock file's `minDriver` (`615.00` for `b11476`, CUDA 13.4). Check with
  `nvidia-smi --query-gpu=driver_version --format=csv,noheader`. A PC without an NVIDIA driver needs nothing here: it
  gets the CPU build.
- The **Microsoft Visual C++ 2015-2022 Redistributable (x64)**. The llama.cpp binaries import `msvcp140.dll`,
  `vcruntime140.dll` and `vcruntime140_1.dll`, and the release zips do not include them. The installer checks
  `System32` and stops if one is missing. It never downloads the redistributable itself; install it from Microsoft.
- The clock in sync (`w32tm /query /status`), and the PC set never to sleep on AC power. The installer warns about
  both.

## Join a node

Use the "Add node" command on the master's Nodes page. It downloads the `win-x64` bundle, checks its SHA-256 and
runs this script:

```powershell
$env:MIRBADLLM_JOIN_TOKEN = '<token>'
.\install-agent.ps1 install -NodeId win-1 -EnrollUrl https://<master>:8443 -CaHash sha256:<hash> -LlamaCpp
```

To keep the token out of the window altogether, leave out the first line and add `-PromptToken`. The script then
reads the token with `Read-Host -AsSecureString`. The token never goes in a parameter, because every user can read
process command lines. The script clears it in a `finally` block, so it is removed even when a step fails.

`install` does this:

1. Copies `mirbadllm-agent.exe` to `C:\Program Files\mirbadllm-agent\app\{version}\`.
2. Creates the service `mirbadllm-agent` under the virtual account `NT SERVICE\mirbadllm-agent`, with startup
   type Automatic (Delayed Start) and restart on failure.
3. Writes `%ProgramData%\mirbadllm-agent\agent.json` once, from `agent.windows.example.json`. Only SYSTEM,
   Administrators and the account can read that folder: its root is protected and everything below it inherits.
4. Sets the service's `PreshutdownTimeout` to `Agent:ShutdownTimeout` + 10 s, so Windows waits for the agent to
   drain before it shuts down, and registers the Event Log source `mirbadllm-agent` in the Application log. The
   virtual account cannot create that source itself.
5. Enrolls the node.
6. With `-LlamaCpp`, installs llama.cpp (see below).
7. Opens TCP 9443 inbound for the service only. The rule covers the Private profile only and allows only the master,
   by default the enroll URL's host. It warns in red when the Windows Firewall is off.
8. Starts the service. Use `-NoStart` to skip this.

Running `install` again upgrades the binary in place and keeps `agent.json` and the identity.

## llama.cpp

```powershell
.\install-agent.ps1 -LlamaCpp                 # the version pinned in llama-cpp.lock.json (b11476)
.\install-agent.ps1 -LlamaCpp b11476 -LlamaCppSource D:\downloads   # zips already downloaded
.\install-agent.ps1 -RemoveLlamaCpp b11476
```

`-LlamaCpp` takes these steps:

1. Chooses the build (see [Which llama.cpp build](#which-llamacpp-build)) and refuses a PC that cannot run it: a PC
   without the VC++ runtime, and for the CUDA build a driver below `minDriver`.
2. Downloads the build's release assets **by tag**. It never uses `releases/latest`, because every `bNNNN` release is
   a prerelease.
3. Checks each zip's size and SHA-256.
4. Unpacks the zips into one staging folder.
5. Requires **exactly** the lock file's files for that build (55 for CUDA, 51 for CPU, 52 for Vulkan): each one
   present with the right size and hash, none missing, none extra. A planted DLL would otherwise load from the
   application folder first.
6. Moves the folder to `C:\Program Files\mirbadllm-agent\runtimes\llama.cpp\b11476\`, read-only for the account.
7. Writes `manifest.json` there and adds the `Agent:Executables` entry to `agent.json`.

If the folder is already installed and verifies, it is kept as it is; if it fails verification, it is installed
again. A running service is restarted so it picks up the change; running models keep running and are adopted again.
`-RemoveLlamaCpp` is refused while a process runs from that folder. It is also refused while that version is the
default of other installed versions.

## Which llama.cpp build

`llama-cpp.lock.json` pins three builds of the same release (`b11476`): the CUDA build at the top level, and the CPU
and Vulkan builds under `variants`. `-Accelerator` picks one, and the script writes the choice to `agent.json` as
`Agent:Accelerator` (no entry for CUDA). The agent reports it, and the master places models by it.

```powershell
.\install-agent.ps1 -LlamaCpp                        # auto: CUDA with an NVIDIA driver, otherwise the CPU build
.\install-agent.ps1 -LlamaCpp -Accelerator cpu       # the CPU build, also on a PC with an NVIDIA card
.\install-agent.ps1 -LlamaCpp -Accelerator vulkan    # the Vulkan build, for an AMD or Intel GPU (experimental)
```

| `-Accelerator` | Asset | What the node does |
|---|---|---|
| `auto` (default) | | `cuda` when `nvidia-smi` reports a driver, otherwise `cpu`. It never picks `vulkan`. |
| `cuda` | `llama-b11476-bin-win-cuda-13.4-x64.zip` and the CUDA runtime zip | As before: per-GPU admission from `nvidia-smi`. |
| `cpu` | `llama-b11476-bin-win-cpu-x64.zip` | llama.cpp on the CPU; a model's budget counts against host RAM. |
| `vulkan` | `llama-b11476-bin-win-vulkan-x64.zip` | llama.cpp through Vulkan, per-GPU admission. Experimental. |

A version folder holds one build. Running `-LlamaCpp` again with another accelerator replaces the build in
`runtimes\llama.cpp\b11476\` and updates `agent.json`; stop the node's models first. Do this after you add or remove a
GPU.

### CPU-only nodes

`auto` installs the CPU build when no NVIDIA driver answers, so the join command from **Add node** works unchanged on
a PC without an NVIDIA card. Use `-Accelerator cpu` on a PC whose NVIDIA card is too old for the CUDA build: the agent
then ignores the card.

On such a node a model's whole budget is counted against host RAM, minus the node's memory reserve, and one model
starts at a time. The **Add model** wizard asks for no GPU, and its estimate counts every weight in RAM whatever
**GPU layers** says. The node's GPU figures stay empty, because there is no GPU to read. The speed of llama.cpp on a
CPU has not been measured for this project; benchmark the model on the node before you rely on it.

### Vulkan nodes (experimental)

This path has not been run on an AMD or Intel graphics card. The GPU readings were compared with `nvidia-smi` on a PC
with an RTX 5070 and an RTX 5060 Ti (same UUIDs, and free VRAM within 40 MiB), and the device selection was checked
with the pinned Vulkan build's `--list-devices` on the same PC. On that PC an agent set to `vulkan` also started
models through the Vulkan build on the card it was told to use, admitted them per GPU and swapped them on demand. That
agent ran as an administrator's process, not as the installed service. It is off unless you pass `-Accelerator vulkan`.
What it does:

- **Requirements.** The Vulkan runtime that the graphics driver installs (`vulkan-1.dll` in `System32`). The script
  stops without it.
- **GPUs.** The agent lists the discrete adapters the Vulkan loader reports. A card is identified by its Vulkan
  device UUID, and its VRAM total is the memory the driver hands out. An integrated GPU is not listed: its memory is
  system RAM, which per-GPU admission cannot budget. A PC whose only GPU is integrated should join as a CPU-only node.
- **Memory in use.** Vulkan reports only what the calling process uses, so the agent reads the system-wide figure
  from the Windows performance counter `\GPU Adapter Memory(*)\Dedicated Usage`, matched to the card by its LUID. A
  service may read these counters only as a member of **Performance Monitor Users**, so the script adds
  `NT SERVICE\mirbadllm-agent` to that group (and `uninstall` removes it). This has not been verified under the
  service account.
- **Admission.** Free VRAM is total minus the counter's value. When the counter cannot be read, the GPU shows "—",
  the node is Degraded with the reason, and a start on that GPU is refused unless you force it.
- **Device selection.** The agent passes the placed GPUs to `llama-server` as Vulkan device indices
  (`GGML_VK_VISIBLE_DEVICES`), in placement order, and turns implicit Vulkan layers off for itself and for the model
  process so both see the same device list.
- **Telemetry.** VRAM total, used and free only. Utilization, power and temperature are not read and show "—".

## Upgrade, rollback, rekey, uninstall

An agent installed before the project's rename is upgraded in place and keeps its old service and folder names; see "Upgrading from a pre-rename install" in [docs/setup.md](../../../docs/setup.md).

```powershell
.\install-agent.ps1 upgrade -Bundle .\win-x64.zip -BundleSha256 <hex>   # or -PublishedDir, or run from the bundle
.\install-agent.ps1 rollback
$env:MIRBADLLM_JOIN_TOKEN = '<rekey token>'
.\install-agent.ps1 rekey -EnrollUrl https://<master>:8443 -CaHash sha256:<hash> [-NodeUid <uid>]
.\install-agent.ps1 uninstall
```

- **upgrade** installs the new version next to the current one and stops the service. It switches the service's
  `binPath` to the new folder, starts it, and waits until it listens on 127.0.0.1:9443. If the new version does not
  come up, it switches back and starts the previous version. `llama-server` processes keep running throughout. The
  last three agent versions are kept.
- **rollback** switches back to the previous version.
- **rekey** stops the service and keeps a copy of `agent.crt`, `agent.key`, `ca.crt` and `node-uid`. It then runs
  `mirbadllm-agent.exe join --rekey`, re-applies the private ACL and starts the service. If the rekey fails, it
  restores exactly the saved identity and starts it again.
- **uninstall** stops only the runtime processes that the service account started from the runtimes folder. It
  removes the service, its Event Log source, the firewall rule and `C:\Program Files\mirbadllm-agent`, and keeps
  `%ProgramData%\mirbadllm-agent`. It then prints the command that removes the account's profile after a reboot.

## Importing a GGUF by hand

```powershell
.\install-agent.ps1 -ImportGguf C:\Users\me\.ollama\models\blobs\sha256-<hex> -Folder planner
```

This copies the file into `{ModelsRoot}\planner\model.gguf` and checks its SHA-256. For an Ollama blob, the hash is
taken from the blob's name; for any other file, pass `-Sha256`. It is a manual fallback only. The designed path is
the agent's own download.

## Dry run and tests

`-DryRun` runs every check (the llama.cpp download and its verification run in a temporary folder) and prints each
change without making it. The Pester suite in `test\` drives the script's own functions against temporary folders,
with the service, driver, port and enrollment calls faked. It runs under Pester 3.4 (inbox) and 5.x:

```powershell
powershell.exe -NoProfile -Command "Invoke-Pester deploy\agent\windows\test"
```

The xUnit test `WindowsInstallerTests` runs that suite under Windows PowerShell 5.1, together with the ACL and parse
checks.
