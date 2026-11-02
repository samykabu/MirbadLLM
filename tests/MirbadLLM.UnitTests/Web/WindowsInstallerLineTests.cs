using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Auth;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The Windows join and rekey lines (<see cref="WindowsInstallerLine"/>, design-p3p4 §A.4.5/§A.4.6, MN-218): the exact
/// line, the prompt variant without any secret, the token cleared <b>inside</b> the <c>finally</c> (spike S4 / F5), never a
/// <c>-JoinToken</c> argument, the bundle's SHA-256 in the line, the round trip through the response's parts (D98), the
/// master's join-token API filling <see cref="JoinCommands.Windows"/> without changing the Linux line, and, on Windows,
/// the line parsed by Windows PowerShell 5.1 and run for real against the master's bundle endpoint (download, hash check,
/// unpack, installer, cleanup, also when the hash check or the installer fails).
/// </summary>
public sealed partial class WindowsInstallerLineTests : IDisposable
{
    private const string CaHash = "sha256:q3vYc2ZpeHR1cmUtY2EtaGFzaA+/=";
    private const string Token = "abc_DEF-123xyz";
    private const string Enroll = "https://192.0.2.191:8443";
    private const string WinUrl = "http://192.0.2.191:8080/agent/bundles/win-x64.zip";

    private static readonly string WinSha = Convert.ToHexStringLower(SHA256.HashData("win"u8));

    private static readonly JsonSerializerOptions Json = CreateJson();

    private static readonly Regex AntiforgeryTokenPattern = new("""name="__RequestVerificationToken" value="([^"]+)""", RegexOptions.Compiled);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ss-mn218 line " + Guid.NewGuid().ToString("N")[..12]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    // ---- the line ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Join_IsTheDesignsLine_WithTheTokenClearedInsideTheFinally()
    {
        WindowsInstallerLine line = WindowsInstallerLine.Join("pc-1", Enroll, CaHash, Download())!;

        string expected =
            "$env:MIRBADLLM_JOIN_TOKEN='abc_DEF-123xyz'; & { $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
            "$d = Join-Path $env:TEMP ([guid]::NewGuid()); New-Item -ItemType Directory $d | Out-Null; " +
            "try { Invoke-WebRequest -UseBasicParsing 'http://192.0.2.191:8080/agent/bundles/win-x64.zip' -OutFile \"$d\\agent.zip\"; " +
            $"if ((Get-FileHash \"$d\\agent.zip\" -Algorithm SHA256).Hash -ne '{WinSha}') {{ throw 'agent bundle hash mismatch' }}; " +
            "Expand-Archive \"$d\\agent.zip\" $d; " +
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Get-ChildItem \"$d\\mirbadllm-agent-*\\install-agent.ps1\").FullName " +
            $"install -NodeId 'pc-1' -EnrollUrl '{Enroll}' -CaHash '{CaHash}' -LlamaCpp; " +
            "if ($LASTEXITCODE -ne 0) { throw \"install-agent.ps1 install failed (exit $LASTEXITCODE)\" } " +
            "} finally { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue; Remove-Item Env:MIRBADLLM_JOIN_TOKEN -ErrorAction SilentlyContinue } }";

        Assert.Equal(expected, line.WithToken(Token));
        Assert.Equal("install", line.Verb);
        Assert.Equal(WinUrl, line.BundleUrl);
        Assert.Equal(WinSha, line.BundleSha256);
    }

    [Fact]
    public void Prompt_IsTheSameLine_WithoutTheTokenStatement_AndWithPromptToken()
    {
        WindowsInstallerLine line = WindowsInstallerLine.Join("pc-1", Enroll, CaHash, Download())!;
        string inline = line.WithToken(Token);
        string prompt = line.Prompt();

        Assert.DoesNotContain(Token, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("$env:MIRBADLLM_JOIN_TOKEN=", prompt, StringComparison.Ordinal);
        Assert.StartsWith("& { ", prompt, StringComparison.Ordinal);
        string tokenStatement = $"$env:MIRBADLLM_JOIN_TOKEN='{Token}'; ";
        Assert.StartsWith(tokenStatement, inline, StringComparison.Ordinal);
        Assert.Equal(inline[tokenStatement.Length..].Replace(" -LlamaCpp; ", " -LlamaCpp -PromptToken; ", StringComparison.Ordinal), prompt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryVariant_ClearsTheToken_InsideTheFinally_AndNeverPassesItAsAnArgument(bool rekey, bool prompt)
    {
        WindowsInstallerLine line = Line(rekey);
        string text = prompt ? line.Prompt() : line.WithToken(Token);

        (string body, string rest) = FinallyBlock(text);
        Assert.Contains("Remove-Item Env:MIRBADLLM_JOIN_TOKEN -ErrorAction SilentlyContinue", body, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -Recurse -Force $d", body, StringComparison.Ordinal);
        // Nothing runs after the finally but the end of the script block: a trailing statement never runs after a throw.
        Assert.Equal(" }", rest);
        Assert.DoesNotContain("JoinToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(prompt ? 0 : 1, Regex.Count(text, Regex.Escape(Token)));
        Assert.Equal(prompt, text.Contains(" -PromptToken", StringComparison.Ordinal));
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", text, StringComparison.Ordinal);
        // The check comes before the unpack, the unpack before the installer.
        Assert.True(text.IndexOf("Get-FileHash", StringComparison.Ordinal) < text.IndexOf("Expand-Archive", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Expand-Archive", StringComparison.Ordinal) < text.IndexOf("install-agent.ps1\").FullName", StringComparison.Ordinal));
        Assert.Contains($"-ne '{WinSha}'", text, StringComparison.Ordinal);
        Assert.True(text.All(c => c is >= ' ' and <= '~'), "one printable ASCII line");
    }

    [Fact]
    public void Rekey_RunsTheRekeyVerb_WithTheUid_AndNoLlamaCppInstall()
    {
        WindowsInstallerLine line = Line(rekey: true);

        Assert.Equal("rekey", line.Verb);
        Assert.Equal($"-NodeId 'pc-1' -NodeUid 'uid-42' -EnrollUrl '{Enroll}' -CaHash '{CaHash}'", line.Arguments);
        Assert.Contains($"install-agent.ps1\").FullName rekey {line.Arguments}; ", line.WithToken(Token), StringComparison.Ordinal);
        Assert.Contains($"install-agent.ps1\").FullName rekey {line.Arguments} -PromptToken; ", line.Prompt(), StringComparison.Ordinal);
        Assert.Contains("throw \"install-agent.ps1 rekey failed (exit $LASTEXITCODE)\"", line.Prompt(), StringComparison.Ordinal);
        Assert.DoesNotContain("-LlamaCpp", line.WithToken(Token), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAUsableWindowsBundle_ThereIsNoLine()
    {
        AgentBundleLink linux = new("linux-x64", "http://m:8080/agent/bundles/linux-x64.tar.gz", new string('a', 64), "v");
        AgentBundleLink win = new("win-x64", WinUrl, WinSha, "v");

        Assert.Null(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, null));
        Assert.Null(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, new AgentDownload { Bundles = [] }));
        Assert.Null(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, new AgentDownload { Bundles = [linux] }));
        Assert.Null(WindowsInstallerLine.Rekey("pc-1", "u", Enroll, CaHash, new AgentDownload { Bundles = [linux] }));
        Assert.Null(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, new AgentDownload { Bundles = [win, win] }));
        Assert.Null(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, new AgentDownload { Bundles = [win with { Sha256 = "NOT-A-HASH" }] }));
        Assert.Null(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, new AgentDownload { Bundles = [win with { Sha256 = WinSha.ToUpperInvariant() }] }));
        Assert.Null(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, new AgentDownload { Bundles = [win with { Url = "ftp://m/win-x64.zip" }] }));
        Assert.Null(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, new AgentDownload { Bundles = [win with { Url = "agent/bundles/win-x64.zip" }] }));
        Assert.NotNull(WindowsInstallerLine.Join("pc-1", Enroll, CaHash, new AgentDownload { Bundles = [linux, win] }));
    }

    [Fact]
    public void QuotesInAValue_StayInsideTheirSingleQuotes()
    {
        var download = new AgentDownload { Bundles = [new AgentBundleLink("win-x64", "http://m:8080/it's/’x/agent/bundles/win-x64.zip", WinSha, "v")] };

        string text = WindowsInstallerLine.Join("pc-1", Enroll, CaHash, download)!.Prompt();

        Assert.Contains("Invoke-WebRequest -UseBasicParsing 'http://m:8080/it''s/’’x/agent/bundles/win-x64.zip' -OutFile", text, StringComparison.Ordinal);
        Assert.Equal("'a''b'", WindowsInstallerLine.Quoted("a'b"));
        Assert.Equal("'‘‘‚‚‛‛'", WindowsInstallerLine.Quoted("‘‚‛"));
        Assert.Throws<ArgumentException>(() => WindowsInstallerLine.Quoted("a\nb"));
        Assert.Throws<ArgumentException>(() => WindowsInstallerLine.Join("pc-1", Enroll + "\r\n", CaHash, Download()));
        Assert.Throws<ArgumentException>(() => WindowsInstallerLine.Join("pc-1", Enroll, CaHash, Download())!.WithToken(""));
    }

    [Fact]
    public void Of_RebuildsTheMastersLine_AndRefusesAnotherOne()
    {
        AgentDownload download = Download();
        JoinTokenCreateResponse join = Response(download, rekey: false);
        JoinTokenCreateResponse rekey = Response(download, rekey: true);

        WindowsInstallerLine? rebuilt = WindowsInstallerLine.Of(join, null);
        Assert.NotNull(rebuilt);
        Assert.Equal(join.Commands.Windows, rebuilt.WithToken(join.Token));
        Assert.DoesNotContain(Token, rebuilt.Prompt(), StringComparison.Ordinal);
        Assert.Equal("rekey", WindowsInstallerLine.Of(rekey, "uid-42")!.Verb);

        Assert.Null(WindowsInstallerLine.Of(rekey, null));          // a rekey line needs the uid it carries
        Assert.Null(WindowsInstallerLine.Of(rekey, "uid-other"));
        Assert.Null(WindowsInstallerLine.Of(join with { Commands = join.Commands with { Windows = null } }, null));
        Assert.Null(WindowsInstallerLine.Of(join with { AgentDownload = null }, null));
        AgentDownload other = new() { Bundles = [download.Bundles[0], download.Bundles[1], download.Bundles[2] with { Sha256 = new string('c', 64) }] };
        Assert.Null(WindowsInstallerLine.Of(join with { AgentDownload = other }, null));
        Assert.Null(WindowsInstallerLine.Of(join with { Commands = join.Commands with { Windows = join.Commands.Windows + " " } }, null));

        // The Linux line of the same response still rebuilds (it ignores the win-x64 entry).
        Assert.NotNull(LinuxInstallerLine.Of(join, null));
    }

    [Fact]
    public void TheLinuxLines_AreUnchanged_ByTheWindowsEntry()
    {
        AgentDownload withWindows = Download();
        AgentDownload linuxOnly = new() { Bundles = [withWindows.Bundles[0], withWindows.Bundles[1]] };

        foreach ((LinuxInstallerLine a, LinuxInstallerLine b) in new[]
        {
            (LinuxInstallerLine.Join("pc-1", Enroll, CaHash, withWindows), LinuxInstallerLine.Join("pc-1", Enroll, CaHash, linuxOnly)),
            (LinuxInstallerLine.Rekey("pc-1", "u1", Enroll, CaHash, withWindows), LinuxInstallerLine.Rekey("pc-1", "u1", Enroll, CaHash, linuxOnly)),
        })
        {
            Assert.True(a.DownloadsAgent);
            Assert.Equal(b.WithToken(Token), a.WithToken(Token));
            Assert.Equal(b.Prompt(), a.Prompt());
            Assert.DoesNotContain("win-x64", a.WithToken(Token), StringComparison.Ordinal);
        }

        // Only a Windows bundle: the Linux line is the pre-bundle one, as with no bundles at all.
        AgentDownload windowsOnly = new() { Bundles = [withWindows.Bundles[2]] };
        Assert.Equal(LinuxInstallerLine.Join("pc-1", Enroll, CaHash).WithToken(Token), LinuxInstallerLine.Join("pc-1", Enroll, CaHash, windowsOnly).WithToken(Token));
    }

    // ---- the master's join-token API ----------------------------------------------------------------------------------

    [Fact]
    public async Task JoinTokenApi_FillsTheMacLine_FromTheServedTarball_AndKeepsTheOtherLines()
    {
        string bundles = Path.Combine(_root, "bundles");
        AgentBundleFixtures.Write(bundles, "linux-arm64", version: "0123456789ab");
        WindowsBundleFixtures.Write(bundles, version: "0123456789ab");
        string mac = AgentBundleFixtures.Write(bundles, "osx-arm64", version: "0123456789ab");

        JoinTokenCreateResponse response = await CreateJoinTokenAsync(bundles);

        Assert.Equal(["linux-arm64", "win-x64", "osx-arm64"], response.AgentDownload!.Bundles.Select(b => b.Rid));
        Assert.Equal(new AgentBundleLink("osx-arm64", "http://localhost/agent/bundles/osx-arm64.tar.gz", mac, "0123456789ab"), response.AgentDownload.Bundles[2]);
        Assert.Equal(MacosInstallerLine.Join("pc-office", response.EnrollUrl, response.CaHash, response.AgentDownload)!.WithToken(response.Token), response.Commands.Macos);
        Assert.Contains($"echo \"{mac}  agent.tar.gz\" | shasum -a 256 -c -", response.Commands.Macos, StringComparison.Ordinal);
        Assert.Equal(LinuxInstallerLine.Join("pc-office", response.EnrollUrl, response.CaHash, response.AgentDownload).WithToken(response.Token), response.Commands.Linux);
        Assert.NotNull(response.Commands.Windows);
        Assert.NotNull(MacosInstallerLine.Of(response, null));
    }


    [Fact]
    public async Task JoinTokenApi_FillsTheWindowsLine_FromTheServedZip_AndKeepsTheLinuxLine()
    {
        string bundles = Path.Combine(_root, "bundles");
        AgentBundleFixtures.Write(bundles, "linux-x64", version: "0123456789ab");
        AgentBundleFixtures.Write(bundles, "linux-arm64", version: "0123456789ab");
        string win = WindowsBundleFixtures.Write(bundles, version: "0123456789ab");

        JoinTokenCreateResponse withWindows = await CreateJoinTokenAsync(bundles);

        Assert.Equal(["linux-x64", "linux-arm64", "win-x64"], withWindows.AgentDownload!.Bundles.Select(b => b.Rid));
        AgentBundleLink link = withWindows.AgentDownload.Bundles[2];
        Assert.Equal(new AgentBundleLink("win-x64", "http://localhost/agent/bundles/win-x64.zip", win, "0123456789ab"), link);
        string expected = WindowsInstallerLine.Join("pc-office", withWindows.EnrollUrl, withWindows.CaHash, withWindows.AgentDownload)!.WithToken(withWindows.Token);
        Assert.Equal(expected, withWindows.Commands.Windows);
        Assert.Contains($"-ne '{win}'", withWindows.Commands.Windows, StringComparison.Ordinal);
        Assert.Contains("install -NodeId 'pc-office' -EnrollUrl 'https://localhost:8443' -CaHash ", withWindows.Commands.Windows, StringComparison.Ordinal);
        Assert.NotNull(WindowsInstallerLine.Of(withWindows, null));

        // The Linux line is the one this master gave before the Windows bundle existed.
        File.Delete(Path.Combine(bundles, "win-x64.zip"));
        JoinTokenCreateResponse linuxOnly = await CreateJoinTokenAsync(bundles);
        Assert.Null(linuxOnly.Commands.Windows);
        // (Each master has its own CA and mints its own token: compare with both replaced.)
        Assert.Equal(Normalized(linuxOnly), Normalized(withWindows));
        Assert.Equal(
            LinuxInstallerLine.Join("pc-office", withWindows.EnrollUrl, withWindows.CaHash, new AgentDownload { Bundles = [.. withWindows.AgentDownload.Bundles.Take(2)] }).WithToken(withWindows.Token),
            withWindows.Commands.Linux);
        Assert.NotNull(LinuxInstallerLine.Of(withWindows, null));
    }

    // ---- Windows PowerShell 5.1 -----------------------------------------------------------------------------------------

    [Theory]
    [Trait("Os", "Windows")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EveryVariant_ParsesUnderWindowsPowerShell51_WithTheTokenRemovalInTheFinally(bool rekey, bool prompt)
    {
        WindowsInstallerLine line = Line(rekey);
        string text = prompt ? line.Prompt() : line.WithToken(Token);
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "line.txt");
        await File.WriteAllTextAsync(file, text, Ct);

        (int exit, string output) = await RunWindowsPowerShellAsync(
            $"$e = $null; $ast = [System.Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText('{file}'), [ref]$null, [ref]$e); " +
            "if ($e) { $e | ForEach-Object { $_.Message }; exit 1 }; " +
            "$try = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.TryStatementAst] }, $true)); " +
            "\"statements=$($ast.EndBlock.Statements.Count)\"; \"tries=$($try.Count)\"; " +
            "\"finally=$($try[0].Finally.Extent.Text)\"; \"version=$($PSVersionTable.PSVersion.Major)\"");

        Assert.True(exit == 0, $"the line does not parse under Windows PowerShell:\n{output}");
        string[] lines = output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(prompt ? "statements=1" : "statements=2", lines);
        Assert.Contains("tries=1", lines);
        Assert.Contains("version=5", lines);
        string finallyBlock = Assert.Single(lines, l => l.StartsWith("finally=", StringComparison.Ordinal));
        Assert.Contains("Remove-Item Env:MIRBADLLM_JOIN_TOKEN", finallyBlock, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Os", "Windows")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheLine_DownloadsChecksUnpacksAndRunsTheBundlesInstaller_ThenCleansUp(bool rekey, bool prompt)
    {
        await using BundleServer server = await BundleServer.StartAsync(_root, Ct);
        WindowsInstallerLine line = rekey
            ? WindowsInstallerLine.Rekey("pc-1", "uid-42", Enroll, CaHash, server.Download)!
            : WindowsInstallerLine.Join("pc-1", Enroll, CaHash, server.Download)!;

        LineRun run = await RunLineAsync(server, prompt ? line.Prompt() : line.WithToken(Token));

        Assert.True(run.Exit == 0, run.Output);
        Assert.Null(run.Error);
        string[] log = await File.ReadAllLinesAsync(server.InstallLog, Ct);
        Assert.Equal("folder=mirbadllm-agent-v9-win-x64", log[0]);
        string[] expectedArgs = rekey
            ? ["rekey", "-NodeId", "pc-1", "-NodeUid", "uid-42", "-EnrollUrl", Enroll, "-CaHash", CaHash]
            : ["install", "-NodeId", "pc-1", "-EnrollUrl", Enroll, "-CaHash", CaHash, "-LlamaCpp"];
        Assert.Equal(prompt ? [.. expectedArgs, "-PromptToken"] : expectedArgs, log.Where(l => l.StartsWith("arg=", StringComparison.Ordinal)).Select(l => l[4..]));
        Assert.Contains(prompt ? "token=unset" : $"token={Token}", log);
        Assert.Contains("version=5", log); // the installer runs under Windows PowerShell 5.1
        Assert.Equal("[]", run.TokenAfter);     // the window's environment no longer has the token
        Assert.Equal(0, run.TempEntriesAfter);  // the download folder is gone
    }

    [Theory]
    [Trait("Os", "Windows")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TheEmittedLine_BindsToTheRealInstallersParameters_AndHandsItTheToken(bool rekey, bool prompt)
    {
        // MN-219 (item 11): the line's verb, switches and named arguments bind to install-agent.ps1's own param block, and
        // the token reaches it under the variable it reads. The bundle carries the real script, cut before its main part
        // (definitions only: nothing that changes this PC runs) and ending with a probe of what was bound.
        string script = await File.ReadAllTextAsync(RealInstaller(), Ct);
        int main = Regex.Match(script, @"^# -+ main\r?$", RegexOptions.Multiline).Index;
        Assert.True(main > 0, "main marker not found");
        string probe = script[..main].Replace("#Requires -RunAsAdministrator", "", StringComparison.Ordinal) +
            "$t = [Environment]::GetEnvironmentVariable($TokenVariable)\r\n" +
            "$log = @(('folder=' + (Split-Path -Leaf $PSScriptRoot)), ('verb=' + $Verb), ('nodeId=' + $NodeId), ('nodeUid=' + $NodeUid),\r\n" +
            "  ('enrollUrl=' + $EnrollUrl.OriginalString), ('caHash=' + $CaHash), ('llamaCpp=' + [bool]$LlamaCpp), ('prompt=' + [bool]$PromptToken),\r\n" +
            "  ('tokenVariable=' + $TokenVariable), ('token=' + $(if ($t) { $t } else { 'unset' })))\r\n" +
            "Set-Content -LiteralPath $env:FAKE_INSTALL_LOG -Value $log -Encoding ASCII\r\n" +
            "exit [int]$env:FAKE_INSTALL_EXIT\r\n";
        await using BundleServer server = await BundleServer.StartAsync(_root, Ct, probe);
        WindowsInstallerLine line = rekey
            ? WindowsInstallerLine.Rekey("pc-1", "uid-42", Enroll, CaHash, server.Download)!
            : WindowsInstallerLine.Join("pc-1", Enroll, CaHash, server.Download)!;

        LineRun run = await RunLineAsync(server, prompt ? line.Prompt() : line.WithToken(Token));

        Assert.True(run.Exit == 0 && run.Error is null, run.Output);
        string[] log = await File.ReadAllLinesAsync(server.InstallLog, Ct);
        Assert.Equal(
            [
                "folder=mirbadllm-agent-v9-win-x64", "verb=" + (rekey ? "rekey" : "install"), "nodeId=pc-1",
                "nodeUid=" + (rekey ? "uid-42" : ""), "enrollUrl=" + Enroll, "caHash=" + CaHash,
                "llamaCpp=" + (rekey ? "False" : "True"), "prompt=" + (prompt ? "True" : "False"),
                "tokenVariable=" + WindowsInstallerLine.TokenVariable, "token=" + (prompt ? "unset" : Token),
            ],
            log);
        Assert.Equal("[]", run.TokenAfter);
    }

    private static string RealInstaller([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "deploy", "agent", "windows", "install-agent.ps1"));

    [Fact]
    [Trait("Os", "Windows")]
    public async Task ATamperedBundle_StopsBeforeTheUnpack_AndTheTokenIsStillCleared()
    {
        await using BundleServer server = await BundleServer.StartAsync(_root, Ct);
        string line = WindowsInstallerLine.Join("pc-1", Enroll, CaHash, server.Download)!.WithToken(Token);
        // The master's file changes after the line was built (or a proxy swaps it): the embedded hash no longer matches.
        WindowsBundleFixtures.Write(server.Served, version: "v9", installScript: BundleServer.FakeInstaller);

        LineRun run = await RunLineAsync(server, line);

        Assert.Equal("agent bundle hash mismatch", run.Error);
        Assert.False(File.Exists(server.InstallLog), "the installer ran despite the hash mismatch");
        Assert.Equal("[]", run.TokenAfter);     // spike S4 / F5: before the fix the token stayed in the window
        Assert.Equal(0, run.TempEntriesAfter);
    }

    [Fact]
    [Trait("Os", "Windows")]
    public async Task AFailingInstaller_EndsTheLineWithItsExitCode_AndTheTokenIsCleared()
    {
        await using BundleServer server = await BundleServer.StartAsync(_root, Ct);
        string line = WindowsInstallerLine.Join("pc-1", Enroll, CaHash, server.Download)!.WithToken(Token);

        LineRun run = await RunLineAsync(server, line, installerExit: 3);

        Assert.Equal("install-agent.ps1 install failed (exit 3)", run.Error);
        Assert.True(File.Exists(server.InstallLog));
        Assert.Equal("[]", run.TokenAfter);
        Assert.Equal(0, run.TempEntriesAfter);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static AgentDownload Download() => new()
    {
        Bundles =
        [
            new AgentBundleLink("linux-x64", "http://192.0.2.191:8080/agent/bundles/linux-x64.tar.gz", Convert.ToHexStringLower(SHA256.HashData("x64"u8)), "v9"),
            new AgentBundleLink("linux-arm64", "http://192.0.2.191:8080/agent/bundles/linux-arm64.tar.gz", Convert.ToHexStringLower(SHA256.HashData("arm"u8)), "v9"),
            new AgentBundleLink("win-x64", WinUrl, WinSha, "v9"),
        ],
    };

    private static WindowsInstallerLine Line(bool rekey) => rekey
        ? WindowsInstallerLine.Rekey("pc-1", "uid-42", Enroll, CaHash, Download())!
        : WindowsInstallerLine.Join("pc-1", Enroll, CaHash, Download())!;

    private static JoinTokenCreateResponse Response(AgentDownload download, bool rekey)
    {
        WindowsInstallerLine windows = rekey
            ? WindowsInstallerLine.Rekey("pc-1", "uid-42", Enroll, CaHash, download)!
            : WindowsInstallerLine.Join("pc-1", Enroll, CaHash, download)!;
        LinuxInstallerLine linux = rekey
            ? LinuxInstallerLine.Rekey("pc-1", "uid-42", Enroll, CaHash, download)
            : LinuxInstallerLine.Join("pc-1", Enroll, CaHash, download);
        return new JoinTokenCreateResponse
        {
            NodeId = "pc-1",
            Token = Token,
            CaHash = CaHash,
            EnrollUrl = Enroll,
            ExpiresAt = DateTimeOffset.UnixEpoch,
            Purpose = rekey ? JoinTokenPurpose.Rekey : JoinTokenPurpose.Join,
            Commands = new JoinCommands(linux.WithToken(Token), windows.WithToken(Token)),
            AgentDownload = download,
        };
    }

    /// <summary>
    /// The finally block's body of <paramref name="line"/> and the text after its closing brace, found by matching braces
    /// outside PowerShell string literals.
    /// </summary>
    private static (string Body, string After) FinallyBlock(string line)
    {
        int start = line.IndexOf("} finally {", StringComparison.Ordinal);
        Assert.True(start >= 0, "no finally block");
        int open = start + "} finally ".Length;
        int depth = 0;
        char? quote = null;
        for (int i = open; i < line.Length; i++)
        {
            char c = line[i];
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return (line[(open + 1)..i], line[(i + 1)..]);
            }
        }

        Assert.Fail("the finally block is not closed");
        return default;
    }

    private static async Task<JoinTokenCreateResponse> CreateJoinTokenAsync(string bundleDir)
    {
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        factory.Settings["MirbadLLM:NodeApi:AgentBundleDirectory"] = bundleDir;
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (HttpResponseMessage page = await client.GetAsync("/login", Ct))
        {
            page.EnsureSuccessStatusCode();
            Match match = AntiforgeryTokenPattern.Match(await page.Content.ReadAsStringAsync(Ct));
            Assert.True(match.Success);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = match.Groups[1].Value,
                ["password"] = "correct-password",
                ["returnUrl"] = "",
            });
            using HttpResponseMessage login = await client.PostAsync("/login/submit", form, Ct);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = "pc-office", Password = "correct-password" }, Json, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JoinTokenCreateResponse>(body, Json)!;
    }

    private static string Normalized(JoinTokenCreateResponse response) =>
        response.Commands.Linux.Replace(response.Token, "TOKEN", StringComparison.Ordinal).Replace(response.CaHash, "CAHASH", StringComparison.Ordinal);

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record LineRun(int Exit, string Output, string? Error, string TokenAfter, int TempEntriesAfter);

    /// <summary>
    /// Pastes <paramref name="line"/> as it is into a Windows PowerShell 5.1 session (a script file run with
    /// <c>-File</c>), with <c>$env:TEMP</c> in the sandbox (a path with a space), then reports the line's error, what is
    /// left of the token in the session's environment and of the temp folder.
    /// </summary>
    private async Task<LineRun> RunLineAsync(BundleServer server, string line, int installerExit = 0)
    {
        string temp = Path.Combine(_root, "temp dir");
        Directory.CreateDirectory(temp);
        string script = Path.Combine(_root, "paste.ps1");
        await File.WriteAllTextAsync(script,
            "try { " + line + " } catch { 'error=' + $_.Exception.Message }\r\n" +
            "'after=[' + $env:MIRBADLLM_JOIN_TOKEN + ']'\r\n" +
            "'temp=' + @(Get-ChildItem -Force $env:TEMP).Count\r\n", Ct);

        (int exit, string output) = await RunWindowsPowerShellAsync(null, script, start =>
        {
            start.Environment["TEMP"] = temp;
            start.Environment["TMP"] = temp;
            start.Environment["FAKE_INSTALL_LOG"] = server.InstallLog;
            start.Environment["FAKE_INSTALL_EXIT"] = installerExit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        string[] lines = output.ReplaceLineEndings("\n").Split('\n');
        string? error = lines.FirstOrDefault(l => l.StartsWith("error=", StringComparison.Ordinal))?[6..];
        string after = lines.Single(l => l.StartsWith("after=", StringComparison.Ordinal))[6..];
        int tempCount = int.Parse(lines.Single(l => l.StartsWith("temp=", StringComparison.Ordinal))[5..], System.Globalization.CultureInfo.InvariantCulture);
        return new LineRun(exit, output, error, after, tempCount);
    }

    private static async Task<(int Exit, string Output)> RunWindowsPowerShellAsync(string? command, string? file = null, Action<ProcessStartInfo>? configure = null)
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

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" })
        {
            start.ArgumentList.Add(arg);
        }

        if (file is not null)
        {
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(file);
        }
        else
        {
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command!)));
        }

        // A PSModulePath inherited from PowerShell 7 makes 5.1 load 7's modules; no token may leak in from the test's own environment.
        start.Environment.Remove("PSModulePath");
        start.Environment.Remove(WindowsInstallerLine.TokenVariable);
        configure?.Invoke(start);
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }

    /// <summary>
    /// The master's bundle routes (<see cref="AgentBundleEndpoints.MapAgentBundles"/>) on a real loopback port, serving a
    /// <c>win-x64.zip</c> whose <c>install-agent.ps1</c> records its folder, arguments, whether it saw the token and the
    /// PowerShell version, and exits with <c>$env:FAKE_INSTALL_EXIT</c>.
    /// </summary>
    private sealed class BundleServer : IAsyncDisposable
    {
        public const string FakeInstaller =
            "$log = @('folder=' + (Split-Path -Leaf $PSScriptRoot))\r\n" +
            "foreach ($a in $args) { $log += 'arg=' + $a }\r\n" +
            "$log += 'token=' + $(if (Test-Path Env:MIRBADLLM_JOIN_TOKEN) { $env:MIRBADLLM_JOIN_TOKEN } else { 'unset' })\r\n" +
            "$log += 'version=' + $PSVersionTable.PSVersion.Major\r\n" +
            "Set-Content -LiteralPath $env:FAKE_INSTALL_LOG -Value $log -Encoding ASCII\r\n" +
            "exit [int]$env:FAKE_INSTALL_EXIT\r\n";

        private readonly WebApplication _app;

        private BundleServer(WebApplication app, string served, string installLog, AgentDownload download)
        {
            _app = app;
            Served = served;
            InstallLog = installLog;
            Download = download;
        }

        public string Served { get; }

        public string InstallLog { get; }

        public AgentDownload Download { get; }

        public static async Task<BundleServer> StartAsync(string root, CancellationToken ct, string? installScript = null)
        {
            string served = Path.Combine(root, "served");
            WindowsBundleFixtures.Write(served, version: "v9", installScript: installScript ?? FakeInstaller);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.Configure<NodeApiOptions>(o => o.AgentBundleDirectory = served);
            builder.Services.AddMirbadLLMAgentBundles();
            WebApplication app = builder.Build();
            app.MapAgentBundles();
            await app.StartAsync(ct);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            AgentDownload download = app.Services.GetRequiredService<AgentBundleCatalog>().DownloadFrom(new Uri(address))!;
            return new BundleServer(app, served, Path.Combine(root, "install.log"), download);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}

/// <summary>
/// Windows agent bundles in the layout the master image's <c>agent-bundles</c> stage writes (design-p3p4 §A.4.5): one folder
/// <c>mirbadllm-agent-{version}-win-x64</c> with the exe, <c>install-agent.ps1</c>, the example configuration, the lock
/// file, the README and <c>BUILD-INFO</c>, for the catalog, endpoint and line tests.
/// </summary>
internal static class WindowsBundleFixtures
{
    /// <summary>Writes <c>{dir}/win-x64.zip</c>; returns its lower-case hex SHA-256.</summary>
    /// <param name="dir">The bundle folder.</param>
    /// <param name="version">The version in the folder name and BUILD-INFO.</param>
    /// <param name="installScript">The <c>install-agent.ps1</c> text.</param>
    /// <param name="omit">Files under the folder to leave out.</param>
    /// <param name="folder">Another top-level folder name.</param>
    /// <param name="backslashes">Entry names with '\' (as an old Windows zip tool writes them).</param>
    /// <param name="extraEntries">More entry names (full paths) with a short content.</param>
    public static string Write(string dir, string version = "abc123def456", string? installScript = null, IReadOnlyCollection<string>? omit = null,
        string? folder = null, bool backslashes = false, IReadOnlyList<string>? extraEntries = null)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, AgentBundleCatalog.FileName(AgentBundleCatalog.WindowsRid));
        string top = folder ?? $"mirbadllm-agent-{version}-win-x64";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            // Not a real agent: an MZ header and random bytes stand in for the single-file exe (the hash is what matters).
            ["mirbadllm-agent.exe"] = [(byte)'M', (byte)'Z', .. RandomNumberGenerator.GetBytes(4096)],
            ["install-agent.ps1"] = Encoding.ASCII.GetBytes(installScript ?? "Write-Host 'fake installer'\r\n"),
            ["agent.windows.example.json"] = "{}\n"u8.ToArray(),
            ["llama-cpp.lock.json"] = "{\"schemaVersion\":1}\n"u8.ToArray(),
            ["README.md"] = "# agent\n"u8.ToArray(),
            ["BUILD-INFO"] = Encoding.UTF8.GetBytes($"revision={version}\nrid=win-x64\nbuilt=2026-10-08T12:00:00Z\n"),
        };

        File.Delete(path);
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach ((string name, byte[] content) in files)
            {
                if (omit?.Contains(name) == true)
                {
                    continue;
                }

                string full = $"{top}/{name}";
                Add(zip, backslashes ? full.Replace('/', '\\') : full, content);
            }

            foreach (string extra in extraEntries ?? [])
            {
                Add(zip, extra, "x"u8.ToArray());
            }
        }

        return AgentBundleFixtures.Sha256(path);
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        stream.Write(content);
    }
}
