#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs, upgrades, rolls back, re-pairs and removes mirbadllm-agent on a Windows worker PC, and installs the
    pinned llama.cpp runtime (P4 node; design-p3p4 sections A.1.2, A.2.3, A.4.2, A.4.4, A.4.6).

.DESCRIPTION
    Runs under Windows PowerShell 5.1 (the default shell of a clean Windows 11 PC) and PowerShell 7 (spike S4 / F6,
    MN-207 D122). The file stays ASCII-only: 5.1 reads a script without a BOM in the ANSI code page.

    Verbs (first argument):
      install    Installs or re-installs the agent: the binary goes to a versioned folder
                 "C:\Program Files\mirbadllm-agent\app\{version}\", the service runs from there under its own
                 virtual account "NT SERVICE\mirbadllm-agent" (owner decisions D13, D111) with startup type
                 Automatic (Delayed Start). agent.json is written from agent.windows.example.json once and then kept,
                 except for the settings a later run passes explicitly (-NodeId, -ModelsRoot, -CacheRoot): those are
                 applied to the existing file and printed, so the file never disagrees with the command line. -NodeId
                 is checked against the agent's own rule before anything is installed (lower-case letters, digits and
                 '-', at most 40, case-sensitive): a machine name like "DevelopmentPC" is refused with the slug to use
                 instead, rather than written into agent.json for the agent to die on.
                 With -EnrollUrl and -CaHash it enrolls the node; with -LlamaCpp it also installs llama.cpp. Starts the
                 service when the node is enrolled (unless -NoStart). Registers the Event Log source
                 "mirbadllm-agent" (Application log) and sets the service's PreshutdownTimeout to
                 Agent:ShutdownTimeout + 10 s. The default verb.
      upgrade    Installs a new agent version (-Bundle <zip> -BundleSha256 <hex>, -PublishedDir, or the bundle the
                 script runs from) next to the current one, switches the service to it and waits until it listens on
                 its port; on failure it switches back and starts the previous version. Running llama-server processes
                 are not touched: they outlive the service and the new agent adopts them (section A.4.4).
      rollback   Switches the service back to the previous agent version.
      rekey      Re-pairs an enrolled node with a rekey token (section A.4.6): stops the service, keeps a copy of the
                 identity, runs "mirbadllm-agent.exe join --rekey", restores the copy when that fails, starts again.
      uninstall  Stops the runtime processes the service account started from the runtimes folder, removes the
                 service, its Event Log source, the firewall rule and the program folder, keeps
                 %ProgramData%\mirbadllm-agent.

    Operations that run alone (no verb) or with install:
      -LlamaCpp [version]       The llama.cpp version pinned in llama-cpp.lock.json (only that one), in the build
                                -Accelerator names: refuses a PC without the Visual C++ 2015-2022 x64 runtime (and, for
                                the CUDA build, a driver below minDriver), downloads the build's assets by tag (or takes
                                them from -LlamaCppSource), checks their SHA-256, unpacks them into one staging folder,
                                requires exactly the lock file's files (hash and size, nothing missing, nothing extra),
                                moves the folder to runtimes\llama.cpp\{tag}\ (read-only for the service account),
                                writes its manifest.json and the Agent:Executables entry in agent.json.
      -Accelerator <auto|cuda|cpu|vulkan>
                                Which build -LlamaCpp installs, written to agent.json as Agent:Accelerator (the agent
                                reports it and the master places models by it). auto, the default: the CUDA build when
                                an NVIDIA driver answers, else the CPU build, so a PC without an NVIDIA card joins as a
                                CPU-only node. cpu: the CPU build also on a PC with an NVIDIA card (one too old for the
                                CUDA build, for example). vulkan: the Vulkan build for AMD and Intel GPUs, experimental
                                and not yet verified on AMD or Intel hardware; it needs the Vulkan runtime the GPU driver
                                installs (vulkan-1.dll) and adds the service account to the local group Performance
                                Monitor Users, because the agent reads each GPU's memory in use from the "GPU Adapter
                                Memory" performance counters. One build per version folder: a run with another
                                accelerator replaces the build of that version.
      -RemoveLlamaCpp <version> Removes an installed version; refused while a process runs from it or while it is the
                                default of other installed versions.
      -ImportGguf <file> -Folder <name> [-Sha256 <hex>]
                                Copies a GGUF (for example an Ollama blob "sha256-<hex>") into the models root as
                                {Folder}\model.gguf and verifies its SHA-256 (taken from the blob's name when -Sha256 is
                                not given). A manual fallback only (section A.2.2).
      -AllowService <name>[,<name>...] [-AllowServiceAutostart]
                                Lets the agent manage these Windows services from the Services page (section A.4.3,
                                MN-228): grants NT SERVICE\mirbadllm-agent start, stop and query rights on each one
                                ("sc sdset", so the agent needs no admin rights) and adds it to the agent's whitelist
                                (state\scm-whitelist.json; empty by default). Services on the agent's deny list (Defender,
                                the Event Log, RPC, DNS client, file sharing, Remote Desktop, Windows Update, the
                                firewall, the NVIDIA display driver, the agent itself) and drivers are refused.
                                -AllowServiceAutostart also grants the right to change the start type, which is the right
                                to change the service's configuration as a whole (its program included): granted only on
                                request. Takes effect without restarting the agent.

    Security (spike S4): %ProgramData%\mirbadllm-agent is readable only by SYSTEM, Administrators and the service
    account (the root is protected, everything below it inherits: F1). The join or rekey token comes only from
    $env:MIRBADLLM_JOIN_TOKEN or from a secure prompt (-PromptToken); it is never a parameter (process command lines
    are readable by every user) and it is cleared from the session in a finally block, also when a step fails (F5).
    TCP 9443 is opened inbound for the service only (a -Service filter, so it survives the binPath switches between
    app\{version} folders), from the master only (-MasterAddress, default: the enroll URL's host), on the Private
    profile only; a Windows Firewall that is off is reported in red (F4). No Docker: the node runs llama.cpp natively
    and the account joins no Docker group (section A.4.2).

    -DryRun runs every check (lock file, driver, Visual C++ runtime, the llama.cpp download and verification into a
    temporary folder, time sync, power plan) and prints every change instead of making it.

.EXAMPLE
    $env:MIRBADLLM_JOIN_TOKEN = '<token>'
    .\install-agent.ps1 install -NodeId win-1 -EnrollUrl https://192.0.2.191:8443 -CaHash sha256:<hash> -LlamaCpp

.EXAMPLE
    .\install-agent.ps1 install -NodeId win-1 -EnrollUrl https://192.0.2.191:8443 -CaHash sha256:<hash> -PromptToken

.EXAMPLE
    .\install-agent.ps1 upgrade -Bundle .\win-x64.zip -BundleSha256 <hex>

.EXAMPLE
    .\install-agent.ps1 -LlamaCpp b11476 -LlamaCppSource D:\downloads\llama.cpp
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('install', 'upgrade', 'rollback', 'rekey', 'uninstall')]
    [string] $Verb,

    [Parameter(Position = 1)]
    [ValidatePattern('^b[0-9]{1,7}$')]
    [string] $LlamaCppVersion,

    # Deliberately no [ValidatePattern]: PowerShell's defaults to RegexOptions.IgnoreCase, so it accepted the machine
    # name 'DevelopmentPC' - a value the agent then refuses in Agent:NodeId, exiting before it can log (MN-239 C1).
    # Assert-NodeId applies the agent's own rule, case-sensitively, and names both the value and the fix.
    [string] $NodeId,

    [string] $ModelsRoot = 'D:\mirbadllm\models',

    [string] $CacheRoot = 'D:\mirbadllm\cache',

    [string] $PublishedDir,

    [string] $Bundle,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $BundleSha256,

    [uri] $EnrollUrl,

    [ValidatePattern('^sha256:')]
    [string] $CaHash,

    [string] $NodeUid,

    [switch] $PromptToken,

    [string[]] $Address = @(),

    [string[]] $MasterAddress = @(),

    [switch] $NoStart,

    [switch] $LlamaCpp,

    [ValidateSet('auto', 'cuda', 'cpu', 'vulkan')]
    [string] $Accelerator = 'auto',

    [string] $LlamaCppSource,

    [ValidatePattern('^b[0-9]{1,7}$')]
    [string] $RemoveLlamaCpp,

    [string] $ImportGguf,

    [string] $Folder,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $Sha256,

    # No wildcard characters: Get-Service -Name 'Sp*' matches whichever service sorts first, so a pattern must never
    # reach the grant (MN-230).
    [ValidatePattern('^[^/\\*?\[\]]{1,256}$')]
    [string[]] $AllowService = @(),

    [switch] $AllowServiceAutostart,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The script's own bound parameters. Inside a function $PSBoundParameters is that function's, so this copy is what
# tells a setting the operator passed explicitly from one that merely has a default (MN-239 C2).
$BoundParameters = $PSBoundParameters

# An agent installed before the rename (SparkServe) keeps its names: install, upgrade (also one pushed from the master),
# rollback and rekey work in place under its old service, account and folders, because the virtual account's SID, and so
# every ACL it holds, derives from the service name. Only the executable inside is new (docs/setup.md, "Upgrading from
# a pre-rename install"). The agent itself tells the two apart by the folder it runs from.
$ServiceName = 'mirbadllm-agent'
$LegacyServiceName = 'spark-serve-agent'
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Service -Name $LegacyServiceName -ErrorAction SilentlyContinue)) {
    $ServiceName = $LegacyServiceName
}
# The pre-rename update helper and join instructions use the old variable names (legacy shim).
if (-not $env:MIRBADLLM_UPDATE_HELPER -and $env:SPARK_SERVE_UPDATE_HELPER) { $env:MIRBADLLM_UPDATE_HELPER = $env:SPARK_SERVE_UPDATE_HELPER }
if (-not $env:MIRBADLLM_JOIN_TOKEN -and $env:SPARK_SERVE_JOIN_TOKEN) { $env:MIRBADLLM_JOIN_TOKEN = $env:SPARK_SERVE_JOIN_TOKEN }
Remove-Item -Path Env:SPARK_SERVE_JOIN_TOKEN -ErrorAction SilentlyContinue
$ServiceAccount = "NT SERVICE\$ServiceName"
$InstallDir = Join-Path $env:ProgramFiles $ServiceName
$AppDir = Join-Path $InstallDir 'app'
$RuntimesDir = Join-Path (Join-Path $InstallDir 'runtimes') 'llama.cpp'
$DataDir = Join-Path $env:ProgramData $ServiceName
$ConfigPath = Join-Path $DataDir 'agent.json'
$StateDir = Join-Path $DataDir 'state'
$FirewallRule = "$ServiceName (mTLS 9443)"
$TokenVariable = 'MIRBADLLM_JOIN_TOKEN'
$LockPath = Join-Path $PSScriptRoot 'llama-cpp.lock.json'
$SystemDir = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { Join-Path $env:SystemRoot 'Sysnative' } else { [Environment]::SystemDirectory }
$KeepAgentVersions = 3
$HealthTimeoutSeconds = 90
$ExecutableName = 'llama-server'
$ServiceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$EventLogName = 'Application'
$IdentityFiles = @('agent.crt', 'agent.key', 'ca.crt', 'node-uid')
$UpdaterName = "$ServiceName-updater"
$UpdateTrustDir = Join-Path $InstallDir 'update'
$VcRuntimeFiles = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')

# ------------------------------------------------------------------------------------------------------------- helpers

function Write-Step([string] $Message) { Write-Host "==> $Message" }

function Write-Alert([string] $Message) { Write-Host "WARNING: $Message" -ForegroundColor Red }

function Invoke-Change([string] $What, [scriptblock] $Action) {
    # Every change to the machine goes through here, so -DryRun can print it instead.
    if ($DryRun) {
        Write-Host "[dry-run] $What"
        return
    }
    & $Action
}

function Set-PrivateAcl([string] $Path, [string] $Account, [string] $Rights = 'M') {
    # SYSTEM and Administrators full control, $Account $Rights (M = modify, RX = read/execute); nothing inherited from
    # above, nobody else. The grant goes on the root only. "/inheritance:r" with "/T" would also run on every existing
    # file, strip its inherited entries and leave it with an empty DACL, because the (OI)(CI) grants do not apply to
    # files: the service then gets "Access is denied" on agent.json and its key (spike S4 / F1). Below the root,
    # "/reset" replaces every explicit entry with the inherited ones, which also repairs a tree an older installer broke.
    & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${Account}:(OI)(CI)$Rights" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on $Path" }
    if (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1) {
        & icacls.exe (Join-Path $Path '*') /reset /T /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls /reset failed below $Path" }
    }
}

function Get-ServiceSid([string] $Name) {
    # A service SID is derived from the service name, so it is known with or without the service.
    $line = (& sc.exe showsid $Name) | Where-Object { $_ -match 'SERVICE SID:\s*(S-1-5-80-[0-9-]+)' } | Select-Object -First 1
    if ($line -and $line -match '(S-1-5-80-[0-9-]+)') { return $Matches[1] }
    return $null
}

function Get-AccountReference {
    # icacls takes "*SID"; the virtual account's SID resolves even before the service exists (a name would not).
    $sid = Get-ServiceSid $ServiceName
    if ($sid) { return "*$sid" }
    return $ServiceAccount
}

function Get-MasterAddress([uri] $Url, [string[]] $Given) {
    if ($Given.Count -gt 0) { return $Given }
    if ($Url) {
        # New-NetFirewallRule takes addresses, not names: resolve the enroll URL's host.
        $resolved = @([System.Net.Dns]::GetHostAddresses($Url.DnsSafeHost) | ForEach-Object { $_.IPAddressToString })
        if ($resolved.Count -gt 0) { return $resolved }
    }
    Write-Alert 'No -MasterAddress and no -EnrollUrl: the firewall rule allows the local subnet. Re-run with -MasterAddress <master IP>.'
    return @('LocalSubnet')
}

function Clear-JoinToken {
    Remove-Item -Path "Env:$TokenVariable" -ErrorAction SilentlyContinue
}

function Get-JsonProperty($Object, [string] $Name) {
    # Under Set-StrictMode a missing property throws; agent.json and the lock file may lack optional members.
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Set-JsonProperty($Object, [string] $Name, $Value) {
    # Windows PowerShell 5.1 cannot turn JSON into a hashtable: edit the PSCustomObject (Add-Member -Force also
    # covers a property the example does not have, under Set-StrictMode).
    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}

function Write-JsonFile([string] $Path, $Object) {
    # UTF-8 without a BOM in 5.1 and 7 alike (5.1 has no BOM-less UTF-8 value for -Encoding); written next to the
    # target and moved over it, so a failure never leaves half a file.
    $temp = "$Path.tmp"
    [IO.File]::WriteAllText($temp, (ConvertTo-Json -InputObject $Object -Depth 20), (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

function Get-FileSha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-AgentConfig {
    if (-not (Test-Path -LiteralPath $ConfigPath)) { return $null }
    return Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
}

function Get-AgentPort {
    $port = Get-JsonProperty (Get-JsonProperty (Read-AgentConfig) 'Agent') 'Port'
    if ($port) { return [int]$port }
    return 9443
}

function Get-ExampleConfigPath {
    # Next to the script in the win-x64 bundle; one folder up in the repository (deploy\agent\windows).
    foreach ($candidate in @((Join-Path $PSScriptRoot 'agent.windows.example.json'), (Join-Path (Split-Path $PSScriptRoot -Parent) 'agent.windows.example.json'))) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "agent.windows.example.json not found next to $PSScriptRoot."
}

# ----------------------------------------------------------------------------------------------------- the node id

function Test-NodeId([string] $Value) {
    # Protocol.NodeIdPattern (src\MirbadLLM.Agent.Contracts\Protocol.cs), matched exactly as the agent matches it in
    # AgentOptions.Validate: RegexOptions.CultureInvariant and nothing else, so the comparison is case-sensitive.
    # [regex]::IsMatch is; PowerShell's own operators and parameter attributes are not, which is the whole bug.
    return [regex]::IsMatch($Value, '^[a-z0-9][a-z0-9-]{0,39}$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

function Get-NodeIdSuggestion([string] $Value) {
    # The mechanical slug of $Value (the idiom of Get-ScmWhitelistName), offered in the message below and never
    # applied: a node id is an identity. It is in the agent certificate, in the master's "Add node" command and in
    # every model definition, so turning 'DevelopmentPC' into 'developmentpc' behind the operator's back would enroll
    # the node under an id nobody typed and leave the two expectations to diverge later.
    $slug = ("$Value".Trim().ToLowerInvariant() -creplace '[^a-z0-9]', '-') -creplace '-{2,}', '-'
    $slug = $slug.Trim('-')
    if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40).TrimEnd('-') }
    return $slug
}

function Assert-NodeId([string] $Value) {
    if (Test-NodeId $Value) { return }
    $suggestion = Get-NodeIdSuggestion $Value
    $fix = 'Pick a node id of lower-case letters, digits and ''-'' (at most 40)'
    if ($suggestion) { $fix = "Re-run with -NodeId $suggestion" }
    throw ("-NodeId '$Value' is not a node id (lower-case letters, digits and '-', at most 40): the machine name of a " +
        'Windows PC usually is not one. The agent refuses it in Agent:NodeId and exits before it can log, which the ' +
        'Service Control Manager reports as error 1053 with nothing in the Application log. Nothing was written. ' +
        "$fix, and use the same id in the master's ""Add node"" command.")
}

# ----------------------------------------------------------------------------------------------- environment checks

function Get-TimeSyncProblem([string[]] $StatusLines, [int] $ExitCode) {
    # "w32tm /query /status": a stopped Windows Time service fails the query; a free-running or CMOS clock is not synced.
    # The agent refuses mutations at more than 5 min skew and is Degraded at 30 s (section A.4.7).
    if ($ExitCode -ne 0) { return 'The Windows Time service does not answer (w32tm /query /status failed): the clock is not synced. Run "w32tm /resync" or start the service, because the agent refuses mutations at more than 5 minutes of skew.' }
    $source = $StatusLines | Where-Object { $_ -match '^\s*Source:\s*(.+?)\s*$' } | Select-Object -First 1
    if ($source -and $source -match '^\s*Source:\s*(.+?)\s*$') {
        $name = $Matches[1]
        if ($name -match 'Local CMOS Clock|Free-running System Clock') {
            return "The clock is not synced (time source: $name). Configure an NTP source (w32tm /config /syncfromflags:manual /manualpeerlist:pool.ntp.org /update), because the agent refuses mutations at more than 5 minutes of skew."
        }
    }
    return $null
}

function Get-PowerPlanProblem([string[]] $StandbyLines) {
    # "powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE": a PC that sleeps on AC power drops its models (Offline).
    $line = $StandbyLines | Where-Object { $_ -match 'Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)' } | Select-Object -First 1
    if ($line -and $line -match 'Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)') {
        $seconds = [Convert]::ToInt64($Matches[1], 16)
        if ($seconds -gt 0) {
            return "The active power plan puts this PC to sleep after $([math]::Round($seconds / 60)) minutes on AC power: the node then goes Offline and its models stop answering. Set ""Sleep"" to Never (powercfg /change standby-timeout-ac 0)."
        }
    }
    return $null
}

function Test-HostWarnings {
    # Warnings only: neither stops the install.
    try {
        $status = @(& w32tm.exe /query /status)
        $problem = Get-TimeSyncProblem $status $LASTEXITCODE
        if ($problem) { Write-Alert $problem }
    } catch {
        Write-Alert "Could not query the Windows Time service: $($_.Exception.Message)"
    }
    try {
        $standby = @(& powercfg.exe /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE)
        if ($LASTEXITCODE -eq 0) {
            $problem = Get-PowerPlanProblem $standby
            if ($problem) { Write-Alert $problem }
        }
    } catch {
        Write-Alert "Could not read the power plan: $($_.Exception.Message)"
    }
}

function Test-VcRuntime {
    # The llama.cpp binaries import the Visual C++ 2015-2022 x64 runtime, which the release zips do not ship (MN-207,
    # D120). Never downloaded here: that would be an unpinned download.
    $missing = @($VcRuntimeFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $SystemDir $_)) })
    if ($missing.Count -gt 0) {
        throw ("The Microsoft Visual C++ 2015-2022 Redistributable (x64) is not installed: $($missing -join ', ') missing in $SystemDir. " +
            'llama.cpp needs it. Install "Microsoft Visual C++ 2015-2022 Redistributable (x64)" (vc_redist.x64.exe from Microsoft) and re-run; this installer does not download it.')
    }
}

function Get-NvidiaDriverVersion {
    $smi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    $path = if ($smi) { $smi.Source } else { Join-Path $SystemDir 'nvidia-smi.exe' }
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    $lines = @(& $path --query-gpu=driver_version --format=csv,noheader)
    if ($LASTEXITCODE -ne 0) { return @() }
    return @($lines | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
}

function Test-NvidiaDriver([string] $MinDriver) {
    $versions = @(Get-NvidiaDriverVersion | Where-Object { $_ })
    if ($versions.Count -eq 0) { throw 'No NVIDIA driver found (nvidia-smi.exe is missing or reports no GPU): the CUDA build of llama.cpp needs an NVIDIA GPU with its driver. Leave out -Accelerator cuda to install the CPU build instead.' }
    foreach ($v in $versions) {
        $parsed = $null
        if (-not [version]::TryParse($v, [ref]$parsed)) { throw "Unreadable NVIDIA driver version '$v'." }
        if ($parsed -lt [version]$MinDriver) {
            throw "NVIDIA driver $v is older than $MinDriver, the minimum for this llama.cpp build (CUDA runtime in llama-cpp.lock.json). Update the driver and re-run, or pass -Accelerator cpu to run models on the CPU of this PC."
        }
    }
}

function Resolve-Accelerator([string] $Requested) {
    # The llama.cpp build this PC gets. auto: CUDA when an NVIDIA driver answers, else the CPU build. Vulkan is
    # experimental, so it is installed only when asked for.
    if ($Requested -and $Requested -ne 'auto') { return $Requested }
    if (@(Get-NvidiaDriverVersion | Where-Object { $_ }).Count -gt 0) { return 'cuda' }
    return 'cpu'
}

function Test-VulkanRuntime {
    # The Vulkan build links the system's Vulkan loader, which the GPU driver installs; the release zip has none.
    return (Test-Path -LiteralPath (Join-Path $SystemDir 'vulkan-1.dll'))
}

function Test-AcceleratorHost($Lock, [string] $Resolved, [string] $Requested) {
    # Fails before anything changes when the chosen build cannot run here, and says what was chosen and why.
    switch ($Resolved) {
        'cuda' { Test-NvidiaDriver $Lock.minDriver }
        'cpu' {
            if ($Requested -eq 'cpu') { Write-Step 'Installing the CPU build of llama.cpp (-Accelerator cpu): this node runs models on the CPU and counts their memory against host RAM.' }
            else { Write-Step 'No NVIDIA driver found (nvidia-smi.exe is missing or reports no GPU): installing the CPU build of llama.cpp. This node runs models on the CPU and counts their memory against host RAM. With an NVIDIA card, install its driver and re-run; with an AMD or Intel GPU, -Accelerator vulkan installs the experimental Vulkan build.' }
        }
        'vulkan' {
            if (-not (Test-VulkanRuntime)) {
                throw "The Vulkan runtime is not installed: vulkan-1.dll is missing in $SystemDir. The Vulkan build of llama.cpp needs it. Install the current AMD or Intel graphics driver (it installs the Vulkan runtime) and re-run, or pass -Accelerator cpu."
            }
            Write-Alert 'The Vulkan build of llama.cpp (AMD and Intel GPUs) is experimental: it has not been verified on AMD or Intel hardware. Free VRAM is read from Windows performance counters; while it cannot be read the agent refuses to start a model on that GPU.'
            if (@(Get-NvidiaDriverVersion | Where-Object { $_ }).Count -gt 0) {
                Write-Alert 'An NVIDIA driver answers on this PC: the CUDA build (-Accelerator cuda, or no -Accelerator) is the supported path for an NVIDIA card.'
            }
        }
    }
}

function Select-LlamaCppBuild($Lock, [string] $Resolved) {
    # The lock file's build for an accelerator, in the lock file's own shape (version, assets, files): the top level is
    # the CUDA build, every other build is an entry of "variants" at the same release tag.
    if ($Resolved -eq 'cuda') { return $Lock }
    $variant = @(Get-JsonProperty $Lock 'variants' | Where-Object { $_ -and $_.accelerator -eq $Resolved }) | Select-Object -First 1
    if (-not $variant) { throw "llama-cpp.lock.json pins no $Resolved build of llama.cpp $($Lock.version)." }
    return [pscustomobject][ordered]@{ version = $Lock.version; accelerator = $Resolved; assets = $variant.assets; files = $variant.files }
}

function Grant-PerformanceCounterAccess {
    # A Vulkan node reads VRAM in use from the "GPU Adapter Memory" performance counters. Windows lets services read
    # them only as SYSTEM, LOCAL SERVICE, NETWORK SERVICE, an administrator or a member of Performance Monitor Users
    # (S-1-5-32-558; the ACL of HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib), and the agent's virtual
    # account is none of them.
    $sid = Get-ServiceSid $ServiceName
    if (-not $sid) {
        Write-Alert "The service $ServiceName does not exist yet, so its account was not added to Performance Monitor Users. Run install first, then -LlamaCpp -Accelerator vulkan again."
        return
    }
    $members = @(Get-LocalGroupMember -SID 'S-1-5-32-558' -ErrorAction SilentlyContinue | ForEach-Object { $_.SID.Value })
    if ($members -contains $sid) { return }
    Invoke-Change "Add $ServiceAccount to the local group Performance Monitor Users (GPU memory counters)" {
        Add-LocalGroupMember -SID 'S-1-5-32-558' -Member $ServiceAccount
    }
}

# --------------------------------------------------------------------------------------------------------- llama.cpp

function Test-RelativePath([string] $Path) {
    # A '/'-separated path below the version folder: no root, drive, stream, parent segment or backslash.
    if (-not $Path -or $Path.StartsWith('/') -or $Path.Contains('\') -or $Path.Contains(':')) { return $false }
    foreach ($segment in $Path.Split('/')) {
        if (-not $segment -or $segment -eq '.' -or $segment -eq '..') { return $false }
    }
    return $true
}

function Read-LlamaCppLock([string] $Path) {
    $lock = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    if ((Get-JsonProperty $lock 'schemaVersion') -ne 1) { throw "$Path`: unknown schemaVersion." }
    $version = Get-JsonProperty $lock 'version'
    if ("$version" -notmatch '^b[0-9]+$') { throw "$Path`: version '$version' is not a bNNNN release tag." }
    foreach ($name in @('cuda', 'minDriver')) {
        if (-not (Get-JsonProperty $lock $name)) { throw "$Path`: $name is missing." }
    }
    $assets = @(Get-JsonProperty $lock 'assets')
    if ($assets.Count -eq 0) { throw "$Path`: no assets." }
    foreach ($a in $assets) {
        # By tag only: every bNNNN release is a prerelease, so releases/latest never returns it (spike S1 / C10).
        $expected = "https://github.com/ggml-org/llama.cpp/releases/download/$version/$($a.name)"
        if ($a.url -ne $expected) { throw "$Path`: asset $($a.name) must be downloaded by tag from $expected, not $($a.url)." }
        if ("$($a.sha256)" -notmatch '^[0-9a-f]{64}$') { throw "$Path`: asset $($a.name) has no SHA-256." }
    }
    $files = @(Get-JsonProperty $lock 'files')
    if ($files.Count -eq 0) { throw "$Path`: the lock file lists no files; every unpacked file must be pinned (spike S1 / C3)." }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($f in $files) {
        if (-not (Test-RelativePath $f.path)) { throw "$Path`: bad file path '$($f.path)'." }
        if ("$($f.sha256)" -notmatch '^[0-9a-f]{64}$' -or [long]$f.bytes -le 0) { throw "$Path`: file $($f.path) has no SHA-256 or size." }
        if (-not $seen.Add($f.path)) { throw "$Path`: file $($f.path) is listed twice." }
    }
    # The CPU and Vulkan builds of the same release: the same rules, per build.
    $accelerators = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($v in @(Get-JsonProperty $lock 'variants' | Where-Object { $_ })) {
        $name = "$(Get-JsonProperty $v 'accelerator')"
        if ($name -notmatch '^(cpu|vulkan)$') { throw "$Path`: variant accelerator '$name' is not cpu or vulkan." }
        if (-not $accelerators.Add($name)) { throw "$Path`: the $name variant is listed twice." }
        $variantAssets = @(Get-JsonProperty $v 'assets')
        if ($variantAssets.Count -eq 0) { throw "$Path`: the $name variant has no assets." }
        foreach ($a in $variantAssets) {
            $expected = "https://github.com/ggml-org/llama.cpp/releases/download/$version/$($a.name)"
            if ($a.url -ne $expected) { throw "$Path`: asset $($a.name) must be downloaded by tag from $expected, not $($a.url)." }
            if ("$($a.sha256)" -notmatch '^[0-9a-f]{64}$') { throw "$Path`: asset $($a.name) has no SHA-256." }
        }
        $variantFiles = @(Get-JsonProperty $v 'files')
        if ($variantFiles.Count -eq 0) { throw "$Path`: the $name variant lists no files; every unpacked file must be pinned." }
        $seenInVariant = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($f in $variantFiles) {
            if (-not (Test-RelativePath $f.path)) { throw "$Path`: bad file path '$($f.path)' in the $name variant." }
            if ("$($f.sha256)" -notmatch '^[0-9a-f]{64}$' -or [long]$f.bytes -le 0) { throw "$Path`: file $($f.path) of the $name variant has no SHA-256 or size." }
            if (-not $seenInVariant.Add($f.path)) { throw "$Path`: file $($f.path) is listed twice in the $name variant." }
        }
    }
    return $lock
}

function Save-LlamaCppAssets($Lock, [string] $Source, [string] $DownloadDir) {
    foreach ($a in @($Lock.assets)) {
        $target = Join-Path $DownloadDir $a.name
        if ($Source) {
            Write-Step "Taking $($a.name) from $Source"
            Copy-Item -LiteralPath (Join-Path $Source $a.name) -Destination $target
        } else {
            Write-Step "Downloading $($a.url)"
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            # The 5.1 progress bar slows a large download a lot.
            $previous = $ProgressPreference
            $ProgressPreference = 'SilentlyContinue'
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $a.url -OutFile $target
            } finally {
                $ProgressPreference = $previous
            }
        }
        $bytes = (Get-Item -LiteralPath $target).Length
        $expectedBytes = [long](Get-JsonProperty $a 'bytes')
        if ($expectedBytes -gt 0 -and $bytes -ne $expectedBytes) { throw "$($a.name): $bytes bytes, the lock file pins $expectedBytes." }
        $hash = Get-FileSha256 $target
        if ($hash -ne $a.sha256) { throw "$($a.name): SHA-256 $hash, the lock file pins $($a.sha256). Not installed." }
    }
}

function Expand-LlamaCppAssets($Lock, [string] $DownloadDir, [string] $Staging) {
    # Both zips unpack flat into one folder. Extracted entry by entry so a path outside the folder or a file both zips
    # carry is refused instead of silently overwritten.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $Staging | Out-Null
    foreach ($a in @($Lock.assets)) {
        $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $DownloadDir $a.name))
        try {
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName.Replace('\', '/')
                if ($name.EndsWith('/')) { continue }
                if (-not (Test-RelativePath $name)) { throw "$($a.name): entry '$($entry.FullName)' would unpack outside the version folder." }
                $destination = Join-Path $Staging $name.Replace('/', '\')
                if (Test-Path -LiteralPath $destination) { throw "$($a.name): $name is also in another asset." }
                New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
            }
        } finally {
            $zip.Dispose()
        }
    }
}

function Test-LlamaCppFiles($Lock, [string] $Folder, [string[]] $Ignore = @()) {
    # Exactly the lock file's set: every file present with its size and hash, nothing missing, nothing extra (a planted
    # DLL would be loaded from the application folder first; spike S1 / C3).
    $root = (Get-Item -LiteralPath $Folder).FullName.TrimEnd('\')
    $expected = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($f in @($Lock.files)) { $expected[$f.path] = $f }
    $problems = New-Object 'System.Collections.Generic.List[string]'
    $found = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($Ignore -contains $relative) { continue }
        [void]$found.Add($relative)
        if (-not $expected.ContainsKey($relative)) {
            $problems.Add("unexpected file $relative")
            continue
        }
        $want = $expected[$relative]
        if ($file.Length -ne [long]$want.bytes) {
            $problems.Add("$relative is $($file.Length) bytes, expected $($want.bytes)")
            continue
        }
        $hash = Get-FileSha256 $file.FullName
        if ($hash -ne $want.sha256) { $problems.Add("$relative has SHA-256 $hash, expected $($want.sha256)") }
    }
    foreach ($path in $expected.Keys) {
        if (-not $found.Contains($path)) { $problems.Add("missing file $path") }
    }
    if ($problems.Count -gt 0) {
        $shown = @($problems | Select-Object -First 10) -join '; '
        throw "llama.cpp $($Lock.version) does not match llama-cpp.lock.json ($($problems.Count) problem(s)): $shown"
    }
}

function New-ExecutableManifest($Lock) {
    # manifest.json in the ExecutableManifest shape (camelCase), from the same list the folder was verified against.
    $files = @($Lock.files | ForEach-Object { [pscustomobject][ordered]@{ path = $_.path; sha256 = $_.sha256; bytes = [long]$_.bytes } })
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        name = $ExecutableName
        version = $Lock.version
        cuda = Get-JsonProperty $Lock 'cuda'
        sources = @($Lock.assets | ForEach-Object { $_.name })
        files = $files
    }
}

function Get-ProcessesUnder([string] $Folder) {
    $prefix = $Folder.TrimEnd('\') + '\'
    return @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}

function Register-LlamaCppExecutable($Lock, [string] $Target) {
    # Agent:Executables in agent.json (ExecutableOptions shape): replace this version, keep the others and the default.
    $config = Read-AgentConfig
    if (-not $config) { throw "No $ConfigPath yet: install the agent first (install -NodeId ...)." }
    $agent = $config.Agent
    $executables = @(Get-JsonProperty $agent 'Executables' | Where-Object { $_ })
    $entry = $executables | Where-Object { $_.Name -eq $ExecutableName } | Select-Object -First 1
    if (-not $entry) {
        $entry = [pscustomobject][ordered]@{ Name = $ExecutableName; Default = $null; Versions = @() }
        $executables += $entry
    }
    $versions = @(Get-JsonProperty $entry 'Versions' | Where-Object { $_ -and $_.Version -ne $Lock.version })
    $versions += [pscustomobject][ordered]@{
        Version = $Lock.version
        Cuda = Get-JsonProperty $Lock 'cuda'
        Path = Join-Path $Target 'llama-server.exe'
        Manifest = Join-Path $Target 'manifest.json'
    }
    Set-JsonProperty $entry 'Versions' $versions
    $default = Get-JsonProperty $entry 'Default'
    if (-not $default -or -not ($versions | Where-Object { $_.Version -eq $default })) {
        Set-JsonProperty $entry 'Default' $Lock.version
    }
    Set-JsonProperty $agent 'Executables' $executables
    # Agent:Accelerator follows the build just registered: cpu or vulkan, and no entry at all for the CUDA build, so an
    # NVIDIA node's agent.json is what it always was.
    $accelerator = Get-JsonProperty $Lock 'accelerator'
    if ($accelerator) { Set-JsonProperty $agent 'Accelerator' $accelerator }
    elseif ($agent.PSObject.Properties['Accelerator']) { $agent.PSObject.Properties.Remove('Accelerator') }
    Write-JsonFile $ConfigPath $config
}

function Get-InstalledLlamaCppSources([string] $Target) {
    # The asset names the installed version folder was unpacked from (its manifest.json); empty when unreadable.
    $manifest = Join-Path $Target 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifest)) { return @() }
    try { return @((Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json).sources | Where-Object { $_ }) } catch { return @() }
}

function Install-LlamaCpp([string] $Version, [string] $Source, [string] $Requested = 'auto') {
    $pinned = Read-LlamaCppLock $LockPath
    if ($Version -and $Version -ne $pinned.version) {
        throw "This installer pins llama.cpp $($pinned.version) (llama-cpp.lock.json); $Version is not pinned."
    }
    $resolved = Resolve-Accelerator $Requested
    $lock = Select-LlamaCppBuild $pinned $resolved
    Test-VcRuntime
    Test-AcceleratorHost $pinned $resolved $Requested
    $target = Join-Path $RuntimesDir $lock.version

    if (Test-Path -LiteralPath $target) {
        $intact = $true
        $installed = @(Get-InstalledLlamaCppSources $target)
        $wanted = @($lock.assets | ForEach-Object { $_.name })
        if ($installed.Count -gt 0 -and (($installed -join '|') -ne ($wanted -join '|'))) {
            # Another build of the same version (the CUDA build on a PC that now runs on the CPU, for example).
            $intact = $false
            Write-Step "$target holds another build ($($installed -join ', ')); replacing it with the $resolved build."
        } else {
            try { Test-LlamaCppFiles $lock $target @('manifest.json') } catch { $intact = $false; Write-Alert "$target does not verify ($($_.Exception.Message)); installing it again." }
        }
        if ($intact -and (Test-Path -LiteralPath (Join-Path $target 'manifest.json'))) {
            Write-Step "llama.cpp $($lock.version) ($resolved build) is installed and verified in $target"
            Invoke-Change "Register llama.cpp $($lock.version) in $ConfigPath" { Register-LlamaCppExecutable $lock $target }
            if ($resolved -eq 'vulkan') { Grant-PerformanceCounterAccess }
            return
        }
        $users = @(Get-ProcessesUnder $target)
        if ($users.Count -gt 0) { throw "$target is in use (process $($users[0].ProcessId)); stop its models first." }
        Invoke-Change "Remove the previous $target" { Remove-Item -LiteralPath $target -Recurse -Force }
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ('mirbadllm-llama.cpp-' + [guid]::NewGuid().ToString('N'))
    # Staged next to the target (same volume, so the final step is a rename); in a dry run in the temporary folder.
    $staging = if ($DryRun) { Join-Path $work 'staging' } else { Join-Path $RuntimesDir ('.staging-' + [guid]::NewGuid().ToString('N')) }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        Save-LlamaCppAssets $lock $Source $work
        Write-Step "Unpacking llama.cpp $($lock.version) ($resolved build)"
        Expand-LlamaCppAssets $lock $work $staging
        Test-LlamaCppFiles $lock $staging
        Write-Step "All $(@($lock.files).Count) files match llama-cpp.lock.json"
        Invoke-Change "Install llama.cpp $($lock.version) to $target (read-only for $ServiceAccount) with manifest.json" {
            Move-Item -LiteralPath $staging -Destination $target
            Write-JsonFile (Join-Path $target 'manifest.json') (New-ExecutableManifest $lock)
            Set-PrivateAcl $target (Get-AccountReference) 'RX'
        }
        Invoke-Change "Register llama.cpp $($lock.version) in $ConfigPath" { Register-LlamaCppExecutable $lock $target }
        if ($resolved -eq 'vulkan') { Grant-PerformanceCounterAccess }
    } finally {
        foreach ($p in @($staging, $work)) {
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

function Remove-LlamaCpp([string] $Version) {
    $target = Join-Path $RuntimesDir $Version
    $users = @(Get-ProcessesUnder $target)
    if ($users.Count -gt 0) { throw "llama.cpp $Version is in use (process $($users[0].ProcessId)); stop its models first." }
    $config = Read-AgentConfig
    if ($config) {
        $agent = $config.Agent
        $executables = @(Get-JsonProperty $agent 'Executables' | Where-Object { $_ })
        $entry = $executables | Where-Object { $_.Name -eq $ExecutableName } | Select-Object -First 1
        if ($entry) {
            $versions = @(Get-JsonProperty $entry 'Versions' | Where-Object { $_ -and $_.Version -ne $Version })
            if ((Get-JsonProperty $entry 'Default') -eq $Version -and $versions.Count -gt 0) {
                throw "llama.cpp $Version is the node default in $ConfigPath; make another version the default first."
            }
            if ($versions.Count -eq 0) {
                $executables = @($executables | Where-Object { $_.Name -ne $ExecutableName })
            } else {
                Set-JsonProperty $entry 'Versions' $versions
            }
            Set-JsonProperty $agent 'Executables' $executables
            Invoke-Change "Remove llama.cpp $Version from $ConfigPath" { Write-JsonFile $ConfigPath $config }
        }
    }
    if (Test-Path -LiteralPath $target) {
        Invoke-Change "Remove $target" { Remove-Item -LiteralPath $target -Recurse -Force }
    } else {
        Write-Step "$target does not exist"
    }
}

# ------------------------------------------------------------------------------------------------------- GGUF import

function Import-Gguf([string] $Path, [string] $Name, [string] $Digest, [string] $Root) {
    # Manual fallback (section A.2.2): copy a GGUF into the models root and verify its SHA-256.
    if ($Name -notmatch '^[A-Za-z0-9._-]{1,200}$' -or $Name -match '^\.+$' -or $Name.EndsWith('.') -or $Name.EndsWith('.partial')) {
        throw "-Folder '$Name' is not a model folder name (letters, digits, '.', '_', '-'; no trailing dot)."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "No file $Path." }
    if (-not $Digest) {
        # An Ollama blob is named after its digest: sha256-<hex>.
        if ((Split-Path $Path -Leaf) -match '^sha256[-:]([0-9a-fA-F]{64})$') { $Digest = $Matches[1] }
        else { throw "Give -Sha256 <hex> for $Path (only an Ollama blob carries its digest in its name)." }
    }
    $Digest = $Digest.ToLowerInvariant()
    $stream = [IO.File]::OpenRead($Path)
    try {
        $magic = New-Object byte[] 4
        $read = $stream.Read($magic, 0, 4)
    } finally {
        $stream.Dispose()
    }
    if ($read -ne 4 -or [Text.Encoding]::ASCII.GetString($magic) -ne 'GGUF') { throw "$Path is not a GGUF file." }

    $destination = Join-Path $Root $Name
    $partial = "$destination.partial"
    foreach ($p in @($destination, $partial)) {
        if (Test-Path -LiteralPath $p) { throw "$p already exists." }
    }
    $fileName = if ($Path.EndsWith('.gguf', [StringComparison]::OrdinalIgnoreCase)) { Split-Path $Path -Leaf } else { 'model.gguf' }
    Invoke-Change "Copy $Path to $destination\$fileName and verify sha256:$Digest" {
        New-Item -ItemType Directory -Force -Path $partial | Out-Null
        $copy = Join-Path $partial $fileName
        Copy-Item -LiteralPath $Path -Destination $copy
        $hash = Get-FileSha256 $copy
        if ($hash -ne $Digest) {
            Remove-Item -LiteralPath $partial -Recurse -Force
            throw "$Path has SHA-256 $hash, expected $Digest. Nothing imported."
        }
        Rename-Item -LiteralPath $partial -NewName $Name
        Write-Step "Imported $destination\$fileName (sha256:$Digest)"
    }
}

# ------------------------------------------------------------------------------------- agent versions and the service

function Get-AgentVersion([string] $ExePath) {
    $info = (Get-Item -LiteralPath $ExePath).VersionInfo
    $version = "$($info.ProductVersion)".Trim()
    if (-not $version) { $version = "$($info.FileVersion)".Trim() }
    if (-not $version) { return '0.0.0-' + (Get-FileSha256 $ExePath).Substring(0, 12) }
    # The informational version may carry "+<full commit>": keep 12 hex digits of it.
    if ($version -match '^([^+]+)\+([0-9a-fA-F]{12})[0-9a-fA-F]*$') { $version = "$($Matches[1])+$($Matches[2])" }
    return ($version -replace '[^A-Za-z0-9._+-]', '-')
}

function Read-AgentVersions {
    $path = Join-Path $AppDir 'versions.json'
    if (Test-Path -LiteralPath $path) {
        $state = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        return [pscustomobject]@{
            current = Get-JsonProperty $state 'current'
            previous = Get-JsonProperty $state 'previous'
            history = @(Get-JsonProperty $state 'history' | Where-Object { $_ })
        }
    }
    return [pscustomobject]@{ current = $null; previous = $null; history = @() }
}

function Save-AgentVersions([string] $Current, [string] $Previous) {
    $state = Read-AgentVersions
    $history = @(@($Current) + @($state.history) | Where-Object { $_ } | Select-Object -Unique)
    $state = [pscustomobject][ordered]@{ current = $Current; previous = $Previous; history = $history }
    New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
    Write-JsonFile (Join-Path $AppDir 'versions.json') $state
}

function Get-AgentExe([string] $Version) {
    $exe = Join-Path (Join-Path $AppDir $Version) 'mirbadllm-agent.exe'
    $legacy = Join-Path (Join-Path $AppDir $Version) 'spark-serve-agent.exe' # a version installed before the rename
    if (-not (Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $legacy)) { return $legacy }
    return $exe
}

function Remove-OldAgentVersions {
    # Keeps the current, the previous and the newest others up to $KeepAgentVersions folders (section A.4.4).
    $state = Read-AgentVersions
    $keep = @(@($state.current, $state.previous) + @($state.history) | Where-Object { $_ } | Select-Object -Unique | Select-Object -First ([math]::Max($KeepAgentVersions, 2)))
    foreach ($dir in @(Get-ChildItem -LiteralPath $AppDir -Directory -ErrorAction SilentlyContinue)) {
        if ($keep -notcontains $dir.Name) {
            Invoke-Change "Remove the old agent version $($dir.FullName)" { Remove-Item -LiteralPath $dir.FullName -Recurse -Force }
        }
    }
    $history = @($state.history | Where-Object { $keep -contains $_ })
    if (@($state.history).Count -ne $history.Count) {
        Invoke-Change 'Trim versions.json' {
            Write-JsonFile (Join-Path $AppDir 'versions.json') ([pscustomobject][ordered]@{ current = $state.current; previous = $state.previous; history = $history })
        }
    }
}

function Install-AgentBinary([string] $SourceDir) {
    # Copies mirbadllm-agent.exe into app\{version}\ and returns the version. The running folder is never written:
    # re-installing the current version stops the service first.
    $source = Join-Path $SourceDir 'mirbadllm-agent.exe'
    if (-not (Test-Path -LiteralPath $source)) { throw "No mirbadllm-agent.exe in $SourceDir." }
    $version = Get-AgentVersion $source
    $state = Read-AgentVersions
    if ($version -eq $state.current -and (Get-AgentServiceState) -eq 'Running') { Stop-AgentService }
    $target = Join-Path $AppDir $version
    Invoke-Change "Install agent $version to $target" {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -LiteralPath $source -Destination (Join-Path $target 'mirbadllm-agent.exe') -Force
    }
    return $version
}

function Get-AgentServiceState {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) { return "$($service.Status)" }
    return $null
}

function Stop-AgentService {
    Invoke-Change "Stop the service $ServiceName" { Stop-Service -Name $ServiceName -Force }
}

function Start-AgentService {
    Invoke-Change "Start the service $ServiceName" { Start-Service -Name $ServiceName }
}

function Get-ServiceBinaryPath([string] $Exe) { return "`"$Exe`" --config `"$ConfigPath`"" }

function Set-ServiceBinaryPath([string] $Name, [string] $BinaryPath) {
    # Through WMI, not "sc.exe config binPath=": Windows PowerShell 5.1 breaks the embedded quotes of a native argument,
    # and sc.exe then refuses the path (exit 1639), which stopped an upgrade on the owner's PC with the service down.
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
    if (-not $service) { throw "No service $Name." }
    $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ PathName = $BinaryPath }
    if ($result.ReturnValue -ne 0) { throw "Could not set the program of $Name (Win32_Service.Change returned $($result.ReturnValue))." }
}

function Set-AgentServiceBinary([string] $Exe) {
    $binaryPath = Get-ServiceBinaryPath $Exe
    Invoke-Change "Point the service $ServiceName at $Exe" {
        Set-ServiceBinaryPath $ServiceName $binaryPath
    }
}

function Set-AgentService([string] $Exe, [bool] $Exists) {
    # The service under its virtual account (created by the SCM with the service), Automatic (Delayed Start) so the
    # network and the NVIDIA driver are up first (section A.4.1), restarted after a crash.
    $binaryPath = Get-ServiceBinaryPath $Exe
    Invoke-Change "Configure the service $ServiceName ($ServiceAccount, Automatic (Delayed Start), $Exe)" {
        if (-not $Exists) {
            New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'mirbadllm node agent' `
                -Description 'mirbadllm node agent: runs models for the mirbadllm master (mTLS on TCP 9443).' -StartupType Manual | Out-Null
        } else {
            Set-ServiceBinaryPath $ServiceName $binaryPath
        }
        & sc.exe config $ServiceName obj= $ServiceAccount | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not set the service account $ServiceAccount." }
        # Restart after a crash: 5 s, 5 s, then 60 s; the failure count resets after a day.
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
        & sc.exe config $ServiceName start= delayed-auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not set the startup type of $ServiceName." }
    }
}

function Test-AgentEventSource {
    return [System.Diagnostics.EventLog]::SourceExists($ServiceName)
}

function Register-AgentEventSource {
    # The agent logs to the Application log under its own source (Microsoft.Extensions.Logging.EventLog, MN-215). Creating
    # a source needs an administrator; the virtual account cannot, so the installer registers it (MN-214/MN-215 finding).
    if (Test-AgentEventSource) {
        Write-Step "Event Log source $ServiceName exists"
        return
    }
    Invoke-Change "Register the Event Log source $ServiceName in the $EventLogName log" { New-EventLog -LogName $EventLogName -Source $ServiceName }
}

function Unregister-AgentEventSource {
    if (Test-AgentEventSource) {
        Invoke-Change "Remove the Event Log source $ServiceName" { Remove-EventLog -Source $ServiceName }
    }
}

function Get-PreshutdownTimeoutMs {
    # The agent drains for Agent:ShutdownTimeout (default 30 s) on SERVICE_CONTROL_PRESHUTDOWN; Windows must wait at least
    # that long plus 10 s of margin before it shuts down anyway (section A.4.1).
    $value = Get-JsonProperty (Get-JsonProperty (Read-AgentConfig) 'Agent') 'ShutdownTimeout'
    $shutdown = [TimeSpan]::FromSeconds(30)
    if ($value) {
        $parsed = [TimeSpan]::Zero
        if (-not [TimeSpan]::TryParse("$value", [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) { throw "Agent:ShutdownTimeout '$value' in $ConfigPath is not a time span (hh:mm:ss)." }
        $shutdown = $parsed
    }
    return [int][math]::Ceiling($shutdown.TotalMilliseconds + 10000)
}

function Set-AgentPreshutdownTimeout {
    $ms = Get-PreshutdownTimeoutMs
    Invoke-Change "Set PreshutdownTimeout of $ServiceName to $ms ms" {
        Set-ItemProperty -Path $ServiceRegistryPath -Name 'PreshutdownTimeout' -Value $ms -Type DWord
    }
}

function Test-LocalPort([int] $Port) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        return ($connect.AsyncWaitHandle.WaitOne(1000) -and $client.Connected)
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Wait-AgentHealthy {
    # Running and listening on its port (it answers only mTLS, so the TCP accept is what can be checked without a
    # client certificate).
    if ($DryRun) { return }
    $port = Get-AgentPort
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $status = Get-AgentServiceState
        if ($status -eq 'Stopped') { throw "The service $ServiceName stopped (see the Application event log)." }
        if ($status -eq 'Running' -and (Test-LocalPort $port)) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "The service $ServiceName does not listen on 127.0.0.1:$port after $HealthTimeoutSeconds s."
}

function Test-Enrolled { return (Test-Path -LiteralPath (Join-Path $StateDir 'agent.crt')) }

function Switch-AgentVersion([string] $To, [string] $From) {
    # Stop, switch binPath, start, wait until it listens; on failure back to $From (section A.4.4). llama-server
    # processes keep running throughout (not in the agent's job) and are adopted by whichever agent starts.
    $toExe = Get-AgentExe $To
    if (-not $DryRun -and -not (Test-Path -LiteralPath $toExe)) { throw "No $toExe." }
    if ((Get-AgentServiceState) -ne 'Stopped') { Stop-AgentService }
    Set-AgentServiceBinary $toExe
    if (-not (Test-Enrolled)) {
        Write-Step "The service now runs agent $To; not started because the node is not enrolled."
    } else {
        try {
            Start-AgentService
            Wait-AgentHealthy
        } catch {
            $reason = $_.Exception.Message
            if (-not $From -or $From -eq $To) { throw "Agent $To did not come up: $reason" }
            Write-Alert "Agent $To did not come up ($reason); switching back to $From."
            if ((Get-AgentServiceState) -ne 'Stopped') { Stop-AgentService }
            Set-AgentServiceBinary (Get-AgentExe $From)
            Start-AgentService
            Wait-AgentHealthy
            throw "Agent $To did not come up: $reason. Agent $From runs again."
        }
    }
    Invoke-Change "Record agent $To as current" {
        $previous = if ($From -and $From -ne $To) { $From } else { (Read-AgentVersions).previous }
        Save-AgentVersions $To $previous
    }
    Remove-OldAgentVersions
}

# ---------------------------------------------------------------------------------------------- enrollment and rekey

function Invoke-Join([string] $Exe, [string] $Node, [switch] $Rekey) {
    $joinArgs = @('join')
    if ($Rekey) { $joinArgs += '--rekey' }
    $joinArgs += @('--enroll-url', $EnrollUrl.AbsoluteUri.TrimEnd('/'), '--ca-hash', $CaHash, '--node-id', $Node, '--state-dir', $StateDir)
    if ($Rekey -and $NodeUid) { $joinArgs += @('--node-uid', $NodeUid) }
    foreach ($a in $Address) { $joinArgs += @('--address', $a) }
    if ($DryRun) {
        Write-Host "[dry-run] $Exe $($joinArgs -join ' ') (token from `$env:$TokenVariable)"
        return
    }
    # The token reaches the agent through MIRBADLLM_JOIN_TOKEN only.
    if ($PromptToken) {
        $secure = Read-Host -AsSecureString 'Join token'
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            Set-Item -Path "Env:$TokenVariable" -Value ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))
            & $Exe @joinArgs
        } finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
            Clear-JoinToken
        }
    } else {
        & $Exe @joinArgs
    }
    if ($LASTEXITCODE -ne 0) { throw "$($joinArgs[0]) failed (exit $LASTEXITCODE)." }
}

function Assert-JoinInput {
    if ($EnrollUrl -xor $CaHash) { throw "Joining needs -EnrollUrl and -CaHash together (and `$env:$TokenVariable or -PromptToken)." }
    if ($PromptToken -and -not $EnrollUrl) { throw '-PromptToken needs -EnrollUrl and -CaHash.' }
    if ($EnrollUrl -and -not $PromptToken -and -not (Test-Path "Env:$TokenVariable")) {
        throw "Set `$env:$TokenVariable to the token, or add -PromptToken (never put the token on the command line)."
    }
}

function Backup-Identity([string] $BackupDir) {
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    foreach ($name in $IdentityFiles) {
        $file = Join-Path $StateDir $name
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $BackupDir $name) -Force }
    }
}

function Restore-Identity([string] $BackupDir) {
    # Back to exactly the saved set: a file the failed rekey created but that did not exist before is removed.
    foreach ($name in $IdentityFiles) {
        $saved = Join-Path $BackupDir $name
        $file = Join-Path $StateDir $name
        if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $file -Force }
        elseif (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
}

function Invoke-Rekey {
    if (-not $EnrollUrl -or -not $CaHash) { throw "rekey needs -EnrollUrl and -CaHash (the master's rekey command) and `$env:$TokenVariable or -PromptToken." }
    Assert-JoinInput
    $node = $NodeId
    if (-not $node) { $node = Get-JsonProperty (Get-JsonProperty (Read-AgentConfig) 'Agent') 'NodeId' }
    if (-not $node) { throw 'rekey needs -NodeId (no agent.json to read it from).' }
    $state = Read-AgentVersions
    if (-not $state.current) { throw 'The agent is not installed (no app\versions.json): run install first.' }
    $exe = Get-AgentExe $state.current
    $wasEnrolled = Test-Enrolled

    $backup = Join-Path $DataDir ('rekey-backup-' + (Get-Date -Format 'yyyyMMddHHmmss'))
    if ((Get-AgentServiceState) -eq 'Running') { Stop-AgentService }
    Invoke-Change "Keep a copy of the identity in $backup" { Backup-Identity $backup }
    Write-Step "Re-pairing node $node at $EnrollUrl"
    try {
        Invoke-Join $exe $node -Rekey
    } catch {
        $reason = $_.Exception.Message
        Invoke-Change 'Restore the previous identity' {
            Restore-Identity $backup
            Set-PrivateAcl $DataDir (Get-AccountReference)
        }
        if ($wasEnrolled) {
            Start-AgentService
            Write-Step 'Started again on the previous identity.'
        }
        throw "rekey failed: $reason. The previous identity was restored."
    } finally {
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Invoke-Change "Re-apply the private ACL to $DataDir" { Set-PrivateAcl $DataDir (Get-AccountReference) }
    # The rekey may carry a new CA (after a CA rotation): the update helper trusts it from now on.
    Install-UpdateHelper $exe
    Start-AgentService
    Wait-AgentHealthy
    Write-Step 'Re-paired. Approve the node on the Nodes page if it shows as pending (compare the fingerprint printed above).'
}

# ------------------------------------------------------------------------------------- SCM host services (MN-228)

function Get-ScmDenyList {
    # The agent's compiled deny list (ScmDenyList.BuiltInPatterns in MirbadLLM.Agent); a test keeps the two equal.
    return @('WinDefend', 'WdNisSvc', 'EventLog', 'RpcSs', 'RpcEptMapper', 'DcomLaunch', 'SamSs', 'Dnscache', 'LanmanServer',
        'TermService', 'wuauserv', 'mpssvc', 'BFE', 'nvlddmkm', 'NVDisplay.ContainerLocalSystem', 'mirbadllm-agent', 'spark-serve-agent')
}

function Get-ScmDeniedBy([string] $Name) {
    # The first deny pattern that matches the service name (case-insensitive, as Windows compares names), or $null.
    foreach ($pattern in (Get-ScmDenyList)) {
        if ($Name -like $pattern) { return $pattern }
    }
    return $null
}

function Get-ScmWhitelistName([string] $Service) {
    # The agent's ScmWhitelist.DeriveName: lower case, anything outside [a-z0-9-] becomes '-', runs of '-' collapsed,
    # trimmed to 64 characters (ComfyUI_Server -> comfyui-server). Case-sensitive operators: the input is lower case.
    $name = ($Service.Trim().ToLowerInvariant() -creplace '[^a-z0-9]', '-') -creplace '-{2,}', '-'
    $name = $name.Trim('-')
    if ($name.Length -gt 64) { $name = $name.Substring(0, 64).TrimEnd('-') }
    if (-not $name) { return 'service' }
    return $name
}

function Split-SddlAces([string] $Text) {
    # The parenthesised ACEs of a DACL body, by depth (a conditional ACE nests parentheses).
    $aces = @()
    $depth = 0
    $start = -1
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $c = $Text[$i]
        if ($c -ceq '(') {
            if ($depth -eq 0) { $start = $i }
            $depth++
        } elseif ($c -ceq ')') {
            $depth--
            if ($depth -lt 0) { throw "Malformed security descriptor near: $Text" }
            if ($depth -eq 0) { $aces += $Text.Substring($start, $i - $start + 1) }
        }
    }
    if ($depth -ne 0) { throw "Malformed security descriptor: $Text" }
    return , $aces
}

function Add-ServiceSddlAce([string] $Sddl, [string] $Sid, [string] $Rights) {
    # $Sddl with one allow ACE "(A;;$Rights;;;$Sid)" at the end of its DACL; an earlier plain allow ACE for the same SID
    # is replaced (re-running -AllowService converges), every other ACE, the owner, group, flags and the SACL are kept.
    $d = $Sddl.IndexOf('D:', [StringComparison]::Ordinal)
    if ($d -lt 0) { throw "The security descriptor has no DACL: $Sddl" }
    $end = $Sddl.Length
    $depth = 0
    for ($i = $d + 2; $i -lt $Sddl.Length; $i++) {
        $c = $Sddl[$i]
        if ($c -ceq '(') { $depth++ }
        elseif ($c -ceq ')') { $depth-- }
        elseif ($depth -eq 0 -and $c -ceq 'S' -and $i + 1 -lt $Sddl.Length -and $Sddl[$i + 1] -ceq ':') { $end = $i; break }
    }
    $dacl = $Sddl.Substring($d + 2, $end - $d - 2)
    $open = $dacl.IndexOf('(')
    $flags = if ($open -lt 0) { $dacl } else { $dacl.Substring(0, $open) }
    $body = if ($open -lt 0) { '' } else { $dacl.Substring($open) }
    $own = '^\(A;;[A-Z]*;;;' + [regex]::Escape($Sid) + '\)$'
    $kept = @((Split-SddlAces $body) | Where-Object { $_ -notmatch $own })
    return $Sddl.Substring(0, $d + 2) + $flags + ($kept -join '') + "(A;;$Rights;;;$Sid)" + $Sddl.Substring($end)
}

function Get-ScmAgentRights([bool] $Autostart) {
    # CC query config, LC query status, LO interrogate, RP start, WP stop, RC read control; DC (change config) only with
    # -AllowServiceAutostart: it changes the start type, but also the program path and the account the service runs as.
    if ($Autostart) { return 'CCLCLORPWPRCDC' }
    return 'CCLCLORPWPRC'
}

function Add-ScmWhitelistEntry([string] $Path, [string] $Service, [string] $DisplayName) {
    # Adds the service to the agent's whitelist file (ScmWhitelist: {"version":1,"services":[{name,service,...}]}) unless
    # it is listed already; the agent reads the file on every call, so a running agent sees it at once.
    $entries = @()
    if (Test-Path -LiteralPath $Path) {
        $file = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
        $entries = @(@(Get-JsonProperty $file 'services') | Where-Object { $null -ne $_ })
    }
    $name = Get-ScmWhitelistName $Service
    foreach ($e in $entries) {
        if ((Get-JsonProperty $e 'service') -eq $Service) {
            Write-Step "$Service is already on the agent's service whitelist as '$(Get-JsonProperty $e 'name')'."
            return
        }
        if ((Get-JsonProperty $e 'name') -ceq $name) { throw "The whitelist name '$name' is already used by $(Get-JsonProperty $e 'service')." }
    }
    if (-not $DisplayName) { $DisplayName = $Service }
    $entry = [pscustomobject][ordered]@{ name = $name; service = $Service; displayName = $DisplayName; protected = $false }
    Write-JsonFile $Path ([pscustomobject][ordered]@{ version = 1; services = @($entries + $entry) })
    Write-Step "Added $Service to the agent's service whitelist as '$name'."
}

function Grant-AgentServiceAccess([string] $Service, [bool] $Autostart) {
    $sid = Get-ServiceSid $ServiceName
    if (-not $sid) { throw "Could not read the SID of $ServiceAccount (sc.exe showsid)." }
    $lines = @(& sc.exe sdshow $Service)
    if ($LASTEXITCODE -ne 0) { throw "sc.exe sdshow $Service failed (exit $LASTEXITCODE): $($lines -join ' ')" }
    $current = (($lines | Where-Object { $_.Trim() }) -join '').Trim()
    $rights = Get-ScmAgentRights $Autostart
    $updated = Add-ServiceSddlAce $current $sid $rights
    Invoke-Change "Grant $ServiceAccount '$rights' on the service $Service (sc.exe sdset)" {
        $out = @(& sc.exe sdset $Service $updated)
        if ($LASTEXITCODE -ne 0) { throw "sc.exe sdset $Service failed (exit $LASTEXITCODE): $($out -join ' ')" }
    }
}

function Invoke-AllowService {
    $path = Join-Path $StateDir 'scm-whitelist.json'
    if (-not $DryRun -and -not (Test-Path -LiteralPath $StateDir)) { throw "No ${StateDir}: install the agent first." }
    foreach ($requested in $AllowService) {
        # Exactly this name (the parameter already refuses wildcards): never "whichever service matched first".
        $svc = Get-Service -Name $requested -ErrorAction SilentlyContinue | Where-Object { $_.Name -ieq $requested } | Select-Object -First 1
        if (-not $svc) { throw "No Windows service named '$requested' on this PC." }
        $key = $svc.Name
        if (([int]$svc.ServiceType -band 0x30) -eq 0) { throw "$key is a driver, not a service: the agent never manages drivers." }
        $pattern = Get-ScmDeniedBy $key
        if ($pattern) { throw "$key is a system service (matches '$pattern' on the agent's deny list): the agent never manages it." }
        Grant-AgentServiceAccess $key ([bool]$AllowServiceAutostart)
        Invoke-Change "Add $key to $path" { Add-ScmWhitelistEntry $path $key $svc.DisplayName }
        if ($AllowServiceAutostart) {
            Write-Alert "$ServiceAccount may now change the configuration of $key, its program path and account included: whoever controls the agent can run code as that service's account. Grant it only for services you would let the agent's account run."
        }
    }
}

# ---------------------------------------------------------------------------- update helper (pushed agent updates)

function Install-UpdateHelper([string] $Exe) {
    # docs/multinode/agent-updates.md, step C: the agent's own executable as the SYSTEM service mirbadllm-agent-updater
    # ("update-helper"), manual start, which only the agent's virtual account may start (RP) and query (LC LO RC). It
    # checks an update order against the master CA saved here, in Program Files (administrators only), then runs the
    # bundle's own install-agent.ps1 upgrade. A pushed upgrade (MIRBADLLM_UPDATE_HELPER=1) keeps the saved CA: the agent
    # never chooses what the helper trusts. Needs an enrolled node (its uid and the master's CA).
    if (-not (Test-Enrolled)) { return }
    $sid = Get-ServiceSid $ServiceName
    if (-not $sid) { Write-Alert "Could not read the SID of ${ServiceAccount}: updates pushed from the master stay off."; return }
    $binaryPath = "`"$Exe`" update-helper"
    $exists = [bool](Get-Service -Name $UpdaterName -ErrorAction SilentlyContinue)
    Invoke-Change "Configure the update helper service $UpdaterName (LocalSystem, manual start, $Exe)" {
        if (-not $exists) {
            New-Service -Name $UpdaterName -BinaryPathName $binaryPath -DisplayName 'mirbadllm agent updater' `
                -Description 'Installs agent updates the mirbadllm master signed (started by the agent, then stops).' -StartupType Manual | Out-Null
        } else {
            Set-ServiceBinaryPath $UpdaterName $binaryPath
        }
        $lines = @(& sc.exe sdshow $UpdaterName)
        if ($LASTEXITCODE -ne 0) { throw "sc.exe sdshow $UpdaterName failed (exit $LASTEXITCODE): $($lines -join ' ')" }
        $current = (($lines | Where-Object { $_.Trim() }) -join '').Trim()
        $updated = Add-ServiceSddlAce $current $sid 'LCLORPRC'
        $out = @(& sc.exe sdset $UpdaterName $updated)
        if ($LASTEXITCODE -ne 0) { throw "sc.exe sdset $UpdaterName failed (exit $LASTEXITCODE): $($out -join ' ')" }
    }
    $keep = $env:MIRBADLLM_UPDATE_HELPER -eq '1' -and (Test-Path -LiteralPath (Join-Path $UpdateTrustDir 'ca.pem')) -and
        (Test-Path -LiteralPath (Join-Path $UpdateTrustDir 'node-uid'))
    Invoke-Change "Save the trusted master CA and node uid in $UpdateTrustDir" {
        New-Item -ItemType Directory -Force -Path $UpdateTrustDir | Out-Null
        if ($keep) {
            Write-Step "Keeping the trusted CA in $UpdateTrustDir (this upgrade was pushed from the master)"
        } else {
            Copy-Item -LiteralPath (Join-Path $StateDir 'ca.crt') -Destination (Join-Path $UpdateTrustDir 'ca.pem') -Force
            Copy-Item -LiteralPath (Join-Path $StateDir 'node-uid') -Destination (Join-Path $UpdateTrustDir 'node-uid') -Force
        }
        Set-Content -LiteralPath (Join-Path $UpdateTrustDir 'rid') -Value 'win-x64' -Encoding ascii
        Set-Content -LiteralPath (Join-Path $UpdateTrustDir 'helper') -Value '' -Encoding ascii
        # The agent reads the marker (it reports agent.update while it exists); nothing here is writable by it.
        & icacls.exe $UpdateTrustDir /grant "$(Get-AccountReference):(OI)(CI)RX" /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls failed on $UpdateTrustDir" }
    }
}

function Remove-UpdateHelper {
    if (Get-Service -Name $UpdaterName -ErrorAction SilentlyContinue) {
        Invoke-Change "Stop and remove the service $UpdaterName" {
            Stop-Service -Name $UpdaterName -Force -ErrorAction SilentlyContinue
            & sc.exe delete $UpdaterName | Out-Null
        }
    }
}

# ------------------------------------------------------------------------------------------------------------ verbs

function Get-RepositoryRoot {
    $root = Join-Path $PSScriptRoot '..\..\..'
    if (-not (Test-Path -LiteralPath (Join-Path $root 'MirbadLLM.slnx'))) { throw "No -PublishedDir or -Bundle, and $PSScriptRoot is not in a mirbadllm repository to publish from." }
    return (Resolve-Path -LiteralPath $root).Path
}

function Get-AgentSourceDir([string] $WorkDir) {
    if ($Bundle) {
        if (-not $BundleSha256) { throw '-Bundle needs -BundleSha256 (shown next to the download on the master).' }
        $hash = Get-FileSha256 $Bundle
        if ($hash -ne $BundleSha256.ToLowerInvariant()) { throw "agent bundle hash mismatch: $hash, expected $($BundleSha256.ToLowerInvariant())." }
        Expand-Archive -LiteralPath $Bundle -DestinationPath $WorkDir
        if (Test-Path -LiteralPath (Join-Path $WorkDir 'mirbadllm-agent.exe')) { return $WorkDir }
        $dir = Get-ChildItem -LiteralPath $WorkDir -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'mirbadllm-agent.exe') } | Select-Object -First 1
        if (-not $dir) { throw "No mirbadllm-agent.exe in $Bundle." }
        return $dir.FullName
    }
    if ($PublishedDir) { return $PublishedDir }
    # Run from an unpacked win-x64 bundle: the binary is next to the script.
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'mirbadllm-agent.exe')) { return $PSScriptRoot }
    $repo = Get-RepositoryRoot
    Write-Step 'Publishing the agent (win-x64, self-contained, single file)'
    # From the repository, so its global.json picks the SDK.
    Push-Location $repo
    try {
        & dotnet publish 'src\MirbadLLM.Agent\MirbadLLM.Agent.csproj' -c Release -r win-x64 `
            --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o $WorkDir --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    } finally {
        Pop-Location
    }
    return $WorkDir
}

function Update-ConfigSetting($Agent, [string] $Parameter, $Value, [string[]] $Names, $Changes) {
    # Writes $Value into every $Names member of the Agent section, but only when the operator passed $Parameter
    # explicitly: a value that merely has a default must never overwrite what is in the file.
    if (-not $BoundParameters.ContainsKey($Parameter)) { return }
    foreach ($name in $Names) {
        $current = "$(Get-JsonProperty $Agent $name)"
        if ($current -cne "$Value") {
            Set-JsonProperty $Agent $name $Value
            $Changes.Add("$name '$current' -> '$Value'")
        }
    }
}

function Update-ExistingConfig([string] $Node) {
    # A second install run keeps agent.json - but not a setting the operator passed on the command line. Before MN-239
    # it printed "Keeping existing agent.json" and dropped -NodeId, so the file kept a stale (and in the owner's case
    # invalid) node id while the node enrolled under the one on the command line: the config and the enrolled identity
    # disagreed, silently (C2). Explicit wins, and every change is printed.
    $config = Read-AgentConfig
    $agent = Get-JsonProperty $config 'Agent'
    if (-not $agent) { throw "$ConfigPath has no Agent section. Move it aside and run install again." }
    $changes = New-Object 'System.Collections.Generic.List[string]'
    if ($BoundParameters.ContainsKey('NodeId')) {
        Assert-NodeId $Node
        $current = "$(Get-JsonProperty $agent 'NodeId')"
        if ($current -cne $Node) {
            Set-JsonProperty $agent 'NodeId' $Node
            $changes.Add("NodeId '$current' -> '$Node'")
            if (Test-Enrolled) {
                # The certificate in the state directory was issued for one node id, and the agent refuses to start when
                # agent.json names another ("The agent certificate is for node ..., but agent.json says ..."). Loud and
                # immediate beats a config that quietly disagrees with the identity - but the operator must be told.
                Write-Alert ("This node already has an identity in $StateDir. If its certificate was issued for '$current', " +
                    "the agent will refuse to start now that agent.json says '$Node'. Re-pair it with " +
                    """install-agent.ps1 rekey -NodeId $Node -EnrollUrl <url> -CaHash <hash>"", or re-run install " +
                    "without -NodeId to keep '$current'.")
            }
        }
    }
    Update-ConfigSetting $agent 'ModelsRoot' $ModelsRoot @('ModelsRoot', 'ModelsHostPath') $changes
    Update-ConfigSetting $agent 'CacheRoot' $CacheRoot @('CacheRoot', 'CacheHostPath') $changes
    if ($changes.Count -eq 0) {
        Write-Step "Keeping existing $ConfigPath"
        return
    }
    Write-Step "Updating $ConfigPath ($($changes -join '; '))"
    Invoke-Change "Write $ConfigPath" { Write-JsonFile $ConfigPath $config }
}

function Write-InitialConfig([string] $Node) {
    if (Test-Path -LiteralPath $ConfigPath) {
        Update-ExistingConfig $Node
        return
    }
    if (-not $Node) { throw "-NodeId is required for the first install (it goes into $ConfigPath)." }
    # Never write a node id the agent would refuse: it would start the service into an immediate exit (MN-239 C1).
    Assert-NodeId $Node
    Write-Step "Installing $ConfigPath for node $Node"
    $config = Get-Content -Raw -LiteralPath (Get-ExampleConfigPath) | ConvertFrom-Json
    Set-JsonProperty $config.Agent 'NodeId' $Node
    Set-JsonProperty $config.Agent 'StateDirectory' $StateDir
    Set-JsonProperty $config.Agent 'ModelsRoot' $ModelsRoot
    Set-JsonProperty $config.Agent 'ModelsHostPath' $ModelsRoot
    Set-JsonProperty $config.Agent 'CacheRoot' $CacheRoot
    Set-JsonProperty $config.Agent 'CacheHostPath' $CacheRoot
    # The example lists the pinned llama.cpp for reference; -LlamaCpp writes the entry once the folder is verified.
    Set-JsonProperty $config.Agent 'Executables' @()
    Invoke-Change "Write $ConfigPath" { Write-JsonFile $ConfigPath $config }
}

function Set-AgentFirewall {
    # The master dials the agent on 9443. The rule is bound to the service (its SID), so it survives binPath changes
    # between app\{version} folders; Private profile only (Domain never applies on a home PC and Public must never get
    # it; without -Profile the cmdlet would use Any); remote address = the master (spike S4 / F4).
    $remote = Get-MasterAddress $EnrollUrl $MasterAddress
    Invoke-Change "Firewall: TCP 9443 inbound from $($remote -join ', ') (Private profile) for the service $ServiceName" {
        Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $FirewallRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort 9443 `
            -Service $ServiceName -RemoteAddress $remote -Profile Private | Out-Null
        Write-Step "Firewall: TCP 9443 inbound from $($remote -join ', ') (Private profile) for the service $ServiceName"
    }
    foreach ($fwProfile in @(Get-NetFirewallProfile -PolicyStore ActiveStore | Where-Object { -not $_.Enabled })) {
        Write-Alert "The Windows Firewall is off for the $($fwProfile.Name) profile: no rule has any effect, so 9443 is reachable from any network this PC joins. Turning it on is your call."
    }
}

function Invoke-Install {
    Assert-JoinInput
    Test-HostWarnings
    if ($LlamaCpp) {
        # Fail before anything changes when the chosen llama.cpp build cannot run here.
        Test-VcRuntime
        $pinned = Read-LlamaCppLock $LockPath
        $resolved = Resolve-Accelerator $Accelerator
        [void](Select-LlamaCppBuild $pinned $resolved)
        if ($resolved -eq 'cuda') { Test-NvidiaDriver $pinned.minDriver }
        elseif ($resolved -eq 'vulkan' -and -not (Test-VulkanRuntime)) { Test-AcceleratorHost $pinned $resolved $Accelerator }
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ('mirbadllm-agent-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        # 1. The binary, in its own version folder.
        $source = Get-AgentSourceDir $work
        $serviceState = Get-AgentServiceState
        $state = Read-AgentVersions
        if ($serviceState -and $serviceState -ne 'Stopped') {
            Write-Step 'Stopping the running service for the upgrade'
            Stop-AgentService
        }
        $version = Install-AgentBinary $source
        $exe = Get-AgentExe $version

        # 2. The service.
        Set-AgentService $exe ([bool]$serviceState)
        $previous = if ($state.current -and $state.current -ne $version) { $state.current } else { $state.previous }
        Invoke-Change "Record agent $version as current" { Save-AgentVersions $version $previous }
        $legacy = Join-Path $InstallDir 'mirbadllm-agent.exe'
        if (Test-Path -LiteralPath $legacy) { Invoke-Change "Remove the unversioned $legacy" { Remove-Item -LiteralPath $legacy -Force } }
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    # 3. Configuration and state, private to the service account. A tree from an older installer may hold files with
    # an empty DACL (spike S4 / F1); fix it first so the files below can be read and replaced.
    $account = Get-AccountReference
    if (Test-Path -LiteralPath $DataDir) { Invoke-Change "Private ACL on $DataDir" { Set-PrivateAcl $DataDir $account } }
    Invoke-Change "Create $StateDir, $ModelsRoot, $CacheRoot" { New-Item -ItemType Directory -Force -Path $StateDir, $ModelsRoot, $CacheRoot | Out-Null }
    Write-InitialConfig $NodeId
    Set-AgentPreshutdownTimeout
    Register-AgentEventSource
    $node = $NodeId
    if (-not $node) { $node = Get-JsonProperty (Get-JsonProperty (Read-AgentConfig) 'Agent') 'NodeId' }

    # 4. Enrollment (the token is cleared by the caller's finally as well).
    if ($EnrollUrl) {
        Write-Step "Enrolling node $node at $EnrollUrl"
        Invoke-Join $exe $node
    }
    Invoke-Change "Private ACL on $DataDir; modify for $ServiceAccount on $ModelsRoot and $CacheRoot" {
        Set-PrivateAcl $DataDir $account
        foreach ($root in @($ModelsRoot, $CacheRoot)) {
            & icacls.exe $root /grant "${account}:(OI)(CI)M" /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "icacls failed on $root" }
        }
    }

    # 5. llama.cpp, before the service starts, so its catalogue sees the version.
    if ($LlamaCpp) { Install-LlamaCpp $LlamaCppVersion $LlamaCppSource $Accelerator }

    # 6. Firewall, and the update helper for updates pushed from the master.
    Set-AgentFirewall
    Install-UpdateHelper $exe

    # 7. Start.
    if (-not (Test-Enrolled)) {
        Write-Step "Installed. Not enrolled yet: re-run with -EnrollUrl/-CaHash and `$env:$TokenVariable or -PromptToken (`"Add node`" on the master)."
    } elseif ($NoStart) {
        Write-Step "Installed and enrolled. Start it with: Start-Service $ServiceName"
    } else {
        Start-AgentService
        Wait-AgentHealthy
        Write-Step "Agent $version is running."
    }
    Remove-OldAgentVersions
}

function Invoke-Upgrade {
    if (-not (Get-AgentServiceState)) { throw "The service $ServiceName is not installed: use install." }
    $work = Join-Path ([IO.Path]::GetTempPath()) ('mirbadllm-agent-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        $state = Read-AgentVersions
        $version = Install-AgentBinary (Get-AgentSourceDir $work)
        Write-Step "Upgrading agent $($state.current) -> $version"
        # Before the switch: the new agent reads the helper marker once, at its start, for agent.update.
        Install-UpdateHelper (Get-AgentExe $version)
        Switch-AgentVersion $version $state.current
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Rollback {
    $state = Read-AgentVersions
    if (-not $state.previous) { throw 'No previous agent version to roll back to.' }
    if (-not (Test-Path -LiteralPath (Get-AgentExe $state.previous))) { throw "The previous version's folder $(Join-Path $AppDir $state.previous) is gone." }
    Write-Step "Rolling back agent $($state.current) -> $($state.previous)"
    Switch-AgentVersion $state.previous $state.current
}

function Invoke-Uninstall {
    $serviceSid = Get-ServiceSid $ServiceName
    Remove-UpdateHelper
    if (Get-AgentServiceState) {
        Invoke-Change "Stop and remove the service $ServiceName" {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            & sc.exe delete $ServiceName | Out-Null
        }
    }

    # Runtime processes (llama-server) outlive the service by design (spike S4 / F2): stop those the service account
    # started from the agent's own folder, and nothing else.
    $runtimes = Join-Path $InstallDir 'runtimes'
    foreach ($p in @(Get-ProcessesUnder $runtimes)) {
        $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid -ErrorAction SilentlyContinue
        if ($serviceSid -and $owner -and $owner.Sid -eq $serviceSid) {
            Invoke-Change "Stop runtime process $($p.ProcessId) ($($p.ExecutablePath))" { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
        }
    }

    Unregister-AgentEventSource
    if ($serviceSid -and (@(Get-LocalGroupMember -SID 'S-1-5-32-558' -ErrorAction SilentlyContinue | ForEach-Object { $_.SID.Value }) -contains $serviceSid)) {
        # A Vulkan node's account was added by -Accelerator vulkan; a removed account must not stay listed by its SID.
        Invoke-Change "Remove $ServiceAccount from the local group Performance Monitor Users" {
            Remove-LocalGroupMember -SID 'S-1-5-32-558' -Member $serviceSid -ErrorAction SilentlyContinue
        }
    }
    Invoke-Change "Remove the firewall rule and $InstallDir" {
        Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        if (Test-Path -LiteralPath $InstallDir) { Remove-Item -Recurse -Force -LiteralPath $InstallDir }
    }
    Write-Step "Removed. $DataDir (configuration and identity) was kept; delete it to forget this node."
    if ($serviceSid) {
        # The virtual account's profile stays loaded until the next reboot (spike S4 / F8); a re-install reuses it.
        Write-Step "The profile C:\Windows\ServiceProfiles\$ServiceName can be removed only after a reboot. Then run:"
        Write-Host "    Get-CimInstance Win32_UserProfile | Where-Object SID -eq '$serviceSid' | Remove-CimInstance"
    }
}

function Restart-AgentIfRunning {
    # The agent reads Agent:Executables at start; running models are separate processes and are adopted again.
    if ((Get-AgentServiceState) -eq 'Running') {
        Stop-AgentService
        Start-AgentService
        Wait-AgentHealthy
    }
}

# ------------------------------------------------------------------------------------------------------------- main

$standalone = [bool]($LlamaCpp -or $RemoveLlamaCpp -or $ImportGguf -or $AllowService)
# No verb: the operations given alone, else install (the form of the MN-207 installer).
$action = if ($Verb) { $Verb } elseif ($standalone) { 'alone' } else { 'install' }
if ($LlamaCppVersion -and -not $LlamaCpp) { throw "A llama.cpp version ($LlamaCppVersion) needs -LlamaCpp." }
if ($action -ne 'install' -and $action -ne 'alone' -and $standalone) { throw "-LlamaCpp, -RemoveLlamaCpp, -ImportGguf and -AllowService run alone or with install, not with $action." }
if ($AllowServiceAutostart -and -not $AllowService) { throw '-AllowServiceAutostart needs -AllowService <name>.' }
if ($action -eq 'install' -and ($RemoveLlamaCpp -or $ImportGguf)) { throw '-RemoveLlamaCpp and -ImportGguf run alone, without a verb.' }
if ($ImportGguf -and -not $Folder) { throw '-ImportGguf needs -Folder <model folder name>.' }
if ($LlamaCppSource -and -not $LlamaCpp) { throw '-LlamaCppSource needs -LlamaCpp.' }
if ($BoundParameters.ContainsKey('Accelerator') -and -not $LlamaCpp) { throw '-Accelerator names the llama.cpp build to install, so it needs -LlamaCpp.' }
# Here, above the verb: a node id the agent refuses must never reach agent.json, the service or an enrollment, and the
# operator must read why instead of hunting a 1053 with an empty Application log (MN-239 C1).
if ($BoundParameters.ContainsKey('NodeId')) { Assert-NodeId $NodeId }
if ($DryRun) { Write-Step 'Dry run: checks run, changes are printed and not made.' }

try {
    switch ($action) {
        'install' {
            Invoke-Install
            if ($AllowService) { Invoke-AllowService }
        }
        'upgrade' { Invoke-Upgrade }
        'rollback' { Invoke-Rollback }
        'rekey' { Invoke-Rekey }
        'uninstall' { Invoke-Uninstall }
        'alone' {
            if ($LlamaCpp) { Install-LlamaCpp $LlamaCppVersion $LlamaCppSource $Accelerator }
            if ($RemoveLlamaCpp) { Remove-LlamaCpp $RemoveLlamaCpp }
            if ($ImportGguf) {
                $root = $ModelsRoot
                $configured = Get-JsonProperty (Get-JsonProperty (Read-AgentConfig) 'Agent') 'ModelsRoot'
                if ($configured -and -not $PSBoundParameters.ContainsKey('ModelsRoot')) { $root = $configured }
                Import-Gguf $ImportGguf $Folder $Sha256 $root
            }
            if ($LlamaCpp -or $RemoveLlamaCpp) { Restart-AgentIfRunning }
            if ($AllowService) { Invoke-AllowService }
        }
    }
} finally {
    # Never leave the token in the session, also when a step above threw (spike S4 / F5).
    Clear-JoinToken
}
