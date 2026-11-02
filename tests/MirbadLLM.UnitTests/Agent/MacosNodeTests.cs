using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.UnitTests.Web;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.Agent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// macOS nodes, Phase 1 (docs/macos/implementation-plan.md): the memory figures from the kernel's VM statistics, the
/// Apple Silicon capability fields (chip, Metal working set), the <c>osx-arm64</c> bundle in the master's catalog and the
/// macOS join and rekey lines — and, on a Mac (the CI's mirbadllm-mac runner), the real probes and the line run by
/// /bin/bash against stand-in curl and sudo.
/// </summary>
public sealed class MacosNodeTests : IDisposable
{
    private const string CaHash = "sha256:q3vYc2ZpeHR1cmUtY2EtaGFzaA+/=";
    private const string Token = "abc_DEF-123xyz";
    private const string Enroll = "https://192.0.2.191:8443";
    private const string MacUrl = "http://192.0.2.191:8080/agent/bundles/osx-arm64.tar.gz";
    private const double GiB = 1024d * 1024 * 1024;

    private static readonly string MacSha = Convert.ToHexStringLower(SHA256.HashData("mac"u8));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ss-macos-" + Guid.NewGuid().ToString("N")[..12]);

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

    // ---- memory ------------------------------------------------------------------------------------------------------

    [Fact]
    public void MemoryProbe_AvailableIsFreePlusInactivePages()
    {
        // An 8 GiB M2 (16 KiB pages): 50,000 free and 100,000 inactive pages = 2.288 GiB available.
        byte[] stats = VmStatistics(free: 50_000, active: 200_000, inactive: 100_000, wired: 80_000);

        MemorySnapshot snapshot = MacMemoryProbe.FromVmStatistics(8UL * 1024 * 1024 * 1024, 16_384, stats);

        Assert.Equal(8.0, snapshot.TotalGiB, 6);
        Assert.Equal(150_000d * 16_384 / GiB, snapshot.AvailableGiB, 6);
    }

    [Theory]
    [InlineData(0UL, 16_384UL, 152)]
    [InlineData(8589934592UL, 0UL, 152)]
    [InlineData(8589934592UL, 16_384UL, 40)]
    public void MemoryProbe_RefusesFiguresThatCannotBeRight(ulong total, ulong page, int length) =>
        Assert.Throws<FormatException>(() => MacMemoryProbe.FromVmStatistics(total, page, new byte[length]));

    [Fact]
    public void MemoryProbe_RefusesMoreAvailableThanInstalled() =>
        Assert.Throws<FormatException>(() => MacMemoryProbe.FromVmStatistics(1024UL * 16_384, 16_384, VmStatistics(free: 2048, inactive: 0)));

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task MemoryProbe_OnAMac_MatchesSysctlAndVmStat()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Reads this Mac's kernel figures.");
        }

        MemorySnapshot snapshot = await new MacMemoryProbe().GetAsync(TestContext.Current.CancellationToken);

        ulong memsize = ulong.Parse(Run("/usr/sbin/sysctl", "-n hw.memsize"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(memsize / GiB, snapshot.TotalGiB, 6);
        Assert.InRange(snapshot.AvailableGiB, 0.01, snapshot.TotalGiB);

        // vm_stat's free + inactive pages at about the same moment (they move; allow 10 % of RAM).
        string vmStat = Run("/usr/bin/vm_stat", "");
        double pageSize = double.Parse(System.Text.RegularExpressions.Regex.Match(vmStat, @"page size of (\d+) bytes").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double free = Pages(vmStat, "Pages free");
        double inactive = Pages(vmStat, "Pages inactive");
        Assert.InRange(snapshot.AvailableGiB, ((free + inactive) * pageSize / GiB) - (0.1 * snapshot.TotalGiB), ((free + inactive) * pageSize / GiB) + (0.1 * snapshot.TotalGiB));
    }

    // ---- capability report -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Capabilities_AddTheChipAndMetalsWorkingSet_OnAUnifiedNode()
    {
        var contributor = new MacCapabilityContributor(() => ("Apple M2", 5_726_633_984UL), () => "Apple M2");

        NodeCapabilities report = await contributor.ContributeAsync(BaseReport(MemoryModel.Unified), TestContext.Current.CancellationToken);

        Assert.Equal("Apple M2", report.Cpu!.Model);
        Assert.Equal(5.33, report.Memory.GpuWorkingSetGiB);
        Assert.Equal(8.0, report.Memory.TotalGiB);
    }

    [Fact]
    public async Task Capabilities_NoMetalDevice_OrADiscreteNode_LeaveTheMemoryAsItWas()
    {
        var noMetal = new MacCapabilityContributor(() => null, () => null);
        var discrete = new MacCapabilityContributor(() => ("Apple M2", 5_726_633_984UL), () => "Apple M2");

        NodeCapabilities a = await noMetal.ContributeAsync(BaseReport(MemoryModel.Unified), TestContext.Current.CancellationToken);
        NodeCapabilities b = await discrete.ContributeAsync(BaseReport(MemoryModel.Discrete), TestContext.Current.CancellationToken);

        Assert.Null(a.Memory.GpuWorkingSetGiB);
        Assert.Null(a.Cpu!.Model);
        Assert.Null(b.Memory.GpuWorkingSetGiB);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Capabilities_OnAMac_TheRealMetalDeviceAndChip()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Asks this Mac's Metal device.");
        }

        (string Name, ulong WorkingSetBytes)? gpu = MacGpu.Query();
        Assert.NotNull(gpu);
        Assert.StartsWith("Apple", gpu.Value.Name, StringComparison.Ordinal);
        ulong memsize = ulong.Parse(Run("/usr/sbin/sysctl", "-n hw.memsize"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(gpu.Value.WorkingSetBytes, memsize / 3, memsize);
        Assert.StartsWith("Apple", MacSysctl.CpuBrand(), StringComparison.Ordinal);
    }

    // ---- master: bundle catalog and the join line ---------------------------------------------------------------------

    [Fact]
    public void Catalog_ServesTheMacTarball_AfterTheOthers_UnderItsOwnName()
    {
        string dir = Path.Combine(_root, "bundles");
        AgentBundleFixtures.Write(dir, "linux-arm64", version: "v9");
        string sha = AgentBundleFixtures.Write(dir, "osx-arm64", version: "v9");

        var catalog = new AgentBundleCatalog(dir, NullLogger<AgentBundleCatalog>.Instance);

        Assert.Equal(["linux-arm64", "osx-arm64"], catalog.Bundles.Select(b => b.Rid));
        AgentBundle mac = catalog.Find("osx-arm64")!;
        Assert.Equal(sha, mac.Sha256);
        Assert.Equal("mirbadllm-agent-v9-osx-arm64", mac.Folder);
        Assert.Equal("osx-arm64.tar.gz", AgentBundleCatalog.FileName("osx-arm64"));
        Assert.Equal("application/gzip", AgentBundleCatalog.ContentType("osx-arm64"));
        Assert.Same(mac, catalog.FindFile("osx-arm64.tar.gz"));
    }

    [Fact]
    public void Join_DownloadsChecksWithShasumAndRunsTheBundlesInstaller()
    {
        MacosInstallerLine line = MacosInstallerLine.Join("mac-1", Enroll, CaHash, Download())!;

        string expected =
            "(set -e; d=\"$(mktemp -d)\"; trap 'rm -rf \"$d\"' EXIT; cd \"$d\"; " +
            "[ \"$(uname -m)\" = arm64 ] || { echo \"the mirbadllm agent needs an Apple Silicon Mac (arm64)\" >&2; exit 1; }; " +
            $"curl -fsSL -o agent.tar.gz '{MacUrl}'; " +
            $"echo \"{MacSha}  agent.tar.gz\" | shasum -a 256 -c -; " +
            "tar -xzf agent.tar.gz; " +
            $"sudo MIRBADLLM_JOIN_TOKEN='{Token}' ./mirbadllm-agent-*/install-agent.sh install --node-id mac-1 --enroll-url {Enroll} --ca-hash {CaHash})";
        Assert.Equal(expected, line.WithToken(Token));
        Assert.DoesNotContain(Token, line.Prompt(), StringComparison.Ordinal);
        Assert.Contains("sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./mirbadllm-agent-*/install-agent.sh install --node-id mac-1", line.Prompt(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rekey_RunsTheRekeyVerbWithTheUid()
    {
        string text = MacosInstallerLine.Rekey("mac-1", "uid-42", Enroll, CaHash, Download())!.WithToken(Token);

        Assert.EndsWith($"./mirbadllm-agent-*/install-agent.sh rekey --node-id mac-1 --node-uid uid-42 --enroll-url {Enroll} --ca-hash {CaHash})", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linux-arm64", "aa")]
    [InlineData("osx-arm64", "not-a-hash")]
    [InlineData("osx-arm64", null)]
    public void WithoutAUsableMacBundle_ThereIsNoMacCommand(string rid, string? sha)
    {
        var download = new AgentDownload { Bundles = [new AgentBundleLink(rid, MacUrl, sha ?? "", "v9")] };

        Assert.Null(MacosInstallerLine.Join("mac-1", Enroll, CaHash, download));
        Assert.Null(MacosInstallerLine.Join("mac-1", Enroll, CaHash, null));
    }

    [Fact]
    public void TheLinuxLine_IgnoresTheMacBundle()
    {
        var withMac = new AgentDownload
        {
            Bundles =
            [
                new AgentBundleLink("linux-arm64", "http://m:8080/agent/bundles/linux-arm64.tar.gz", MacSha, "v9"),
                new AgentBundleLink("osx-arm64", MacUrl, MacSha, "v9"),
            ],
        };
        var linuxOnly = new AgentDownload { Bundles = [withMac.Bundles[0]] };

        Assert.Equal(
            LinuxInstallerLine.Join("pc-1", Enroll, CaHash, linuxOnly).WithToken(Token),
            LinuxInstallerLine.Join("pc-1", Enroll, CaHash, withMac).WithToken(Token));
    }

    [Fact]
    public void Of_RebuildsTheMastersMacLine_AndRefusesAnotherOne()
    {
        string macos = MacosInstallerLine.Join("mac-1", Enroll, CaHash, Download())!.WithToken(Token);
        JoinTokenCreateResponse response = Response(new JoinCommands("linux", null, macos));

        Assert.NotNull(MacosInstallerLine.Of(response, null));
        Assert.Null(MacosInstallerLine.Of(Response(new JoinCommands("linux", null, macos + " ")), null));
        Assert.Null(MacosInstallerLine.Of(Response(new JoinCommands("linux")), null));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void TheLine_RunByBash_DownloadsChecksUnpacksAndRunsTheInstaller_OnAMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Runs the line with macOS's own bash 3.2, shasum, tar and mktemp.");
        }

        string served = Path.Combine(_root, "served");
        string bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        string log = Path.Combine(_root, "install.log");
        string sha = AgentBundleFixtures.Write(served, "osx-arm64", version: "v9", installScript: "#!/bin/bash\n{ basename \"$(cd \"$(dirname \"$0\")\" && pwd)\"; printf '%s\\n' \"$@\"; echo \"token=${MIRBADLLM_JOIN_TOKEN-unset}\"; } >\"$FAKE_INSTALL_LOG\"\n");
        WriteScript(bin, "curl", "#!/bin/bash\nout=\"\"; url=\"\"\nwhile [ $# -gt 0 ]; do case \"$1\" in -o) out=\"$2\"; shift 2 ;; -*) shift ;; *) url=\"$1\"; shift ;; esac; done\ncp \"$FAKE_SERVED/${url##*/}\" \"$out\"\n");
        WriteScript(bin, "sudo", "#!/bin/bash\nwhile [ $# -gt 0 ]; do case \"$1\" in --preserve-env=*) shift ;; [A-Z_]*=*) export \"$1\"; shift ;; *) break ;; esac; done\nexec \"$@\"\n");
        var download = new AgentDownload { Bundles = [new AgentBundleLink("osx-arm64", MacUrl, sha, "v9")] };
        string line = MacosInstallerLine.Join("mac-1", Enroll, CaHash, download)!.WithToken(Token);

        (int code, string output) = Bash(line, bin, served, log);

        Assert.True(code == 0, output);
        string[] recorded = File.ReadAllLines(log);
        Assert.Equal("mirbadllm-agent-v9-osx-arm64", recorded[0]);
        Assert.Equal(["install", "--node-id", "mac-1", "--enroll-url", Enroll, "--ca-hash", CaHash], recorded[1..8]);
        Assert.Equal($"token={Token}", recorded[8]);

        // A tampered bundle stops at the hash check: the installer never runs.
        File.Delete(log);
        File.AppendAllText(Path.Combine(served, "osx-arm64.tar.gz"), "x");
        (int failed, _) = Bash(line, bin, served, log);
        Assert.NotEqual(0, failed);
        Assert.False(File.Exists(log));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static AgentDownload Download() => new()
    {
        Bundles =
        [
            new AgentBundleLink("linux-arm64", "http://192.0.2.191:8080/agent/bundles/linux-arm64.tar.gz", Convert.ToHexStringLower(SHA256.HashData("arm"u8)), "v9"),
            new AgentBundleLink("osx-arm64", MacUrl, MacSha, "v9"),
        ],
    };

    private static JoinTokenCreateResponse Response(JoinCommands commands) => new()
    {
        Token = Token,
        NodeId = "mac-1",
        EnrollUrl = Enroll,
        CaHash = CaHash,
        ExpiresAt = DateTimeOffset.UnixEpoch,
        Commands = commands,
        AgentDownload = Download(),
    };

    private static NodeCapabilities BaseReport(MemoryModel model) => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.0.0",
        Os = new NodeOsInfo("macos", "macOS 27.0.1"),
        Arch = "arm64",
        Cpu = new NodeCpuInfo(8),
        Memory = new NodeMemoryInfo(model, 8.0),
    };

    private static byte[] VmStatistics(uint free = 0, uint active = 0, uint inactive = 0, uint wired = 0)
    {
        byte[] raw = new byte[152];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0, 4), free);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4, 4), active);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8, 4), inactive);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12, 4), wired);
        return raw;
    }

    private static double Pages(string vmStat, string label) =>
        double.Parse(System.Text.RegularExpressions.Regex.Match(vmStat, label + @":\s+(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static string Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private static (int Code, string Output) Bash(string line, string bin, string served, string log)
    {
        var start = new ProcessStartInfo("/bin/bash", ["-c", line]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["PATH"] = bin + ":/usr/bin:/bin:/usr/sbin:/sbin";
        start.Environment["FAKE_SERVED"] = served;
        start.Environment["FAKE_INSTALL_LOG"] = log;
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void WriteScript(string dir, string name, string text)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
