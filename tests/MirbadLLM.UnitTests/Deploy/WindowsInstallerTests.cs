using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.UnitTests.Deploy;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The Windows installer (<c>deploy/agent/windows/install-agent.ps1</c>) and the pinned llama.cpp lock file, as the W11
/// review (MN-207, D120/D122) left them after spikes S1 and S4: the ACL step leaves every existing file readable by the
/// service (F1), the script runs under Windows PowerShell 5.1 (F6), the firewall rule is service-bound, Private-only and
/// scoped to the master (F4), the join token is never a parameter and is cleared in a <c>finally</c> (F5), and the lock
/// file pins <c>b11476</c> with the measured SHA-256 of both assets and of every unpacked file (C3, C10). The static
/// checks run on every OS; the 5.1 parse and the ACL behaviour run on Windows with <c>powershell.exe</c> (skipped
/// elsewhere with a reason). MN-216 (W12) extends it: the verbs, Automatic (Delayed Start) and no Docker, the single
/// by-tag download, the Visual C++ runtime and driver checks, a lint pass standing in for PSScriptAnalyzer, the Pester
/// suite under <c>deploy/agent/windows/test/</c> run under 5.1, and the files the script writes (<c>manifest.json</c>,
/// <c>Agent:Executables</c> in <c>agent.json</c>) and the Windows example read with the frozen types.
/// </summary>
public sealed partial class WindowsInstallerTests
{
    private static readonly JsonSerializerOptions WireStrict = new(AgentJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static string Script => File.ReadAllText(RepoFile("deploy", "agent", "windows", "install-agent.ps1"));

    // ---------------------------------------------------------------- F6: Windows PowerShell 5.1

    [Fact]
    public void Script_UsesNothingWindowsPowerShell51Lacks()
    {
        string script = Script;
        Assert.DoesNotMatch(new Regex(@"#Requires\s+-Version", RegexOptions.IgnoreCase), script);
        Assert.Contains("#Requires -RunAsAdministrator", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-AsHashtable", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("utf8NoBOM", script, StringComparison.OrdinalIgnoreCase);

        // 5.1 reads a BOM-less script in the ANSI code page: any non-ASCII character would be garbled.
        int nonAscii = script.IndexOf(script.FirstOrDefault(c => c > 0x7F));
        Assert.True(script.All(c => c <= 0x7F), $"install-agent.ps1 must be ASCII-only (first non-ASCII at offset {nonAscii}).");
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void Script_ParsesUnderWindowsPowerShell51()
    {
        string path = RepoFile("deploy", "agent", "windows", "install-agent.ps1");
        (int exit, string output) = RunWindowsPowerShell(
            $"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile({Quote(path)}, [ref]$null, [ref]$e); " +
            "if ($e) { $e | ForEach-Object { $_.Message }; exit 1 }; $PSVersionTable.PSVersion.Major");
        Assert.True(exit == 0, $"install-agent.ps1 does not parse under Windows PowerShell:\n{output}");
        Assert.Equal("5", output.Trim());
    }

    // ---------------------------------------------------------------- F1: private ACL

    [Fact]
    [Trait("Os", "Windows")]
    public void SetPrivateAcl_ProtectsTheRootOnly_ExistingFilesInheritAndStayReadable()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-mn207-acl-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "state");
        string key = Path.Combine(state, "agent.key");
        string config = Path.Combine(root, "agent.json");
        Directory.CreateDirectory(state);

        // As in the product: the installer writes agent.json and the enrolled identity before it applies the ACL.
        File.WriteAllText(key, "key");
        File.WriteAllText(config, "{}");
        try
        {
            string script = RepoFile("deploy", "agent", "windows", "install-agent.ps1");
            (int exit, string output) = RunWindowsPowerShell(
                "$ErrorActionPreference = 'Stop'; $e = $null; " +
                $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quote(script)}, [ref]$null, [ref]$e); " +
                "$fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Set-PrivateAcl' }, $true); " +
                ". ([scriptblock]::Create($fn.Extent.Text)); " +
                "$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; " +
                $"Set-PrivateAcl {Quote(root)} \"*$sid\"; " +
                "function Describe($p) { $a = Get-Acl -LiteralPath $p; @{ protected = $a.AreAccessRulesProtected; " +
                "rules = @($a.Access).Count; inherited = @($a.Access | Where-Object IsInherited).Count } }; " +
                $"@{{ root = (Describe {Quote(root)}); state = (Describe {Quote(state)}); key = (Describe {Quote(key)}); " +
                $"config = (Describe {Quote(config)}); content = [IO.File]::ReadAllText({Quote(key)}) }} | ConvertTo-Json -Compress");
            Assert.True(exit == 0, $"Set-PrivateAcl failed:\n{output}");

            JsonNode result = JsonNode.Parse(output.Trim())!;

            // The root: protected, exactly SYSTEM, Administrators and the account, nothing inherited from above.
            AssertAcl(result["root"]!, isProtected: true, rules: 3, inherited: 0);

            // Everything below it inherits those three entries; before the fix the files ended with an empty, protected
            // DACL (D:PAI) and the service got "Access is denied" on its own key (spike S4 / F1).
            AssertAcl(result["state"]!, isProtected: false, rules: 3, inherited: 3);
            AssertAcl(result["key"]!, isProtected: false, rules: 3, inherited: 3);
            AssertAcl(result["config"]!, isProtected: false, rules: 3, inherited: 3);
            Assert.Equal("key", result["content"]!.GetValue<string>());
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                using var reset = Process.Start(new ProcessStartInfo("icacls.exe") { ArgumentList = { root, "/reset", "/T", "/C", "/Q" }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                reset?.WaitForExit(30_000);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetPrivateAcl_GrantsTheRootWithoutRecursingAndResetsTheChildren()
    {
        string function = FunctionText(Script, "Set-PrivateAcl");
        string grant = function.Split('\n').Single(l => l.TrimStart().StartsWith("& icacls.exe", StringComparison.Ordinal) && l.Contains("/inheritance:r", StringComparison.Ordinal));
        Assert.DoesNotContain("/T", grant, StringComparison.Ordinal);
        Assert.Contains("/reset /T", function, StringComparison.Ordinal);
        Assert.Contains("Join-Path $Path '*'", function, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- F4: firewall

    [Fact]
    public void FirewallRule_IsServiceBound_PrivateOnly_FromTheMaster_AndAnOffFirewallIsReported()
    {
        string script = Script;
        string rule = Regex.Match(script, @"New-NetFirewallRule[^\n]*`\r?\n[^\n]*").Value;
        Assert.Contains("-Service $ServiceName", rule, StringComparison.Ordinal);
        Assert.Contains("-Profile Private", rule, StringComparison.Ordinal);
        Assert.Contains("-RemoteAddress $remote", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("-Program", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("Domain", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("Public", rule, StringComparison.Ordinal);

        // The default remote address is the enroll URL's host, resolved; LocalSubnet only as a warned fallback.
        Assert.Contains("GetHostAddresses($Url.DnsSafeHost)", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetFirewallProfile -PolicyStore ActiveStore", script, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- F5: join token

    [Fact]
    public void JoinToken_IsNeverAParameter_HasASecurePrompt_AndIsClearedInFinally()
    {
        string script = Script;
        string param = Regex.Match(script, @"param\((?<p>.*?)\n\)", RegexOptions.Singleline).Groups["p"].Value;
        Assert.NotEmpty(param);
        foreach (Match p in Regex.Matches(param, @"\$(\w+)"))
        {
            // A switch that asks for the token is fine; a parameter carrying it is not (Win32_Process shows command lines).
            Assert.True(p.Groups[1].Value is "PromptToken" || !p.Groups[1].Value.Contains("Token", StringComparison.OrdinalIgnoreCase), $"parameter ${p.Groups[1].Value}");
        }

        Assert.Contains("[switch] $PromptToken", param, StringComparison.Ordinal);
        Assert.Contains("Read-Host -AsSecureString", script, StringComparison.Ordinal);
        Assert.Contains("ZeroFreeBSTR", script, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\}\s*finally\s*\{\s*#[^\n]*\n\s*Clear-JoinToken", RegexOptions.Singleline), script);
        Assert.Contains("Remove-Item -Path \"Env:$TokenVariable\"", FunctionText(script, "Clear-JoinToken"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- F8: uninstall

    [Fact]
    public void Uninstall_StopsOnlyTheAccountsRuntimes_AndPrintsTheProfileRemoval()
    {
        string script = Script;
        Assert.Contains("GetOwnerSid", script, StringComparison.Ordinal);
        Assert.Contains("$owner.Sid -eq $serviceSid", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $InstallDir 'runtimes'", script, StringComparison.Ordinal);
        Assert.Contains("Win32_UserProfile", script, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- C3/C10: the llama.cpp pin

    [Fact]
    public void LockFile_PinsB11476_WithBothAssetsAndEveryUnpackedFile()
    {
        LlamaCppLockFile lockFile = JsonSerializer.Deserialize<LlamaCppLockFile>(
            File.ReadAllText(RepoFile("deploy", "agent", "windows", "llama-cpp.lock.json")), WireStrict)!;

        Assert.Equal(("b11476", "13.4", "615.00", "988190680"), (lockFile.Version, lockFile.Cuda, lockFile.MinDriver, lockFile.Commit));
        Assert.Equal(
            ["llama-b11476-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-x64.zip"],
            lockFile.Assets.Select(a => a.Name));
        Assert.All(lockFile.Assets, a =>
        {
            Assert.Equal($"https://github.com/ggml-org/llama.cpp/releases/download/{lockFile.Version}/{a.Name}", a.Url);
            Assert.Matches(Sha256Regex(), a.Sha256);
            Assert.True(a.Bytes > 0);
        });
        Assert.Equal("0f947f51ece3b807a7f556dbdf45dc1a7f31aa6ea265657639007a33ef1ce5e0", lockFile.Assets[0].Sha256);
        Assert.Equal("738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668", lockFile.Assets[1].Sha256);

        // Every unpacked file (55 at b11476), not just the 9 KB launcher: the server is llama-server-impl.dll (S1 / C3).
        Assert.Equal(55, lockFile.Files.Count);
        Assert.Equal(lockFile.Files.Count, lockFile.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(lockFile.Files, f =>
        {
            Assert.Matches(Sha256Regex(), f.Sha256);
            Assert.True(f.Bytes > 0);
            Assert.DoesNotContain('\\', f.Path);
            Assert.DoesNotContain("..", f.Path, StringComparison.Ordinal);
        });
        string[] required = ["llama-server.exe", "llama-server-impl.dll", "llama.dll", "llama-common.dll", "ggml.dll", "ggml-base.dll", "ggml-cuda.dll", "mtmd.dll", "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll"];
        Assert.All(required, r => Assert.Contains(lockFile.Files, f => f.Path == r));

        // The contract fixture carries a subset of the same measured values.
        LlamaCppLockFile fixture = JsonSerializer.Deserialize<LlamaCppLockFile>(
            File.ReadAllText(Path.Combine(RepoRoot(), "tests", "MirbadLLM.UnitTests", "Contracts", "Fixtures", "P3", "LlamaCppLockFile.json")), WireStrict)!;
        Assert.Equal(lockFile.Assets, fixture.Assets);
        Assert.All(fixture.Files, f => Assert.Contains(f, lockFile.Files));
    }

    // ---------------------------------------------------------------- MN-216: verbs, service, llama.cpp, warnings

    [Fact]
    public void Script_HasTheVerbs_DelayedStart_NoDocker_AndPinsByTagOnly()
    {
        string script = Script;
        Assert.Contains("[ValidateSet('install', 'upgrade', 'rollback', 'rekey', 'uninstall')]", script, StringComparison.Ordinal);
        foreach (string parameter in new[] { "[switch] $LlamaCpp", "[string] $RemoveLlamaCpp", "[string] $ImportGguf", "[string] $Bundle", "[switch] $DryRun" })
        {
            Assert.Contains(parameter, script, StringComparison.Ordinal);
        }

        // Automatic (Delayed Start) once the agent answers the SCM (MN-215); no Docker Desktop on this node (section A.4.2).
        Assert.Contains("start= delayed-auto", script, StringComparison.Ordinal);
        Assert.DoesNotContain("start= demand", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-users", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LocalGroup", script, StringComparison.Ordinal);

        // One download site, by tag, with the 5.1 progress bar off; nothing else (the VC++ runtime is never downloaded).
        Assert.Single(Regex.Matches(script, @"Invoke-WebRequest"));
        string save = FunctionText(script, "Save-LlamaCppAssets");
        Assert.Contains("Invoke-WebRequest -UseBasicParsing -Uri $a.url", save, StringComparison.Ordinal);
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", save, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"https://\S*releases/latest"), script);
        Assert.Contains("releases/download/$version/$($a.name)", FunctionText(script, "Read-LlamaCppLock"), StringComparison.Ordinal);

        string vc = FunctionText(script, "Test-VcRuntime");
        Assert.Contains("Visual C++ 2015-2022 Redistributable (x64)", vc, StringComparison.Ordinal);
        Assert.Contains("@('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')", script, StringComparison.Ordinal);
        Assert.Contains("--query-gpu=driver_version", FunctionText(script, "Get-NvidiaDriverVersion"), StringComparison.Ordinal);
        Assert.Contains("w32tm.exe /query /status", script, StringComparison.Ordinal);
        Assert.Contains("SUB_SLEEP STANDBYIDLE", script, StringComparison.Ordinal);

        // MN-214/MN-215 findings: the Event Log source (the virtual account cannot create it) is registered by install
        // after agent.json exists and removed by uninstall; PreshutdownTimeout is set after the service exists.
        string install = FunctionText(script, "Invoke-Install");
        int service = install.IndexOf("Set-AgentService $exe", StringComparison.Ordinal);
        int config = install.IndexOf("Write-InitialConfig $NodeId", StringComparison.Ordinal);
        int preshutdown = install.IndexOf("Set-AgentPreshutdownTimeout", StringComparison.Ordinal);
        int source = install.IndexOf("Register-AgentEventSource", StringComparison.Ordinal);
        Assert.True(service >= 0 && config > service && preshutdown > config && source > config, "install order: service, agent.json, PreshutdownTimeout, Event Log source");
        Assert.Contains("Unregister-AgentEventSource", FunctionText(script, "Invoke-Uninstall"), StringComparison.Ordinal);
        Assert.Contains("New-EventLog -LogName $EventLogName -Source $ServiceName", FunctionText(script, "Register-AgentEventSource"), StringComparison.Ordinal);
        Assert.Contains("$EventLogName = 'Application'", script, StringComparison.Ordinal);
        Assert.Contains("[System.Diagnostics.EventLog]::SourceExists($ServiceName)", script, StringComparison.Ordinal);
        Assert.Contains("$ServiceRegistryPath = \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\$ServiceName\"", script, StringComparison.Ordinal);
        Assert.Contains("-Name 'PreshutdownTimeout' -Value $ms -Type DWord", FunctionText(script, "Set-AgentPreshutdownTimeout"), StringComparison.Ordinal);

        // rekey goes through the same join path, so the token handling (prompt, finally) is the install's.
        Assert.Contains("Invoke-Join $exe $node -Rekey", FunctionText(script, "Invoke-Rekey"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void Script_LintsClean_NoAliases_ApprovedVerbs_NoInvokeExpression()
    {
        // PSScriptAnalyzer is not installed on the runners or this PC (and nothing may be downloaded): its two rules that
        // matter for a 5.1 script, run from the same AST (PSAvoidUsingCmdletAliases, PSUseApprovedVerbs), plus
        // PSAvoidUsingInvokeExpression and PSAvoidUsingPositionalParameters' cousin: no three-argument Join-Path (7 only).
        string path = RepoFile("deploy", "agent", "windows", "install-agent.ps1");
        (int exit, string output) = RunWindowsPowerShell(
            "$e = $null; " +
            $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quote(path)}, [ref]$null, [ref]$e); " +
            "$verbs = @(Get-Verb | ForEach-Object { $_.Verb }); $bad = @(); " +
            "foreach ($c in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) { " +
            "  $name = $c.GetCommandName(); if (-not $name) { continue }; " +
            "  if (Get-Alias -Name $name -ErrorAction SilentlyContinue) { $bad += \"alias $name at line $($c.Extent.StartLineNumber)\" }; " +
            "  if ($name -eq 'Invoke-Expression') { $bad += \"Invoke-Expression at line $($c.Extent.StartLineNumber)\" }; " +
            "  if ($name -eq 'Join-Path' -and @($c.CommandElements | Where-Object { $_ -isnot [System.Management.Automation.Language.CommandParameterAst] }).Count -gt 3) { $bad += \"Join-Path with 3 paths at line $($c.Extent.StartLineNumber)\" } }; " +
            "foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { " +
            "  if ($verbs -notcontains $f.Name.Split('-')[0]) { $bad += \"unapproved verb $($f.Name)\" } }; " +
            "$bad -join \"`n\"");
        Assert.True(exit == 0, output);
        Assert.Equal("", output.Trim());
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void Pester_SuitePassesUnderWindowsPowerShell51()
    {
        // deploy/agent/windows/test/install-agent.Tests.ps1 drives the installer's own functions against temp folders with
        // the service, driver, process, port and join calls faked: the lock-file verification (tampered, extra, missing,
        // overlapping and escaping files, asset hash), the driver and VC++ runtime refusals, the dry run, upgrade with
        // fallback, rollback, retention, rekey with restore, the join arguments, the GGUF import and the node id
        // agent.json is written with (MN-239 C1/C2).
        string tests = RepoFile("deploy", "agent", "windows", "test");
        (int exit, string output) = RunWindowsPowerShell(
            "Import-Module Pester -ErrorAction SilentlyContinue; if (-not (Get-Command Invoke-Pester -ErrorAction SilentlyContinue)) { 'NO-PESTER'; exit 0 }; " +
            $"$r = Invoke-Pester {Quote(tests)} -PassThru 6>$null 3>$null; " +
            "if ($r.PSObject.Properties['TestResult']) { $r.TestResult | Where-Object { -not $_.Passed } | ForEach-Object { \"FAIL $($_.Context) / $($_.Name): $($_.FailureMessage)\" } } " +
            "else { $r.Failed | ForEach-Object { \"FAIL $($_.ExpandedPath): $($_.ErrorRecord)\" } }; " +
            "\"RESULT $($r.PassedCount) $($r.FailedCount)\"",
            timeoutSeconds: 600);
        if (output.Trim() == "NO-PESTER")
        {
            Assert.Skip("Pester is not installed for Windows PowerShell.");
        }

        Assert.True(exit == 0, output);
        Match result = Regex.Match(output, @"RESULT (\d+) (\d+)");
        Assert.True(result.Success, output);
        Assert.True(int.Parse(result.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) == 0, output);
        // Guards against a suite that silently stopped discovering its tests (43 under Pester 3.4 at MN-239).
        Assert.True(int.Parse(result.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 42, output);
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void Manifest_WrittenByTheScript_IsAnExecutableManifest_ListingEveryLockedFile()
    {
        string lockPath = RepoFile("deploy", "agent", "windows", "llama-cpp.lock.json");
        (int exit, string output) = RunWindowsPowerShell(
            LoadInstallerFunctions() + "$ExecutableName = 'llama-server'; " +
            $"ConvertTo-Json -InputObject (New-ExecutableManifest (Read-LlamaCppLock {Quote(lockPath)})) -Depth 20");
        Assert.True(exit == 0, output);

        ExecutableManifest manifest = JsonSerializer.Deserialize<ExecutableManifest>(output, WireStrict)!;
        LlamaCppLockFile lockFile = JsonSerializer.Deserialize<LlamaCppLockFile>(File.ReadAllText(lockPath), WireStrict)!;
        Assert.Equal((1, "llama-server", lockFile.Version, lockFile.Cuda), (manifest.SchemaVersion, manifest.Name, manifest.Version, manifest.Cuda));
        Assert.Equal(lockFile.Assets.Select(a => a.Name), manifest.Sources);
        Assert.Equal(lockFile.Files.Select(f => new ExecutableManifestFile(f.Path, f.Sha256, f.Bytes)), manifest.Files);
        Assert.Equal(ExecutableNames.LlamaServer, manifest.Name);
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void AgentJson_WrittenAndRegisteredByTheScript_BindsWithAgentOptions()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-mn216-config-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "agent.json");
        string target = @"C:\Program Files\mirbadllm-agent\runtimes\llama.cpp\b11476";
        Directory.CreateDirectory(root);
        try
        {
            string lockPath = RepoFile("deploy", "agent", "windows", "llama-cpp.lock.json");
            (int exit, string output) = RunWindowsPowerShell(
                LoadInstallerFunctions() +
                $"$ConfigPath = {Quote(config)}; $StateDir = {Quote(Path.Combine(root, "state"))}; $DryRun = $false; $ExecutableName = 'llama-server'; " +
                @"$ModelsRoot = 'E:\models'; $CacheRoot = 'E:\cache'; " +
                $"function Get-ExampleConfigPath {{ {Quote(RepoFile("deploy", "agent", "agent.windows.example.json"))} }}; " +
                $"Write-InitialConfig 'win-7'; $lock = Read-LlamaCppLock {Quote(lockPath)}; " +
                // Twice: a re-run keeps one entry.
                $"Register-LlamaCppExecutable $lock {Quote(target)}; Register-LlamaCppExecutable $lock {Quote(target)}; 'ok'");
            Assert.True(exit == 0, output);

            byte[] bytes = File.ReadAllBytes(config);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "agent.json must have no BOM");

            AgentOptions options = AgentOptions.FromConfiguration(new ConfigurationBuilder().AddJsonFile(config, optional: false, reloadOnChange: false).Build());
            Assert.Equal("win-7", options.NodeId);
            Assert.Equal(@"E:\models", options.ModelsRoot);
            Assert.Equal(Path.Combine(root, "state"), options.StateDirectory);
            Assert.False(options.Docker.Enabled);
            ExecutableOptions executable = Assert.Single(options.Executables);
            Assert.Equal(("llama-server", "b11476"), (executable.Name, executable.Default));
            ExecutableVersionOptions version = Assert.Single(executable.Versions);
            Assert.Equal(("b11476", "13.4"), (version.Version, version.Cuda));
            Assert.Equal(target + @"\llama-server.exe", version.Path);
            Assert.Equal(target + @"\manifest.json", version.Manifest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowsExample_TurnsDockerOff_ListsThePinnedLlamaCpp_AndTheProcessDefaults()
    {
        AgentOptions options = AgentOptions.FromConfiguration(
            new ConfigurationBuilder().AddJsonFile(RepoFile("deploy", "agent", "agent.windows.example.json"), optional: false, reloadOnChange: false).Build());
        LlamaCppLockFile lockFile = JsonSerializer.Deserialize<LlamaCppLockFile>(
            File.ReadAllText(RepoFile("deploy", "agent", "windows", "llama-cpp.lock.json")), WireStrict)!;

        Assert.False(options.Docker.Enabled);
        Assert.False(options.ManageContainers);
        Assert.False(options.HostdExpected);

        ExecutableOptions executable = Assert.Single(options.Executables);
        Assert.Equal((ExecutableNames.LlamaServer, lockFile.Version), (executable.Name, executable.Default));
        ExecutableVersionOptions version = Assert.Single(executable.Versions);
        string folder = $@"C:\Program Files\mirbadllm-agent\runtimes\llama.cpp\{lockFile.Version}\";
        Assert.Equal((lockFile.Version, lockFile.Cuda, folder + "llama-server.exe", folder + "manifest.json"), (version.Version, version.Cuda, version.Path, version.Manifest));

        var defaults = new AgentProcessOptions();
        Assert.Equal(
            (defaults.StartTimeout, defaults.RestartBudget, defaults.RestartWindow, defaults.LogMaxBytes, defaults.LogFilesKept, defaults.HealthPollInterval),
            (options.Process.StartTimeout, options.Process.RestartBudget, options.Process.RestartWindow, options.Process.LogMaxBytes, options.Process.LogFilesKept, options.Process.HealthPollInterval));

        // The example's "Process" section names every option, so a renamed one fails here instead of binding nothing.
        JsonNode process = JsonNode.Parse(File.ReadAllText(RepoFile("deploy", "agent", "agent.windows.example.json")))!["Agent"]!["Process"]!;
        Assert.Equal(typeof(AgentProcessOptions).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal), process.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- MN-239 C1/C2: the node id in agent.json

    [Fact]
    public void NodeId_IsCheckedWithTheAgentsOwnRule_CaseSensitively_BeforeAnythingIsInstalled()
    {
        string script = Script;

        // The same rule, not an approximation of it: the literal is Protocol.NodeIdPattern and it is matched with
        // CultureInvariant alone, so the match is case-sensitive like AgentOptions.Validate's.
        string test = FunctionText(script, "Test-NodeId");
        Assert.Contains($"'{Protocol.NodeIdPattern}'", test, StringComparison.Ordinal);
        Assert.Contains("[regex]::IsMatch($Value,", test, StringComparison.Ordinal);
        Assert.Contains("[Text.RegularExpressions.RegexOptions]::CultureInvariant", test, StringComparison.Ordinal);
        Assert.DoesNotContain("IgnoreCase", test, StringComparison.Ordinal);

        // The parameter must carry no [ValidatePattern]: PowerShell's defaults to RegexOptions.IgnoreCase, which is how
        // the machine name 'DevelopmentPC' reached Agent:NodeId and the agent then exited code 2 before it could log
        // (the SCM reported error 1053 with an empty Application log).
        string param = Regex.Match(script, @"param\((?<p>.*?)\n\)", RegexOptions.Singleline).Groups["p"].Value;
        Assert.NotEmpty(param);
        Assert.DoesNotMatch(new Regex(@"\[ValidatePattern\([^\n]*\r?\n(\s*#[^\n]*\r?\n)*\s*\[string\] \$NodeId"), param);

        // Checked in the main part, above the verb switch: nothing is installed, enrolled or started first.
        int guard = script.IndexOf("if ($BoundParameters.ContainsKey('NodeId')) { Assert-NodeId $NodeId }", StringComparison.Ordinal);
        int main = Regex.Match(script, @"^# -+ main\r?$", RegexOptions.Multiline).Index;
        int dispatch = script.IndexOf("switch ($action)", StringComparison.Ordinal);
        Assert.True(guard > main && guard < dispatch, "-NodeId is checked in the main part, before the verb runs");
        Assert.Contains("Assert-NodeId $Node", FunctionText(script, "Write-InitialConfig"), StringComparison.Ordinal);

        // $PSBoundParameters inside a function is that function's: the script's copy is what tells an explicitly passed
        // setting from a defaulted one (MN-239 C2).
        Assert.Contains("$BoundParameters = $PSBoundParameters", script, StringComparison.Ordinal);
        Assert.Contains("$BoundParameters.ContainsKey('NodeId')", FunctionText(script, "Update-ExistingConfig"), StringComparison.Ordinal);
        Assert.Contains("$BoundParameters.ContainsKey($Parameter)", FunctionText(script, "Update-ConfigSetting"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void FirstInstall_RefusesANodeIdTheAgentRefuses_NamesTheValueAndTheFix_AndWritesNoAgentJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-mn239-nodeid-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "agent.json");
        Directory.CreateDirectory(root);
        try
        {
            (int exit, string output) = RunWindowsPowerShell(
                LoadInstallerFunctions() + NodeIdLayout(root) +
                "$BoundParameters = @{ NodeId = 'DevelopmentPC' }; " +
                "try { Write-InitialConfig 'DevelopmentPC'; 'NO-ERROR' } catch { $_.Exception.Message }; " +
                $"\"written={{0}}\" -f (Test-Path -LiteralPath {Quote(config)})");
            Assert.True(exit == 0, output);

            // powershell.exe wraps a long line at the console width: compare the message as one run of words.
            string message = Regex.Replace(output, @"\s+", " ");
            Assert.DoesNotContain("NO-ERROR", message, StringComparison.Ordinal);
            Assert.Contains("'DevelopmentPC' is not a node id", message, StringComparison.Ordinal);
            // The message names the fix (the slug) and the symptom the owner saw, so the next operator can match them.
            Assert.Contains("-NodeId developmentpc", message, StringComparison.Ordinal);
            Assert.Contains("error 1053", message, StringComparison.Ordinal);
            Assert.Contains("written=False", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Os", "Windows")]
    public void ASecondInstall_AppliesAnExplicitNodeId_KeepsADefaultedOne_AndLeavesABindableAgentJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-mn239-second-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "agent.json");
        Directory.CreateDirectory(root);
        try
        {
            (int exit, string output) = RunWindowsPowerShell(
                LoadInstallerFunctions() + NodeIdLayout(root) +
                // Run 1 wrote a valid id; run 2 passes another one explicitly; run 3 only defaults to a third.
                "$BoundParameters = @{ NodeId = 'win-1' }; Write-InitialConfig 'win-1'; " +
                "$BoundParameters = @{ NodeId = 'devpc' }; Write-InitialConfig 'devpc'; " +
                "$BoundParameters = @{}; Write-InitialConfig 'developmentpc'; 'ok'");
            Assert.True(exit == 0, output);
            Assert.Contains("Updating", output, StringComparison.Ordinal);
            Assert.Contains("Keeping existing", output, StringComparison.Ordinal);

            // Before MN-239 the second run printed "Keeping existing agent.json" and the file stayed 'win-1' while the
            // node enrolled as 'devpc'; the third must not follow a value the operator never passed.
            AgentOptions options = AgentOptions.FromConfiguration(
                new ConfigurationBuilder().AddJsonFile(config, optional: false, reloadOnChange: false).Build());
            Assert.Equal("devpc", options.NodeId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- MN-218's bundle line (powershell.exe -File)

    [Theory]
    [Trait("Os", "Windows")]
    [InlineData("install", "-NodeId|win-1|-EnrollUrl|https://192.0.2.191:8443|-CaHash|sha256:abc|-LlamaCpp", "install|win-1||https://192.0.2.191:8443/|sha256:abc|True|False")]
    [InlineData("install", "-NodeId|win-1|-EnrollUrl|https://192.0.2.191:8443|-CaHash|sha256:abc|-LlamaCpp|-PromptToken", "install|win-1||https://192.0.2.191:8443/|sha256:abc|True|True")]
    [InlineData("rekey", "-NodeId|win-1|-NodeUid|u-42|-EnrollUrl|https://192.0.2.191:8443|-CaHash|sha256:abc", "rekey|win-1|u-42|https://192.0.2.191:8443/|sha256:abc|False|False")]
    public void BundleLine_BindsTheArguments_AndFindsTheBundleFilesNextToTheScript(string verb, string arguments, string expected)
    {
        // The unpacked win-x64 bundle (MN-218): one folder with the exe, the script, the example and the lock file. The
        // script is cut before its main part (definitions only, so nothing runs that could change this PC) and ends with
        // a probe instead; it is started exactly as the line starts it.
        string root = Path.Combine(Path.GetTempPath(), "ss-mn216-bundle-" + Guid.NewGuid().ToString("N"));
        string bundle = Path.Combine(root, "mirbadllm-agent-1.2.3-win-x64");
        Directory.CreateDirectory(bundle);
        try
        {
            string script = Script;
            int main = Regex.Match(script, @"^# -+ main\r?$", RegexOptions.Multiline).Index;
            Assert.True(main > 0, "main marker not found");
            File.WriteAllText(Path.Combine(bundle, "install-agent.ps1"), script[..main].Replace("#Requires -RunAsAdministrator", "", StringComparison.Ordinal) +
                "$work = Join-Path $env:TEMP ([guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $work | Out-Null\n" +
                "@($Verb, $NodeId, $NodeUid, $EnrollUrl.AbsoluteUri, $CaHash, [bool]$LlamaCpp, [bool]$PromptToken) -join '|'\n" +
                "Get-ExampleConfigPath; $LockPath; Get-AgentSourceDir $work\n" +
                "Remove-Item -LiteralPath $work -Recurse -Force\n");
            File.WriteAllText(Path.Combine(bundle, "mirbadllm-agent.exe"), "fake");
            File.Copy(RepoFile("deploy", "agent", "agent.windows.example.json"), Path.Combine(bundle, "agent.windows.example.json"));
            File.Copy(RepoFile("deploy", "agent", "windows", "llama-cpp.lock.json"), Path.Combine(bundle, "llama-cpp.lock.json"));

            (int exit, string output) = RunWindowsPowerShellFile(Path.Combine(bundle, "install-agent.ps1"), [verb, .. arguments.Split('|')]);
            Assert.True(exit == 0, output);
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal([expected, Path.Combine(bundle, "agent.windows.example.json"), Path.Combine(bundle, "llama-cpp.lock.json"), bundle], lines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [Trait("Os", "Windows")]
    [InlineData("install|-NodeId|win-1|-JoinToken|abc", "JoinToken")]
    [InlineData("deploy", "Verb")]
    [InlineData("install|-LlamaCpp|latest", "LlamaCppVersion")]
    [InlineData("install|b11476", "needs -LlamaCpp")]
    [InlineData("rekey|-NodeId|win-1|-NodeUid|u-42", "rekey needs -EnrollUrl and -CaHash")]
    [InlineData("-ImportGguf|x.gguf", "needs -Folder")]
    [InlineData("upgrade|-LlamaCpp", "not with upgrade")]
    // A wildcard would grant rights on whichever service Get-Service matched first (MN-230).
    [InlineData("-AllowService|Sp*", "AllowService")]
    [InlineData("-AllowServiceAutostart", "needs -AllowService")]
    public void Script_ExitsNonZero_OnAFailure(string arguments, string message)
    {
        // The line checks $LASTEXITCODE: every failure must end powershell.exe -File with a non-zero code. Each case fails
        // before the script changes anything (parameter binding, the argument checks, rekey's first check).
        string root = Path.Combine(Path.GetTempPath(), "ss-mn216-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Without #Requires, so the check is the same on an elevated and a non-elevated runner.
            string copy = Path.Combine(root, "install-agent.ps1");
            File.WriteAllText(copy, Script.Replace("#Requires -RunAsAdministrator", "", StringComparison.Ordinal));
            (int exit, string output) = RunWindowsPowerShellFile(copy, arguments.Split('|'));
            Assert.True(exit != 0, $"exit 0 for '{arguments}':\n{output}");
            Assert.Contains(message, output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Runs a script file as the bundle line does (<c>powershell.exe -NoProfile -ExecutionPolicy Bypass -File</c>).</summary>
    private static (int Exit, string Output) RunWindowsPowerShellFile(string script, string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows PowerShell 5.1 exists only on Windows.");
        }

        string exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(exe))
        {
            Assert.Skip($"No Windows PowerShell at {exe}.");
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in (string[])["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. arguments])
        {
            start.ArgumentList.Add(a);
        }

        start.Environment.Remove("PSModulePath");
        start.Environment.Remove("MIRBADLLM_JOIN_TOKEN");
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("powershell.exe did not finish within 120 s.");
        }

        return (process.ExitCode, process.ExitCode == 0 ? stdout.Result : stdout.Result + stderr.Result);
    }

    /// <summary>A command prefix that defines every top-level function of install-agent.ps1 (without running its main part).</summary>
    private static string LoadInstallerFunctions() =>
        "$ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest; $e = $null; " +
        $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quote(RepoFile("deploy", "agent", "windows", "install-agent.ps1"))}, [ref]$null, [ref]$e); " +
        "foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) { . ([scriptblock]::Create($fn.Extent.Text)) }; ";

    /// <summary>The installer's configuration variables pointed at a throwaway tree, with the Windows example as template.</summary>
    private static string NodeIdLayout(string root) =>
        $"$ConfigPath = {Quote(Path.Combine(root, "agent.json"))}; $StateDir = {Quote(Path.Combine(root, "state"))}; " +
        $"$ModelsRoot = {Quote(Path.Combine(root, "models"))}; $CacheRoot = {Quote(Path.Combine(root, "cache"))}; $DryRun = $false; " +
        $"function Get-ExampleConfigPath {{ {Quote(RepoFile("deploy", "agent", "agent.windows.example.json"))} }}; ";

    private static void AssertAcl(JsonNode acl, bool isProtected, int rules, int inherited)
    {
        Assert.Equal(isProtected, acl["protected"]!.GetValue<bool>());
        Assert.Equal(rules, acl["rules"]!.GetValue<int>());
        Assert.Equal(inherited, acl["inherited"]!.GetValue<int>());
    }

    private static string FunctionText(string script, string name)
    {
        Match m = Regex.Match(script, $@"function {Regex.Escape(name)}\b.*?\n\}}", RegexOptions.Singleline);
        Assert.True(m.Success, $"function {name} not found");
        return m.Value.Replace("\r", "", StringComparison.Ordinal);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Runs a command in Windows PowerShell 5.1 (<c>powershell.exe</c>); skips the test where there is none.</summary>
    private static (int Exit, string Output) RunWindowsPowerShell(string command, int timeoutSeconds = 120)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows PowerShell 5.1 exists only on Windows.");
        }

        string exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(exe))
        {
            Assert.Skip($"No Windows PowerShell at {exe}.");
        }

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'; " + command)) },
        };
        // A PSModulePath inherited from PowerShell 7 makes 5.1 load 7's modules (Get-Acl then fails to load).
        start.Environment.Remove("PSModulePath");
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"powershell.exe did not finish within {timeoutSeconds} s.");
        }

        // stderr carries CLIXML progress records even on success: only stdout is the answer.
        return (process.ExitCode, process.ExitCode == 0 ? stdout.Result : stdout.Result + stderr.Result);
    }

    private static string RepoFile(params string[] parts) => Path.Combine([RepoRoot(), .. parts]);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MirbadLLM.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("MirbadLLM.slnx not found above the test binaries.");
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();
}
