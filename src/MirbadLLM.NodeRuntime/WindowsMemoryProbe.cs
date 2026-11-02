using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Host memory on Windows (design-p3p4 §A.4.3, MN-214): <c>GlobalMemoryStatusEx</c> from <c>kernel32</c> (P/Invoke, no
/// package), <c>ullTotalPhys</c> as the total and <c>ullAvailPhys</c> as the available memory, both in GiB. The Windows
/// counterpart of <see cref="ProcMeminfoMemoryProbe"/>; registered instead of it on a Windows agent (MN-232). A failing
/// call throws <see cref="Win32Exception"/>, which the sampler turns into a skipped sample (nothing is invented).
/// <para>Only <see cref="GetAsync"/> needs Windows; the conversion (<see cref="FromStatus"/>) is OS-neutral.</para>
/// </summary>
public sealed partial class WindowsMemoryProbe : IMemoryProbe
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    /// <inheritdoc />
    [SupportedOSPlatform("windows")]
    public Task<MemorySnapshot> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GlobalMemoryStatusEx failed.");
        }

        return Task.FromResult(FromStatus(status.TotalPhys, status.AvailPhys));
    }

    /// <summary>
    /// The snapshot for the two <c>MEMORYSTATUSEX</c> byte counts. Pure, so the conversion is tested on every OS. Throws
    /// <see cref="FormatException"/> when the values cannot be right (no total, or more available than installed).
    /// </summary>
    public static MemorySnapshot FromStatus(ulong totalPhysBytes, ulong availPhysBytes)
    {
        if (totalPhysBytes == 0 || availPhysBytes > totalPhysBytes)
        {
            throw new FormatException(
                $"GlobalMemoryStatusEx returned {availPhysBytes} bytes available of {totalPhysBytes} bytes total.");
        }

        return new MemorySnapshot(totalPhysBytes / BytesPerGiB, availPhysBytes / BytesPerGiB);
    }

    /// <summary><c>MEMORYSTATUSEX</c> (sysinfoapi.h); <see cref="Length"/> must be set before the call.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
