using MirbadLLM.Agent;
using MirbadLLM.NodeRuntime;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Downloads;

/// <summary>
/// <see cref="DownloadHelperUser"/> (MN-126): which user the Hugging Face download helper runs as, the
/// <c>/proc/self/status</c> parse it reads the agent's identity from, and the <c>Agent:DownloadHelperUser</c> option.
/// </summary>
public sealed class DownloadHelperUserTests
{
    private const string ProcStatus = "Name:\tdotnet\nUmask:\t0022\nState:\tS (sleeping)\nUid:\t1654\t1654\t1654\t1654\nGid:\t1654\t1655\t1654\t1654\nGroups:\t998 1654\n";

    [Fact]
    public void Resolve_Auto_RunsAsTheAgentsOwnUidAndGid()
    {
        Assert.Equal("1654:1654", DownloadHelperUser.Resolve("auto", (1654, 1654), () => true));
        Assert.Equal("1001:1002", DownloadHelperUser.Resolve(null, (1001, 1002), () => true));
        Assert.Equal("1001:1002", DownloadHelperUser.Resolve(" AUTO ", (1001, 1002), () => true));
    }

    [Fact]
    public void Resolve_Auto_KeepsTheImageDefault_WhenUnknownRootOrTheModelsRootIsNotWritable()
    {
        Assert.Null(DownloadHelperUser.Resolve("auto", null, () => true));
        Assert.Null(DownloadHelperUser.Resolve("auto", (0, 0), () => true));
        Assert.Null(DownloadHelperUser.Resolve("auto", (1654, 1654), () => false));
    }

    [Fact]
    public void Resolve_Auto_OnlyProbesTheModelsRootForANonRootIdentity()
    {
        int probes = 0;
        Assert.Null(DownloadHelperUser.Resolve("auto", (0, 0), () => ++probes > 0));
        Assert.Null(DownloadHelperUser.Resolve("auto", null, () => ++probes > 0));
        Assert.Equal(0, probes);
    }

    [Fact]
    public void Resolve_ImageAndLiteralValues()
    {
        Assert.Null(DownloadHelperUser.Resolve("image", (1654, 1654), () => true));
        Assert.Equal("2000:3000", DownloadHelperUser.Resolve("2000:3000", (1654, 1654), () => false));
        Assert.Equal("nobody", DownloadHelperUser.Resolve("nobody", null, () => true));
    }

    [Fact]
    public void ParseProcStatus_ReadsTheEffectiveIds()
    {
        Assert.Equal((1654L, 1655L), DownloadHelperUser.ParseProcStatus(ProcStatus));
        Assert.Equal((0L, 0L), DownloadHelperUser.ParseProcStatus("Uid:\t0\t0\t0\t0\r\nGid:\t0\t0\t0\t0\r\n"));
        Assert.Null(DownloadHelperUser.ParseProcStatus("Name:\tdotnet\nUid:\t1654\t1654\t1654\t1654\n"));
        Assert.Null(DownloadHelperUser.ParseProcStatus("Uid:\tx\ty\nGid:\t1\t1\n"));
    }

    [Fact]
    public void ReadProcessIdentity_IsNullOffLinux_AndMatchesProcSelfStatusOnLinux()
    {
        var identity = DownloadHelperUser.ReadProcessIdentity();
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(DownloadHelperUser.ParseProcStatus(File.ReadAllText("/proc/self/status")), identity);
        }
        else
        {
            Assert.Null(identity);
        }
    }

    [Fact]
    public void CanCreateIn_TrueForAWritableDirectory_FalseForAMissingOne_AndLeavesNothingBehind()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mirbadllm-helper-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(DownloadHelperUser.CanCreateIn(dir));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
            Assert.False(DownloadHelperUser.CanCreateIn(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AgentOption_DefaultsToAuto_AndValidatesItsValue()
    {
        Assert.Equal("auto", new AgentOptions().DownloadHelperUser);
        foreach (string ok in new[] { "auto", "image", "1654", "1654:1654", "mirbadllm-agent:docker" })
        {
            new AgentOptions { NodeId = "dgx", StateDirectory = "/s", DownloadHelperUser = ok }.Validate();
        }

        foreach (string bad in new[] { "", "1654:", ":1654", "a b", "1654:1654:1", "root;rm" })
        {
            var options = new AgentOptions { NodeId = "dgx", StateDirectory = "/s", DownloadHelperUser = bad };
            Assert.Throws<ArgumentException>(options.Validate);
        }
    }
}
