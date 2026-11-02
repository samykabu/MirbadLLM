using MirbadLLM.Orchestration;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class NvidiaSmiGpuProbeTests
{
    [Fact]
    public void ParseLine_RealDgxOutput_ParsesAllFourFields()
    {
        var snapshot = NvidiaSmiGpuProbe.ParseLine("0, 10.17, 44, 2411");

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.UtilizationPercent);
        Assert.Equal(10.17, snapshot.PowerWatts);
        Assert.Equal(44.0, snapshot.TemperatureC);
        Assert.Equal(2411.0, snapshot.SmClockMHz);
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("[N/A]")]
    [InlineData("n/a")]
    public void ParseLine_NotAvailableMarkers_BecomeNull(string marker)
    {
        var snapshot = NvidiaSmiGpuProbe.ParseLine($"12, {marker}, 45, 2000");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.PowerWatts);
        Assert.Equal(12.0, snapshot.UtilizationPercent);
    }

    [Fact]
    public void ParseLine_AllFieldsNotAvailable()
    {
        var snapshot = NvidiaSmiGpuProbe.ParseLine("[N/A], [N/A], [N/A], [N/A]");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.UtilizationPercent);
        Assert.Null(snapshot.PowerWatts);
        Assert.Null(snapshot.TemperatureC);
        Assert.Null(snapshot.SmClockMHz);
    }

    [Fact]
    public void ParseLine_Garbage_ReturnsNullFields_NotThrows()
    {
        var snapshot = NvidiaSmiGpuProbe.ParseLine("not, a, valid, line, at, all");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.UtilizationPercent);
        Assert.Null(snapshot.PowerWatts);
        Assert.Null(snapshot.TemperatureC);
        Assert.Null(snapshot.SmClockMHz);
    }

    [Fact]
    public void ParseLine_TooFewFields_ReturnsNull()
    {
        Assert.Null(NvidiaSmiGpuProbe.ParseLine("0, 10.17"));
    }

    [Fact]
    public void ParseLine_EmptyOutput_ReturnsNull()
    {
        Assert.Null(NvidiaSmiGpuProbe.ParseLine(string.Empty));
        Assert.Null(NvidiaSmiGpuProbe.ParseLine("\n\n   \n"));
    }

    [Fact]
    public async Task GetAsync_NeverThrows_RegardlessOfWhetherNvidiaSmiIsInstalled()
    {
        // This dev box has no nvidia-smi on PATH; the probe must return null rather than throw. On a real
        // GPU host it would return a snapshot instead - either way, this call must complete without an
        // exception escaping.
        var probe = new NvidiaSmiGpuProbe(Microsoft.Extensions.Logging.Abstractions.NullLogger<NvidiaSmiGpuProbe>.Instance);

        await probe.GetAsync();
    }
}
