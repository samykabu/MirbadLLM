using System.Globalization;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>Settings of <see cref="ExternalGpuUseContributor"/>.</summary>
public sealed record ExternalGpuUseOptions
{
    /// <summary>
    /// Per-GPU reserve by GPU UUID (the node's <c>GpuReserveGiB</c>, design §6.1). A GPU with no known reserve is not
    /// checked: without one every desktop GPU would be "above its reserve" all the time. The reserves the master sends
    /// with a launch replace these (<see cref="ExternalGpuUseContributor.RememberReserves"/>).
    /// </summary>
    public IReadOnlyDictionary<string, double> GpuReservesGiB { get; init; } = new Dictionary<string, double>();

    /// <summary>Consecutive samples above (raise) or at/below (clear) the reserve before the reason changes (default 2).</summary>
    public int ConsecutiveSamples { get; init; } = 2;
}

/// <summary>
/// The Degraded reason <see cref="HealthReasonCodes.GpuExternalUseHigh"/> (design-p3p4 §A.4.3, MN-214): per GPU,
/// <c>external = memory.used − Σ measured model deltas</c>, where <c>memory.used</c> comes from the sampler's
/// <c>nvidia-smi --query-gpu</c> (never from CUDA or llama.cpp, spike S1 / C9) and each model's delta is its
/// <see cref="ProcessRecord.MeasuredGpuGiB"/> ("measured at start", <see cref="NvidiaSmiGpuStartMeter"/>). When the
/// external use stays above the GPU's reserve for <see cref="ExternalGpuUseOptions.ConsecutiveSamples"/> samples (the owner
/// opened a game or ComfyUI) the reason is raised with the exact numbers; it clears after as many samples at or below.
/// Models keep running; admission may refuse new starts on its own figures.
/// <para>Fed by the sampler (<see cref="ITelemetrySampleObserver"/>, the measured-delta hook), so
/// <see cref="CheckAsync"/> only reports the current state and never runs a probe. A sample in which a GPU cannot be
/// judged leaves that GPU's state and counters as they were: no <c>memory.used</c>, an unreadable
/// <c>processes.json</c>, or an instance on the GPU without a measured delta yet (while it starts, its memory would
/// otherwise count as external). A model on several GPUs is split by its <c>--tensor-split</c> weights when its argv has
/// them, else evenly (the record keeps only the sum).</para>
/// </summary>
public sealed partial class ExternalGpuUseContributor(
    IProcessRecordStore records,
    ExternalGpuUseOptions options,
    ILogger<ExternalGpuUseContributor> logger) : IHealthContributor, ITelemetrySampleObserver
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, GpuState> _gpus = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _needed = Math.Max(1, options.ConsecutiveSamples);
    private Dictionary<string, double> _reserves = Normalize(options.GpuReservesGiB);
    private double? _defaultReserve;

    /// <summary>The last judged external use per GPU UUID (GiB), for refusal texts; empty before the first sample.</summary>
    public IReadOnlyDictionary<string, double> ExternalGiB
    {
        get
        {
            lock (_gate)
            {
                return _gpus.Where(kv => kv.Value.ExternalGiB is not null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.ExternalGiB!.Value, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Takes the reserves of a launch's admission (the master's current <c>GpuReserveGiB</c> for this node, design §6.1):
    /// <see cref="AdmissionRequest.GpuReservesGiB"/> per GPU, <see cref="AdmissionRequest.GpuReserveGiB"/> for every other
    /// GPU. A request that carries neither changes nothing.
    /// </summary>
    public void RememberReserves(AdmissionRequest admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.GpuReservesGiB is null && admission.GpuReserveGiB is null)
        {
            return;
        }

        lock (_gate)
        {
            if (admission.GpuReservesGiB is { } reserves)
            {
                _reserves = Normalize(reserves);
            }

            _defaultReserve = admission.GpuReserveGiB;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var raised = _gpus.Values.Where(g => g.Raised).OrderBy(g => g.Index).ToList();
            if (raised.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<HealthReason>>([]);
            }

            string text = string.Join(" ", raised.Select(Describe)) +
                " Models keep running; new starts on these GPUs may be refused.";
            return Task.FromResult<IReadOnlyList<HealthReason>>([new HealthReason(HealthReasonCodes.GpuExternalUseHigh, text)]);
        }
    }

    /// <inheritdoc />
    public async Task ObserveAsync(NodeSample sample, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        IReadOnlyList<ProcessRecord>? known;
        try
        {
            known = await records.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecordsUnreadable(logger, ex);
            known = null;
        }

        var modelShares = known is null ? null : SharesPerGpu(known);
        lock (_gate)
        {
            foreach (var gpu in sample.Gpus)
            {
                if (gpu.Uuid is not { Length: > 0 } uuid)
                {
                    continue;
                }

                if (ReserveOf(uuid) is not { } reserve)
                {
                    // No longer judged (the master's reserves leave it out): a raised reason must not stick (MN-219).
                    if (_gpus.Remove(uuid, out var dropped) && dropped.Raised)
                    {
                        LogCleared(logger, uuid, dropped.ExternalGiB ?? 0, dropped.ReserveGiB);
                    }

                    continue;
                }

                var state = _gpus.TryGetValue(uuid, out var existing) ? existing : _gpus[uuid] = new GpuState();
                state.Index = gpu.Index;
                state.Name = gpu.Name;
                if (gpu.MemoryUsedGiB is not { } used || modelShares is null ||
                    modelShares.Unmeasured.Contains(uuid))
                {
                    continue;
                }

                double models = modelShares.MeasuredGiB.GetValueOrDefault(uuid);
                double external = Math.Max(0, used - models);
                state.UsedGiB = used;
                state.ModelsGiB = models;
                state.ExternalGiB = external;
                state.ReserveGiB = reserve;
                Advance(state, external > reserve, uuid);
            }
        }
    }

    private void Advance(GpuState state, bool high, string uuid)
    {
        if (high)
        {
            state.Low = 0;
            state.High++;
            if (!state.Raised && state.High >= _needed)
            {
                state.Raised = true;
                LogRaised(logger, uuid, state.ExternalGiB ?? 0, state.ReserveGiB);
            }
        }
        else
        {
            state.High = 0;
            state.Low++;
            if (state.Raised && state.Low >= _needed)
            {
                state.Raised = false;
                LogCleared(logger, uuid, state.ExternalGiB ?? 0, state.ReserveGiB);
            }
        }
    }

    private double? ReserveOf(string uuid) =>
        _reserves.TryGetValue(uuid, out double reserve) ? reserve : _defaultReserve;

    private static string Describe(GpuState gpu) => string.Create(CultureInfo.InvariantCulture,
        $"GPU {gpu.Index}{(gpu.Name is { Length: > 0 } name ? $" ({name})" : "")}: {gpu.ExternalGiB:0.0} GiB used by programs other than mirbadllm's models, above its {gpu.ReserveGiB:0.0} GiB reserve ({gpu.UsedGiB:0.0} GiB used, {gpu.ModelsGiB:0.0} GiB measured at start for models).");

    /// <summary>
    /// Σ measured GiB per GPU over the records, and the GPUs with an instance that has no measurement yet. Pure, for tests.
    /// </summary>
    internal static ModelShares SharesPerGpu(IReadOnlyList<ProcessRecord> processRecords)
    {
        var measured = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var unmeasured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in processRecords)
        {
            var gpus = record.Gpus.Select(g => g.Uuid).Where(u => !string.IsNullOrEmpty(u)).ToList();
            if (gpus.Count == 0)
            {
                continue;
            }

            // A Ready instance whose start was not measured (an nvidia-smi failure at create) counts its budget, else the
            // GPU would stay unjudged until the model restarts (MN-219); one still starting stays unjudged.
            double? share = record.MeasuredGpuGiB ?? (record.ReadyAt is not null ? BudgetOf(record) : null);
            if (share is not { } total)
            {
                unmeasured.UnionWith(gpus);
                continue;
            }

            var weights = TensorSplit(record.Spec.Argv, gpus.Count) ?? [.. gpus.Select(_ => 1.0)];
            double sum = weights.Sum();
            for (int i = 0; i < gpus.Count; i++)
            {
                measured[gpus[i]] = measured.GetValueOrDefault(gpus[i]) + (total * weights[i] / sum);
            }
        }

        return new ModelShares(measured, unmeasured);
    }

    private static double? BudgetOf(ProcessRecord record) =>
        record.Spec.Labels.TryGetValue(ContainerLabels.BudgetGiB, out string? text) &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double budget) && double.IsFinite(budget) && budget >= 0
            ? budget
            : null;

    /// <summary>The <c>--tensor-split a,b,…</c> weights of an argv when there is one per GPU and they are positive; else null.</summary>
    internal static IReadOnlyList<double>? TensorSplit(IReadOnlyList<string> argv, int gpuCount)
    {
        for (int i = 0; i < argv.Count - 1; i++)
        {
            if (argv[i] is not ("--tensor-split" or "-ts"))
            {
                continue;
            }

            var parts = argv[i + 1].Split([',', '/'], StringSplitOptions.TrimEntries);
            var weights = new List<double>();
            foreach (string part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) || w < 0 || !double.IsFinite(w))
                {
                    return null;
                }

                weights.Add(w);
            }

            return weights.Count == gpuCount && weights.Sum() > 0 ? weights : null;
        }

        return null;
    }

    private static Dictionary<string, double> Normalize(IReadOnlyDictionary<string, double> reserves) =>
        reserves.Where(kv => double.IsFinite(kv.Value) && kv.Value >= 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    internal sealed record ModelShares(IReadOnlyDictionary<string, double> MeasuredGiB, IReadOnlySet<string> Unmeasured);

    private sealed class GpuState
    {
        public int Index { get; set; }

        public string? Name { get; set; }

        public double UsedGiB { get; set; }

        public double ModelsGiB { get; set; }

        public double? ExternalGiB { get; set; }

        public double ReserveGiB { get; set; }

        public int High { get; set; }

        public int Low { get; set; }

        public bool Raised { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "processes.json could not be read; the external GPU use is not judged this sample.")]
    private static partial void LogRecordsUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GPU {Gpu}: {ExternalGiB:0.0} GiB used outside mirbadllm's models, above the {ReserveGiB:0.0} GiB reserve (gpu_external_use_high).")]
    private static partial void LogRaised(ILogger logger, string gpu, double externalGiB, double reserveGiB);

    [LoggerMessage(Level = LogLevel.Information, Message = "GPU {Gpu}: external use back to {ExternalGiB:0.0} GiB, within the {ReserveGiB:0.0} GiB reserve.")]
    private static partial void LogCleared(ILogger logger, string gpu, double externalGiB, double reserveGiB);
}
