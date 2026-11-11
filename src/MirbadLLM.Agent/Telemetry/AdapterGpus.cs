using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// The GPU inventory the agent decides its memory model from at start and reports in its capabilities:
/// <see cref="NvidiaSmiGpuInventory"/> on an NVIDIA node, <see cref="AdapterGpus"/> on a Vulkan node.
/// </summary>
public interface IGpuStartupInventory : IGpuInventory
{
    /// <summary>The GPUs in index order; the first successful read is kept. Empty when there are none or the read fails.</summary>
    Task<IReadOnlyList<GpuCapability>> GetAsync(CancellationToken ct = default);

    /// <summary>True once a read answered (GPUs, or "none"); false while every read so far failed.</summary>
    bool Answered { get; }
}

/// <summary>One GPU of a Vulkan node (<c>Agent:Accelerator = vulkan</c>) as its source read it.</summary>
/// <param name="Index">
/// The number that selects the GPU: on Windows the Vulkan enumeration index llama.cpp takes in
/// <c>GGML_VK_VISIBLE_DEVICES</c>, on Linux the DRM card number (display only; a container gets the render node).
/// </param>
/// <param name="Uuid">Identity for placements, <c>GPU-…</c>.</param>
/// <param name="Name">Product name.</param>
/// <param name="TotalGiB">VRAM total; null when the driver reports none.</param>
/// <param name="UsedGiB">VRAM in use by every process on the machine; null when it cannot be read. Never estimated.</param>
/// <param name="DeviceNodes">Device nodes a container needs for this GPU (Linux: its DRM render node); empty elsewhere.</param>
public sealed record GpuAdapter(int Index, string Uuid, string Name, double? TotalGiB, double? UsedGiB, IReadOnlyList<string> DeviceNodes);

/// <summary>One read of a Vulkan node's GPUs.</summary>
/// <param name="Adapters">The GPUs; null when they could not be listed at all.</param>
/// <param name="Error">
/// Why the read is incomplete: with <paramref name="Adapters"/> null, why nothing could be listed; otherwise why the
/// memory in use of one or more GPUs could not be read (their <see cref="GpuAdapter.UsedGiB"/> is then null).
/// </param>
public sealed record GpuAdapterRead(IReadOnlyList<GpuAdapter>? Adapters, string? Error = null);

/// <summary>Reads a Vulkan node's GPUs without <c>nvidia-smi</c>. Seam for tests.</summary>
public interface IGpuAdapterSource
{
    /// <summary>
    /// Reads every GPU now. <paramref name="reenumerate"/> asks for the adapter list itself to be read again (a GPU added,
    /// removed or renumbered); otherwise a source may keep it and read only the memory in use. Never throws.
    /// </summary>
    GpuAdapterRead Read(bool reenumerate = false);
}

/// <summary>
/// The Windows source: adapters from the Vulkan loader (<see cref="VulkanAdapters"/>: identity, VRAM total, enumeration
/// index) and memory in use from the operating system's per-adapter counters (<see cref="WindowsGpuMemoryCounters"/>),
/// matched by LUID. Only discrete adapters are listed: an integrated GPU's memory is system RAM, which per-GPU admission
/// cannot budget, so a PC whose only GPU is integrated is a CPU-only node. An adapter without a device UUID is left out
/// (it could not be placed on), and an adapter whose counter is missing keeps a null <see cref="GpuAdapter.UsedGiB"/>.
/// <para>Checked against <c>nvidia-smi</c> on an RTX 5070 and an RTX 5060 Ti (the same UUIDs; total within the VRAM the
/// driver reserves; used within 40 MiB). Not yet run on an AMD or Intel discrete card.</para>
/// </summary>
public sealed class VulkanGpuSource(
    Func<IReadOnlyList<VulkanAdapter>> enumerate,
    Func<(IReadOnlyDictionary<ulong, long>? Usage, string? Error)> readUsage) : IGpuAdapterSource
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;
    private readonly Lock _gate = new();
    private IReadOnlyList<VulkanAdapter>? _adapters;

    /// <summary>The source over the real Vulkan loader and performance counters.</summary>
    [SupportedOSPlatform("windows")]
    public static VulkanGpuSource ForWindows()
    {
        var counters = new WindowsGpuMemoryCounters();
        return new VulkanGpuSource(VulkanAdapters.Enumerate, () => (counters.Read(out string? error), error));
    }

    /// <inheritdoc />
    public GpuAdapterRead Read(bool reenumerate = false)
    {
        lock (_gate)
        {
            if (reenumerate || _adapters is null)
            {
                try
                {
                    _adapters = enumerate();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _adapters = null;
                    return new GpuAdapterRead(null, $"The Vulkan loader could not list the GPUs: {ex.Message}");
                }
            }

            (IReadOnlyDictionary<ulong, long>? usage, string? usageError) = (null, null);
            try
            {
                (usage, usageError) = readUsage();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                usageError = ex.Message;
            }

            return ToRead(_adapters, usage, usageError);
        }
    }

    /// <summary>The read for <paramref name="adapters"/> and the counters' answer (<paramref name="usage"/> null = unreadable). Pure.</summary>
    public static GpuAdapterRead ToRead(IReadOnlyList<VulkanAdapter> adapters, IReadOnlyDictionary<ulong, long>? usage, string? usageError)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var gpus = new List<GpuAdapter>();
        var withoutCounter = new List<string>();
        foreach (VulkanAdapter adapter in adapters.OrderBy(a => a.Index))
        {
            if (adapter.Type != VulkanDeviceType.Discrete || adapter.DeviceUuid.All(c => c is '0' or '-'))
            {
                continue;
            }

            string uuid = "GPU-" + adapter.DeviceUuid;
            if (gpus.Any(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal)))
            {
                continue; // the same card through a second driver
            }

            double? used = null;
            if (usage is not null && adapter.Luid is { } luid && usage.TryGetValue(luid, out long bytes))
            {
                used = bytes / BytesPerGiB;
            }
            else if (usage is not null)
            {
                withoutCounter.Add(adapter.Name);
            }

            gpus.Add(new GpuAdapter(
                adapter.Index, uuid, adapter.Name, adapter.DedicatedBytes > 0 ? adapter.DedicatedBytes / BytesPerGiB : null, used, []));
        }

        string? error = gpus.Count == 0 ? null
            : usage is null ? $"GPU memory in use cannot be read: {usageError ?? "the performance counters did not answer."}"
            : withoutCounter.Count > 0 ? $"Windows lists no GPU memory counter for {string.Join(", ", withoutCounter)}."
            : null;
        return new GpuAdapterRead(gpus, error);
    }
}

/// <summary>
/// The Linux source: <see cref="LinuxDrmGpus"/> (the <c>amdgpu</c> driver's VRAM total and system-wide VRAM in use in
/// sysfs). The identity is the PCI address (<c>GPU-pci-0000-03-00-0</c>), the index the DRM card number, and the device
/// node the card's render node, which the container driver maps into the model's container. Not verified on AMD hardware.
/// </summary>
public sealed class LinuxDrmGpuSource(string sysRoot = LinuxDrmGpus.DefaultSysRoot, string devRoot = LinuxDrmGpus.DefaultDevRoot) : IGpuAdapterSource
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    /// <inheritdoc />
    public GpuAdapterRead Read(bool reenumerate = false)
    {
        IReadOnlyList<LinuxDrmGpu>? cards;
        try
        {
            cards = LinuxDrmGpus.Read(sysRoot, devRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GpuAdapterRead(null, $"{sysRoot} could not be read: {ex.Message}");
        }

        if (cards is null)
        {
            return new GpuAdapterRead(null, $"{sysRoot} does not exist: this kernel exposes no DRM devices.");
        }

        var gpus = cards.Select(c => new GpuAdapter(
            c.Card,
            UuidOf(c.PciAddress),
            NameOf(c),
            c.VramTotalBytes / BytesPerGiB,
            c.VramUsedBytes is { } used ? used / BytesPerGiB : null,
            c.RenderNode is null ? [] : [c.RenderNode])).ToList();
        var unread = gpus.Where(g => g.UsedGiB is null).Select(g => g.Name).ToList();
        return new GpuAdapterRead(gpus, unread.Count == 0 ? null : $"mem_info_vram_used could not be read for {string.Join(", ", unread)}.");
    }

    /// <summary><c>GPU-pci-0000-03-00-0</c> for <c>0000:03:00.0</c>.</summary>
    public static string UuidOf(string pciAddress)
    {
        ArgumentException.ThrowIfNullOrEmpty(pciAddress);
        return "GPU-pci-" + pciAddress.Replace(':', '-').Replace('.', '-');
    }

    private static string NameOf(LinuxDrmGpu card)
    {
        if (!string.IsNullOrWhiteSpace(card.ProductName))
        {
            return card.ProductName;
        }

        string vendor = card.VendorId switch { 0x1002 => "AMD", 0x8086 => "Intel", 0x10de => "NVIDIA", _ => "GPU" };
        return string.Create(CultureInfo.InvariantCulture, $"{vendor} GPU {card.VendorId:x4}:{card.DeviceId:x4}");
    }
}

/// <summary>
/// A Vulkan node's GPUs for the rest of the agent, over an <see cref="IGpuAdapterSource"/>: the telemetry probe (per-GPU
/// VRAM total, used and free; utilization, power and temperature are not read and stay null), the inventory (capability
/// report, <c>gpu_missing</c>, the memory model at start) and a health reason.
/// <para>Free VRAM is <c>total − used</c> with both read from the machine, and null as soon as one of them is missing.
/// Discrete admission refuses a start on a GPU without a free figure (<c>ReservationManager</c>), so a GPU whose memory
/// in use cannot be read takes no model until it can, unless the operator forces the start. The node then reports
/// <see cref="HealthReasonCodes.GpuProbeFailing"/> with the reason.</para>
/// </summary>
public sealed partial class AdapterGpus(IGpuAdapterSource source, ILogger<AdapterGpus> logger) : IGpuTelemetryProbe, IGpuStartupInventory, IHealthContributor
{
    private volatile GpuTelemetryReading? _last;
    private volatile IReadOnlyList<GpuAdapter>? _adapters;
    private volatile string? _error;
    private string? _logged = "";

    /// <inheritdoc />
    public GpuTelemetryReading? Last => _last;

    /// <inheritdoc />
    IReadOnlyList<GpuCapability>? IGpuInventory.Last => _adapters is { } adapters ? Capabilities(adapters) : null;

    /// <inheritdoc />
    public bool Answered => _adapters is not null;

    /// <inheritdoc />
    public Task<GpuTelemetryReading> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ReadOnce(reenumerate: false));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GpuCapability>> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_adapters is null)
        {
            ReadOnce(reenumerate: false);
        }

        return Task.FromResult(_adapters is { } adapters ? Capabilities(adapters) : []);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GpuCapability>> RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        GpuTelemetryReading reading = ReadOnce(reenumerate: true);
        return Task.FromResult(reading.Status == GpuProbeStatus.Ok && _adapters is { } adapters ? Capabilities(adapters) : []);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default)
    {
        if (_last is null)
        {
            ReadOnce(reenumerate: false);
        }

        // A failed listing is reported by GpuHealthContributor from the reading's status; this is the other case: the
        // GPUs are listed but their memory in use is not, so starts on them are refused.
        return Task.FromResult<IReadOnlyList<HealthReason>>(
            _last is { Status: GpuProbeStatus.Ok } && _error is { } error
                ? [new HealthReason(HealthReasonCodes.GpuProbeFailing, $"{error} A model is not started on a GPU whose free memory is unknown.")]
                : []);
    }

    /// <summary>The device nodes a container needs for the GPU with <paramref name="uuid"/>; null when it is not listed or has none.</summary>
    public IReadOnlyList<string>? DeviceNodesFor(string uuid) =>
        _adapters?.FirstOrDefault(a => string.Equals(a.Uuid, uuid, StringComparison.Ordinal)) is { DeviceNodes.Count: > 0 } adapter
            ? adapter.DeviceNodes
            : null;

    /// <summary>The telemetry samples of <paramref name="adapters"/>: free is total − used, or null when either is missing.</summary>
    public static IReadOnlyList<GpuSample> Samples(IReadOnlyList<GpuAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        return
        [
            .. adapters.OrderBy(a => a.Index).Select(a => new GpuSample
            {
                Index = a.Index,
                Uuid = a.Uuid,
                Name = a.Name,
                MemoryTotalGiB = a.TotalGiB,
                MemoryUsedGiB = a.UsedGiB,
                MemoryFreeGiB = a.TotalGiB is { } total && a.UsedGiB is { } used ? Math.Max(0, total - used) : null,
            }),
        ];
    }

    private static IReadOnlyList<GpuCapability> Capabilities(IReadOnlyList<GpuAdapter> adapters) =>
    [
        .. adapters.OrderBy(a => a.Index).Select(a => new GpuCapability
        {
            Index = a.Index,
            Uuid = a.Uuid,
            Name = a.Name,
            MemoryTotalGiB = a.TotalGiB,
        }),
    ];

    private GpuTelemetryReading ReadOnce(bool reenumerate)
    {
        GpuAdapterRead read;
        try
        {
            read = source.Read(reenumerate);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            read = new GpuAdapterRead(null, ex.Message);
        }

        GpuTelemetryReading reading;
        if (read.Adapters is { } adapters)
        {
            _adapters = adapters;
            _error = read.Error;
            reading = new GpuTelemetryReading(GpuProbeStatus.Ok, Samples(adapters));
        }
        else
        {
            // The last listing is kept for the inventory (a transient failure must not make every placed GPU look missing).
            _error = read.Error;
            reading = new GpuTelemetryReading(GpuProbeStatus.Failed, [], read.Error ?? "The GPUs could not be read.");
        }

        string state = read.Adapters is null ? "failed: " + reading.Error : read.Error ?? "ok";
        if (!string.Equals(Interlocked.Exchange(ref _logged, state), state, StringComparison.Ordinal))
        {
            if (state == "ok")
            {
                LogReads(logger, read.Adapters!.Count);
            }
            else
            {
                LogIncomplete(logger, state);
            }
        }

        _last = reading;
        return reading;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vulkan node: {Count} GPU(s) read with their memory in use.")]
    private static partial void LogReads(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vulkan node: the GPU read is incomplete, so starts on the affected GPUs are refused: {Reason}")]
    private static partial void LogIncomplete(ILogger logger, string reason);
}
