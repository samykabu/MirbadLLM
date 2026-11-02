using Microsoft.Extensions.Configuration;
using MirbadLLM.Orchestration;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class ProcMeminfoMemoryProbeTests
{
    private const string Fixture = """
        MemTotal:       132271932 kB
        MemFree:         4211932 kB
        MemAvailable:   98765432 kB
        Buffers:          123456 kB
        Cached:         20000000 kB
        SwapCached:            0 kB
        """;

    [Fact]
    public void ParseMemInfo_ExtractsTotalAndAvailable_ConvertedToGiB()
    {
        var snapshot = ProcMeminfoMemoryProbe.ParseMemInfo(Fixture);

        Assert.Equal(132271932.0 / (1024 * 1024), snapshot.TotalGiB, precision: 6);
        Assert.Equal(98765432.0 / (1024 * 1024), snapshot.AvailableGiB, precision: 6);
    }

    [Fact]
    public void ParseMemInfo_MissingRequiredLines_Throws()
    {
        Assert.Throws<FormatException>(() => ProcMeminfoMemoryProbe.ParseMemInfo("SomeOtherLine: 123 kB"));
    }

    [Fact]
    public async Task GetAsync_OnNonLinux_ReturnsConfiguredDevValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MirbadLLM:DevMemoryTotalGiB"] = "200",
                ["MirbadLLM:DevMemoryAvailableGiB"] = "80",
            })
            .Build();
        var probe = new ProcMeminfoMemoryProbe(configuration);

        var snapshot = await probe.GetAsync();

        if (!OperatingSystem.IsLinux())
        {
            Assert.Equal(200, snapshot.TotalGiB);
            Assert.Equal(80, snapshot.AvailableGiB);
        }
    }

    [Fact]
    public async Task GetAsync_OnNonLinux_DefaultsTo121TotalAnd60Available()
    {
        var configuration = new ConfigurationBuilder().Build();
        var probe = new ProcMeminfoMemoryProbe(configuration);

        var snapshot = await probe.GetAsync();

        if (!OperatingSystem.IsLinux())
        {
            Assert.Equal(121, snapshot.TotalGiB);
            Assert.Equal(60, snapshot.AvailableGiB);
        }
    }
}
