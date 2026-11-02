using MirbadLLM.Core;
using MirbadLLM.Web.Components.Widgets;

namespace MirbadLLM.Web;

/// <summary>
/// One colour per mirbadllm model across every per-model chart: the model's position among all registered model
/// ids (ordinal sort) picks a fixed categorical slot, so a model keeps its colour while others start and stop. Past
/// the eighth slot a model gets the neutral "other" tone (and the legend names it) instead of a generated hue.
/// </summary>
public static class ModelColors
{
    public const int Slots = 8;

    /// <summary>Key prefix for a named non-model GPU consumer (e.g. "other:ComfyUI"); they take the slots after the models.</summary>
    public const string OtherPrefix = "other:";

    public static IReadOnlyDictionary<string, string> Assign(IEnumerable<string> modelIds, IEnumerable<string>? otherConsumers = null)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        var keys = modelIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Concat((otherConsumers ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(name => OtherPrefix + name));
        return keys
            .Select((id, i) => (id, color: i < Slots ? $"var(--ss-cat-{i + 1})" : "var(--ss-series-other)"))
            .ToDictionary(x => x.id, x => x.color, StringComparer.Ordinal);
    }
}

/// <summary>Builds the host-level per-model chart series on the Monitoring page from the metrics history.</summary>
public static class MonitoringSeries
{
    public const string OtherProcesses = "Other processes";
    public const string Free = "Free";
    public const string Used = "Used";
    public const string Total = "Total generated";

    /// <summary>
    /// Stacked memory over time, bottom to top: one area per model that held memory at any sample in the window (its
    /// measured GPU memory, or its budget when no measurement was available; a gap while it wasn't running), then each
    /// identifiable non-model GPU consumer (e.g. ComfyUI), then everything else in use, then free memory. The stack's
    /// top is the host's total. "Other processes" is used memory minus all of the above, never below 0.
    /// </summary>
    public static List<LineChartSeries> Memory(
        IReadOnlyList<HostSample> host,
        IReadOnlyDictionary<string, string> colors,
        Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(nameOf);

        var modelIds = host.SelectMany(s => s.ModelMemoryGiB?.Keys ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => SlotOrder(colors, id))
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();

        var result = modelIds
            .Select(id => new LineChartSeries(
                nameOf(id),
                colors.GetValueOrDefault(id, "var(--ss-series-other)"),
                host.Select(s => new LineChartPoint(s.At, s.ModelMemoryGiB is { } m && m.TryGetValue(id, out double gib) ? gib : null)).ToList()))
            .ToList();

        var consumers = host.SelectMany(s => s.OtherGpuGiB?.Keys ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        result.AddRange(consumers.Select(name => new LineChartSeries(
            name,
            colors.GetValueOrDefault(ModelColors.OtherPrefix + name, "var(--ss-series-other)"),
            host.Select(s => new LineChartPoint(s.At, s.OtherGpuGiB is { } o && o.TryGetValue(name, out double gib) ? gib : null)).ToList())));

        result.Add(new LineChartSeries(OtherProcesses, "var(--ss-series-other)", host.Select(s =>
        {
            double used = s.Memory.TotalGiB - s.Memory.AvailableGiB;
            double known = (s.ModelMemoryGiB?.Values.Sum() ?? 0) + (s.OtherGpuGiB?.Values.Sum() ?? 0);
            return new LineChartPoint(s.At, Math.Max(0, used - known));
        }).ToList()));
        result.Add(new LineChartSeries(Free, "var(--ss-series-reserve)", host.Select(s => new LineChartPoint(s.At, Math.Max(0, s.Memory.AvailableGiB))).ToList()));
        return result;
    }

    /// <summary>
    /// Tokens per second per model over time: generated tokens for a generation model, input (prompt) tokens for an
    /// embedding/reranking model, which generates none (its name says "input"). A sample without a rate (the first
    /// after a start) and any stretch longer than <paramref name="gapAfter"/> without samples (the model wasn't
    /// Ready) are gaps, never zeros. A dotted total sums the generation models' generated tokens at each sample
    /// time where at least one of them had a rate (input tokens are not added to it).
    /// </summary>
    public static List<LineChartSeries> Throughput(
        IReadOnlyDictionary<string, IReadOnlyList<ModelSample>> samples,
        IReadOnlyDictionary<string, string> colors,
        Func<string, string> nameOf,
        Func<string, bool> isPooling,
        TimeSpan gapAfter)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentNullException.ThrowIfNull(isPooling);

        var result = new List<LineChartSeries>();
        var totals = new SortedDictionary<DateTimeOffset, double?>();
        foreach (var (id, list) in samples
            .Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => SlotOrder(colors, kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            bool pooling = isPooling(id);
            var points = new List<LineChartPoint>(list.Count + 4);
            DateTimeOffset? previous = null;
            foreach (var sample in list.OrderBy(s => s.At))
            {
                if (previous is { } last && sample.At - last > gapAfter)
                {
                    points.Add(new LineChartPoint(last + (sample.At - last) / 2, null));
                }

                double? rate = sample.HasRates
                    ? pooling ? sample.Kpis.Load.PromptTokensPerSecond : sample.Kpis.Load.GenerationTokensPerSecond
                    : null;
                if (rate is double r && (double.IsNaN(r) || double.IsInfinity(r)))
                {
                    rate = null;
                }

                points.Add(new LineChartPoint(sample.At, rate));
                if (!pooling)
                {
                    totals[sample.At] = rate is double add ? (totals.GetValueOrDefault(sample.At) ?? 0) + add : totals.GetValueOrDefault(sample.At);
                }

                previous = sample.At;
            }

            result.Add(new LineChartSeries(pooling ? $"{nameOf(id)} (input)" : nameOf(id), colors.GetValueOrDefault(id, "var(--ss-series-other)"), points)
            {
                Style = LineStyle.Solid,
            });
        }

        int generationModels = samples.Count(kv => kv.Value.Count > 0 && !isPooling(kv.Key));
        if (generationModels > 1)
        {
            var totalPoints = new List<LineChartPoint>(totals.Count);
            DateTimeOffset? previous = null;
            foreach (var (at, value) in totals)
            {
                if (previous is { } last && at - last > gapAfter)
                {
                    totalPoints.Add(new LineChartPoint(last + (at - last) / 2, null));
                }

                totalPoints.Add(new LineChartPoint(at, value));
                previous = at;
            }

            result.Add(new LineChartSeries(Total, "var(--ss-text-muted)", totalPoints) { Style = LineStyle.Dotted });
        }

        return result;
    }

    /// <summary>
    /// The memory chart's note on where the per-model figures come from in this window: measured, budgets, or both
    /// (the host agent appeared or went away mid-window). Null without samples.
    /// </summary>
    public static string? MemorySourceNote(IReadOnlyList<HostSample> host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var sources = host.Where(s => s.ModelMemoryGiB is not null).Select(s => s.MemorySource).Distinct().ToList();
        return sources.Count switch
        {
            0 => null,
            > 1 => "Per model: GPU memory measured by nvidia-smi where the host agent reported it, the configured budget elsewhere in this window.",
            _ when sources[0] == ModelMemorySource.Measured => "Per model: GPU memory measured by nvidia-smi (through the host agent).",
            _ => "Per model: the configured memory budget. The host agent's GPU process readout isn't available (agent missing or older).",
        };
    }

    /// <summary>One GPU of a discrete node in the window, as the per-GPU charts name it.</summary>
    /// <param name="Uuid">GPU UUID (the identity; a GPU keeps its chart when its index changes).</param>
    /// <param name="Index">Its newest <c>nvidia-smi</c> index in the window.</param>
    public sealed record GpuInWindow(string Uuid, int Index)
    {
        /// <summary>"GPU 0".</summary>
        public string Label => $"GPU {Index}";
    }

    /// <summary>
    /// The GPUs of a discrete node that appear in <paramref name="host"/> (design §8.3), by newest index. Null when no
    /// sample carries per-GPU readings: a unified node (or samples from before P2) keeps today's single-pool charts.
    /// </summary>
    public static IReadOnlyList<GpuInWindow>? Gpus(IReadOnlyList<HostSample> host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.Any(s => s.Gpus is not null))
        {
            return null;
        }

        var newest = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gpu in host.Where(s => s.Gpus is not null).OrderBy(s => s.At).SelectMany(s => s.Gpus!))
        {
            newest[gpu.Uuid] = gpu.Index;
        }

        return [.. newest.Select(kv => new GpuInWindow(kv.Key, kv.Value)).OrderBy(g => g.Index).ThenBy(g => g.Uuid, StringComparer.Ordinal)];
    }

    /// <summary>Utilization per GPU of a discrete node, one line per GPU in index order; a gap where a sample lacks the GPU.</summary>
    public static List<LineChartSeries> GpuUtilization(IReadOnlyList<HostSample> host, IReadOnlyList<GpuInWindow> gpus)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(gpus);
        return [.. gpus.Select((gpu, i) => new LineChartSeries(
            gpu.Label,
            i < ModelColors.Slots ? $"var(--ss-cat-{i + 1})" : "var(--ss-series-other)",
            host.Select(s => new LineChartPoint(s.At, Find(s, gpu.Uuid)?.UtilizationPercent)).ToList()))];
    }

    /// <summary>
    /// One GPU's VRAM stacked over time (a discrete node's "Memory by model" per GPU, design §8.3): one area per model that
    /// held memory on this GPU at any sample in the window, then "Other processes" (used minus the models, never below
    /// 0), then free (total minus used). The stack's top is the GPU's total. A sample without this GPU, or without its
    /// memory readings, is a gap in every area.
    /// </summary>
    public static List<LineChartSeries> GpuMemory(
        IReadOnlyList<HostSample> host,
        string uuid,
        IReadOnlyDictionary<string, string> colors,
        Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(uuid);
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(nameOf);

        var readings = host.Select(s => (s.At, Gpu: Find(s, uuid))).ToList();
        var modelIds = readings.SelectMany(r => r.Gpu?.ModelMemoryGiB?.Keys ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => SlotOrder(colors, id))
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();

        static bool Measured(HostGpuSample? gpu) => gpu is { MemoryUsedGiB: not null, MemoryTotalGiB: not null };

        var result = modelIds
            .Select(id => new LineChartSeries(
                nameOf(id),
                colors.GetValueOrDefault(id, "var(--ss-series-other)"),
                readings.Select(r => new LineChartPoint(r.At,
                    Measured(r.Gpu) && r.Gpu!.ModelMemoryGiB is { } m && m.TryGetValue(id, out double gib) ? gib : null)).ToList()))
            .ToList();

        result.Add(new LineChartSeries(OtherProcesses, "var(--ss-series-other)", readings.Select(r => new LineChartPoint(r.At,
            Measured(r.Gpu) ? Math.Max(0, r.Gpu!.MemoryUsedGiB!.Value - (r.Gpu.ModelMemoryGiB?.Values.Sum() ?? 0)) : null)).ToList()));
        result.Add(new LineChartSeries(Free, "var(--ss-series-reserve)", readings.Select(r => new LineChartPoint(r.At,
            Measured(r.Gpu) ? Math.Max(0, r.Gpu!.MemoryTotalGiB!.Value - r.Gpu.MemoryUsedGiB!.Value) : null)).ToList()));
        return result;
    }

    /// <summary>
    /// Host RAM of a discrete node stacked over time: used, then free; the top is the host's total. Models live in VRAM
    /// there (the per-GPU charts), so RAM is not split per model.
    /// </summary>
    public static List<LineChartSeries> HostRam(IReadOnlyList<HostSample> host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return
        [
            new LineChartSeries(Used, "var(--ss-series-1)", host.Select(s => new LineChartPoint(s.At, Math.Max(0, s.Memory.TotalGiB - s.Memory.AvailableGiB))).ToList()),
            new LineChartSeries(Free, "var(--ss-series-reserve)", host.Select(s => new LineChartPoint(s.At, Math.Max(0, s.Memory.AvailableGiB))).ToList()),
        ];
    }

    /// <summary>
    /// The model ids that belong to <paramref name="nodeId"/>: placed there, or unplaced while it is the default (bootstrap)
    /// node, where every P1 model ran. Monitoring's model tabs and throughput chart list only these (design §8.3).
    /// </summary>
    public static HashSet<string> ModelsOn(IEnumerable<ModelDefinition> definitions, string nodeId, string defaultNodeId)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        bool isDefault = string.Equals(nodeId, defaultNodeId, StringComparison.Ordinal);
        return definitions
            .Where(d => d.Placement is { } p ? string.Equals(p.NodeId, nodeId, StringComparison.Ordinal) : isDefault)
            .Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HostGpuSample? Find(HostSample sample, string uuid) =>
        sample.Gpus?.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal));

    private static int SlotOrder(IReadOnlyDictionary<string, string> colors, string id)
    {
        int i = 0;
        foreach (var key in colors.Keys.Order(StringComparer.Ordinal))
        {
            if (string.Equals(key, id, StringComparison.Ordinal))
            {
                return i;
            }

            i++;
        }

        return int.MaxValue;
    }
}
