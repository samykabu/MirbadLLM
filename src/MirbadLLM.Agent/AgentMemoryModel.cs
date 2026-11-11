using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;

namespace MirbadLLM.Agent;

/// <summary>
/// The node's memory model as the P2 composition (MN-130, D101) decides it, once per process: the forced
/// <see cref="TelemetryOptions.MemoryModel"/> when set, else what the GPU probe reports (<c>nvidia-smi</c>: discrete when a
/// GPU has its own VRAM; the GB10 reports none, and a node without <c>nvidia-smi</c> or with a failing one is unified).
/// It selects, for the whole life of the process, the reservation manager's admission (<see cref="AdmissionOptions.MemoryModel"/>
/// and its per-GPU reader), the <c>/status</c> memory reader and whether the P2 capability flags are reported. A unified
/// node (the DGX) therefore keeps P1 exactly: unified admission, P1's <c>/status</c> reader and no P2 flag.
/// </summary>
public sealed partial class AgentMemoryModel
{
    private readonly Lazy<MemoryModel> _model;

    /// <summary>Creates the decision; nothing runs until <see cref="Model"/> is first read.</summary>
    public AgentMemoryModel(IGpuStartupInventory inventory, IOptions<TelemetryOptions> telemetry, ILogger<AgentMemoryModel> logger)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(logger);
        IsForced = telemetry.Value.MemoryModel is not null;
        _model = new Lazy<MemoryModel>(() => Resolve(inventory, telemetry.Value.MemoryModel, logger), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True when <c>Agent:MemoryModel</c> forces the model (the DGX's <c>agent.dgx.json</c>, or an operator's choice).</summary>
    public bool IsForced { get; }

    /// <summary>The memory model (read once; the first read may run <c>nvidia-smi</c>, whose inventory is then cached).</summary>
    public MemoryModel Model => _model.Value;

    /// <summary>True on a discrete node.</summary>
    public bool IsDiscrete => Model == MemoryModel.Discrete;

    private static MemoryModel Resolve(IGpuStartupInventory inventory, MemoryModel? forced, ILogger logger)
    {
        if (forced is { } model)
        {
            LogDecided(logger, model, "forced by the telemetry options");
            return model;
        }

        try
        {
            // Sync over async, once per process, off any synchronization context (the first reader is reconciliation).
            var gpus = Task.Run(() => inventory.GetAsync()).GetAwaiter().GetResult();
            if (!inventory.Answered)
            {
                // nvidia-smi exists but failed (GetAsync's empty list is not "no GPU"): P1's unified admission until a restart.
                LogProbeUnanswered(logger);
                return MemoryModel.Unified;
            }

            model = NvidiaSmiGpuInventory.MemoryModelOf(gpus);
            LogDecided(logger, model, gpus.Count == 0 ? "no GPU reported" : $"{gpus.Count} GPU(s) reported");
            return model;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // P1 behaviour when the probe cannot tell: unified admission, no P2 flag.
            LogProbeFailed(logger, ex);
            return MemoryModel.Unified;
        }
    }

    [LoggerMessage(EventId = 2640, Level = LogLevel.Information, Message = "Memory model {Model} ({Why}): admission, /status memory and the P2 capability flags follow it")]
    private static partial void LogDecided(ILogger logger, MemoryModel model, string why);

    [LoggerMessage(EventId = 2641, Level = LogLevel.Warning, Message = "The GPU probe failed while deciding the memory model; the node admits as unified (P1)")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2642, Level = LogLevel.Warning, Message = "nvidia-smi failed while deciding the memory model; the node admits and reports as unified (P1) until the agent is restarted with a working nvidia-smi")]
    private static partial void LogProbeUnanswered(ILogger logger);

    [LoggerMessage(EventId = 2643, Level = LogLevel.Warning, Message = "The GPU probe now reports a {Reported} node, but this agent decided {Decided} at startup; the capability report keeps {Decided} (admission and /status follow the startup decision). Restart the agent")]
    internal static partial void LogReportDiffers(ILogger logger, MemoryModel reported, MemoryModel decided);

    // MN-124: a forced model that differs from the probe is the operator's choice, not a stale startup decision.
    [LoggerMessage(EventId = 2644, Level = LogLevel.Information, Message = "Agent:MemoryModel forces {Decided}; the GPU probe reports a {Reported} node. The capability report, admission and /status follow {Decided}, as configured")]
    internal static partial void LogForcedDiffers(ILogger logger, MemoryModel reported, MemoryModel decided);
}

/// <summary>
/// The dedicated node memory reader of discrete admission (<see cref="DiscreteAdmission.CreateMemoryReader"/>: a fresh GPU
/// read per decision, never the <c>/status</c> reader with its reuse window), owned here so the container disposes it.
/// Resolved only on a discrete node.
/// </summary>
internal sealed class AdmissionNodeMemory(IMemoryProbe host, IGpuTelemetryProbe gpus, TimeProvider time) : IDisposable
{
    public DiscreteNodeMemoryReader Reader { get; } = DiscreteAdmission.CreateMemoryReader(host, gpus, time);

    public void Dispose() => Reader.Dispose();
}

/// <summary>
/// Reports what <paramref name="inner"/> adds only when this process decided the node discrete (<see cref="AgentMemoryModel"/>,
/// the same decision admission and <c>/status</c> follow): a unified node — the DGX — keeps P1's feature list (MN-130,
/// D101). Never removes what other contributors added. W9 review (MN-132, D102): gated on the process decision, not on the
/// report's memory model, so a node whose <c>nvidia-smi</c> failed at startup and works later cannot advertise
/// <c>gpus.uuids</c>/<c>launch.env</c> while it admits as unified.
/// </summary>
internal sealed class DiscreteNodeContributor(ICapabilityContributor inner, AgentMemoryModel memory) : ICapabilityContributor
{
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return memory.IsDiscrete ? inner.ContributeAsync(current, ct) : Task.FromResult(current);
    }
}

/// <summary>
/// Makes the capability report's memory model the process decision (<see cref="AgentMemoryModel"/>), registered right after
/// the telemetry contributor (W9 review MN-132, D102). The master fits, places and admits by the reported model, so it must
/// be the one the agent's admission and <c>/status</c> use; a later GPU read that disagrees (a probe that failed at startup)
/// is logged once (2643, restart advice) and does not change the report; a model forced by <c>Agent:MemoryModel</c> that
/// differs from the probe is logged once as information (2644, no restart advice: it is the operator's choice, MN-124). On
/// the DGX both are unified: nothing changes.
/// </summary>
internal sealed class MemoryModelReportContributor(AgentMemoryModel memory, ILogger<AgentMemoryModel> logger) : ICapabilityContributor
{
    private int _warned;

    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        MemoryModel decided = memory.Model;
        if (current.Memory.Model == decided)
        {
            return Task.FromResult(current);
        }

        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            if (memory.IsForced)
            {
                AgentMemoryModel.LogForcedDiffers(logger, current.Memory.Model, decided);
            }
            else
            {
                AgentMemoryModel.LogReportDiffers(logger, current.Memory.Model, decided);
            }
        }

        return Task.FromResult(current with { Memory = current.Memory with { Model = decided } });
    }
}
