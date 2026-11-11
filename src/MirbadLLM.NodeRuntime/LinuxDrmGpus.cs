using System.Globalization;

namespace MirbadLLM.NodeRuntime;

/// <summary>One GPU as the kernel's DRM subsystem lists it under <c>/sys/class/drm</c>.</summary>
/// <param name="Card">Card number (<c>card1</c> → 1).</param>
/// <param name="PciAddress">PCI address, e.g. <c>0000:03:00.0</c>: the card's identity (it changes only when the card moves to another slot).</param>
/// <param name="VendorId">PCI vendor id (<c>0x1002</c> AMD).</param>
/// <param name="DeviceId">PCI device id.</param>
/// <param name="ProductName">The driver's product name, when it exposes one.</param>
/// <param name="VramTotalBytes">The driver's VRAM total (<c>mem_info_vram_total</c>).</param>
/// <param name="VramUsedBytes">VRAM in use across every process (<c>mem_info_vram_used</c>); null when unreadable.</param>
/// <param name="RenderNode">The DRM render node (<c>/dev/dri/renderD128</c>) a container needs to use the card; null when there is none.</param>
public sealed record LinuxDrmGpu(
    int Card, string PciAddress, uint VendorId, uint DeviceId, string? ProductName, long VramTotalBytes, long? VramUsedBytes, string? RenderNode);

/// <summary>
/// GPUs with driver-side VRAM accounting on Linux, from sysfs (no library, no tool): the <c>amdgpu</c> driver's
/// <c>mem_info_vram_total</c> and <c>mem_info_vram_used</c> under <c>/sys/class/drm/cardN/device</c>. The used figure is
/// the driver's own count over all processes, which is what per-GPU admission needs. A card without these files (Intel's
/// <c>i915</c> and <c>xe</c> drivers publish no system-wide VRAM figure in sysfs, nor does <c>nouveau</c>) is not listed: a
/// Vulkan node offers only GPUs whose free memory it can read. On an AMD integrated GPU the total is the memory the
/// firmware set aside for it, not system RAM.
/// <para>Not verified on AMD hardware: the parser is tested against sysfs trees written by the tests.</para>
/// </summary>
public static class LinuxDrmGpus
{
    /// <summary>The real sysfs class folder.</summary>
    public const string DefaultSysRoot = "/sys/class/drm";

    /// <summary>The real device-node folder.</summary>
    public const string DefaultDevRoot = "/dev/dri";

    /// <summary>
    /// The cards under <paramref name="sysRoot"/> that report VRAM, in card order. Null when the folder does not exist (no
    /// DRM subsystem); an unreadable card is skipped. <paramref name="devRoot"/> is where render nodes are named.
    /// </summary>
    public static IReadOnlyList<LinuxDrmGpu>? Read(string sysRoot = DefaultSysRoot, string devRoot = DefaultDevRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(sysRoot);
        ArgumentException.ThrowIfNullOrEmpty(devRoot);
        if (!Directory.Exists(sysRoot))
        {
            return null;
        }

        var gpus = new List<LinuxDrmGpu>();
        foreach (string cardPath in Directory.EnumerateDirectories(sysRoot, "card*"))
        {
            // Connectors are cardN-<output>; only cardN is a device.
            string name = Path.GetFileName(cardPath);
            if (!int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int card))
            {
                continue;
            }

            string device = Path.Combine(cardPath, "device");
            if (ReadLong(Path.Combine(device, "mem_info_vram_total")) is not { } total || total <= 0)
            {
                continue;
            }

            if (PciAddressOf(device) is not { } address)
            {
                continue;
            }

            gpus.Add(new LinuxDrmGpu(
                card,
                address,
                ReadHex(Path.Combine(device, "vendor")) ?? 0,
                ReadHex(Path.Combine(device, "device")) ?? 0,
                ReadText(Path.Combine(device, "product_name")),
                total,
                ReadLong(Path.Combine(device, "mem_info_vram_used")),
                RenderNodeOf(device, devRoot)));
        }

        return [.. gpus.OrderBy(g => g.Card)];
    }

    /// <summary>
    /// The card's PCI address from its <c>uevent</c> file (<c>PCI_SLOT_NAME=0000:03:00.0</c>); null when it is not a PCI
    /// device or the file cannot be read.
    /// </summary>
    internal static string? PciAddressOf(string devicePath)
    {
        const string Key = "PCI_SLOT_NAME=";
        string? uevent = ReadText(Path.Combine(devicePath, "uevent"));
        string? line = uevent?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(Key, StringComparison.Ordinal));
        string? address = line?[Key.Length..].ToLowerInvariant();
        return address is not null && IsPciAddress(address) ? address : null;
    }

    /// <summary>True for <c>dddd:bb:dd.f</c> (hex digits).</summary>
    internal static bool IsPciAddress(string text) =>
        text.Length == 12 && text[4] == ':' && text[7] == ':' && text[10] == '.'
        && text.Where((_, i) => i is not (4 or 7 or 10)).All(Uri.IsHexDigit);

    private static string? RenderNodeOf(string devicePath, string devRoot)
    {
        string drm = Path.Combine(devicePath, "drm");
        if (!Directory.Exists(drm))
        {
            return null;
        }

        string? node = Directory.EnumerateFileSystemEntries(drm, "renderD*").Select(Path.GetFileName).Order(StringComparer.Ordinal).FirstOrDefault();
        return node is null ? null : devRoot.TrimEnd('/') + "/" + node;
    }

    private static string? ReadText(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadLong(string path) =>
        long.TryParse(ReadText(path), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static uint? ReadHex(string path)
    {
        string? text = ReadText(path);
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value) ? value : null;
    }
}
