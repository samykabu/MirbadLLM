using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// Builds the capability report (design §3.3; <c>GET /capabilities</c> and the report embedded in enrollment): the base
/// report (protocol range, agent version, OS, architecture, CPU, memory total from the injected
/// <see cref="IMemoryProbe"/> when one is registered) folded through every <see cref="ICapabilityContributor"/> in
/// registration order. The base memory model is <see cref="MemoryModel.Unified"/> (the P1 DGX); the GPU contributor
/// (MN-018) sets the real model. A contributor that throws despite its contract is logged and skipped.
/// </summary>
public sealed class AgentCapabilityReporter(
    AgentBootInfo boot,
    IEnumerable<ICapabilityContributor> contributors,
    ILogger<AgentCapabilityReporter> logger,
    IMemoryProbe? memoryProbe = null)
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    private readonly ICapabilityContributor[] _contributors = [.. contributors];
    private IReadOnlyList<string>? _features;

    /// <summary>The current report.</summary>
    public async Task<NodeCapabilities> GetAsync(CancellationToken ct = default) => (await BuildAsync(ct).ConfigureAwait(false)).Report;

    /// <summary>
    /// The features of this agent (design §3.6), for the request pipeline's feature gate (W2 review MN-092 item 4).
    /// Features say what the agent can do, from its configuration, never whether a subsystem answers right now (Docker,
    /// hostd and <c>nvidia-smi</c> health goes to the Degraded reasons and to the executors' own errors, W4 review
    /// MN-096), so the list is built once (the host warms it before the listener opens) and kept for the life of the
    /// process: a gated request never runs the contributors, which may wait on Docker. A build in which a contributor
    /// failed is used but not kept.
    /// </summary>
    internal async ValueTask<IReadOnlyList<string>> GetFeaturesAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _features) is { } known)
        {
            return known;
        }

        (NodeCapabilities report, bool complete) = await BuildAsync(ct).ConfigureAwait(false);
        return complete
            ? Interlocked.CompareExchange(ref _features, report.Features, null) ?? report.Features
            : report.Features;
    }

    private async Task<(NodeCapabilities Report, bool Complete)> BuildAsync(CancellationToken ct)
    {
        bool complete = true;
        NodeCapabilities report = new()
        {
            Protocol = Protocol.Range,
            AgentVersion = boot.AgentVersion,
            Os = new NodeOsInfo(OsFamily(), RuntimeInformation.OSDescription, Environment.OSVersion.Version.ToString()),
            Arch = Architecture(RuntimeInformation.OSArchitecture),
            Cpu = new NodeCpuInfo(Environment.ProcessorCount),
            Memory = new NodeMemoryInfo(MemoryModel.Unified, await TotalMemoryGiBAsync(ct).ConfigureAwait(false)),
        };

        foreach (ICapabilityContributor contributor in _contributors)
        {
            try
            {
                report = await contributor.ContributeAsync(report, ct).ConfigureAwait(false) ?? report;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                complete = false;
                AgentHostLog.ContributorFailed(logger, ex, contributor.GetType().Name);
            }
        }

        return (report, complete);
    }

    /// <summary>
    /// <c>arm64</c> / <c>amd64</c> as design §3.3 names them; other architectures in lower case.
    /// </summary>
    internal static string Architecture(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    private static string OsFamily() =>
        OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : RuntimeInformation.OSDescription.ToLowerInvariant();

    private async Task<double> TotalMemoryGiBAsync(CancellationToken ct)
    {
        if (memoryProbe is not null)
        {
            try
            {
                MemorySnapshot snapshot = await memoryProbe.GetAsync(ct).ConfigureAwait(false);
                if (snapshot.TotalGiB > 0)
                {
                    return snapshot.TotalGiB;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AgentHostLog.ContributorFailed(logger, ex, memoryProbe.GetType().Name);
            }
        }

        return Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / BytesPerGiB, 2);
    }
}
