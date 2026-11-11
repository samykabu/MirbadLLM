using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MirbadLLM.NodeRuntime;

/// <summary>One Vulkan physical device, as <c>vkEnumeratePhysicalDevices</c> lists it.</summary>
/// <param name="Index">
/// Position in the enumeration. It is the number llama.cpp's Vulkan backend takes in <c>GGML_VK_VISIBLE_DEVICES</c>, so
/// it must come from the same loader configuration the model process gets (<see cref="VulkanAdapters.LoaderEnvironment"/>).
/// </param>
/// <param name="DeviceUuid">
/// <c>VkPhysicalDeviceIDProperties.deviceUUID</c> as text (<c>8c822d09-60c6-35e0-6c1e-a9adb2a03ab7</c>, the 16 bytes in
/// order). The driver keeps it across reboots; for an NVIDIA card it is the UUID <c>nvidia-smi</c> prints after
/// <c>GPU-</c> (checked on an RTX 5070 and an RTX 5060 Ti, driver 621.01).
/// </param>
/// <param name="Name">Product name.</param>
/// <param name="VendorId">PCI vendor id (<c>0x1002</c> AMD, <c>0x8086</c> Intel, <c>0x10de</c> NVIDIA).</param>
/// <param name="DeviceId">PCI device id.</param>
/// <param name="Type">Device type.</param>
/// <param name="Luid">The adapter's locally unique id on Windows (valid until the next boot); null when the driver gives none.</param>
/// <param name="DedicatedBytes">
/// Sum of the device-local memory heaps: the VRAM the driver hands out. On the two NVIDIA cards above it is
/// <c>nvidia-smi</c>'s <c>memory.total</c> minus what the driver keeps for itself (11 943 of 12 227 MiB, 16 050 of
/// 16 311 MiB). On an integrated GPU the "device-local" heap is system memory.
/// </param>
public sealed record VulkanAdapter(
    int Index, string DeviceUuid, string Name, uint VendorId, uint DeviceId, VulkanDeviceType Type, ulong? Luid, ulong DedicatedBytes);

/// <summary><c>VkPhysicalDeviceType</c>.</summary>
public enum VulkanDeviceType
{
    /// <summary>None of the others.</summary>
    Other = 0,

    /// <summary>Integrated with the CPU: shares system memory.</summary>
    Integrated = 1,

    /// <summary>A separate card with its own VRAM.</summary>
    Discrete = 2,

    /// <summary>A virtual device.</summary>
    Virtual = 3,

    /// <summary>A software renderer.</summary>
    Cpu = 4,
}

/// <summary>
/// The machine's Vulkan devices through the system's Vulkan loader (<c>vulkan-1.dll</c>, installed by every GPU driver;
/// P/Invoke, no package): identity, VRAM total and enumeration index of each adapter, whatever its vendor. This is what
/// a Vulkan node (<c>Agent:Accelerator = vulkan</c>) reports instead of <c>nvidia-smi</c>. It does not report memory in
/// use: Vulkan's own figure (<c>VK_EXT_memory_budget</c>) covers the calling process only, which is why llama.cpp's
/// <c>--list-devices</c> shows a GPU as nearly empty while other programs hold gigabytes of it. The system-wide figure
/// comes from <see cref="WindowsGpuMemoryCounters"/>.
/// <para>The byte layouts are read at fixed offsets (64-bit), so <see cref="Parse"/> is plain code that runs on every OS.</para>
/// </summary>
public static partial class VulkanAdapters
{
    /// <summary>Bytes the loader may write for <c>VkPhysicalDeviceProperties2</c> (840 on 64-bit; rounded up).</summary>
    public const int PropertiesSize = 1024;

    /// <summary>Size of <c>VkPhysicalDeviceIDProperties</c>.</summary>
    public const int IdPropertiesSize = 64;

    /// <summary>Size of <c>VkPhysicalDeviceMemoryProperties</c>.</summary>
    public const int MemoryPropertiesSize = 520;

    private const uint StructureTypeApplicationInfo = 0;
    private const uint StructureTypeInstanceCreateInfo = 1;
    private const uint StructureTypePhysicalDeviceProperties2 = 1000059001;
    private const uint StructureTypePhysicalDeviceIdProperties = 1000071004;
    private const uint ApiVersion11 = (1u << 22) | (1u << 12);
    private const uint MemoryHeapDeviceLocal = 0x1;
    private const int Incomplete = 5;

    /// <summary>
    /// The loader setting both the agent and every model process it starts use: implicit layers off. Layers that drivers
    /// and overlays register (NVIDIA Optimus, AMD switchable graphics, capture hooks) can hide or reorder devices, and the
    /// index the agent reads must be the index the model process sees. A loader older than 1.3.234 ignores the variable,
    /// in both processes alike.
    /// </summary>
    public static KeyValuePair<string, string> LoaderEnvironment { get; } = new("VK_LOADER_LAYERS_DISABLE", "~implicit~");

    /// <summary>
    /// Every physical device in enumeration order. Throws <see cref="DllNotFoundException"/> or
    /// <see cref="EntryPointNotFoundException"/> without a Vulkan 1.1 loader and <see cref="InvalidOperationException"/>
    /// when the loader refuses the instance.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static unsafe IReadOnlyList<VulkanAdapter> Enumerate()
    {
        Environment.SetEnvironmentVariable(LoaderEnvironment.Key, LoaderEnvironment.Value);
        var application = new ApplicationInfo { StructureType = StructureTypeApplicationInfo, ApiVersion = ApiVersion11 };
        var create = new InstanceCreateInfo { StructureType = StructureTypeInstanceCreateInfo, ApplicationInfo = &application };
        IntPtr instance;
        int result = vkCreateInstance(&create, IntPtr.Zero, &instance);
        if (result != 0)
        {
            throw new InvalidOperationException($"vkCreateInstance failed with VkResult {result}.");
        }

        try
        {
            uint count = 0;
            result = vkEnumeratePhysicalDevices(instance, &count, null);
            if (result != 0)
            {
                throw new InvalidOperationException($"vkEnumeratePhysicalDevices failed with VkResult {result}.");
            }

            if (count == 0)
            {
                return [];
            }

            var devices = new IntPtr[count];
            fixed (IntPtr* list = devices)
            {
                result = vkEnumeratePhysicalDevices(instance, &count, list);
            }

            if (result is not (0 or Incomplete))
            {
                throw new InvalidOperationException($"vkEnumeratePhysicalDevices failed with VkResult {result}.");
            }

            var adapters = new List<VulkanAdapter>((int)count);
            byte* properties = (byte*)NativeMemory.AllocZeroed(PropertiesSize);
            byte* id = stackalloc byte[IdPropertiesSize];
            byte* memory = stackalloc byte[MemoryPropertiesSize];
            try
            {
                for (int i = 0; i < (int)count; i++)
                {
                    new Span<byte>(properties, PropertiesSize).Clear();
                    new Span<byte>(id, IdPropertiesSize).Clear();
                    new Span<byte>(memory, MemoryPropertiesSize).Clear();
                    *(uint*)properties = StructureTypePhysicalDeviceProperties2;
                    *(IntPtr*)(properties + 8) = (IntPtr)id;
                    *(uint*)id = StructureTypePhysicalDeviceIdProperties;
                    vkGetPhysicalDeviceProperties2(devices[i], properties);
                    vkGetPhysicalDeviceMemoryProperties(devices[i], memory);
                    adapters.Add(Parse(
                        i,
                        new ReadOnlySpan<byte>(properties, PropertiesSize),
                        new ReadOnlySpan<byte>(id, IdPropertiesSize),
                        new ReadOnlySpan<byte>(memory, MemoryPropertiesSize)));
                }
            }
            finally
            {
                NativeMemory.Free(properties);
            }

            return adapters;
        }
        finally
        {
            vkDestroyInstance(instance, IntPtr.Zero);
        }
    }

    /// <summary>
    /// One device from the three structures the loader filled (64-bit layouts): <c>VkPhysicalDeviceProperties2</c>
    /// (vendor id at 24, device id at 28, type at 32, the name's 256 bytes at 36), <c>VkPhysicalDeviceIDProperties</c>
    /// (UUID at 16, LUID at 48, its validity at 60) and <c>VkPhysicalDeviceMemoryProperties</c> (heap count at 260, then
    /// 16-byte heaps of size and flags from 264).
    /// </summary>
    public static VulkanAdapter Parse(int index, ReadOnlySpan<byte> properties2, ReadOnlySpan<byte> idProperties, ReadOnlySpan<byte> memoryProperties)
    {
        if (properties2.Length < 36 + 256 || idProperties.Length < IdPropertiesSize || memoryProperties.Length < MemoryPropertiesSize)
        {
            throw new ArgumentException("A Vulkan structure is shorter than its layout.");
        }

        uint vendor = BinaryPrimitives.ReadUInt32LittleEndian(properties2[24..]);
        uint device = BinaryPrimitives.ReadUInt32LittleEndian(properties2[28..]);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(properties2[32..]);
        ReadOnlySpan<byte> nameBytes = properties2.Slice(36, 256);
        int end = nameBytes.IndexOf((byte)0);
        string name = Encoding.UTF8.GetString(end < 0 ? nameBytes : nameBytes[..end]).Trim();

        bool luidValid = BinaryPrimitives.ReadUInt32LittleEndian(idProperties[60..]) != 0;
        ulong? luid = luidValid ? BinaryPrimitives.ReadUInt64LittleEndian(idProperties[48..]) : null;

        uint heaps = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(memoryProperties[260..]), 16);
        ulong dedicated = 0;
        for (int h = 0; h < heaps; h++)
        {
            ReadOnlySpan<byte> heap = memoryProperties.Slice(264 + (h * 16), 16);
            if ((BinaryPrimitives.ReadUInt32LittleEndian(heap[8..]) & MemoryHeapDeviceLocal) != 0)
            {
                dedicated += BinaryPrimitives.ReadUInt64LittleEndian(heap);
            }
        }

        return new VulkanAdapter(
            index,
            FormatUuid(idProperties.Slice(16, 16)),
            name.Length == 0 ? "GPU" : name,
            vendor,
            device,
            Enum.IsDefined((VulkanDeviceType)type) ? (VulkanDeviceType)type : VulkanDeviceType.Other,
            luid,
            dedicated);
    }

    /// <summary>The 16 UUID bytes in order as <c>8-4-4-4-12</c> lower-case hex, as <c>vulkaninfo</c> and <c>nvidia-smi</c> print them.</summary>
    public static string FormatUuid(ReadOnlySpan<byte> uuid)
    {
        if (uuid.Length != 16)
        {
            throw new ArgumentException("A device UUID has 16 bytes.", nameof(uuid));
        }

        string hex = Convert.ToHexStringLower(uuid);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ApplicationInfo
    {
        public uint StructureType;
        public IntPtr Next;
        public IntPtr ApplicationName;
        public uint ApplicationVersion;
        public IntPtr EngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct InstanceCreateInfo
    {
        public uint StructureType;
        public IntPtr Next;
        public uint Flags;
        public ApplicationInfo* ApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr EnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("vulkan-1")]
    private static unsafe partial int vkCreateInstance(InstanceCreateInfo* createInfo, IntPtr allocator, IntPtr* instance);

    [SupportedOSPlatform("windows")]
    [LibraryImport("vulkan-1")]
    private static partial void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [SupportedOSPlatform("windows")]
    [LibraryImport("vulkan-1")]
    private static unsafe partial int vkEnumeratePhysicalDevices(IntPtr instance, uint* count, IntPtr* devices);

    [SupportedOSPlatform("windows")]
    [LibraryImport("vulkan-1")]
    private static unsafe partial void vkGetPhysicalDeviceProperties2(IntPtr device, byte* properties);

    [SupportedOSPlatform("windows")]
    [LibraryImport("vulkan-1")]
    private static unsafe partial void vkGetPhysicalDeviceMemoryProperties(IntPtr device, byte* properties);
}
