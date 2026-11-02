using System.Globalization;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Fakes;

/// <summary>
/// Settings of the development fake runtime (MN-042; discrete mode MN-122), section <see cref="SectionName"/> of
/// <c>agent.json</c> (top level, next to <c>Agent</c>), or <c>--dev-fake-runtime</c> on the command line for <c>Enabled</c>:
/// <code>
/// "DevFakeRuntime": { "Enabled": true, "Memory": "unified:64", "StartDelay": "00:00:02", "TokenDelay": "00:00:00.02" }
/// "DevFakeRuntime": { "Enabled": true, "Memory": "discrete:2x16", "HostMemoryGiB": 32,
///                     "GpuUuids": [ "GPU-00000000-0000-0000-0000-00000000de00", "GPU-00000000-0000-0000-0000-00000000de01" ] }
/// </code>
/// </summary>
public sealed class DevFakeRuntimeOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "DevFakeRuntime";

    /// <summary>Most fake GPUs a discrete node may have.</summary>
    public const int MaxGpus = 16;

    /// <summary>VRAM every fake GPU uses with no model on it (desktop, driver), as a real card reports.</summary>
    public const double GpuBaselineUsedGiB = 0.5;

    private const string DefaultGpuName = "Fake GPU (dev)";

    /// <summary>Runs fake models instead of containers. Refused outside the Development environment.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The node's fake memory model (design §13 item 5): <c>unified:&lt;GiB&gt;</c> (the DGX: one pool, no GPU list) or
    /// <c>discrete:&lt;n&gt;x&lt;GiB&gt;</c> (a PC: <c>n</c> GPUs with their own VRAM, e.g. <c>discrete:2x16</c>; MN-122).
    /// </summary>
    public string Memory { get; set; } = "unified:64";

    /// <summary>Host RAM of a discrete fake node (the host pool of its <c>/status</c>). Ignored for unified nodes.</summary>
    public double HostMemoryGiB { get; set; } = 64;

    /// <summary>
    /// UUIDs of a discrete node's GPUs in index order, one per GPU. Null = the placeholders <see cref="PlaceholderUuid"/>
    /// (<c>GPU-00000000-0000-0000-0000-00000000de00</c>, <c>…de01</c>, …). Reordering them and restarting the agent
    /// renumbers the GPUs; replacing one is a swapped card (a placement on the old UUID then gets <c>gpu_missing</c>).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Bound from configuration.")]
    public List<string>? GpuUuids { get; set; }

    /// <summary>Product name the fake <c>nvidia-smi</c> reports for every GPU of a discrete node.</summary>
    public string GpuName { get; set; } = DefaultGpuName;

    /// <summary>How long a fake model "loads" (Starting, <c>/health</c> 503) before it is Ready.</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Delay between generated tokens.</summary>
    public TimeSpan TokenDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>The memory model parsed from <see cref="Memory"/> by <see cref="Validate"/>.</summary>
    public MemoryModel MemoryModel { get; private set; } = MemoryModel.Unified;

    /// <summary>True for <c>discrete:…</c>.</summary>
    public bool IsDiscrete => MemoryModel == MemoryModel.Discrete;

    /// <summary>Total host memory: the unified pool parsed from <see cref="Memory"/>, or <see cref="HostMemoryGiB"/> when discrete.</summary>
    public double MemoryGiB { get; private set; } = 64;

    /// <summary>The discrete node's GPUs in index order (empty when unified), set by <see cref="Validate"/>.</summary>
    public IReadOnlyList<DevFakeGpu> Gpus { get; private set; } = [];

    /// <summary>Placeholder UUID of fake GPU <paramref name="index"/> (hex only, like the recorded fixtures' placeholders).</summary>
    public static string PlaceholderUuid(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"GPU-00000000-0000-0000-0000-00000000de{index:x2}");

    /// <summary>Parses <see cref="Memory"/>, the GPUs and the delays; throws <see cref="ArgumentException"/> naming the problem.</summary>
    public void Validate()
    {
        if (StartDelay < TimeSpan.Zero || TokenDelay < TimeSpan.Zero)
        {
            throw new ArgumentException($"{SectionName}:StartDelay and TokenDelay must not be negative.", nameof(StartDelay));
        }

        string memory = (Memory ?? "").Trim();
        int colon = memory.IndexOf(':', StringComparison.Ordinal);
        string model = colon < 0 ? memory : memory[..colon];
        string size = colon < 0 ? "" : memory[(colon + 1)..];
        if (string.Equals(model, "discrete", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDiscrete(size);
            return;
        }

        if (!string.Equals(model, "unified", StringComparison.OrdinalIgnoreCase) || !TryGiB(size, out double gib))
        {
            throw new ArgumentException(
                $"{SectionName}:Memory '{Memory}' is not unified:<GiB> or discrete:<n>x<GiB> (e.g. unified:64, discrete:2x16).", nameof(Memory));
        }

        if (GpuUuids is { Count: > 0 })
        {
            throw new ArgumentException(
                $"{SectionName}:GpuUuids is for discrete nodes; a unified node ({Memory}) has no GPU list.", nameof(GpuUuids));
        }

        MemoryModel = MemoryModel.Unified;
        MemoryGiB = gib;
        Gpus = [];
    }

    private void ValidateDiscrete(string size)
    {
        int x = size.IndexOfAny(['x', 'X']);
        if (x <= 0
            || !int.TryParse(size[..x], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count is < 1 or > MaxGpus
            || !TryGiB(size[(x + 1)..], out double perGpu) || perGpu > 1024)
        {
            throw Invalid(
                $"{SectionName}:Memory '{Memory}' is not discrete:<n>x<GiB> with 1–{MaxGpus} GPUs of at most 1024 GiB (e.g. discrete:2x16).",
                nameof(Memory));
        }

        if (HostMemoryGiB is not (> 0 and <= 4096))
        {
            throw Invalid(
                $"{SectionName}:HostMemoryGiB must be above 0 and at most 4096 (it is {HostMemoryGiB.ToString(CultureInfo.InvariantCulture)}).",
                nameof(HostMemoryGiB));
        }

        List<string> uuids = GpuUuids is { Count: > 0 } configured
            ? [.. configured.Select(u => (u ?? "").Trim())]
            : [.. Enumerable.Range(0, count).Select(PlaceholderUuid)];
        if (uuids.Count != count)
        {
            throw Invalid(
                $"{SectionName}:GpuUuids lists {uuids.Count} UUID(s) for {count} GPU(s) ({Memory}); list one per GPU.", nameof(GpuUuids));
        }

        if (uuids.FirstOrDefault(u => !u.StartsWith("GPU-", StringComparison.Ordinal) || u.Length < 8 || u.Contains(',', StringComparison.Ordinal)) is { } bad)
        {
            throw Invalid($"{SectionName}:GpuUuids: '{bad}' is not a GPU UUID (GPU-…, no commas).", nameof(GpuUuids));
        }

        if (uuids.GroupBy(u => u, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw Invalid($"{SectionName}:GpuUuids lists {duplicate.Key} more than once.", nameof(GpuUuids));
        }

        string name = string.IsNullOrWhiteSpace(GpuName) ? DefaultGpuName : GpuName.Trim();
        if (name.Contains(',', StringComparison.Ordinal))
        {
            throw Invalid($"{SectionName}:GpuName '{GpuName}' must not contain commas (nvidia-smi prints CSV).", nameof(GpuName));
        }

        MemoryModel = MemoryModel.Discrete;
        MemoryGiB = HostMemoryGiB;
        Gpus = [.. uuids.Select((uuid, index) => new DevFakeGpu(index, uuid, name, perGpu))];
    }

    private static ArgumentException Invalid(string message, string paramName) => new(message, paramName);

    private static bool TryGiB(string text, out double gib) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out gib) && gib is > 0 and <= 4096;
}

/// <summary>One GPU of a discrete fake node (MN-122).</summary>
/// <param name="Index">Its index: its position in <see cref="DevFakeRuntimeOptions.GpuUuids"/>.</param>
/// <param name="Uuid">Its UUID, the identity a placement stores (design §5.1).</param>
/// <param name="Name">Product name.</param>
/// <param name="TotalGiB">VRAM total.</param>
public sealed record DevFakeGpu(int Index, string Uuid, string Name, double TotalGiB);
