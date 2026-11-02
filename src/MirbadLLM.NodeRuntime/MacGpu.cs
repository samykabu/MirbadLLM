using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// The Apple GPU as Metal sees it (docs/macos/implementation-plan.md M7, spike S5): <c>MTLCreateSystemDefaultDevice()</c>
/// and the device's <c>name</c> and <c>recommendedMaxWorkingSetSize</c>, through the Objective-C runtime (no package, no
/// helper binary). The working-set size is how much of the unified memory Metal lets the GPU keep resident — on an 8 GiB
/// M2, 5.33 GiB, exactly two thirds — so a model larger than it fails at load whatever the free memory says.
/// </summary>
public static partial class MacGpu
{
    /// <summary>The default Metal device's name and working-set size in bytes, or null when there is no Metal device.</summary>
    [SupportedOSPlatform("macos")]
    public static (string Name, ulong WorkingSetBytes)? Query()
    {
        IntPtr device = MTLCreateSystemDefaultDevice();
        if (device == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            ulong workingSet = objc_msgSend_UInt64(device, sel_registerName("recommendedMaxWorkingSetSize"));
            IntPtr nsName = objc_msgSend_IntPtr(device, sel_registerName("name"));
            IntPtr utf8 = nsName == IntPtr.Zero ? IntPtr.Zero : objc_msgSend_IntPtr(nsName, sel_registerName("UTF8String"));
            string name = Marshal.PtrToStringUTF8(utf8) ?? "Apple GPU";
            return (name, workingSet);
        }
        finally
        {
            _ = objc_msgSend_IntPtr(device, sel_registerName("release"));
        }
    }

    [SupportedOSPlatform("macos")]
    [LibraryImport("/System/Library/Frameworks/Metal.framework/Metal")]
    private static partial IntPtr MTLCreateSystemDefaultDevice();

    [SupportedOSPlatform("macos")]
    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [SupportedOSPlatform("macos")]
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial ulong objc_msgSend_UInt64(IntPtr receiver, IntPtr selector);

    [SupportedOSPlatform("macos")]
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);
}

/// <summary>String <c>sysctl</c> values on macOS.</summary>
public static partial class MacSysctl
{
    /// <summary><c>machdep.cpu.brand_string</c>, e.g. <c>Apple M2</c>; null when it cannot be read.</summary>
    [SupportedOSPlatform("macos")]
    public static string? CpuBrand() => String("machdep.cpu.brand_string");

    [SupportedOSPlatform("macos")]
    internal static string? String(string name)
    {
        nuint size = 0;
        if (sysctlbyname(name, IntPtr.Zero, ref size, IntPtr.Zero, 0) != 0 || size == 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((nint)size);
        try
        {
            return sysctlbyname(name, buffer, ref size, IntPtr.Zero, 0) == 0 ? Marshal.PtrToStringUTF8(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("macos")]
    [LibraryImport("libSystem.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sysctlbyname(string name, IntPtr oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);
}
