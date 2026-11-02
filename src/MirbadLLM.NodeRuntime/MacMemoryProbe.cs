using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Host memory on macOS (docs/macos/implementation-plan.md, Phase 1): the total from <c>sysctl hw.memsize</c>, the
/// available memory from the kernel's VM statistics (<c>host_statistics64(HOST_VM_INFO64)</c>) as free plus inactive
/// pages — the pages the system hands to a new allocation without swapping anything out, the figure <c>psutil</c> and
/// Activity Monitor's "Memory Pressure" start from. P/Invoke into <c>libSystem</c>, no package; the macOS counterpart of
/// <see cref="ProcMeminfoMemoryProbe"/> and <see cref="WindowsMemoryProbe"/>. A failing call throws, which the sampler
/// turns into a skipped sample (nothing is invented).
/// <para>Only <see cref="GetAsync"/> needs macOS; the parsing (<see cref="FromVmStatistics"/>) is OS-neutral.</para>
/// </summary>
public sealed partial class MacMemoryProbe : IMemoryProbe
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    // <mach/host_info.h>: HOST_VM_INFO64 flavour, and vm_statistics64 is 38 integer_t words (152 bytes).
    private const int HostVmInfo64 = 4;
    private const int VmStatistics64Words = 38;

    /// <inheritdoc />
    [SupportedOSPlatform("macos")]
    public Task<MemorySnapshot> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ulong total = SysctlUInt64("hw.memsize");

        uint host = Host();
        int pageResult = host_page_size(host, out nuint pageSize);
        if (pageResult != 0)
        {
            throw new Win32Exception(pageResult, $"host_page_size failed ({pageResult}).");
        }

        var words = new int[VmStatistics64Words];
        int count = VmStatistics64Words;
        int statsResult = host_statistics64(host, HostVmInfo64, words, ref count);
        if (statsResult != 0)
        {
            throw new Win32Exception(statsResult, $"host_statistics64 failed ({statsResult}).");
        }

        byte[] raw = new byte[VmStatistics64Words * sizeof(int)];
        Buffer.BlockCopy(words, 0, raw, 0, raw.Length);
        return Task.FromResult(FromVmStatistics(total, (ulong)pageSize, raw));
    }

    /// <summary>
    /// The snapshot for <c>hw.memsize</c>, the page size and a raw <c>vm_statistics64</c> (native byte order: little
    /// endian on Apple Silicon). Pure, so the conversion is tested on every OS. Throws <see cref="FormatException"/> when
    /// the values cannot be right.
    /// </summary>
    public static MemorySnapshot FromVmStatistics(ulong totalBytes, ulong pageSize, ReadOnlySpan<byte> vmStatistics64)
    {
        if (totalBytes == 0 || pageSize == 0 || vmStatistics64.Length < 96)
        {
            throw new FormatException($"Unusable macOS memory figures: total {totalBytes} bytes, page {pageSize} bytes, {vmStatistics64.Length} statistics bytes.");
        }

        // vm_statistics64: natural_t free_count @0, active_count @4, inactive_count @8, wire_count @12, ...
        uint free = BinaryPrimitives.ReadUInt32LittleEndian(vmStatistics64[0..4]);
        uint inactive = BinaryPrimitives.ReadUInt32LittleEndian(vmStatistics64[8..12]);
        ulong available = ((ulong)free + inactive) * pageSize;
        if (available > totalBytes)
        {
            throw new FormatException($"host_statistics64 reported {available} bytes available of {totalBytes} bytes total.");
        }

        return new MemorySnapshot(totalBytes / BytesPerGiB, available / BytesPerGiB);
    }

    // mach_host_self() hands out a send right on every call; take one and keep it for the process.
    private static uint s_host;

    [SupportedOSPlatform("macos")]
    private static uint Host()
    {
        uint host = Volatile.Read(ref s_host);
        if (host == 0)
        {
            host = mach_host_self();
            Volatile.Write(ref s_host, host);
        }

        return host;
    }

    [SupportedOSPlatform("macos")]
    internal static ulong SysctlUInt64(string name)
    {
        ulong value = 0;
        nuint size = sizeof(ulong);
        if (sysctlbyname(name, ref value, ref size, IntPtr.Zero, 0) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"sysctlbyname({name}) failed.");
        }

        return value;
    }

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.dylib", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int sysctlbyname(string name, ref ulong oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.dylib")]
    private static partial uint mach_host_self();

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.dylib")]
    private static partial int host_page_size(uint host, out nuint pageSize);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.dylib")]
    private static partial int host_statistics64(uint host, int flavor, [Out] int[] info, ref int count);
}
