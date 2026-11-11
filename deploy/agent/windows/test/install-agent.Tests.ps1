# Pester tests for install-agent.ps1 (MN-216). Run from the repository root:
#     powershell.exe -NoProfile -Command "Invoke-Pester deploy\agent\windows\test"
# Written for Pester 3.4 (inbox on Windows 10/11) and 5.x alike: assertions are plain throws, every Context reloads
# the installer's functions before it adds its fakes. Nothing here changes the machine: all paths are under a new
# temporary folder, and the service, driver, process, port and enrollment functions are replaced by fakes. The xUnit
# test WindowsInstallerTests.Pester_SuitePassesUnderWindowsPowerShell51 runs this file under Windows PowerShell 5.1.

Describe 'install-agent.ps1' {
    BeforeAll {
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

        $InstallerPath = (Resolve-Path (Join-Path $PSScriptRoot '..\install-agent.ps1')).Path
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($InstallerPath, [ref]$null, [ref]$parseErrors)
        if ($parseErrors) { throw "install-agent.ps1 does not parse: $($parseErrors[0].Message)" }
        $InstallerFunctions = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false) | ForEach-Object { $_.Extent.Text })
        # Dot-sourced by each Context: the real functions, in the Context's scope.
        $LoadInstaller = { foreach ($text in $InstallerFunctions) { . ([scriptblock]::Create($text)) } }

        function Assert-True($Condition, [string] $Message) { if (-not $Condition) { throw "Assertion failed: $Message" } }
        function Assert-Equal($Expected, $Actual, [string] $Message) {
            if ($Expected -ne $Actual) { throw "Assertion failed: $Message (expected '$Expected', got '$Actual')" }
        }
        function Assert-Throws([scriptblock] $Action, [string] $Like) {
            $thrown = $null
            try { & $Action } catch { $thrown = $_.Exception.Message }
            if ($null -eq $thrown) { throw "Assertion failed: expected an error like '*$Like*', nothing was thrown" }
            if ($thrown -notlike "*$Like*") { throw "Assertion failed: expected an error like '*$Like*', got '$thrown'" }
        }
        function Get-Sha([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
        function New-TempDir {
            $d = Join-Path ([IO.Path]::GetTempPath()) ('ss-mn216-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $d | Out-Null
            return $d
        }
        function New-Zip([string] $Path, [hashtable] $Entries) {
            # Entries: name -> content; the name is written as given (so a test can plant "../x.dll").
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Create)
            $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
            try {
                foreach ($name in ($Entries.Keys | Sort-Object)) {
                    $writer = New-Object IO.StreamWriter($zip.CreateEntry($name).Open())
                    try { $writer.Write($Entries[$name]) } finally { $writer.Dispose() }
                }
            } finally {
                $zip.Dispose()
                $stream.Dispose()
            }
        }

        # A synthetic release "b1" with the real layout: a launcher, the server DLL, a CUDA backend and a cudart zip.
        $ReleaseFiles = [ordered]@{
            'llama-server.exe' = 'launcher'
            'llama-server-impl.dll' = 'server code'
            'ggml-cuda.dll' = 'cuda kernels'
        }
        $CudartFiles = [ordered]@{ 'cudart64_13.dll' = 'cuda runtime' }
        $MainAsset = 'llama-b1-bin-win-cuda-13.4-x64.zip'
        $CudartAsset = 'cudart-llama-bin-win-cuda-13.4-x64.zip'
        # The same release's builds for a PC without a usable NVIDIA card: one zip each, no CUDA runtime.
        $CpuAsset = 'llama-b1-bin-win-cpu-x64.zip'
        $VulkanAsset = 'llama-b1-bin-win-vulkan-x64.zip'
        $CpuFiles = [ordered]@{
            'llama-server.exe' = 'launcher'
            'llama-server-impl.dll' = 'server code'
            'ggml-cpu-x64.dll' = 'cpu kernels'
        }
        $VulkanFiles = [ordered]@{
            'llama-server.exe' = 'launcher'
            'llama-server-impl.dll' = 'server code'
            'ggml-vulkan.dll' = 'vulkan kernels'
        }

        function New-Release([hashtable] $Main, [hashtable] $Cudart, [hashtable] $LockedFiles) {
            # Writes both zips and a lock file pinning their hashes and $LockedFiles (name -> content) as the file set.
            $dir = New-TempDir
            New-Zip (Join-Path $dir $MainAsset) $Main
            New-Zip (Join-Path $dir $CudartAsset) $Cudart
            $files = @()
            foreach ($name in ($LockedFiles.Keys | Sort-Object)) {
                $bytes = [Text.Encoding]::UTF8.GetBytes($LockedFiles[$name])
                $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
                $files += [pscustomobject][ordered]@{ path = $name; sha256 = $sha; bytes = $bytes.Length }
            }
            $variants = @(
                foreach ($v in @(@('cpu', $CpuAsset, $CpuFiles), @('vulkan', $VulkanAsset, $VulkanFiles))) {
                    New-Zip (Join-Path $dir $v[1]) (Copy-Table $v[2])
                    $variantFiles = @()
                    foreach ($name in ($v[2].Keys | Sort-Object)) {
                        $bytes = [Text.Encoding]::UTF8.GetBytes($v[2][$name])
                        $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
                        $variantFiles += [pscustomobject][ordered]@{ path = $name; sha256 = $sha; bytes = $bytes.Length }
                    }
                    [pscustomobject][ordered]@{
                        accelerator = $v[0]
                        assets = @([pscustomobject][ordered]@{ name = $v[1]; url = "https://github.com/ggml-org/llama.cpp/releases/download/b1/$($v[1])"; sha256 = (Get-Sha (Join-Path $dir $v[1])); bytes = (Get-Item (Join-Path $dir $v[1])).Length })
                        files = $variantFiles
                    }
                })
            $lock = [pscustomobject][ordered]@{
                schemaVersion = 1; version = 'b1'; cuda = '13.4'; minDriver = '615.00'; commit = 'abc1234'
                assets = @(
                    foreach ($a in @($MainAsset, $CudartAsset)) {
                        [pscustomobject][ordered]@{ name = $a; url = "https://github.com/ggml-org/llama.cpp/releases/download/b1/$a"; sha256 = (Get-Sha (Join-Path $dir $a)); bytes = (Get-Item (Join-Path $dir $a)).Length }
                    })
                files = $files
                variants = $variants
            }
            [IO.File]::WriteAllText((Join-Path $dir 'lock.json'), (ConvertTo-Json -InputObject $lock -Depth 10))
            return $dir
        }
        function Get-AllFiles { $all = @{}; foreach ($k in $ReleaseFiles.Keys) { $all[$k] = $ReleaseFiles[$k] }; foreach ($k in $CudartFiles.Keys) { $all[$k] = $CudartFiles[$k] }; return $all }
        function Copy-Table([System.Collections.IDictionary] $Table) { $copy = @{}; foreach ($k in $Table.Keys) { $copy[$k] = $Table[$k] }; return $copy }

        $GoodRelease = New-Release (Copy-Table $ReleaseFiles) (Copy-Table $CudartFiles) (Get-AllFiles)

        # The layout every Context's BeforeEach dot-sources: the script's variables, pointed at a fresh temp folder.
        $InitLayout = {
            $TestRoot = New-TempDir
            $ServiceName = 'mirbadllm-agent'
            $ServiceAccount = "NT SERVICE\$ServiceName"
            $InstallDir = Join-Path $TestRoot 'Program Files\mirbadllm-agent'
            $AppDir = Join-Path $InstallDir 'app'
            $RuntimesDir = Join-Path (Join-Path $InstallDir 'runtimes') 'llama.cpp'
            $DataDir = Join-Path $TestRoot 'ProgramData\mirbadllm-agent'
            $ConfigPath = Join-Path $DataDir 'agent.json'
            $StateDir = Join-Path $DataDir 'state'
            $FirewallRule = 'mirbadllm-agent (mTLS 9443)'
            $TokenVariable = 'MIRBADLLM_JOIN_TOKEN'
            $LockPath = Join-Path $GoodRelease 'lock.json'
            $SystemDir = Join-Path $TestRoot 'System32'
            $KeepAgentVersions = 3
            $HealthTimeoutSeconds = 2
            $ExecutableName = 'llama-server'
            $IdentityFiles = @('agent.crt', 'agent.key', 'ca.crt', 'node-uid')
            $UpdaterName = 'mirbadllm-agent-updater'
            $UpdateTrustDir = Join-Path $InstallDir 'update'
            $VcRuntimeFiles = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
            $DryRun = $false
            $NodeId = 'win-1'
            $NodeUid = $null
            $EnrollUrl = $null
            $CaHash = $null
            $PromptToken = $false
            $Address = @()
            New-Item -ItemType Directory -Force -Path $SystemDir, $StateDir, $RuntimesDir | Out-Null
            foreach ($dll in $VcRuntimeFiles) { Set-Content -LiteralPath (Join-Path $SystemDir $dll) -Value 'x' }
            [IO.File]::WriteAllText($ConfigPath, '{ "Agent": { "NodeId": "win-1", "Port": 9443, "Executables": [] } }')
        }
        $CleanLayout = {
            if ($TestRoot -and (Test-Path -LiteralPath $TestRoot)) {
                & icacls.exe $TestRoot /reset /T /C /Q | Out-Null
                Remove-Item -LiteralPath $TestRoot -Recurse -Force
            }
        }
        # The test process's own user stands in for the service account in ACLs (the virtual account needs the service).
        $CurrentUserRef = '*' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    }

    AfterAll {
        if ($GoodRelease -and (Test-Path -LiteralPath $GoodRelease)) { Remove-Item -LiteralPath $GoodRelease -Recurse -Force }
    }

    Context 'llama.cpp: verified install against the lock file' {
        BeforeAll {
            . $LoadInstaller
            function Get-NvidiaDriverVersion { @($FakeDriver | Where-Object { $_ }) }
            function Get-ProcessesUnder([string] $Folder) { @($FakeProcesses | Where-Object { $_ }) }
            function Get-AccountReference { $CurrentUserRef }
            # The group change of a Vulkan install is counted, never made.
            function Grant-PerformanceCounterAccess { $script:CounterGrants++ }
        }
        BeforeEach {
            . $InitLayout
            $FakeDriver = '617.14'
            $FakeProcesses = @()
            $Target = Join-Path $RuntimesDir 'b1'
            $script:CounterGrants = 0
        }
        AfterEach { . $CleanLayout }

        It 'installs exactly the locked files, writes manifest.json and the Agent:Executables entry, read-only for the account' {
            Install-LlamaCpp '' $GoodRelease

            [string[]] $files = @(Get-ChildItem -LiteralPath $Target -File | ForEach-Object { $_.Name })
            [Array]::Sort($files, [StringComparer]::Ordinal)
            Assert-Equal 'cudart64_13.dll,ggml-cuda.dll,llama-server-impl.dll,llama-server.exe,manifest.json' ($files -join ',') 'version folder content'
            $manifest = Get-Content -Raw -LiteralPath (Join-Path $Target 'manifest.json') | ConvertFrom-Json
            Assert-Equal 'llama-server' $manifest.name 'manifest name'
            Assert-Equal 'b1' $manifest.version 'manifest version'
            Assert-Equal '13.4' $manifest.cuda 'manifest cuda'
            Assert-Equal 4 @($manifest.files).Count 'manifest file count'
            Assert-Equal "$MainAsset,$CudartAsset" (@($manifest.sources) -join ',') 'manifest sources'
            $impl = @($manifest.files | Where-Object { $_.path -eq 'llama-server-impl.dll' })[0]
            Assert-Equal (Get-Sha (Join-Path $Target 'llama-server-impl.dll')) $impl.sha256 'manifest hash of the server DLL'

            $config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
            $entry = @($config.Agent.Executables)[0]
            Assert-Equal 'llama-server' $entry.Name 'executable name'
            Assert-Equal 'b1' $entry.Default 'default version'
            Assert-Equal (Join-Path $Target 'llama-server.exe') @($entry.Versions)[0].Path 'executable path'
            Assert-Equal (Join-Path $Target 'manifest.json') @($entry.Versions)[0].Manifest 'manifest path'

            $acl = Get-Acl -LiteralPath $Target
            Assert-True $acl.AreAccessRulesProtected 'the version folder does not inherit from Program Files'
            $account = @($acl.Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $CurrentUserRef.TrimStart('*') })
            Assert-Equal 1 $account.Count 'one entry for the account'
            Assert-True (($account[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -eq 0) 'the account cannot write'
            Assert-Equal 0 @(Get-ChildItem -LiteralPath $RuntimesDir -Directory -Filter '.staging-*' -Force).Count 'no staging folder left'
        }

        It 'aborts on a tampered file in the staging folder and leaves nothing behind' {
            $main = Copy-Table $ReleaseFiles
            $main['llama-server-impl.dll'] = 'server cod3'   # same size, other bytes
            $release = New-Release $main (Copy-Table $CudartFiles) (Get-AllFiles)
            try {
                $LockPath = Join-Path $release 'lock.json'
                Assert-Throws { Install-LlamaCpp '' $release } 'llama-server-impl.dll has SHA-256'
                Assert-True (-not (Test-Path -LiteralPath $Target)) 'no version folder'
                Assert-Equal 0 @(Get-ChildItem -LiteralPath $RuntimesDir -Force).Count 'no staging folder left'
                Assert-Equal 0 @((Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json).Agent.Executables).Count 'agent.json untouched'
            } finally { Remove-Item -LiteralPath $release -Recurse -Force }
        }

        It 'aborts on an extra file (a planted DLL)' {
            $main = Copy-Table $ReleaseFiles
            $main['version.dll'] = 'planted'
            $release = New-Release $main (Copy-Table $CudartFiles) (Get-AllFiles)
            try {
                $LockPath = Join-Path $release 'lock.json'
                Assert-Throws { Install-LlamaCpp '' $release } 'unexpected file version.dll'
                Assert-True (-not (Test-Path -LiteralPath $Target)) 'no version folder'
            } finally { Remove-Item -LiteralPath $release -Recurse -Force }
        }

        It 'aborts on a missing file and on a size mismatch' {
            $locked = Get-AllFiles
            $locked['mtmd.dll'] = 'multimodal'
            $release = New-Release (Copy-Table $ReleaseFiles) (Copy-Table $CudartFiles) $locked
            try {
                $LockPath = Join-Path $release 'lock.json'
                Assert-Throws { Install-LlamaCpp '' $release } 'missing file mtmd.dll'
            } finally { Remove-Item -LiteralPath $release -Recurse -Force }

            $main = Copy-Table $ReleaseFiles
            $main['ggml-cuda.dll'] = 'cuda kernels, longer'
            $release = New-Release $main (Copy-Table $CudartFiles) (Get-AllFiles)
            try {
                $LockPath = Join-Path $release 'lock.json'
                Assert-Throws { Install-LlamaCpp '' $release } 'ggml-cuda.dll is 20 bytes, expected 12'
            } finally { Remove-Item -LiteralPath $release -Recurse -Force }
        }

        It 'refuses an asset whose SHA-256 differs from the lock file before unpacking it' {
            $release = New-Release (Copy-Table $ReleaseFiles) (Copy-Table $CudartFiles) (Get-AllFiles)
            try {
                $lock = Get-Content -Raw (Join-Path $release 'lock.json') | ConvertFrom-Json
                $lock.assets[1].sha256 = '0' * 64
                [IO.File]::WriteAllText((Join-Path $release 'lock.json'), (ConvertTo-Json -InputObject $lock -Depth 10))
                $LockPath = Join-Path $release 'lock.json'
                Assert-Throws { Install-LlamaCpp '' $release } "$($CudartAsset): SHA-256"
                Assert-Equal 0 @(Get-ChildItem -LiteralPath $RuntimesDir -Force).Count 'nothing unpacked'
            } finally { Remove-Item -LiteralPath $release -Recurse -Force }
        }

        It 'refuses a file both zips carry and an entry that would unpack outside the folder' {
            $cudart = Copy-Table $CudartFiles
            $cudart['ggml-cuda.dll'] = 'cuda kernels'
            $release = New-Release (Copy-Table $ReleaseFiles) $cudart (Get-AllFiles)
            try {
                $LockPath = Join-Path $release 'lock.json'
                Assert-Throws { Install-LlamaCpp '' $release } 'ggml-cuda.dll is also in another asset'
            } finally { Remove-Item -LiteralPath $release -Recurse -Force }

            $main = Copy-Table $ReleaseFiles
            $main['../evil.dll'] = 'outside'
            $release = New-Release $main (Copy-Table $CudartFiles) (Get-AllFiles)
            try {
                $LockPath = Join-Path $release 'lock.json'
                Assert-Throws { Install-LlamaCpp '' $release } 'would unpack outside the version folder'
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'runtimes\evil.dll'))) 'nothing written outside'
            } finally { Remove-Item -LiteralPath $release -Recurse -Force }
        }

        It 'refuses a driver below minDriver, and the CUDA build on a PC without an NVIDIA driver' {
            $FakeDriver = '614.99'
            Assert-Throws { Install-LlamaCpp '' $GoodRelease } 'NVIDIA driver 614.99 is older than 615.00'
            Assert-Throws { Install-LlamaCpp '' $GoodRelease } 'pass -Accelerator cpu'
            $FakeDriver = $null
            Assert-Throws { Install-LlamaCpp '' $GoodRelease 'cuda' } 'No NVIDIA driver found'
            Assert-True (-not (Test-Path -LiteralPath $Target)) 'nothing installed'
        }

        It 'installs the CPU build on a PC without an NVIDIA driver and writes Agent:Accelerator cpu' {
            $FakeDriver = $null
            Assert-Equal 'cpu' (Resolve-Accelerator 'auto') 'auto without a driver'

            Install-LlamaCpp '' $GoodRelease

            [string[]] $files = @(Get-ChildItem -LiteralPath $Target -File | ForEach-Object { $_.Name })
            [Array]::Sort($files, [StringComparer]::Ordinal)
            Assert-Equal 'ggml-cpu-x64.dll,llama-server-impl.dll,llama-server.exe,manifest.json' ($files -join ',') 'the CPU build, and no CUDA file'
            $manifest = Get-Content -Raw -LiteralPath (Join-Path $Target 'manifest.json') | ConvertFrom-Json
            Assert-True ($null -eq $manifest.cuda) 'the manifest names no CUDA variant'
            Assert-Equal $CpuAsset (@($manifest.sources) -join ',') 'manifest sources'
            $config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
            Assert-Equal 'cpu' $config.Agent.Accelerator 'Agent:Accelerator'
            Assert-True ($null -eq @(@($config.Agent.Executables)[0].Versions)[0].Cuda) 'the version entry names no CUDA variant'
            Assert-Equal 0 $script:CounterGrants 'no group change for a CPU node'
        }

        It 'keeps the CUDA build the default when an NVIDIA driver answers, and writes no Agent:Accelerator' {
            Assert-Equal 'cuda' (Resolve-Accelerator 'auto') 'auto with a driver'
            Install-LlamaCpp '' $GoodRelease
            $config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
            Assert-True ($null -eq $config.Agent.PSObject.Properties['Accelerator']) 'no Accelerator entry on an NVIDIA node'
        }

        It 'replaces the build of a version when the accelerator changes, and Agent:Accelerator follows' {
            Install-LlamaCpp '' $GoodRelease 'cpu'
            Assert-Equal 'cpu' (Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json).Agent.Accelerator 'cpu asked for on an NVIDIA PC'
            Assert-True (Test-Path -LiteralPath (Join-Path $Target 'ggml-cpu-x64.dll')) 'the CPU build'

            & icacls.exe $Target /grant "${CurrentUserRef}:(OI)(CI)F" /T /Q | Out-Null
            Install-LlamaCpp '' $GoodRelease 'cuda'
            Assert-True (Test-Path -LiteralPath (Join-Path $Target 'ggml-cuda.dll')) 'the CUDA build replaced it'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $Target 'ggml-cpu-x64.dll'))) 'no CPU file left'
            $config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
            Assert-True ($null -eq $config.Agent.PSObject.Properties['Accelerator']) 'the Accelerator entry is removed with the CUDA build'
            Assert-Equal 1 @(@($config.Agent.Executables)[0].Versions).Count 'still one version entry'
        }

        It 'installs the Vulkan build only when asked, needs the Vulkan runtime, and grants the counter access' {
            $FakeDriver = $null
            Assert-Throws { Install-LlamaCpp '' $GoodRelease 'vulkan' } 'vulkan-1.dll is missing'
            Assert-True (-not (Test-Path -LiteralPath $Target)) 'nothing installed without the runtime'

            Set-Content -LiteralPath (Join-Path $SystemDir 'vulkan-1.dll') -Value 'x'
            Install-LlamaCpp '' $GoodRelease 'vulkan'

            Assert-True (Test-Path -LiteralPath (Join-Path $Target 'ggml-vulkan.dll')) 'the Vulkan build'
            Assert-Equal 'vulkan' (Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json).Agent.Accelerator 'Agent:Accelerator'
            Assert-Equal 1 $script:CounterGrants 'the service account is granted the GPU memory counters'

            # A second run keeps the verified folder and still makes sure of the grant.
            Install-LlamaCpp '' $GoodRelease 'vulkan'
            Assert-Equal 2 $script:CounterGrants 'the grant is checked again on a re-run'
        }

        It 'refuses an accelerator the lock file pins no build for, and a malformed variant' {
            $lock = Get-Content -Raw $LockPath | ConvertFrom-Json
            $lock.variants = @($lock.variants | Where-Object { $_.accelerator -ne 'vulkan' })
            $bad = Join-Path $TestRoot 'no-vulkan.json'
            [IO.File]::WriteAllText($bad, (ConvertTo-Json -InputObject $lock -Depth 10))
            Assert-Throws { Select-LlamaCppBuild (Read-LlamaCppLock $bad) 'vulkan' } 'pins no vulkan build'

            $lock = Get-Content -Raw $LockPath | ConvertFrom-Json
            $lock.variants[0].assets[0].url = "https://github.com/ggml-org/llama.cpp/releases/latest/download/$CpuAsset"
            [IO.File]::WriteAllText($bad, (ConvertTo-Json -InputObject $lock -Depth 10))
            Assert-Throws { Read-LlamaCppLock $bad } 'must be downloaded by tag'

            $lock = Get-Content -Raw $LockPath | ConvertFrom-Json
            $lock.variants[0].files = @()
            [IO.File]::WriteAllText($bad, (ConvertTo-Json -InputObject $lock -Depth 10))
            Assert-Throws { Read-LlamaCppLock $bad } 'the cpu variant lists no files'

            $lock = Get-Content -Raw $LockPath | ConvertFrom-Json
            $lock.variants[0].accelerator = 'rocm'
            [IO.File]::WriteAllText($bad, (ConvertTo-Json -InputObject $lock -Depth 10))
            Assert-Throws { Read-LlamaCppLock $bad } "variant accelerator 'rocm' is not cpu or vulkan"
        }

        It 'stops with a message naming the Visual C++ redistributable when one of its DLLs is missing' {
            Remove-Item -LiteralPath (Join-Path $SystemDir 'vcruntime140_1.dll')
            Assert-Throws { Install-LlamaCpp '' $GoodRelease } 'Microsoft Visual C++ 2015-2022 Redistributable (x64) is not installed: vcruntime140_1.dll missing'
            Assert-True (-not (Test-Path -LiteralPath $Target)) 'nothing installed'
        }

        It 'installs only the pinned version' {
            Assert-Throws { Install-LlamaCpp 'b2' $GoodRelease } 'b2 is not pinned'
        }

        It 'reads only a lock file that pins by tag and lists every file' {
            $lock = Get-Content -Raw $LockPath | ConvertFrom-Json
            $lock.assets[0].url = "https://github.com/ggml-org/llama.cpp/releases/latest/download/$MainAsset"
            $bad = Join-Path $TestRoot 'latest.json'
            [IO.File]::WriteAllText($bad, (ConvertTo-Json -InputObject $lock -Depth 10))
            Assert-Throws { Read-LlamaCppLock $bad } 'must be downloaded by tag'

            $lock = Get-Content -Raw $LockPath | ConvertFrom-Json
            $lock.files = @()
            [IO.File]::WriteAllText($bad, (ConvertTo-Json -InputObject $lock -Depth 10))
            Assert-Throws { Read-LlamaCppLock $bad } 'lists no files'

            $lock = Get-Content -Raw $LockPath | ConvertFrom-Json
            $lock.files[0].path = '..\x.dll'
            [IO.File]::WriteAllText($bad, (ConvertTo-Json -InputObject $lock -Depth 10))
            Assert-Throws { Read-LlamaCppLock $bad } 'bad file path'
        }

        It 'keeps a verified installation and re-installs a damaged one' {
            Install-LlamaCpp '' $GoodRelease
            Install-LlamaCpp '' $GoodRelease
            Assert-Equal 1 @(@((Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json).Agent.Executables)[0].Versions).Count 'one version entry after a re-run'

            & icacls.exe $Target /grant "${CurrentUserRef}:(OI)(CI)F" /T /Q | Out-Null
            Set-Content -LiteralPath (Join-Path $Target 'ggml-cuda.dll') -Value 'cuda kernelZ' -NoNewline
            Install-LlamaCpp '' $GoodRelease
            Assert-Equal 'cuda kernels' (Get-Content -Raw -LiteralPath (Join-Path $Target 'ggml-cuda.dll')) 'damaged file replaced'

            & icacls.exe $Target /grant "${CurrentUserRef}:(OI)(CI)F" /T /Q | Out-Null
            Set-Content -LiteralPath (Join-Path $Target 'ggml-cuda.dll') -Value 'cuda kernelZ' -NoNewline
            $FakeProcesses = @([pscustomobject]@{ ProcessId = 4242; ExecutablePath = (Join-Path $Target 'llama-server.exe') })
            Assert-Throws { Install-LlamaCpp '' $GoodRelease } 'in use (process 4242)'
        }

        It 'keeps other versions and the existing default in agent.json' {
            [IO.File]::WriteAllText($ConfigPath, '{ "Agent": { "NodeId": "win-1", "Executables": [ { "Name": "llama-server", "Default": "b0", "Versions": [ { "Version": "b0", "Cuda": "13.4", "Path": "C:\\x\\b0\\llama-server.exe", "Manifest": "C:\\x\\b0\\manifest.json" } ] } ] } }')
            Install-LlamaCpp '' $GoodRelease
            $entry = @((Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json).Agent.Executables)[0]
            Assert-Equal 'b0' $entry.Default 'default kept'
            Assert-Equal 'b0,b1' ((@($entry.Versions) | ForEach-Object { $_.Version }) -join ',') 'both versions listed'
        }

        It 'verifies but changes nothing in a dry run' {
            $DryRun = $true
            Install-LlamaCpp '' $GoodRelease
            Assert-True (-not (Test-Path -LiteralPath $Target)) 'no version folder'
            Assert-Equal 0 @((Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json).Agent.Executables).Count 'agent.json untouched'

            $main = Copy-Table $ReleaseFiles
            $main['version.dll'] = 'planted'
            $release = New-Release $main (Copy-Table $CudartFiles) (Get-AllFiles)
            try {
                $LockPath = Join-Path $release 'lock.json'
                Assert-Throws { Install-LlamaCpp '' $release } 'unexpected file version.dll'
            } finally { Remove-Item -LiteralPath $release -Recurse -Force }
        }

        It 'removes a version only when no process uses it and it is not the default of others' {
            Install-LlamaCpp '' $GoodRelease
            $FakeProcesses = @([pscustomobject]@{ ProcessId = 7; ExecutablePath = (Join-Path $Target 'llama-server.exe') })
            Assert-Throws { Remove-LlamaCpp 'b1' } 'in use (process 7)'
            Assert-True (Test-Path -LiteralPath $Target) 'still installed'

            $FakeProcesses = @()
            $config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
            $entry = @($config.Agent.Executables)[0]
            $entry.Versions = @(@($entry.Versions) + [pscustomobject]@{ Version = 'b0'; Cuda = '13.4'; Path = 'C:\x\llama-server.exe'; Manifest = 'C:\x\manifest.json' })
            [IO.File]::WriteAllText($ConfigPath, (ConvertTo-Json -InputObject $config -Depth 10))
            Assert-Throws { Remove-LlamaCpp 'b1' } 'is the node default'

            $entry.Versions = @(@($entry.Versions) | Where-Object { $_.Version -eq 'b1' })
            [IO.File]::WriteAllText($ConfigPath, (ConvertTo-Json -InputObject $config -Depth 10))
            # This user also stands in for the read-only service; restore the installer's admin rights for removal.
            & icacls.exe $Target /grant:r "${CurrentUserRef}:(OI)(CI)F" /T /Q | Out-Null
            Assert-Equal 0 $LASTEXITCODE 'restore installer rights in the fixture'
            Remove-LlamaCpp 'b1'
            Assert-True (-not (Test-Path -LiteralPath $Target)) 'folder removed'
            Assert-Equal 0 @((Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json).Agent.Executables).Count 'entry removed'
        }
    }

    Context 'agent versions: upgrade, fallback, rollback, retention' {
        BeforeAll {
            . $LoadInstaller
            # The binary's content is its version; the service is a hashtable the fakes drive.
            function Get-AgentVersion([string] $ExePath) { (Get-Content -Raw -LiteralPath $ExePath).Trim() }
            function Get-AgentServiceState { $Svc.State }
            function Stop-AgentService { $Svc.Calls.Add('stop'); $Svc.State = 'Stopped' }
            function Start-AgentService {
                $Svc.Calls.Add('start')
                $running = Split-Path (Split-Path $Svc.Exe -Parent) -Leaf
                $Svc.State = if ($Svc.Broken -contains $running) { 'Stopped' } else { 'Running' }
            }
            function Set-AgentServiceBinary([string] $Exe) { $Svc.Calls.Add('binPath'); $Svc.Exe = $Exe }
            function Test-LocalPort([int] $Port) { $Svc.State -eq 'Running' }
            function Publish-Fake([string] $Version) {
                $dir = Join-Path $TestRoot "publish-$Version"
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                Set-Content -LiteralPath (Join-Path $dir 'mirbadllm-agent.exe') -Value $Version
                return $dir
            }
            function Install-Fake([string] $Version) {
                $state = Read-AgentVersions
                Assert-Equal $Version (Install-AgentBinary (Publish-Fake $Version)) 'installed version'
                Switch-AgentVersion $Version $state.current
            }
        }
        BeforeEach {
            . $InitLayout
            $Svc = @{ State = 'Stopped'; Exe = $null; Broken = @(); Calls = New-Object 'System.Collections.Generic.List[string]' }
            Set-Content -LiteralPath (Join-Path $StateDir 'agent.crt') -Value 'cert'
        }
        AfterEach { . $CleanLayout }

        It 'upgrades into a new version folder, switches the service and keeps the previous version' {
            Install-Fake '1.0.0'
            Install-Fake '1.1.0'
            Assert-Equal (Join-Path $AppDir '1.1.0\mirbadllm-agent.exe') $Svc.Exe 'binPath'
            Assert-Equal 'Running' $Svc.State 'service state'
            $state = Read-AgentVersions
            Assert-Equal '1.1.0' $state.current 'current'
            Assert-Equal '1.0.0' $state.previous 'previous'
            Assert-True (Test-Path -LiteralPath (Join-Path $AppDir '1.0.0\mirbadllm-agent.exe')) 'previous folder kept'
        }

        It 'switches back and starts the previous version when the new one does not come up' {
            Install-Fake '1.0.0'
            $Svc.Broken = @('2.0.0')
            Assert-Throws { Install-Fake '2.0.0' } 'Agent 2.0.0 did not come up'
            Assert-Equal (Join-Path $AppDir '1.0.0\mirbadllm-agent.exe') $Svc.Exe 'binPath back on the old version'
            Assert-Equal 'Running' $Svc.State 'old version running'
            Assert-Equal '1.0.0' (Read-AgentVersions).current 'current unchanged'
        }

        It 'rolls back to the previous version' {
            Install-Fake '1.0.0'
            Install-Fake '1.1.0'
            Invoke-Rollback
            Assert-Equal (Join-Path $AppDir '1.0.0\mirbadllm-agent.exe') $Svc.Exe 'binPath'
            $state = Read-AgentVersions
            Assert-Equal '1.0.0' $state.current 'current'
            Assert-Equal '1.1.0' $state.previous 'previous'
        }

        It 'keeps three agent versions' {
            foreach ($v in @('1.0.0', '1.1.0', '1.2.0', '1.3.0', '1.4.0')) { Install-Fake $v }
            $kept = @(Get-ChildItem -LiteralPath $AppDir -Directory | ForEach-Object { $_.Name } | Sort-Object)
            Assert-Equal '1.2.0,1.3.0,1.4.0' ($kept -join ',') 'kept folders'
        }

        It 'does not start an agent that is not enrolled' {
            Remove-Item -LiteralPath (Join-Path $StateDir 'agent.crt')
            Install-Fake '1.0.0'
            Assert-True (-not $Svc.Calls.Contains('start')) 'not started'
            Assert-Equal (Join-Path $AppDir '1.0.0\mirbadllm-agent.exe') $Svc.Exe 'binPath set'
        }

        It 'reports a service that stops after its start' {
            Install-Fake '1.0.0'
            $Svc.Broken = @('1.0.0')
            Assert-Throws { Switch-AgentVersion '1.0.0' $null } 'stopped'
        }

        It 'names a binary without version information after its hash, and keeps 12 digits of a commit' {
            . ([scriptblock]::Create(($InstallerFunctions | Where-Object { $_ -like 'function Get-AgentVersion*' })))
            $exe = Join-Path (Publish-Fake 'x') 'mirbadllm-agent.exe'
            Assert-True ((Get-AgentVersion $exe) -match '^0\.0\.0-[0-9a-f]{12}$') 'hash-named version'
            $ps = Join-Path $PSHOME 'powershell.exe'
            if (Test-Path -LiteralPath $ps) { Assert-True ((Get-AgentVersion $ps) -match '^[0-9]+\.[0-9.]+') 'product version' }
        }
    }

    Context 'rekey: identity kept aside and restored on failure' {
        BeforeAll {
            . $LoadInstaller
            function Get-AgentServiceState { $Svc.State }
            function Stop-AgentService { $Svc.State = 'Stopped' }
            function Start-AgentService { $Svc.State = 'Running' }
            function Test-LocalPort([int] $Port) { $Svc.State -eq 'Running' }
            function Set-PrivateAcl([string] $Path, [string] $Account, [string] $Rights = 'M') { $Svc.Acl++ }
            function Get-AccountReference { $CurrentUserRef }
            # The real one registers a service (administrators only); the rekey must call it, for the CA it may bring.
            function Install-UpdateHelper([string] $Exe) { $Svc.Helper = $Exe }
            function Invoke-Join([string] $Exe, [string] $Node, [switch] $Rekey) {
                Assert-True $Rekey 'join --rekey'
                Set-Content -LiteralPath (Join-Path $StateDir 'agent.crt') -Value 'new cert'
                Set-Content -LiteralPath (Join-Path $StateDir 'node-uid') -Value 'uid'
                if ($JoinFails) { throw 'join failed (exit 1).' }
            }
        }
        BeforeEach {
            . $InitLayout
            $Svc = @{ State = 'Running'; Acl = 0 }
            $JoinFails = $false
            $EnrollUrl = [uri]'https://192.0.2.191:8443'
            $CaHash = 'sha256:abc'
            Set-Item -Path "Env:$TokenVariable" -Value 'test-token'
            New-Item -ItemType Directory -Force -Path (Join-Path $AppDir '1.0.0') | Out-Null
            Save-AgentVersions '1.0.0' $null
            Set-Content -LiteralPath (Join-Path $StateDir 'agent.crt') -Value 'old cert'
            Set-Content -LiteralPath (Join-Path $StateDir 'agent.key') -Value 'old key'
        }
        AfterEach {
            Remove-Item -Path "Env:$TokenVariable" -ErrorAction SilentlyContinue
            . $CleanLayout
        }

        It 're-pairs, re-applies the ACL, starts the service and removes the copy' {
            Invoke-Rekey
            Assert-Equal 'new cert' (Get-Content -LiteralPath (Join-Path $StateDir 'agent.crt')) 'new identity'
            Assert-True ([string]$Svc.Helper -like '*1.0.0*mirbadllm-agent.exe') 'the update helper is refreshed with the current agent'
            Assert-Equal 'Running' $Svc.State 'started'
            Assert-True ($Svc.Acl -ge 1) 'ACL re-applied'
            Assert-Equal 0 @(Get-ChildItem -LiteralPath $DataDir -Directory -Filter 'rekey-backup-*').Count 'no copy left'
        }

        It 'restores exactly the previous identity and starts it again when join fails' {
            $JoinFails = $true
            Assert-Throws { Invoke-Rekey } 'The previous identity was restored'
            Assert-Equal 'old cert' (Get-Content -LiteralPath (Join-Path $StateDir 'agent.crt')) 'old certificate back'
            Assert-Equal 'old key' (Get-Content -LiteralPath (Join-Path $StateDir 'agent.key')) 'old key kept'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $StateDir 'node-uid'))) 'file the failed rekey created is removed'
            Assert-Equal 'Running' $Svc.State 'old identity running'
            Assert-Equal 0 @(Get-ChildItem -LiteralPath $DataDir -Directory -Filter 'rekey-backup-*').Count 'no copy left'
        }

        It 'needs the token from the environment or the prompt' {
            Remove-Item -Path "Env:$TokenVariable"
            Assert-Throws { Invoke-Rekey } 'never put the token on the command line'
        }
    }

    Context 'enrollment command line' {
        BeforeAll { . $LoadInstaller }
        BeforeEach {
            . $InitLayout
            $EnrollUrl = [uri]'https://192.0.2.191:8443/'
            $CaHash = 'sha256:abc'
            $Address = @('192.0.2.50')
            $Record = Join-Path $TestRoot 'join.txt'
            # Stands in for mirbadllm-agent.exe: records its arguments and whether the token came in the environment.
            $FakeAgent = Join-Path $TestRoot 'fake-agent.ps1'
            Set-Content -LiteralPath $FakeAgent -Value ("Set-Content -LiteralPath '$Record' -Value ((`$args -join ' ') + ' | token=' + `$env:MIRBADLLM_JOIN_TOKEN); exit `$env:FAKE_EXIT")
            Set-Item -Path "Env:$TokenVariable" -Value 'secret-token'
        }
        AfterEach {
            Remove-Item -Path "Env:$TokenVariable", 'Env:FAKE_EXIT' -ErrorAction SilentlyContinue
            . $CleanLayout
        }

        It 'passes the token only through the environment and builds the rekey arguments' {
            $env:FAKE_EXIT = '0'
            $NodeUid = 'uid-1'
            Invoke-Join $FakeAgent 'win-1' -Rekey
            $line = Get-Content -LiteralPath $Record
            Assert-Equal "join --rekey --enroll-url https://192.0.2.191:8443 --ca-hash sha256:abc --node-id win-1 --state-dir $StateDir --node-uid uid-1 --address 192.0.2.50 | token=secret-token" $line 'arguments'
        }

        It 'fails on a non-zero exit' {
            $env:FAKE_EXIT = '3'
            Assert-Throws { Invoke-Join $FakeAgent 'win-1' } 'join failed (exit 3)'
        }
    }

    Context 'GGUF import' {
        BeforeAll {
            . $LoadInstaller
            function New-Gguf([string] $Path, [string] $Body) { [IO.File]::WriteAllText($Path, "GGUF$Body") }
        }
        BeforeEach {
            . $InitLayout
            $Models = Join-Path $TestRoot 'models'
            New-Item -ItemType Directory -Path $Models | Out-Null
            $Blob = Join-Path $TestRoot 'blob.gguf'
            New-Gguf $Blob 'weights'
            $Digest = Get-Sha $Blob
        }
        AfterEach { . $CleanLayout }

        It 'imports an Ollama blob named after its digest' {
            $ollama = Join-Path $TestRoot "sha256-$Digest"
            Copy-Item -LiteralPath $Blob -Destination $ollama
            Import-Gguf $ollama 'planner' $null $Models
            Assert-Equal 'GGUFweights' (Get-Content -Raw -LiteralPath (Join-Path $Models 'planner\model.gguf')) 'imported'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $Models 'planner.partial'))) 'no partial folder'
        }

        It 'refuses a wrong digest and leaves nothing behind' {
            Assert-Throws { Import-Gguf $Blob 'm' ('0' * 64) $Models } 'Nothing imported'
            Assert-Equal 0 @(Get-ChildItem -LiteralPath $Models -Force).Count 'models root unchanged'
        }

        It 'refuses a file that is not GGUF, a file without a digest, a bad folder name and an existing folder' {
            $text = Join-Path $TestRoot 'notes.gguf'
            Set-Content -LiteralPath $text -Value 'hello'
            Assert-Throws { Import-Gguf $text 'm' (Get-Sha $text) $Models } 'is not a GGUF file'
            Assert-Throws { Import-Gguf $Blob 'm' $null $Models } 'Give -Sha256'
            foreach ($bad in @('..', 'a b', 'x.', 'C:', 'm.partial')) { Assert-Throws { Import-Gguf $Blob $bad $Digest $Models } 'is not a model folder name' }
            New-Item -ItemType Directory -Path (Join-Path $Models 'taken') | Out-Null
            Assert-Throws { Import-Gguf $Blob 'taken' $Digest $Models } 'already exists'
        }

        It 'keeps the .gguf file name' {
            Import-Gguf $Blob 'embed' $Digest $Models
            Assert-True (Test-Path -LiteralPath (Join-Path $Models 'embed\blob.gguf')) 'file name kept'
        }
    }

    Context 'service registration: Event Log source and PreshutdownTimeout' {
        BeforeAll {
            . $LoadInstaller
            # Shadow the cmdlets that would change the machine: they only record their arguments.
            function Test-AgentEventSource { $Reg.SourceExists }
            function New-EventLog([string] $LogName, [string] $Source) { $Reg.Calls.Add("New-EventLog $LogName $Source") }
            function Remove-EventLog([string] $Source) { $Reg.Calls.Add("Remove-EventLog $Source") }
            function Set-ItemProperty([string] $Path, [string] $Name, $Value, [string] $Type) { $Reg.Calls.Add("Set-ItemProperty $Path $Name $Value $Type") }
            function Write-Config([string] $ShutdownTimeout) {
                $agent = if ($ShutdownTimeout) { "{ `"NodeId`": `"win-1`", `"ShutdownTimeout`": `"$ShutdownTimeout`" }" } else { '{ "NodeId": "win-1" }' }
                [IO.File]::WriteAllText($ConfigPath, "{ `"Agent`": $agent }")
            }
        }
        BeforeEach {
            . $InitLayout
            $ServiceRegistryPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\mirbadllm-agent'
            $EventLogName = 'Application'
            $Reg = @{ SourceExists = $false; Calls = New-Object 'System.Collections.Generic.List[string]' }
        }
        AfterEach { . $CleanLayout }

        It 'registers the mirbadllm-agent source in the Application log once' {
            Register-AgentEventSource
            Assert-Equal 'New-EventLog Application mirbadllm-agent' ($Reg.Calls -join '|') 'source created'
            $Reg.Calls.Clear()
            $Reg.SourceExists = $true
            Register-AgentEventSource
            Assert-Equal 0 $Reg.Calls.Count 'an existing source is kept'
        }

        It 'removes the source on uninstall only when it exists' {
            Unregister-AgentEventSource
            Assert-Equal 0 $Reg.Calls.Count 'nothing to remove'
            $Reg.SourceExists = $true
            Unregister-AgentEventSource
            Assert-Equal 'Remove-EventLog mirbadllm-agent' ($Reg.Calls -join '|') 'source removed'
        }

        It 'sets PreshutdownTimeout to Agent:ShutdownTimeout + 10 s as a DWORD under the service key' {
            Write-Config '00:00:30'
            Set-AgentPreshutdownTimeout
            Assert-Equal 'Set-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\mirbadllm-agent PreshutdownTimeout 40000 DWord' ($Reg.Calls -join '|') 'registry value'
            Write-Config '00:02:00'
            Assert-Equal 130000 (Get-PreshutdownTimeoutMs) 'two minutes'
            Write-Config '00:00:00.5'
            Assert-Equal 10500 (Get-PreshutdownTimeoutMs) 'fractions round up'
            Write-Config $null
            Assert-Equal 40000 (Get-PreshutdownTimeoutMs) 'the agent default of 30 s'
            Remove-Item -LiteralPath $ConfigPath
            Assert-Equal 40000 (Get-PreshutdownTimeoutMs) 'no agent.json yet'
            Write-Config 'soon'
            Assert-Throws { Get-PreshutdownTimeoutMs } 'is not a time span'
        }

        It 'changes nothing in a dry run' {
            $DryRun = $true
            Register-AgentEventSource
            Set-AgentPreshutdownTimeout
            $Reg.SourceExists = $true
            Unregister-AgentEventSource
            Assert-Equal 0 $Reg.Calls.Count 'no calls'
        }
    }

    Context 'host checks' {
        BeforeAll { . $LoadInstaller }

        It 'warns when the clock is not synced' {
            Assert-True ((Get-TimeSyncProblem @() 1) -like '*does not answer*') 'stopped service'
            Assert-True ((Get-TimeSyncProblem @('Leap Indicator: 3(not synchronized)', 'Source: Local CMOS Clock') 0) -like '*not synced (time source: Local CMOS Clock)*') 'CMOS clock'
            Assert-True ((Get-TimeSyncProblem @('Source: Free-running System Clock') 0) -like '*not synced*') 'free-running'
            Assert-Equal $null (Get-TimeSyncProblem @('Source: time.windows.com,0x9') 0) 'synced'
        }

        It 'warns when the PC sleeps on AC power' {
            Assert-True ((Get-PowerPlanProblem @('    Current AC Power Setting Index: 0x00000708', '    Current DC Power Setting Index: 0x00000384')) -like '*after 30 minutes on AC power*') 'sleeps'
            Assert-Equal $null (Get-PowerPlanProblem @('    Current AC Power Setting Index: 0x00000000')) 'never sleeps'
            Assert-Equal $null (Get-PowerPlanProblem @('unreadable')) 'other language'
        }

        It 'checks the three Visual C++ runtime DLLs' {
            . $InitLayout
            try {
                Test-VcRuntime
                Remove-Item -LiteralPath (Join-Path $SystemDir 'msvcp140.dll')
                Assert-Throws { Test-VcRuntime } 'msvcp140.dll missing'
            } finally { . $CleanLayout }
        }
    }

    Context 'agent.json: the node id and a second install run (MN-239)' {
        BeforeAll {
            . $LoadInstaller
            $ExampleConfig = (Resolve-Path (Join-Path $PSScriptRoot '..\..\agent.windows.example.json')).Path
            # The script finds the example next to itself in the bundle; here it is two folders up.
            function Get-ExampleConfigPath { $ExampleConfig }
            # Test-Enrolled looks for state\agent.crt; the fake says whether this node has an identity yet.
            function Test-Enrolled { $FakeEnrolled }
            function Read-Config { Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json }
        }
        BeforeEach {
            . $InitLayout
            $ModelsRoot = Join-Path $TestRoot 'models'
            $CacheRoot = Join-Path $TestRoot 'cache'
            $FakeEnrolled = $false
            # The script's own $PSBoundParameters: what tells an explicitly passed setting from a defaulted one.
            $BoundParameters = @{}
        }
        AfterEach { . $CleanLayout }

        It 'refuses a node id the agent refuses, names the rejected value and the fix, and writes no agent.json' {
            # The owner's machine name is 'DevelopmentPC'. PowerShell's -match, -like and [ValidatePattern] are
            # case-insensitive, so an upper-case id passed the installer, reached Agent:NodeId and was then refused by
            # the agent, which exited before it could log anything: the SCM reported error 1053 and the Application log
            # stayed empty. The installer must apply the agent's rule itself, case-sensitively (MN-239 C1).
            Remove-Item -LiteralPath $ConfigPath
            Assert-Throws { Write-InitialConfig 'DevelopmentPC' } "'DevelopmentPC' is not a node id"
            Assert-Throws { Write-InitialConfig 'DevelopmentPC' } '-NodeId developmentpc'
            Assert-Throws { Write-InitialConfig 'DevelopmentPC' } '1053'
            Assert-True (-not (Test-Path -LiteralPath $ConfigPath)) 'no agent.json was written'
            foreach ($bad in @('DevelopmentPC', 'Win-1', 'win_1', '-win-1', 'win 1', 'win.1', ('a' * 41))) {
                Assert-Throws { Write-InitialConfig $bad } 'is not a node id'
            }
            Assert-Throws { Write-InitialConfig '' } '-NodeId is required for the first install'
            Assert-True (-not (Test-Path -LiteralPath $ConfigPath)) 'still no agent.json'

            Assert-True (Test-NodeId 'win-1') 'win-1 is a node id'
            Assert-True (Test-NodeId ('a' * 40)) '40 characters'
            Assert-True (-not (Test-NodeId ('a' * 41))) '41 characters'
            Assert-True (-not (Test-NodeId 'Win-1')) 'upper case is not a node id'
            Assert-Equal 'developmentpc' (Get-NodeIdSuggestion 'DevelopmentPC') 'the suggested slug'
            Assert-Equal 'my-pc' (Get-NodeIdSuggestion '  My__PC!!  ') 'runs of punctuation collapse and are trimmed'

            # The sanitised id is accepted when the operator passes it: offered in the message, never applied for them.
            Write-InitialConfig 'developmentpc'
            Assert-Equal 'developmentpc' (Read-Config).Agent.NodeId 'the id the operator chose'
            Assert-Equal $StateDir (Read-Config).Agent.StateDirectory 'the rest of the file is still written'
        }

        It 'applies an explicitly passed -NodeId to an existing agent.json instead of keeping the stale one' {
            # The owner's second run: "install -NodeId devpc" printed "Keeping existing ...\agent.json" and dropped the
            # flag, so the file still said DevelopmentPC while the node enrolled as devpc (MN-239 C2).
            [IO.File]::WriteAllText($ConfigPath, '{ "Agent": { "NodeId": "DevelopmentPC", "Port": 9443, "Executables": [] } }')
            $BoundParameters = @{ NodeId = 'devpc' }
            Write-InitialConfig 'devpc'
            Assert-Equal 'devpc' (Read-Config).Agent.NodeId 'the explicitly passed node id wins'
            Assert-Equal 9443 (Read-Config).Agent.Port 'everything else is kept'
        }

        It 'keeps a node id that was only defaulted, and refuses an explicitly passed one the agent refuses' {
            $BoundParameters = @{}
            Write-InitialConfig 'developmentpc'
            Assert-Equal 'win-1' (Read-Config).Agent.NodeId 'a defaulted or derived id never overwrites the file'
            $BoundParameters = @{ NodeId = 'win-1' }
            Write-InitialConfig 'win-1'
            Assert-Equal 'win-1' (Read-Config).Agent.NodeId 'the same id is not a change'
            $BoundParameters = @{ NodeId = 'DevPC' }
            Assert-Throws { Write-InitialConfig 'DevPC' } "'DevPC' is not a node id"
            Assert-Equal 'win-1' (Read-Config).Agent.NodeId 'the file is untouched by the refusal'
        }

        It 'warns about the certificate when it changes the node id of an enrolled node' {
            $FakeEnrolled = $true
            $BoundParameters = @{ NodeId = 'devpc' }
            $Alerts = New-Object 'System.Collections.Generic.List[string]'
            function Write-Alert([string] $Message) { $Alerts.Add($Message) }
            Write-InitialConfig 'devpc'
            Assert-Equal 'devpc' (Read-Config).Agent.NodeId 'the file follows the flag'
            Assert-Equal 1 $Alerts.Count 'one warning'
            Assert-True ($Alerts[0] -like "*'win-1'*") 'the warning names the id that was in the file'
            Assert-True ($Alerts[0] -like '*rekey*') 'the warning names the way out'
        }

        It 'updates an explicitly passed models or cache root and keeps a defaulted one' {
            [IO.File]::WriteAllText($ConfigPath, '{ "Agent": { "NodeId": "win-1", "ModelsRoot": "D:\\old\\models", "ModelsHostPath": "D:\\old\\models", "CacheRoot": "D:\\old\\cache", "CacheHostPath": "D:\\old\\cache" } }')
            $BoundParameters = @{ ModelsRoot = $ModelsRoot }
            Write-InitialConfig ''
            $config = Read-Config
            Assert-Equal $ModelsRoot $config.Agent.ModelsRoot 'the models root follows the flag'
            Assert-Equal $ModelsRoot $config.Agent.ModelsHostPath 'and so does the host path it is mounted from'
            Assert-Equal 'D:\old\cache' $config.Agent.CacheRoot 'a defaulted cache root is kept'
            Assert-Equal 'win-1' $config.Agent.NodeId 'the node id is kept'
        }

        It 'changes nothing in a dry run' {
            $DryRun = $true
            $BoundParameters = @{ NodeId = 'devpc' }
            Write-InitialConfig 'devpc'
            Assert-Equal 'win-1' (Read-Config).Agent.NodeId 'the file is only printed, not written'
        }
    }
}
