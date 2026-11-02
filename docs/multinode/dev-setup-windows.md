# Windows development setup (P3/P4)

One Windows box that runs the whole P3/P4 fleet: the **master** in `Development`, the existing **Linux dev agents**
(`dgx`, unified, the fake runtime — `tools/dev-multinode`), and a **Windows dev agent** `win-1` that uses the **real
process driver** with the fake `llama-server` registered as the logical executable `llama-server` in a throwaway
catalogue. Nothing on the box needs a GPU, Docker, a Windows service or a real model file.

It exists so that P3/P4 work — llama.cpp process delivery, the SCM host services, the GPU-budget admission, the fit
preview, benchmarks and tuning — can be developed, demonstrated and reviewed without the owner's PC. It is **not** a
substitute for the live acceptance of plan-p3p4 §4.2 (MN-241): the fake server has no CUDA, no WDDM and no real
timings.

The same composition runs in one test class, which is the development-setup acceptance for P3/P4:

```
dotnet artifacts/bin/MirbadLLM.UnitTests/debug/MirbadLLM.UnitTests.dll \
  -class MirbadLLM.UnitTests.Multinode.DevSetup.WindowsDevSetupTests
```

| Acceptance item | Test |
|---|---|
| Enrollment and approval of a Windows node | `TheWindowsDevNode_Enrolls_IsApproved_AndServesAnEmbeddingAndAChatModelThroughV1` |
| An embedding model and a chat model through `/v1` | the same test (`/v1/embeddings`, `/v1/chat/completions` buffered and streamed) |
| A benchmark | `TheWindowsDevNode_RunsABenchmark_AndAppliesATuneCandidate_ThroughTheMaster` |
| A tune apply | the same test (`POST /tune` with **no goal**, then `…/candidates/{i}/apply`) |
| An agent restart with adoption | `TheWindowsDevAgent_Restarts_AndAdoptsItsRunningLlamaServerProcess` |
| The two composed runtime drivers (D131) | `TheWindowsDevNode_ComposesTheFakeRuntimeNextToTheProcessDriver_AndResolvesBothUpstreamsAndKeys` |
| The generated `agent.json` (D131) | `TheGeneratedWindowsDevConfig_…`, `AWindowsDevConfigWithoutACacheRoot_…` |

`SS_DEV_AGENT_LOG=1` makes those tests print the dev agents' own logs (launches, ports, the proxy's upstream
decisions), which is the quickest way to see why a dev-setup test failed.

---

## 1. What the Windows dev agent is

`win-1` is composed by the **real** `Program.CreateAsync` + `AddMirbadLLMAgent`, with exactly one switch flipped:

```
"Agent": { "Docker": { "Enabled": false } }
```

That single switch (design-p3p4 §A.4.3) is what makes it a **process node**: the container driver is never registered,
there is no Docker probe and no 2-minute Docker wait, the capability report carries no container runtime, no image list
and no Docker feature, and the composition registers `ProcessRuntimeDriver` with its collaborators —
`ProcessRecordStore` (`processes.json`), `ExecutableCatalog`, `GgufReader`, the `nvidia-smi` GPU start meter and
GPU-process probe, `GpuReserveStore` and `ExternalGpuUseContributor` — plus the SCM host services.

On top of that the dev agent keeps the **development fake runtime** on, because a dev box has no GPUs:

```
"DevFakeRuntime": { "Enabled": true, "Memory": "discrete:2x16", "HostMemoryGiB": 32, "GpuUuids": [ … ] }
```

`DevFakeRuntime` answers `nvidia-smi` (inventory and telemetry) and the host memory probe, so the real GPU inventory,
the real discrete admission, the real `/status` reader and the real capability report all see two 16 GiB cards.

### The node therefore composes **two** runtime drivers

`DevFakeRuntime` registers an `IRuntimeDriver` of its own (`FakeRuntimeDriver`, container delivery). With Docker **off**
the process driver is **added next to it**, not substituted for it — only the Docker-on path replaces a pre-registered
driver. That is the configuration `RuntimeDriverUpstreamResolver`'s per-driver fold and `AgentProxy`'s API-key walk
exist for (MN-230/MN-234, D130/D131):

- a llama.cpp **process** model resolves its upstream through the process driver and its `/v1` traffic gets the
  per-instance `LLAMA_API_KEY` the driver sealed into `processes.json`;
- a model of the **fake** driver resolves its own loopback upstream and no key (a container on loopback needs none);
- neither driver answers for the other's model, and a driver that cannot answer does not silence the other.

Note the asymmetry this creates, and that it is deliberate: because a Docker-off node reports **no container runtime at
all**, the master will not place a vLLM model on `win-1` (`runtime_unavailable`). The fake driver is there for the fake
GPUs and the fake memory, not to serve fleet models. That is also why the test drives the fake driver's model through
the driver itself.

### Three things the generated `agent.json` must get right (W14 review, D131)

1. **`Agent:CacheRoot` is mandatory.** A native launch's argv may name it as `{cache}`, so a process node without one
   refuses to start: `Program.CreateAsync` throws an `AgentStartupException` reading
   *"Agent:CacheRoot is required on a node that runs native processes (Agent:Docker:Enabled = false)…"*. Under the SCM
   that reason goes to the Event Log, not to stderr. `install-agent.ps1` always writes it (`-CacheRoot`, default
   `D:\mirbadllm\cache`) and `agent.windows.example.json` has it — a **hand-written dev config is the one place it
   gets forgotten**, so the generator below always writes it.
2. **One `Agent:Executables` entry per executable name.** Every `llama-server` version goes in that one entry's
   `Versions`. Two entries for the same name are refused in validation (*"put every version of an executable in one
   entry"*), because each entry derives its own default version — the node's default `llama-server`, and every launch
   fingerprint through it, would otherwise follow JSON ordering.
3. **Keep `Agent:Docker:Enabled = false`.** With it true the composition registers the container driver instead and
   leaves `IMemoryProbe` at the `/proc/meminfo` default, which on Windows means garbage `/status` memory rather than a
   clear error (MN-239 item 15).

---

## 2. The script

PowerShell 7 on Windows, from the repo root. Nothing here installs a service, touches an existing service, opens a
firewall rule or writes outside `$Root`. **Pick ports the box does not already use** — in particular never `8000`,
which is the owner's own `llama-server`.

```powershell
# ---------------------------------------------------------------- settings
$Root        = Join-Path (Get-Location) 'artifacts/dev-windows'
$MasterPort  = 5080
$EnrollPort  = 8443
$DgxPort     = 9443          # the existing Linux dev agent
$WinPort     = 9445          # the Windows dev agent
$WinModelPorts = 18200       # win-1 hands its models 18200..18249 — never 8000
$LlamaVersions = @('b11410', 'b11476')   # the dev catalogue; the last one becomes the default
# The UUIDs the P3 model fixtures place on, in index order. The fake reports both cards as 16 GiB "Fake GPU (dev)"
# (set DevFakeRuntime:GpuName to change it), so unlike the real PC they are interchangeable.
$WinGpus = @('GPU-0a4f2d61-8b3c-4e7a-b1d9-2c5e8f0a3b17',   # index 0
             'GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44')   # index 1

$agentDll  = 'artifacts/bin/MirbadLLM.Agent/debug/mirbadllm-agent.dll'
$masterDll = 'artifacts/bin/MirbadLLM.Web/debug/MirbadLLM.Web.dll'
$fakeDir   = 'artifacts/bin/MirbadLLM.FakeLlamaServer/debug'
$master    = "http://127.0.0.1:$MasterPort"

# ---------------------------------------------------------------- 1. build
dotnet build MirbadLLM.slnx -v q -nologo
New-Item -ItemType Directory -Force -Path $Root, "$Root/logs", "$Root/bootstrap", "$Root/join" | Out-Null

# ---------------------------------------------------------------- 2. the fake llama-server as a catalogue
# One version folder per entry, each a copy of the published fake server, with the manifest.json the ExecutableCatalog
# verifies the folder against (schemaVersion 1; it lists every file but itself, and it MUST list every .exe/.dll in the
# folder). `llama-server` is the logical name a launch asks for; the file on disk keeps its own name, because a .NET
# apphost needs its .dll, .deps.json and .runtimeconfig.json to match it.
function New-FakeLlamaVersion([string] $Version) {
    $folder = Join-Path $Root "win-1/runtimes/llama.cpp/$Version"
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Copy-Item "$fakeDir/*" $folder -Recurse -Force
    Remove-Item (Join-Path $folder 'manifest.json') -ErrorAction SilentlyContinue
    $files = Get-ChildItem $folder -Recurse -File | ForEach-Object {
        [ordered]@{
            path   = $_.FullName.Substring($folder.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes  = [long]$_.Length
        }
    }
    [ordered]@{
        schemaVersion = 1
        name          = 'llama-server'
        version       = $Version
        cuda          = '13.4'
        sources       = @("fake-llama-server-$Version.zip")
        files         = @($files)
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $folder 'manifest.json') -Encoding UTF8
    return $folder
}

$versionFolders = @{}
foreach ($v in $LlamaVersions) { $versionFolders[$v] = New-FakeLlamaVersion $v }

# ---------------------------------------------------------------- 3. models on the dev node
# The fake server never reads the GGUF, but the agent's launch validator requires the file to exist. Drop a real small
# GGUF in instead if you want gguf.inspect, the fit preview and the GGUF picker to answer something.
$winModels = Join-Path $Root 'win-1/models'
foreach ($file in 'Qwen3-Embedding-4B-GGUF/Qwen3-Embedding-4B-Q8_0.gguf',
                  'ov-intent-analysis-v7-q8/model.gguf') {
    $path = Join-Path $winModels $file
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    if (-not (Test-Path $path)) { [IO.File]::WriteAllBytes($path, [byte[]](0x47,0x47,0x55,0x46,3,0,0,0)) }
}

# ---------------------------------------------------------------- 4. win-1's agent.json
$winState = Join-Path $Root 'win-1/state'
$winCache = Join-Path $Root 'win-1/cache'
New-Item -ItemType Directory -Force -Path $winState, $winCache | Out-Null
$config = [ordered]@{
    Agent = [ordered]@{
        NodeId          = 'win-1'
        StateDirectory  = $winState
        ListenAddress   = '127.0.0.1'      # loopback only: DevFakeRuntime refuses any other listener
        Port            = $WinPort
        ModelsRoot      = $winModels
        ModelsHostPath  = $winModels
        CacheRoot       = $winCache        # MANDATORY on a process node — see §1 item 1
        CacheHostPath   = $winCache
        UseContainerNetwork = $false
        UpstreamHost    = '127.0.0.1'
        HostdExpected   = $false           # no hostd on Windows
        ManageSystemdUnits = $false
        ManageContainers   = $false
        ManageBackupMount  = $false
        PortRangeStart  = $WinModelPorts
        PortRangeEnd    = $WinModelPorts + 49
        ShutdownTimeout = '00:00:30'
        Docker          = [ordered]@{ Enabled = $false }   # the one P3/P4 switch
        Executables     = @(
            # ONE entry for llama-server, every version inside it — see §1 item 2
            [ordered]@{
                Name     = 'llama-server'
                Default  = $LlamaVersions[-1]
                Versions = @($LlamaVersions | ForEach-Object {
                    [ordered]@{
                        Version  = $_
                        Cuda     = '13.4'
                        Path     = Join-Path $versionFolders[$_] 'fake-llama-server.exe'
                        Manifest = Join-Path $versionFolders[$_] 'manifest.json'
                    }
                })
            }
        )
        Process = [ordered]@{ StartTimeout = '00:02:00'; HealthPollInterval = '00:00:01' }
    }
    DevFakeRuntime = [ordered]@{
        Enabled = $true; Memory = 'discrete:2x16'; HostMemoryGiB = 32; GpuUuids = $WinGpus
        StartDelay = '00:00:01'; TokenDelay = '00:00:00.02'
    }
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
}
$winConfig = Join-Path $Root 'win-1/agent.json'
$config | ConvertTo-Json -Depth 8 | Set-Content $winConfig -Encoding UTF8

# ---------------------------------------------------------------- 5. the master and the Linux dev agent
# The master in Development, with the bootstrap bundle for dgx and a dev join token for win-1. (tools/dev-multinode
# mints one for dev-b only, so start the master here rather than from that script; add Nodes:1=dev-b if you also want
# the discrete Linux dev agent.)
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$pids = Join-Path $Root 'pids'
Remove-Item $pids -ErrorAction SilentlyContinue
(Start-Process dotnet -PassThru -ArgumentList @(
    "`"$masterDll`"", '--urls', $master,
    "--MirbadLLM:DataPath=`"$Root/master-data`"",
    "--MirbadLLM:Backup:Directory=`"$Root/backups`"",
    "--MirbadLLM:HostAgentSocket=`"$Root/no-hostd.sock`"",
    "--MirbadLLM:Enrollment:Port=$EnrollPort",
    "--MirbadLLM:Enrollment:BootstrapDirectory=`"$Root/bootstrap`"",
    "--MirbadLLM:Enrollment:BootstrapEnrollEndpoint=https://127.0.0.1:$EnrollPort",
    "--MirbadLLM:Enrollment:BootstrapAgentAddress=https://127.0.0.1:$DgxPort",
    '--MirbadLLM:DevJoinTokens:Nodes:0=win-1',
    "--MirbadLLM:DevJoinTokens:OutputDirectory=`"$Root/join`"",
    '--MirbadLLM:DevJoinTokens:AutoApprove=false',
    '--MirbadLLM:MetricsSampleIntervalSeconds=1'
) -RedirectStandardOutput "$Root/logs/master.log" -RedirectStandardError "$Root/logs/master.err.log" `
  -WindowStyle Hidden).Id | Add-Content $pids
$deadline = (Get-Date).AddMinutes(2)
while ($true) {
    try { $null = Invoke-WebRequest "$master/healthz" -UseBasicParsing -TimeoutSec 5; break } catch { }
    if ((Get-Date) -gt $deadline) { throw "The master never answered $master/healthz (see $Root/logs/master.err.log)." }
    Start-Sleep -Milliseconds 500
}

# dgx: the existing Linux dev agent, unified, bootstrap enrollment (one config, as tools/dev-multinode writes it).
$dgxModels = Join-Path $Root 'dgx/models/fake-model'
New-Item -ItemType Directory -Force -Path $dgxModels, "$Root/dgx/state", "$Root/dgx/cache" | Out-Null
Set-Content (Join-Path $dgxModels 'config.json') '{"architectures":["LlamaForCausalLM"],"model_type":"llama"}'
$dgxConfig = Join-Path $Root 'dgx/agent.json'
[ordered]@{
    Agent = [ordered]@{
        NodeId = 'dgx'; StateDirectory = "$Root/dgx/state"; ListenAddress = '127.0.0.1'; Port = $DgxPort
        ModelsRoot = "$Root/dgx/models"; ModelsHostPath = "$Root/dgx/models"
        CacheRoot = "$Root/dgx/cache"; CacheHostPath = "$Root/dgx/cache"
        UseContainerNetwork = $false; DockerStartupTimeout = '00:00:05'
        HostdSocket = "$Root/dgx/no-hostd.sock"; HostdExpected = $false
        ManageSystemdUnits = $false; ManageContainers = $false; ManageBackupMount = $false; AllowTagPulls = $true
        PortRangeStart = 18100; PortRangeEnd = 18149; ShutdownTimeout = '00:00:10'
        BootstrapDirectory = "$Root/bootstrap"; BootstrapWait = '00:02:00'; Addresses = @('127.0.0.1')
    }
    DevFakeRuntime = [ordered]@{ Enabled = $true; Memory = 'unified:64'; StartDelay = '00:00:01'; TokenDelay = '00:00:00.02' }
} | ConvertTo-Json -Depth 6 | Set-Content $dgxConfig -Encoding UTF8
(Start-Process dotnet -PassThru -ArgumentList @("`"$agentDll`"", '--config', "`"$dgxConfig`"") `
    -RedirectStandardOutput "$Root/logs/agent-dgx.log" -RedirectStandardError "$Root/logs/agent-dgx.err.log" `
    -WindowStyle Hidden).Id | Add-Content $pids

# ---------------------------------------------------------------- 6. enroll and approve win-1
$join = Get-Content -Raw (Join-Path $Root 'join/win-1.join.json') | ConvertFrom-Json
$env:MIRBADLLM_JOIN_TOKEN = $join.token
try {
    dotnet $agentDll join --enroll-url $join.enrollUrl --ca-hash $join.caHash `
        --node-id win-1 --state-dir $winState --address "https://127.0.0.1:$WinPort"
} finally { Remove-Item Env:MIRBADLLM_JOIN_TOKEN -ErrorAction SilentlyContinue }

# The operator's approval, as the Nodes page does it (the dev helper only mints tokens).
Invoke-WebRequest -Method Post -Uri "$master/api/nodes/win-1/approve" -UseBasicParsing | Out-Null

# ---------------------------------------------------------------- 7. start the Windows dev agent
$env:DOTNET_ENVIRONMENT = 'Development'
(Start-Process dotnet -PassThru -ArgumentList @("`"$agentDll`"", '--config', "`"$winConfig`"") `
    -RedirectStandardOutput (Join-Path $Root 'logs/agent-win-1.log') `
    -RedirectStandardError  (Join-Path $Root 'logs/agent-win-1.err.log') -WindowStyle Hidden).Id | Add-Content $pids
```

Stop everything with the pids the script recorded, then the model processes, then reset:

```powershell
Get-Content (Join-Path $Root 'pids') | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
Get-Process fake-llama-server -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force $Root      # new CA, new enrollments, fresh catalogue
```

The `llama-server` children are deliberately **not** kill-on-close in the agent's job object (§A.1.3), so they survive
the agent stopping and are adopted by the next one — which is the point of the restart test, and why a reset has to end
them explicitly.

### Smoke test

```powershell
# The node reports a discrete process node with a verified default llama-server and no container runtime.
$caps = (Invoke-RestMethod "$master/api/nodes/win-1").node.lastCapabilities
$caps.memory.model                                   # discrete
$caps.gpus | Select-Object index, uuid, memoryTotalGiB
$caps.features -match 'runtime\.llamacpp\.process'   # present
$caps.features -match 'runtime\.vllm\.container'     # absent (Docker off)
$caps.runtimes | Where-Object delivery -eq 'process' | Select-Object -ExpandProperty versions

# An embedding model on GPU 1 and a chat model on GPU 0, then /v1 through the master's router and the agent's proxy.
# (These are the two llama.cpp definitions of tests/MirbadLLM.UnitTests/Contracts/Fixtures/P3/models.v2.llamacpp.json
#  with AutoStart false, no Sources and one GPU each; take ExtraArgs, templates and budgets from there if you want more.)
$embedder = [ordered]@{
    id = 'qwen3-embedding-4b-win'; servedName = 'qwen3-embedding-4b-win'; modelPath = 'Qwen3-Embedding-4B-GGUF'
    memoryBudgetGiB = 8.9; hostMemoryBudgetGiB = 2; maxModelLen = 262144; maxNumSeqs = 16; autoStart = $false
    runtime = 'llamacpp'
    placement = [ordered]@{ nodeId = 'win-1'; gpuUuids = @($WinGpus[1]); gpuIndices = @(1) }
    llamaCpp = [ordered]@{
        modelFile = 'Qwen3-Embedding-4B-GGUF/Qwen3-Embedding-4B-Q8_0.gguf'; contextSize = 8192; parallel = 2
        gpuLayers = 999; cacheTypeK = 'f16'; cacheTypeV = 'f16'; delivery = 'process'
        embedding = $true; pooling = 'last'; emulateDimensions = $true
    }
} | ConvertTo-Json -Depth 6
$chat = [ordered]@{
    id = 'ov-intent-analysis-win'; servedName = 'ov-planner-win'; modelPath = 'ov-intent-analysis-v7-q8'
    memoryBudgetGiB = 1.8; maxModelLen = 262144; maxNumSeqs = 16; autoStart = $false; runtime = 'llamacpp'
    placement = [ordered]@{ nodeId = 'win-1'; gpuUuids = @($WinGpus[0]); gpuIndices = @(0) }
    llamaCpp = [ordered]@{
        modelFile = 'ov-intent-analysis-v7-q8/model.gguf'; contextSize = 4096; parallel = 4; gpuLayers = 999
        flashAttention = $true; cacheTypeK = 'f16'; cacheTypeV = 'f16'; delivery = 'process'
        chatTemplate = 'builtin:chatml'; reasoning = $false
    }
} | ConvertTo-Json -Depth 6

foreach ($body in $embedder, $chat) { Invoke-RestMethod -Method Post "$master/api/models" -ContentType application/json -Body $body }
Invoke-RestMethod -Method Post "$master/api/models/qwen3-embedding-4b-win/start"
Invoke-RestMethod -Method Post "$master/api/models/ov-intent-analysis-win/start"
Invoke-RestMethod "$master/v1/models"
Invoke-RestMethod -Method Post "$master/v1/embeddings" -ContentType application/json `
    -Body '{"model":"qwen3-embedding-4b-win","input":"hello","dimensions":8}'
Invoke-RestMethod -Method Post "$master/v1/chat/completions" -ContentType application/json `
    -Body '{"model":"ov-planner-win","messages":[{"role":"user","content":"hello"}],"max_tokens":8}'

# A benchmark and a tune of the chat model. The job is held by win-1, not the bootstrap node, so read it there.
Invoke-WebRequest -Method Post "$master/api/models/ov-intent-analysis-win/benchmark" -UseBasicParsing
Invoke-RestMethod "$master/api/jobs"                       # [] when it is done
Invoke-RestMethod "$master/api/models/ov-intent-analysis-win/benchmarks"
Invoke-WebRequest -Method Post "$master/api/models/ov-intent-analysis-win/tune" -UseBasicParsing `
    -ContentType application/json -Body '{"steps":["KvCacheTypes"]}'
$run = (Invoke-RestMethod "$master/api/models/ov-intent-analysis-win/tuning")[0]
Invoke-RestMethod -Method Post "$master/api/models/ov-intent-analysis-win/tuning/$($run.id)/candidates/$($run.winnerIndex)/apply"
```

### Benchmark and tuning sizes on a dev box

`BenchmarkSuiteOptions` and `ModelJobOptions` are **plain DI singletons of `AddJobs`, not configuration-bound**, so the
master's defaults (3 runs, 256 generated tokens, a 64 Ki prefill, a 120 s drain, 3 + 5 repetitions) apply unless the
composition replaces them. The dev setup's test replaces both with development sizes — one repetition, 8 generated
tokens, one concurrency level, no prefill, a 5 s drain — and sets
`MirbadLLM:MetricsSampleIntervalSeconds = 1` so the drain sees a fresh sample. Running the dev setup as processes
(§2) keeps the master's defaults, so expect a benchmark to take minutes rather than seconds there.

A tune started **without a goal** takes the API default `Balanced`, which a llama.cpp model remaps to the
`InteractiveChat` workload preset (`ModelJobService.EffectiveGoal`, D130). That remap is what makes a tune possible at
all: scored the classic way, every candidate — the baseline included — is rejected with *"No N=16 measurement"*,
because llama.cpp's chat levels stop at its `-np` slots.

---

## 3. What is real and what is not

| | Dev setup | The owner's PC (MN-241) |
|---|---|---|
| Master, enrollment, mTLS, fencing, operation log, admission, events, proxy, telemetry | real | real |
| Process driver, `processes.json`, executable catalogue + manifest verification, GGUF reader, launch validator | real | real |
| `llama-server` | `MirbadLLM.FakeLlamaServer` (`/health`, `/v1/models`, `/v1/embeddings`, streaming `/v1/chat/completions`, `/metrics` with the `llamacpp:*` families) | real llama.cpp |
| GPUs, VRAM, `nvidia-smi` | `DevFakeRuntime` (`discrete:2x16`, placeholder UUIDs) | real cards |
| Host memory | `DevFakeRuntime`'s fixed pool | `WindowsMemoryProbe` (`GlobalMemoryStatusEx`) |
| Process host, DPAPI instance keys, SCM | real on Windows (the agent runs as the logged-in user in a console, **not** as a service); faked in the test so the Linux CI leg runs the same composition | real, under `NT SERVICE\mirbadllm-agent` |
| Service hosting, 1053, the installer's ACLs and firewall rule, Event Log | **not exercised** | `deploy/agent/windows/install-agent.ps1` |
| Timings, VRAM measurement, external GPU use, WDDM | meaningless | the only place they mean anything |

Because the dev agent runs in a console and not under the SCM, nothing here proves §A.4.1 (the 30 s start budget, error
1053) or §A.4.2 (the service account, ACLs, the firewall rule). Those stay with `install-agent.ps1`'s own Pester tests
and with MN-241.

The SCM half is real but inert: on Windows the dev agent opens the **real** Service Control Manager and reports
`services.scm`, so the Services page lists the box's services — read-only. The managed whitelist lives in the agent's
state directory and starts **empty**, and the compiled deny list (§9.1, plus the agent's own service) applies to every
path whitelisted or not, so a dev agent cannot start, stop or reconfigure anything. Leave the whitelist empty unless
you are deliberately exercising MN-228, and never point a dev agent at a service you care about.

---

## 4. Troubleshooting

**The agent exits at once with `Agent:CacheRoot is required on a node that runs native processes`.** The config has
`Docker:Enabled = false` and no `CacheRoot`. Add it (§2 step 4). Under the SCM this message is in the Event Log, not on
stderr.

**`Agent:Executables lists 'llama-server' twice; put every version of an executable in one entry`.** Merge the entries:
one `Name`, every version in its `Versions`. Each entry would otherwise derive its own default version, so the node's
default `llama-server` would follow the order of the JSON.

**The node reports no `llama-server` version, or an unverified one**, and a launch is refused with *"pins no
llama-server version and node 'win-1' reports no verified default version"*. The catalogue verifies each version folder
against its `manifest.json`: every file it lists must match by size and SHA-256, and **every `.exe`/`.dll` in the folder
must be listed**. Regenerate the manifest after changing anything in the folder (`New-FakeLlamaVersion` above does), and
give the catalogue a moment — the first full hash runs in a background service after the listener opens, never before.

**A launch is refused `spec_rejected` with *"'-m' path … does not resolve to an existing file inside the models
folder"***. The GGUF named by `LlamaCpp.ModelFile` must exist under `ModelsRoot` (§2 step 3). The fake server does not
read it; the agent's validator does.

**The master refuses a vLLM model on `win-1` with `runtime_unavailable`.** Expected: a Docker-off node reports no
container runtime. Place vLLM models on `dgx`.

**`/v1` answers 503 `model_not_running` … *its instance did not answer*** right after a restart or a tuning candidate.
`RuntimeDriverUpstreamResolver` caches a resolved upstream for `AgentProxyOptions.UpstreamCacheDuration` (1 s) and
nothing invalidates it when the driver replaces an instance on a new port, so a server that restarts inside that second
can be sent to the old port. A real llama-server takes far longer than a second to restart; a fake one does not. The dev
setup therefore sets `UpstreamCacheDuration = 0`. Reported for MN-239.

**A benchmark reports 0 tok/s.** The suite takes its client-side token counts from the final `usage` chunk of the
stream (`stream_options.include_usage`). `MirbadLLM.FakeLlamaServer` sends it (MN-236 taught it to); a server that does
not leaves `aggregateTokS` and `perRequestTokS` at 0 while llama-server's own `/metrics` counters still grow — and every
tuning candidate is then rejected with *"Missing measurements for this preset"* although the run itself succeeded.

**`DevFakeRuntime … is a development-only setting` / `… needs a loopback-only listener`.** Set
`DOTNET_ENVIRONMENT=Development` and keep `Agent:ListenAddress` on `127.0.0.1`. A fake node must never be reachable
from the network.

**Ports.** Two agents on one box must not share a model-port range (`PortRangeStart..PortRangeEnd`), and the range must
avoid anything already listening — in particular the owner's own `llama-server` on **8000**. A model whose port is taken
is skipped under the admission lock (D127), but only for ports the agent can see as taken.

---

## 5. See also

- `docs/multinode/design-p3p4.md` §A.1 (process delivery), §A.4.1–§A.4.3 (service hosting, the composition gate)
- `docs/multinode/operations-p3p4.md` — the owner-facing runbook for the real PC (MN-238)
- `deploy/agent/windows/install-agent.ps1`, `deploy/agent/agent.windows.example.json` — the real install
- `tools/dev-multinode/start.ps1` — the Linux/unified dev agents this setup joins
