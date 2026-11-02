<#
.SYNOPSIS
  Starts the two-agent multinode dev setup on this machine (MN-042, MN-122): one master and two agents with the fake
  runtime (no GPU, no Docker models): "dgx" unified like the DGX, "dev-b" a discrete PC with two fake 16 GiB GPUs.
  Windows PowerShell 5.1 or PowerShell 7 on Windows, Linux or macOS.

.DESCRIPTION
  1. builds MirbadLLM.slnx (skip with -NoBuild);
  2. starts the master (Development) on http://127.0.0.1:<MasterPort> with its enroll listener on <EnrollPort>, the
     bootstrap bundle for "dgx" and the development join-token helper for "dev-b";
  3. starts agent "dgx" in bootstrap mode on 127.0.0.1:<DgxPort> (it enrolls from the bundle, pre-approved);
  4. runs "mirbadllm-agent join" for "dev-b" with the helper's token, approves it through the node API
     (POST /api/nodes/dev-b/approve, as the Nodes page does) and starts it on 127.0.0.1:<DevBPort> with the fake
     runtime in discrete mode (discrete:2x16, placeholder GPU UUIDs ...de00 and ...de01);
  5. unless -NoSmoke: checks that dev-b reports a discrete node with both GPUs and the gpus.uuids feature, adds a fake
     model on each node (fake-dgx; fake-devb placed on dev-b's GPU 1 by UUID), starts both and sends /v1 chat requests
     (buffered and streamed) to each through the master.
  Everything lives under artifacts/dev-multinode (logs in logs/). Stop with tools/dev-multinode/stop.ps1.
  -Reset wipes that folder first (new CA, new enrollments).

.EXAMPLE
  pwsh tools/dev-multinode/start.ps1 -Reset
#>
[CmdletBinding()]
param(
    [int]$MasterPort = 5080,
    [int]$EnrollPort = 8443,
    [int]$DgxPort = 9443,
    [int]$DevBPort = 9444,
    [string]$Root,
    [switch]$Reset,
    [switch]$NoBuild,
    [switch]$NoSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Root) { $Root = Join-Path $repo 'artifacts/dev-multinode' }
$masterDll = Join-Path $repo 'artifacts/bin/MirbadLLM.Web/debug/MirbadLLM.Web.dll'
$agentDll = Join-Path $repo 'artifacts/bin/MirbadLLM.Agent/debug/mirbadllm-agent.dll'
$master = "http://127.0.0.1:$MasterPort"
# dev-b's fake GPUs: placeholder UUIDs (not real cards), index order.
$devBGpus = @('GPU-00000000-0000-0000-0000-00000000de00', 'GPU-00000000-0000-0000-0000-00000000de01')

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Wait-Until([scriptblock]$condition, [string]$what, [int]$seconds = 90) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not (& $condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out after $seconds s waiting for $what. Logs: $(Join-Path $Root 'logs')" }
        Start-Sleep -Milliseconds 500
    }
}

function Test-Http([string]$url) {
    try { $null = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5; return $true } catch { return $false }
}

function Start-Dotnet([string]$name, [string]$workingDirectory, [string[]]$arguments, [hashtable]$environment) {
    $saved = @{}
    foreach ($key in $environment.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $environment[$key])
    }
    try {
        $logs = Join-Path $Root 'logs'
        $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $workingDirectory -PassThru `
            -RedirectStandardOutput (Join-Path $logs "$name.log") -RedirectStandardError (Join-Path $logs "$name.err.log") `
            -WindowStyle Hidden
    }
    finally {
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
    Add-Content -Path (Join-Path $Root 'pids') -Value "$name $($process.Id)"
    Write-Host "    $name pid $($process.Id) (log: logs/$name.log)"
    return $process
}

function Write-AgentConfig([string]$nodeId, [int]$port, [int]$portRangeStart, [string]$bootstrapDirectory, [System.Collections.IDictionary]$fake) {
    $node = Join-Path $Root $nodeId
    $models = Join-Path $node 'models'
    New-Item -ItemType Directory -Force -Path (Join-Path $models 'fake-model'), (Join-Path $node 'cache'), (Join-Path $node 'state') | Out-Null
    Set-Content -Path (Join-Path $models 'fake-model/config.json') -Value '{"architectures":["LlamaForCausalLM"],"model_type":"llama"}'
    $agent = [ordered]@{
        NodeId = $nodeId
        StateDirectory = (Join-Path $node 'state')
        ListenAddress = '127.0.0.1'
        Port = $port
        ModelsRoot = $models
        ModelsHostPath = $models
        CacheRoot = (Join-Path $node 'cache')
        CacheHostPath = (Join-Path $node 'cache')
        UseContainerNetwork = $false
        DockerStartupTimeout = '00:00:05'
        HostdSocket = (Join-Path $node 'no-hostd.sock')
        HostdExpected = $false
        ManageSystemdUnits = $false
        ManageContainers = $false
        ManageBackupMount = $false
        AllowTagPulls = $true
        PortRangeStart = $portRangeStart
        PortRangeEnd = $portRangeStart + 49
        ShutdownTimeout = '00:00:10'
    }
    if ($bootstrapDirectory) {
        $agent.BootstrapDirectory = $bootstrapDirectory
        $agent.BootstrapWait = '00:02:00'
        $agent.Addresses = @('127.0.0.1')
    }
    $config = [ordered]@{
        Agent = $agent
        DevFakeRuntime = $fake
        Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning'; 'System.Net.Http.HttpClient' = 'Warning' } }
    }
    $path = Join-Path $node 'agent.json'
    $config | ConvertTo-Json -Depth 5 | Set-Content -Path $path -Encoding UTF8
    return $path
}

if (Test-Path (Join-Path $Root 'pids')) {
    throw "A dev setup is already running (or was not stopped): run tools/dev-multinode/stop.ps1 first."
}

if ($Reset -and (Test-Path $Root)) {
    Write-Step "Reset: removing $Root"
    Remove-Item -Recurse -Force $Root
}

New-Item -ItemType Directory -Force -Path $Root, (Join-Path $Root 'logs'), (Join-Path $Root 'bootstrap'), (Join-Path $Root 'join') | Out-Null

if (-not $NoBuild) {
    Write-Step 'Building MirbadLLM.slnx'
    # From the repo root, so its global.json picks the SDK (not whatever SDK the caller's folder resolves to).
    Push-Location $repo
    try { & dotnet build (Join-Path $repo 'MirbadLLM.slnx') -v q -nologo }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$dgxConfig = Write-AgentConfig 'dgx' $DgxPort 18100 (Join-Path $Root 'bootstrap') `
    ([ordered]@{ Enabled = $true; Memory = 'unified:64'; StartDelay = '00:00:02'; TokenDelay = '00:00:00.02' })
$devBConfig = Write-AgentConfig 'dev-b' $DevBPort 18150 $null `
    ([ordered]@{ Enabled = $true; Memory = 'discrete:2x16'; HostMemoryGiB = 32; GpuUuids = $devBGpus; StartDelay = '00:00:02'; TokenDelay = '00:00:00.02' })
$joinFile = Join-Path $Root 'join/dev-b.join.json'

Write-Step "Master on $master (enroll listener https://127.0.0.1:$EnrollPort)"
$null = Start-Dotnet 'master' (Join-Path $repo 'src/MirbadLLM.Web') @(
    "`"$masterDll`"",
    '--urls', $master,
    "--MirbadLLM:DataPath=`"$(Join-Path $Root 'master-data')`"",
    "--MirbadLLM:Backup:Directory=`"$(Join-Path $Root 'backups')`"",
    "--MirbadLLM:HostAgentSocket=`"$(Join-Path $Root 'no-hostd.sock')`"",
    "--MirbadLLM:Enrollment:Port=$EnrollPort",
    "--MirbadLLM:Enrollment:BootstrapDirectory=`"$(Join-Path $Root 'bootstrap')`"",
    "--MirbadLLM:Enrollment:BootstrapEnrollEndpoint=https://127.0.0.1:$EnrollPort",
    "--MirbadLLM:Enrollment:BootstrapAgentAddress=https://127.0.0.1:$DgxPort",
    '--MirbadLLM:DevJoinTokens:Nodes:0=dev-b',
    "--MirbadLLM:DevJoinTokens:OutputDirectory=`"$(Join-Path $Root 'join')`"",
    '--MirbadLLM:DevJoinTokens:AutoApprove=false'
) @{ ASPNETCORE_ENVIRONMENT = 'Development'; DOTNET_ENVIRONMENT = 'Development' }
Wait-Until { Test-Http "$master/healthz" } 'the master /healthz'

Write-Step "Agent dgx (bootstrap mode) on https://127.0.0.1:$DgxPort"
$null = Start-Dotnet 'agent-dgx' (Join-Path $Root 'dgx') @("`"$agentDll`"", '--config', "`"$dgxConfig`"") @{ DOTNET_ENVIRONMENT = 'Development' }

$devBState = Join-Path $Root 'dev-b/state'
if (-not (Test-Path (Join-Path $devBState 'agent.crt'))) {
    Write-Step 'Joining dev-b with the dev helper''s token'
    Wait-Until { Test-Path $joinFile } 'the dev join token for dev-b (master log: logs/master.log)'
    $join = Get-Content -Raw $joinFile | ConvertFrom-Json
    $env:MIRBADLLM_JOIN_TOKEN = $join.token
    try {
        & dotnet $agentDll join --enroll-url $join.enrollUrl --ca-hash $join.caHash --node-id dev-b --state-dir $devBState --address "https://127.0.0.1:$DevBPort"
        if ($LASTEXITCODE -ne 0) { throw "mirbadllm-agent join failed (exit $LASTEXITCODE)." }
    }
    finally {
        Remove-Item Env:MIRBADLLM_JOIN_TOKEN -ErrorAction SilentlyContinue
    }
}
else {
    Write-Step 'dev-b is enrolled already (state kept); skipping join'
}

# Approve dev-b through the node API (MN-102), as the Nodes page does; 409 = approved already (state kept).
$approval = try { (Invoke-WebRequest -Method Post -Uri "$master/api/nodes/dev-b/approve" -UseBasicParsing).StatusCode } catch { [int]$_.Exception.Response.StatusCode }
if ($approval -eq 200) { Write-Host '    approved dev-b (POST /api/nodes/dev-b/approve)' }
elseif ($approval -eq 409) { Write-Host '    dev-b is approved already' }
else { throw "Approving dev-b answered $approval (master log: logs/master.log)." }

Write-Step "Agent dev-b on https://127.0.0.1:$DevBPort"
$null = Start-Dotnet 'agent-dev-b' (Join-Path $Root 'dev-b') @("`"$agentDll`"", '--config', "`"$devBConfig`"") @{ DOTNET_ENVIRONMENT = 'Development' }

if (-not $NoSmoke) {
    Write-Step 'Smoke: dev-b is discrete with two GPUs'
    # The master stores each node's capability report when it connects; placement validation reads it.
    Wait-Until { try { (Invoke-RestMethod -Uri "$master/api/nodes/dev-b").node.lastCapabilities.memory.model -eq 'discrete' } catch { $false } } 'dev-b''s discrete capability report' 120
    $caps = (Invoke-RestMethod -Uri "$master/api/nodes/dev-b").node.lastCapabilities
    foreach ($gpu in $caps.gpus) { Write-Host "    dev-b GPU $($gpu.index): $($gpu.uuid) $($gpu.name) $($gpu.memoryTotalGiB) GiB" }
    if (@($caps.gpus).Count -ne 2) { throw "dev-b reports $(@($caps.gpus).Count) GPU(s), expected 2." }
    if ($caps.features -notcontains 'gpus.uuids') { throw 'dev-b does not report the gpus.uuids feature.' }
    Write-Host "    dev-b P2 features: $(@($caps.features | Where-Object { $_ -in 'gpus.uuids', 'launch.env', 'admission.discrete', 'images.digest-pinned' }) -join ', ')"

    Write-Step 'Smoke: a fake model on each node (fake-devb on dev-b GPU 1), /v1 chat through both'
    foreach ($pair in @(@('fake-dgx', @{ nodeId = 'dgx' }), @('fake-devb', @{ nodeId = 'dev-b'; gpuUuids = @($devBGpus[1]) }))) {
        $model = @{ id = $pair[0]; servedName = $pair[0]; modelPath = 'fake-model'; memoryBudgetGiB = 4; maxModelLen = 8192; maxNumSeqs = 4; placement = $pair[1] }
        $null = Invoke-RestMethod -Method Post -Uri "$master/api/models" -ContentType 'application/json' -Body ($model | ConvertTo-Json -Depth 4)
        # 503/409 while the node is not Online yet or another start holds the node's start slot: retry.
        Wait-Until { try { $null = Invoke-WebRequest -Method Post -Uri "$master/api/models/$($pair[0])/start" -UseBasicParsing -TimeoutSec 60; $true } catch { $false } } "a start of $($pair[0]) on $($pair[1].nodeId)" 120
        Write-Host "    started $($pair[0]) on $($pair[1].nodeId)"
    }
    $placed = (Invoke-RestMethod -Uri "$master/api/models/fake-devb").placement
    Write-Host "    fake-devb placement: node $($placed.nodeId), GPU uuid(s) $($placed.gpuUuids -join ','), index(es) $($placed.gpuIndices -join ',')"
    Wait-Until { $m = (Invoke-WebRequest -Uri "$master/v1/models" -UseBasicParsing).Content; $m -match '"fake-dgx"' -and $m -match '"fake-devb"' } 'both fake models in /v1/models'
    foreach ($name in 'fake-dgx', 'fake-devb') {
        $body = @{ model = $name; messages = @(@{ role = 'user'; content = 'hello' }); max_tokens = 8 } | ConvertTo-Json -Depth 4
        $answer = Invoke-RestMethod -Method Post -Uri "$master/v1/chat/completions" -ContentType 'application/json' -Body $body
        Write-Host "    $name (buffered): $($answer.choices[0].message.content)"
        $streamBody = @{ model = $name; messages = @(@{ role = 'user'; content = 'hello' }); max_tokens = 8; stream = $true } | ConvertTo-Json -Depth 4
        $stream = Invoke-WebRequest -Method Post -Uri "$master/v1/chat/completions" -ContentType 'application/json' -Body $streamBody -UseBasicParsing
        $chunks = ([regex]::Matches($stream.Content, '(?m)^data: ')).Count
        if ($stream.Content -notmatch 'data: \[DONE\]') { throw "$name stream did not end with [DONE]." }
        Write-Host "    $name (streamed): $chunks SSE events, ended with [DONE]"
    }
}

Write-Step 'Up'
Write-Host "    UI:       $master/ (Nodes: $master/nodes)"
Write-Host "    /v1:      $master/v1/models"
Write-Host "    logs:     $(Join-Path $Root 'logs')"
Write-Host "    stop:     tools/dev-multinode/stop.ps1"
