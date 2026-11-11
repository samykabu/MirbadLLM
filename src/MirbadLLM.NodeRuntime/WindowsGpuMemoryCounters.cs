using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Dedicated GPU memory in use on Windows, per adapter and for the whole system: the performance counter
/// <c>\GPU Adapter Memory(luid_…_phys_N)\Dedicated Usage</c> that Task Manager shows, read through PDH (<c>pdh.dll</c>,
/// P/Invoke, no package). It counts every process, the desktop included, which Vulkan's and DXGI's own queries do not
/// (they answer for the calling process). Instances are named after the adapter's LUID, the same value Vulkan reports as
/// <c>deviceLUID</c>, so a reading is matched to a <see cref="VulkanAdapter"/> without guessing.
/// <para>On an RTX 5070 and an RTX 5060 Ti (driver 621.01) the counter read 2 095 and 4 472 MiB while
/// <c>nvidia-smi</c> reported 2 080 and 4 435 MiB of <c>memory.used</c>, read a few seconds apart on a desktop in use.</para>
/// <para>Windows lets a service read these counters only as SYSTEM, LOCAL SERVICE, NETWORK SERVICE, an administrator or
/// a member of Performance Monitor Users, so <c>install-agent.ps1 -Accelerator vulkan</c> adds the agent's account to
/// that group. Without the right, <see cref="Read"/> returns null with the PDH status and no value is assumed.</para>
/// <para>Only the reads need Windows; <see cref="TryParseLuid"/> is plain text handling.</para>
/// </summary>
public sealed partial class WindowsGpuMemoryCounters : IDisposable
{
    /// <summary>The counter path (English names, so it works on every display language).</summary>
    public const string CounterPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

    private const uint FormatLarge = 0x00000400;
    private const uint MoreData = 0x800007D2;
    private const uint StatusValid = 0;
    private const uint StatusNew = 1;
    private const int ItemSize = 24; // PDH_FMT_COUNTERVALUE_ITEM_W on 64-bit: name pointer, status, padding, value

    private readonly Lock _gate = new();
    private IntPtr _query;
    private IntPtr _counter;

    /// <summary>
    /// Bytes of dedicated memory in use per adapter LUID, summed over the adapter's physical parts. Null when the
    /// counters cannot be read (<paramref name="error"/> says why); an adapter without an instance is simply absent.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public IReadOnlyDictionary<ulong, long>? Read(out string? error)
    {
        lock (_gate)
        {
            if (TryRead(out var usage, out error))
            {
                return usage;
            }

            // A query can go stale (a driver update replaces the provider): open a new one once before giving up.
            Close();
            return TryRead(out usage, out error) ? usage : null;
        }
    }

    /// <summary>
    /// The LUID in an instance name such as <c>luid_0x00000000_0x0001bd18_phys_0</c> (high part, low part); false for
    /// anything else.
    /// </summary>
    public static bool TryParseLuid(string? instance, out ulong luid)
    {
        luid = 0;
        if (instance is null || !instance.StartsWith("luid_0x", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = instance.Split('_');
        if (parts.Length < 3 || !TryHex(parts[1], out uint high) || !TryHex(parts[2], out uint low))
        {
            return false;
        }

        luid = ((ulong)high << 32) | low;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            lock (_gate)
            {
                Close();
            }
        }
    }

    private static bool TryHex(string text, out uint value)
    {
        value = 0;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    [SupportedOSPlatform("windows")]
    private bool TryRead(out Dictionary<ulong, long>? usage, out string? error)
    {
        usage = null;
        if (_query == IntPtr.Zero)
        {
            uint opened = PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out _query);
            if (opened != 0)
            {
                _query = IntPtr.Zero;
                error = Describe("PdhOpenQuery", opened);
                return false;
            }

            uint added = PdhAddEnglishCounterW(_query, CounterPath, IntPtr.Zero, out _counter);
            if (added != 0)
            {
                error = Describe("PdhAddEnglishCounter", added);
                Close();
                return false;
            }
        }

        uint collected = PdhCollectQueryData(_query);
        if (collected != 0)
        {
            error = Describe("PdhCollectQueryData", collected);
            return false;
        }

        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(_counter, FormatLarge, ref size, out uint count, IntPtr.Zero);
        if (status != MoreData || size == 0)
        {
            error = Describe("PdhGetFormattedCounterArray", status);
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(_counter, FormatLarge, ref size, out count, buffer);
            if (status != 0)
            {
                error = Describe("PdhGetFormattedCounterArray", status);
                return false;
            }

            var result = new Dictionary<ulong, long>();
            for (int i = 0; i < (int)count; i++)
            {
                IntPtr item = buffer + (i * ItemSize);
                uint itemStatus = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                if (itemStatus is not (StatusValid or StatusNew) ||
                    !TryParseLuid(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)), out ulong luid))
                {
                    continue;
                }

                long bytes = Marshal.ReadInt64(item, 16);
                result[luid] = result.GetValueOrDefault(luid) + Math.Max(0, bytes);
            }

            usage = result;
            error = null;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private void Close()
    {
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
        }

        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }

    private static string Describe(string call, uint status) =>
        string.Create(CultureInfo.InvariantCulture, $"{call} for {CounterPath} failed with PDH status 0x{status:X8}.");

    [SupportedOSPlatform("windows")]
    [LibraryImport("pdh")]
    private static partial uint PdhOpenQueryW(IntPtr dataSource, IntPtr userData, out IntPtr query);

    [SupportedOSPlatform("windows")]
    [LibraryImport("pdh", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [SupportedOSPlatform("windows")]
    [LibraryImport("pdh")]
    private static partial uint PdhCollectQueryData(IntPtr query);

    [SupportedOSPlatform("windows")]
    [LibraryImport("pdh")]
    private static partial uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [SupportedOSPlatform("windows")]
    [LibraryImport("pdh")]
    private static partial uint PdhCloseQuery(IntPtr query);
}
