using System.Globalization;
using System.Text;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Fakes;

/// <summary>
/// <c>nvidia-smi</c> of a discrete fake node (MN-122): answers the agent's two queries (the inventory of
/// <see cref="NvidiaSmiGpuInventory"/>, also its legacy form, and the telemetry of <see cref="NvidiaSmiGpuTelemetryProbe"/>)
/// with the configured GPUs, in the CSV the real driver prints (<c>--format=csv,noheader,nounits</c>, MiB). VRAM used is
/// <see cref="DevFakeRuntimeOptions.GpuBaselineUsedGiB"/> plus each running fake model's share on that GPU
/// (<see cref="FakeRuntimeDriver.GpuUsedGiB"/>), so <c>/status</c>, telemetry, <c>/gpu/idle</c> and the master's fit see
/// models come and go. Any other query fails as an unknown query would (exit 2), so a new caller is noticed, not faked.
/// </summary>
internal sealed class DevFakeNvidiaSmi(DevFakeRuntimeOptions fake, FakeRuntimeDriver driver) : INvidiaSmiRunner
{
    /// <summary>Driver version the fake reports.</summary>
    public const string DriverVersion = "580.00";

    /// <summary>Compute capability the fake reports (a Blackwell desktop card).</summary>
    public const string ComputeCapability = "12.0";

    private const double MiBPerGiB = 1024;

    /// <inheritdoc />
    public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string? output = arguments switch
        {
            NvidiaSmiGpuInventory.Arguments => Rows(g => [Index(g), g.Uuid, g.Name, MiB(g.TotalGiB), ComputeCapability, DriverVersion]),
            NvidiaSmiGpuInventory.LegacyArguments => Rows(g => [Index(g), g.Uuid, g.Name, MiB(g.TotalGiB), DriverVersion]),
            NvidiaSmiGpuTelemetryProbe.Arguments => Rows(Telemetry),
            _ => null,
        };
        return Task.FromResult(output is null
            ? new NvidiaSmiResult(true, 2, "", $"Invalid combination of input arguments (the dev fake nvidia-smi answers only the agent's queries): {arguments}")
            : new NvidiaSmiResult(true, 0, output, ""));
    }

    private string[] Telemetry(DevFakeGpu gpu)
    {
        double used = Math.Min(gpu.TotalGiB, DevFakeRuntimeOptions.GpuBaselineUsedGiB + driver.GpuUsedGiB(gpu.Uuid));
        bool busy = driver.GpuUsedGiB(gpu.Uuid) > 0;
        return
        [
            Index(gpu), gpu.Uuid, busy ? "35" : "0", busy ? "95.00" : "15.00", busy ? "55" : "40", busy ? "2520" : "210",
            MiB(used), MiB(gpu.TotalGiB), MiB(Math.Max(0, gpu.TotalGiB - used)), gpu.Name,
        ];
    }

    private string Rows(Func<DevFakeGpu, string[]> row)
    {
        var text = new StringBuilder();
        foreach (DevFakeGpu gpu in fake.Gpus)
        {
            text.Append(string.Join(", ", row(gpu))).Append('\n');
        }

        return text.ToString();
    }

    private static string Index(DevFakeGpu gpu) => gpu.Index.ToString(CultureInfo.InvariantCulture);

    private static string MiB(double gib) => Math.Round(gib * MiBPerGiB).ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>
/// The discrete fake node's P2 feature (MN-122, D57: fixed per process): <see cref="AgentFeatures.GpuSelectionUuids"/>,
/// because <see cref="FakeRuntimeDriver"/> launches with <c>gpus.mode = uuids</c> (422 <c>gpu_missing</c> before reserving,
/// read-back labels). Without it the master refuses to place a model on the node's GPUs. The other P2 flags come from
/// their own packages when they apply to a fake node: <see cref="AgentFeatures.AdmissionDiscrete"/> from MN-105's
/// admission contributor; <see cref="AgentFeatures.LaunchEnv"/> and <see cref="AgentFeatures.ImagesDigestPinned"/> describe
/// the container driver, which a fake node does not run. Registered only on a discrete fake node.
/// </summary>
internal sealed class DevFakeCapabilityContributor : ICapabilityContributor
{
    /// <summary>The P2 features a discrete fake node reports itself.</summary>
    public static IReadOnlyList<string> Features { get; } = [AgentFeatures.GpuSelectionUuids];

    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var missing = Features.Where(f => !current.Features.Contains(f, StringComparer.Ordinal)).ToList();
        return Task.FromResult(missing.Count == 0 ? current : current with { Features = [.. current.Features, .. missing] });
    }
}
