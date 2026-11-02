using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// The Apple Silicon parts of the capability report (docs/macos/implementation-plan.md, Phase 1): the chip name as the
/// CPU model and, on a unified node, Metal's GPU working-set size (M7), so the master can admit a model against what the
/// GPU may actually keep resident rather than the whole of RAM. Registered on macOS only; a missing Metal device leaves
/// the report as it was (nothing is invented).
/// </summary>
public sealed class MacCapabilityContributor(Func<(string Name, ulong WorkingSetBytes)?> queryGpu, Func<string?> cpuModel) : ICapabilityContributor
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        NodeCapabilities report = current;
        if (cpuModel() is { Length: > 0 } model && report.Cpu is { } cpu && string.IsNullOrEmpty(cpu.Model))
        {
            report = report with { Cpu = cpu with { Model = model } };
        }

        if (report.Memory.Model == MemoryModel.Unified && queryGpu() is { WorkingSetBytes: > 0 } gpu)
        {
            report = report with { Memory = report.Memory with { GpuWorkingSetGiB = Math.Round(gpu.WorkingSetBytes / BytesPerGiB, 2) } };
        }

        return Task.FromResult(report);
    }

    /// <summary>The contributor wired to the real Metal device and <c>sysctl machdep.cpu.brand_string</c>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public static MacCapabilityContributor ForThisMac() => new(MacGpu.Query, MacSysctl.CpuBrand);
}
