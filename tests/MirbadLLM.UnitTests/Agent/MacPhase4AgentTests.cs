using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// macOS Phase 4 on the agent (docs/macos/implementation-plan.md M8, M10): the Apple GPU's IOKit statistics as GPU
/// samples, and the macOS hostd's launchd jobs as <c>services.launchd</c> on the host-service routes.
/// </summary>
public sealed class MacPhase4AgentTests
{
    // ---- Apple GPU telemetry (M8) ----

    [Fact]
    public void AnAppleGpuReadingIsOneSampleWithUtilisationAndNameAndNothingItCannotMeasure()
    {
        var reading = AppleGpuTelemetryProbe.ToReading(
        [
            new IoAcceleratorReading("Apple M2", 10, new Dictionary<string, long>
            {
                [AppleGpuTelemetryProbe.UtilizationKey] = 37,
                ["Renderer Utilization %"] = 30,
                ["In use system memory"] = 32_948_224,
            }),
        ]);

        Assert.Equal(GpuProbeStatus.Ok, reading.Status);
        var gpu = Assert.Single(reading.Gpus);
        Assert.Equal(0, gpu.Index);
        Assert.Equal(37, gpu.UtilizationPercent);
        Assert.Equal("Apple M2 GPU (10 cores)", gpu.Name);
        // No power, temperature or clock without powermetrics (root), and no memory: the GPU uses the host's unified
        // memory, and a GPU memory total would make the sampler call the node discrete.
        Assert.Null(gpu.PowerWatts);
        Assert.Null(gpu.TemperatureC);
        Assert.Null(gpu.SmClockMHz);
        Assert.Null(gpu.MemoryTotalGiB);
        Assert.Null(gpu.MemoryUsedGiB);
        Assert.Null(gpu.Uuid);
    }

    [Fact]
    public void AFailedIoKitQueryIsFailedAndNoAcceleratorIsLikeNoNvidiaSmi()
    {
        Assert.Equal(GpuProbeStatus.Failed, AppleGpuTelemetryProbe.ToReading(null).Status);
        Assert.Equal(GpuProbeStatus.NotInstalled, AppleGpuTelemetryProbe.ToReading([]).Status);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void AUtilisationOutsideZeroToHundredIsNotShown(long busy)
    {
        var reading = AppleGpuTelemetryProbe.ToReading(
            [new IoAcceleratorReading("Apple M2", 10, new Dictionary<string, long> { [AppleGpuTelemetryProbe.UtilizationKey] = busy })]);

        Assert.Null(Assert.Single(reading.Gpus).UtilizationPercent);
    }

    [Fact]
    public void TheNameUsesWhatIoKitGave()
    {
        Assert.Equal("Apple GPU", AppleGpuTelemetryProbe.NameOf(new IoAcceleratorReading(null, null, new Dictionary<string, long>())));
        Assert.Equal("Apple M4 Pro GPU", AppleGpuTelemetryProbe.NameOf(new IoAcceleratorReading(" Apple M4 Pro ", 0, new Dictionary<string, long>())));
    }

    [Fact]
    public async Task TheProbeKeepsItsLastReadForTheHealthCheckAndAFailureIsDegraded()
    {
        IReadOnlyList<IoAcceleratorReading>? next = null;
        var probe = new AppleGpuTelemetryProbe(() => next, Microsoft.Extensions.Logging.Abstractions.NullLogger<AppleGpuTelemetryProbe>.Instance);
        var health = new GpuHealthContributor(probe);

        await probe.ReadAsync(TestContext.Current.CancellationToken);
        var degraded = await health.CheckAsync(TestContext.Current.CancellationToken);
        next = [new IoAcceleratorReading("Apple M2", 10, new Dictionary<string, long> { [AppleGpuTelemetryProbe.UtilizationKey] = 5 })];
        await probe.ReadAsync(TestContext.Current.CancellationToken);
        var healthy = await health.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthReasonCodes.GpuProbeFailing, Assert.Single(degraded).Code);
        Assert.Contains("IOKit", degraded[0].Message, StringComparison.Ordinal);
        Assert.Empty(healthy);
        Assert.Equal(5, probe.Last!.Gpus[0].UtilizationPercent);
    }

    [Fact]
    public async Task OffMacOsTheIoKitProbeNeverCallsIoKit()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        var probe = AppleGpuTelemetryProbe.ForThisMac(Microsoft.Extensions.Logging.Abstractions.NullLogger<AppleGpuTelemetryProbe>.Instance);
        Assert.Equal(GpuProbeStatus.Failed, (await probe.ReadAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public void OnAMacIoKitListsTheAppleGpu()
    {
        if (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
        {
            return;
        }

        var accelerators = MacIoAccelerator.Read();

        Assert.NotNull(accelerators);
        var gpu = Assert.Single(accelerators);
        Assert.StartsWith("Apple ", gpu.Model, StringComparison.Ordinal);
        Assert.True(gpu.CoreCount > 0);
        Assert.InRange(gpu.Statistics[AppleGpuTelemetryProbe.UtilizationKey], 0, 100);
    }

    // ---- launchd host services (M10) ----

    private static NodeCapabilities MacReport() => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.0.0",
        Features = [AgentFeatures.TelemetryV1],
        Os = new NodeOsInfo("macos", "macOS 27.0.1"),
        Arch = "arm64",
        Memory = new NodeMemoryInfo(MemoryModel.Unified, 8),
    };

    [Fact]
    public async Task AMacWithHostdReportsLaunchdJobsAndNeitherSystemdNorGpuProcesses()
    {
        var options = new AgentHostServicesOptions
        {
            Manager = HostServiceManagers.Launchd,
            Systemd = true,
            Containers = false,
            BackupMount = false,
        };
        var contributor = new HostServicesContributor(new ScriptedHostd(), options, dockerAvailable: false);

        var report = await contributor.ContributeAsync(MacReport(), TestContext.Current.CancellationToken);

        Assert.Equal([AgentFeatures.TelemetryV1, AgentFeatures.ServicesLaunchd], report.Features);
        Assert.Equal(new HostServicesCapability(HostServiceManagers.Launchd, true, null, false), report.HostServices);
        Assert.Null(report.BackupMount);
    }

    [Fact]
    public async Task AMacWithoutHostdReportsNoHostServices()
    {
        var options = new AgentHostServicesOptions { Manager = HostServiceManagers.Launchd, Systemd = false, Containers = false, BackupMount = false };
        var contributor = new HostServicesContributor(new ScriptedHostd(), options, dockerAvailable: false);

        var report = await contributor.ContributeAsync(MacReport(), TestContext.Current.CancellationToken);

        Assert.Equal([AgentFeatures.TelemetryV1], report.Features);
        Assert.Null(report.HostServices);
        Assert.Empty(await contributor.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheHostServiceMutationsAreOfferedByLaunchdAndTheGpuProcessRouteIsNot()
    {
        string[] mac = [AgentFeatures.ServicesLaunchd];

        Assert.True(AgentRoutes.HostServiceStart.IsOfferedBy(mac));
        Assert.True(AgentRoutes.HostServiceStop.IsOfferedBy(mac));
        Assert.True(AgentRoutes.HostServiceRestart.IsOfferedBy(mac));
        Assert.True(AgentRoutes.HostServiceAutostart.IsOfferedBy(mac));
        Assert.True(AgentRoutes.HostWhitelistAdd.IsOfferedBy(mac));
        Assert.True(AgentRoutes.HostWhitelistUpdate.IsOfferedBy(mac));
        Assert.True(AgentRoutes.HostWhitelistRemove.IsOfferedBy(mac));
        Assert.False(AgentRoutes.HostGpuProcesses.IsOfferedBy(mac));
        Assert.Contains(AgentFeatures.ServicesLaunchd, AgentFeatures.MacOS);
    }

    [Fact]
    public void TheMacWordingNamesIoKitForGpuProcesses()
    {
        Assert.Contains("IOKit", HostServiceWording.NoGpuProcesses(HostServiceManagers.Launchd), StringComparison.Ordinal);
        Assert.Contains("nvidia-smi", HostServiceWording.NoGpuProcesses(HostServiceManagers.Systemd), StringComparison.Ordinal);
        Assert.Equal("autostart disabled", HostServiceWording.Autostart(HostServiceManagers.Launchd, "disabled"));
    }

    [Fact]
    public void TheMacExampleConfigPointsAtTheMacSocketWithLaunchdJobsOffUntilHostdIsInstalled()
    {
        string json = File.ReadAllText(MirbadLLM.UnitTests.HostAgent.HostAgentFixtures.FindRepoFile(Path.Combine("deploy", "agent", "agent.macos.example.json")));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var agent = doc.RootElement.GetProperty("Agent");

        Assert.Equal("/var/run/mirbadllm-hostd/hostd.sock", agent.GetProperty("HostdSocket").GetString());
        Assert.False(agent.GetProperty("ManageLaunchdJobs").GetBoolean());
        Assert.False(agent.GetProperty("HostdExpected").GetBoolean());
        Assert.False(agent.GetProperty("ManageBackupMount").GetBoolean());
    }
}
