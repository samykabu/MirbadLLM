using MirbadLLM.Core;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// The node's GPUs for the capability report (design §3.3) and the P2 inventory (<see cref="IGpuInventory"/>, MN-104):
/// <c>nvidia-smi --query-gpu=index,uuid,name,memory.total,compute_cap,driver_version</c>, on Linux and Windows alike.
/// Drivers older than <c>compute_cap</c> support are asked again without it.
/// <para><see cref="GetAsync"/> serves the capability report as in P1: the first successful read is cached (GPUs do not
/// change while the agent runs), a failed one is retried on the next call, a missing <c>nvidia-smi</c> is remembered.
/// <see cref="RefreshAsync"/> always asks <c>nvidia-smi</c> again, so a renumbered or removed GPU is seen; it also updates
/// what <see cref="GetAsync"/> returns.</para>
/// <para><see cref="Last"/> is the last <b>successful</b> read: a failing <c>nvidia-smi</c> leaves it as it was, so a
/// transient failure does not make every placed UUID look missing (<see cref="IGpuInventory.Missing"/>). It becomes null
/// only when <c>nvidia-smi</c> is not installed.</para>
/// </summary>
public sealed class NvidiaSmiGpuInventory(INvidiaSmiRunner runner) : IGpuStartupInventory
{
    /// <summary>Full query (index, uuid, name, memory.total, compute_cap, driver_version).</summary>
    public const string Arguments = "--query-gpu=index,uuid,name,memory.total,compute_cap,driver_version --format=csv,noheader,nounits";

    /// <summary>Fallback for drivers without <c>compute_cap</c>.</summary>
    public const string LegacyArguments = "--query-gpu=index,uuid,name,memory.total,driver_version --format=csv,noheader,nounits";

    private volatile IReadOnlyList<GpuCapability>? _cached;
    private volatile IReadOnlyList<GpuCapability>? _last;

    /// <inheritdoc />
    public IReadOnlyList<GpuCapability>? Last => _last;

    /// <summary>
    /// True once <c>nvidia-smi</c> answered: a successful read, or "not installed". False while every run so far failed (the
    /// driver not loaded yet at boot, for example), when <see cref="GetAsync"/>'s empty list does not mean "no GPU".
    /// </summary>
    public bool Answered => _cached is not null;

    /// <summary>The GPUs in index order; empty when there is no <c>nvidia-smi</c> or it fails. Never throws except for cancellation.</summary>
    public async Task<IReadOnlyList<GpuCapability>> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        // Concurrent first calls may both run nvidia-smi; both results are equal, so the race is harmless.
        return await ReadAsync(ct).ConfigureAwait(false) ?? [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GpuCapability>> RefreshAsync(CancellationToken ct = default) =>
        await ReadAsync(ct).ConfigureAwait(false) ?? [];

    /// <summary>Runs the query (and the legacy one when needed). Null when <c>nvidia-smi</c> ran but failed.</summary>
    private async Task<IReadOnlyList<GpuCapability>?> ReadAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(Arguments, ct).ConfigureAwait(false);
        if (!result.Found)
        {
            _cached = [];
            _last = null;
            return [];
        }

        IReadOnlyList<GpuCapability>? gpus = result.Succeeded ? Parse(result.Output, withComputeCap: true) : null;
        if (gpus is null)
        {
            var legacy = await runner.RunAsync(LegacyArguments, ct).ConfigureAwait(false);
            gpus = legacy.Succeeded ? Parse(legacy.Output, withComputeCap: false) : null;
        }

        if (gpus is null)
        {
            return null;
        }

        _cached = gpus;
        _last = gpus;
        return gpus;
    }

    /// <summary>
    /// Parses the CSV of <see cref="Arguments"/> (or <see cref="LegacyArguments"/>). Null when a row is malformed or has
    /// no UUID. A GPU without its own memory total (GB10) shares host memory.
    /// </summary>
    public static IReadOnlyList<GpuCapability>? Parse(string output, bool withComputeCap)
    {
        int columns = withComputeCap ? 6 : 5;
        var gpus = new List<GpuCapability>();
        foreach (var row in NvidiaSmiCsv.Rows(output))
        {
            if (row.Length < columns || NvidiaSmiCsv.Number(row[0]) is not { } index || NvidiaSmiCsv.Text(row[1]) is not { } uuid)
            {
                return null;
            }

            double? memory = NvidiaSmiCsv.GiB(row[3]);
            gpus.Add(new GpuCapability
            {
                Index = (int)index,
                Uuid = uuid,
                Name = NvidiaSmiCsv.Text(row[2]) ?? "GPU",
                MemoryTotalGiB = memory,
                ComputeCapability = withComputeCap ? NvidiaSmiCsv.Text(row[4]) : null,
                Driver = NvidiaSmiCsv.Text(row[withComputeCap ? 5 : 4]),
                SharesHostMemory = memory is null,
            });
        }

        return gpus.OrderBy(g => g.Index).ToList();
    }

    /// <summary>Discrete when any GPU has its own VRAM, unified otherwise (also for a node without GPUs).</summary>
    public static MemoryModel MemoryModelOf(IEnumerable<GpuCapability> gpus) =>
        gpus.Any(g => !g.SharesHostMemory) ? MemoryModel.Discrete : MemoryModel.Unified;
}
