using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MirbadLLM.NodeRuntime;

/// <summary>One Apple GPU as IOKit's <c>IOAccelerator</c> service reports it.</summary>
/// <param name="Model">The <c>model</c> property, e.g. <c>Apple M2</c>; null when absent.</param>
/// <param name="CoreCount">The <c>gpu-core-count</c> property; null when absent.</param>
/// <param name="Statistics">
/// The numeric entries of <c>PerformanceStatistics</c>, e.g. <c>Device Utilization %</c>, <c>Renderer Utilization %</c>,
/// <c>In use system memory</c> (bytes); entries that are not numbers are left out.
/// </param>
public sealed record IoAcceleratorReading(string? Model, int? CoreCount, IReadOnlyDictionary<string, long> Statistics);

/// <summary>
/// Apple GPU statistics from IOKit (docs/macos/implementation-plan.md M8): every <c>IOAccelerator</c> service's
/// <c>PerformanceStatistics</c> dictionary, the same numbers <c>ioreg -r -c IOAccelerator</c> prints and Activity Monitor's
/// GPU history uses. No root needed (unlike <c>powermetrics</c>), no helper binary: IOKit and CoreFoundation through
/// P/Invoke. Power and temperature are not in it, so Apple GPU samples carry neither.
/// </summary>
public static partial class MacIoAccelerator
{
    private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const int SInt64Type = 4; // kCFNumberSInt64Type

    /// <summary>
    /// Every accelerator's reading, in IOKit's order; empty when the Mac has none (a VM without a GPU). Null when the
    /// IOKit query itself fails.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static IReadOnlyList<IoAcceleratorReading>? Read()
    {
        IntPtr matching = IOServiceMatching("IOAccelerator");
        if (matching == IntPtr.Zero)
        {
            return null;
        }

        // IOServiceGetMatchingServices consumes the matching dictionary, whatever it returns.
        if (IOServiceGetMatchingServices(0, matching, out uint iterator) != 0)
        {
            return null;
        }

        var result = new List<IoAcceleratorReading>();
        IntPtr statisticsKey = CfString("PerformanceStatistics");
        IntPtr modelKey = CfString("model");
        IntPtr coresKey = CfString("gpu-core-count");
        try
        {
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    var statistics = new Dictionary<string, long>(StringComparer.Ordinal);
                    IntPtr dictionary = IORegistryEntryCreateCFProperty(service, statisticsKey, IntPtr.Zero, 0);
                    if (dictionary != IntPtr.Zero)
                    {
                        try
                        {
                            ReadNumbers(dictionary, statistics);
                        }
                        finally
                        {
                            CFRelease(dictionary);
                        }
                    }

                    result.Add(new IoAcceleratorReading(ReadString(service, modelKey), ReadInt(service, coresKey), statistics));
                }
                finally
                {
                    _ = IOObjectRelease(service);
                }
            }
        }
        finally
        {
            _ = IOObjectRelease(iterator);
            CFRelease(statisticsKey);
            CFRelease(modelKey);
            CFRelease(coresKey);
        }

        return result;
    }

    [SupportedOSPlatform("macos")]
    private static void ReadNumbers(IntPtr dictionary, Dictionary<string, long> into)
    {
        if (CFGetTypeID(dictionary) != CFDictionaryGetTypeID())
        {
            return;
        }

        nint count = CFDictionaryGetCount(dictionary);
        if (count <= 0 || count > 4096)
        {
            return;
        }

        var keys = new IntPtr[count];
        var values = new IntPtr[count];
        CFDictionaryGetKeysAndValues(dictionary, keys, values);
        for (int i = 0; i < count; i++)
        {
            if (ToText(keys[i]) is { } key && ToLong(values[i]) is { } value)
            {
                into[key] = value;
            }
        }
    }

    [SupportedOSPlatform("macos")]
    private static string? ReadString(uint service, IntPtr key)
    {
        IntPtr value = IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ToText(value);
        }
        finally
        {
            CFRelease(value);
        }
    }

    [SupportedOSPlatform("macos")]
    private static int? ReadInt(uint service, IntPtr key)
    {
        IntPtr value = IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ToLong(value) is { } number and >= 0 and <= int.MaxValue ? (int)number : null;
        }
        finally
        {
            CFRelease(value);
        }
    }

    [SupportedOSPlatform("macos")]
    private static string? ToText(IntPtr value)
    {
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
        {
            return null;
        }

        var buffer = new byte[512];
        return CFStringGetCString(value, buffer, buffer.Length, Utf8)
            ? System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Array.IndexOf(buffer, (byte)0)))
            : null;
    }

    [SupportedOSPlatform("macos")]
    private static long? ToLong(IntPtr value) =>
        value != IntPtr.Zero && CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, SInt64Type, out long number)
            ? number
            : null;

    [SupportedOSPlatform("macos")]
    private static IntPtr CfString(string text) => CFStringCreateWithCString(IntPtr.Zero, text, Utf8);

    [SupportedOSPlatform("macos")]
    [LibraryImport(IoKit, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr IOServiceMatching(string name);

    [SupportedOSPlatform("macos")]
    [LibraryImport(IoKit)]
    private static partial int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);

    [SupportedOSPlatform("macos")]
    [LibraryImport(IoKit)]
    private static partial uint IOIteratorNext(uint iterator);

    [SupportedOSPlatform("macos")]
    [LibraryImport(IoKit)]
    private static partial int IOObjectRelease(uint service);

    [SupportedOSPlatform("macos")]
    [LibraryImport(IoKit)]
    private static partial IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(IntPtr text, [Out] byte[] buffer, nint size, uint encoding);

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(IntPtr value);

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    private static partial nuint CFDictionaryGetTypeID();

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNumberGetTypeID();

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetCount(IntPtr dictionary);

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    private static partial void CFDictionaryGetKeysAndValues(IntPtr dictionary, [Out] IntPtr[] keys, [Out] IntPtr[] values);

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(IntPtr number, int type, out long value);
}
