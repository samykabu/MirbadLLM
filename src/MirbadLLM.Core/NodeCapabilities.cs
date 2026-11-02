using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>
/// A protocol version range <c>[Min, Current]</c> (design §3.6). Both sides have one; see
/// <c>MirbadLLM.Agent.Contracts.Protocol.Negotiate</c> for the selection rule.
/// </summary>
/// <param name="Min">Oldest protocol version this side still speaks.</param>
/// <param name="Current">Newest protocol version this side speaks.</param>
public sealed record ProtocolRange(int Min, int Current);

/// <summary>
/// How a node's memory is organized (design §1 glossary, §6.1). Serialized as <c>unified</c> / <c>discrete</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryModel>))]
public enum MemoryModel
{
    /// <summary>CPU and GPU share one pool (DGX GB10, Apple Silicon). Admission uses host available memory.</summary>
    [JsonStringEnumMemberName("unified")]
    Unified,

    /// <summary>Host RAM plus per-GPU VRAM (PCs). Admission is per GPU plus a host part (P2).</summary>
    [JsonStringEnumMemberName("discrete")]
    Discrete,
}

/// <summary>
/// A node's capability report, <c>GET /agent/v1/capabilities</c> (design §3.3; also embedded in enrollment). The master
/// stores the last report in <c>nodes.json</c> (<see cref="NodeRecord.LastCapabilities"/>) so the UI can show an
/// Offline node's hardware, and checks <see cref="Features"/> before offering an action (§3.6).
/// </summary>
public sealed record NodeCapabilities
{
    /// <summary>Protocol versions the agent speaks.</summary>
    public required ProtocolRange Protocol { get; init; }

    /// <summary>Agent informational version, e.g. <c>1.0.0+78b39d2</c>.</summary>
    public required string AgentVersion { get; init; }

    /// <summary>Feature flags inside the protocol version (constants in <c>MirbadLLM.Agent.Contracts.AgentFeatures</c>).</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>Operating system.</summary>
    public required NodeOsInfo Os { get; init; }

    /// <summary>CPU architecture: <c>arm64</c> or <c>amd64</c>.</summary>
    public required string Arch { get; init; }

    /// <summary>CPU description.</summary>
    public NodeCpuInfo? Cpu { get; init; }

    /// <summary>Memory model and total.</summary>
    public required NodeMemoryInfo Memory { get; init; }

    /// <summary>GPUs in <c>nvidia-smi</c> index order. Empty on a CPU-only node.</summary>
    public IReadOnlyList<GpuCapability> Gpus { get; init; } = [];

    /// <summary>Runtime kinds and deliveries this node can run.</summary>
    public IReadOnlyList<RuntimeCapability> Runtimes { get; init; } = [];

    /// <summary>Container images present on the node (the master merges the vLLM catalog per node, design §2.4).</summary>
    public IReadOnlyList<ImageInfo> Images { get; init; } = [];

    /// <summary>Logical storage roots (<c>models</c>, <c>cache</c>) with free space.</summary>
    public IReadOnlyList<ModelRootInfo> ModelRoots { get; init; } = [];

    /// <summary>Host-service management support. Null = the agent does not manage services (design §9.1).</summary>
    public HostServicesCapability? HostServices { get; init; }

    /// <summary>Backup-share mount support (topology a, design §9.3). Null = not supported.</summary>
    public BackupMountCapability? BackupMount { get; init; }
}

/// <summary>Operating system of a node (design §3.3).</summary>
/// <param name="Family"><c>linux</c>, <c>windows</c> or <c>macos</c>.</param>
/// <param name="Description">Human-readable name, e.g. <c>Ubuntu 24.04 LTS</c>.</param>
/// <param name="Kernel">Kernel or OS build version. Null when unknown.</param>
public sealed record NodeOsInfo(string Family, string Description, string? Kernel = null);

/// <summary>CPU of a node (design §3.3).</summary>
/// <param name="LogicalCores">Logical core count.</param>
/// <param name="Model">CPU model name. Null when unknown.</param>
public sealed record NodeCpuInfo(int LogicalCores, string? Model = null);

/// <summary>Memory model and host total of a node (design §3.3).</summary>
/// <param name="Model">Unified or discrete.</param>
/// <param name="TotalGiB">Host memory total (MemTotal on Linux).</param>
/// <param name="GpuWorkingSetGiB">
/// On a unified node whose GPU may hold only part of the memory, how much it may keep resident (Apple Silicon: Metal's
/// <c>recommendedMaxWorkingSetSize</c>, two thirds of RAM on an 8 GiB M2). Null where the GPU can use all of it or the
/// agent does not know (the DGX, discrete nodes, older agents).
/// </param>
public sealed record NodeMemoryInfo(
    MemoryModel Model,
    double TotalGiB,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? GpuWorkingSetGiB = null);

/// <summary>
/// One GPU in a capability report (design §3.3). GB10 reports no per-GPU memory through <c>nvidia-smi</c>, hence a null
/// <see cref="MemoryTotalGiB"/> and <see cref="SharesHostMemory"/> = true; the node's memory model is then unified.
/// </summary>
public sealed record GpuCapability
{
    /// <summary><c>nvidia-smi</c> index. Display aid only; may change between driver updates (design §5.1).</summary>
    public required int Index { get; init; }

    /// <summary>GPU UUID, e.g. <c>GPU-5967…</c>: the GPU's identity for placements.</summary>
    public required string Uuid { get; init; }

    /// <summary>Product name, e.g. <c>NVIDIA GB10</c>.</summary>
    public required string Name { get; init; }

    /// <summary>VRAM total in GiB (MiB ÷ 1024). Null on unified GPUs. The denominator for discrete budgets (§5.1).</summary>
    public double? MemoryTotalGiB { get; init; }

    /// <summary>CUDA compute capability, e.g. <c>12.1</c>.</summary>
    public string? ComputeCapability { get; init; }

    /// <summary>Driver version.</summary>
    public string? Driver { get; init; }

    /// <summary>True when the GPU uses host memory (unified).</summary>
    public bool SharesHostMemory { get; init; }
}

/// <summary>A runtime kind + delivery a node supports (design §3.3).</summary>
public sealed record RuntimeCapability
{
    /// <summary>Runtime kind.</summary>
    public required RuntimeKind Kind { get; init; }

    /// <summary>Container or native process.</summary>
    public required LaunchDelivery Delivery { get; init; }

    /// <summary>False when the node knows the kind but cannot run it now (e.g. no image).</summary>
    public bool Available { get; init; }

    /// <summary>Images usable for this kind (container delivery), when the agent knows them.</summary>
    public IReadOnlyList<string>? Images { get; init; }

    /// <summary>
    /// Installed versions of the logical executable (process delivery, design-p3p4 §A.1.2; MN-202, D116; reported by
    /// MN-222's <c>LlamaCppRuntimeContributor</c>). <see cref="Available"/> is true when at least one entry is verified.
    /// Omitted when null, so every container capability (the DGX's) is unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RuntimeVersion>? Versions { get; init; }
}

/// <summary>One installed version of a process runtime's executable (design-p3p4 §A.1.2, §A.7 version picker).</summary>
/// <param name="Version">Release tag, e.g. <c>b11476</c>.</param>
/// <param name="Cuda">CUDA variant of the build, e.g. <c>13.4</c>; null when not a CUDA build.</param>
/// <param name="Verified">
/// False when the files no longer match the install manifest (health reason <c>executable_unverified</c>; launches naming
/// it get 422 <c>executable_unverified</c>).
/// </param>
/// <param name="Default">True for the node's default version (used when a model pins none).</param>
public sealed record RuntimeVersion(string Version, string? Cuda, bool Verified, bool Default = false);

/// <summary>
/// A container image on a node (design §3.3 <c>images</c>, §4.3 <c>GET /images</c>). <see cref="Digest"/> is the
/// repository digest used by the approved-digest allow-list (design §10.3).
/// </summary>
public sealed record ImageInfo
{
    /// <summary>Reference as the operator uses it, e.g. <c>local/qwen38-vllm-dflash2:66e5414c6d75</c>.</summary>
    public required string Ref { get; init; }

    /// <summary>Local image id (<c>sha256:…</c>). Null when unknown.</summary>
    public string? Id { get; init; }

    /// <summary>Repository digest (<c>sha256:…</c>). Null for a locally built image without one.</summary>
    public string? Digest { get; init; }

    /// <summary>Image architecture, e.g. <c>arm64</c>.</summary>
    public string? Arch { get; init; }

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>A logical storage root on a node (design §3.3 <c>modelRoots</c>, §10.3 logical volumes).</summary>
/// <param name="Name">Logical volume name: <c>models</c> or <c>cache</c>.</param>
/// <param name="FreeBytes">Free bytes on the volume.</param>
/// <param name="TotalBytes">Volume size in bytes.</param>
/// <param name="FolderCount">Number of top-level folders (checkpoints for <c>models</c>).</param>
public sealed record ModelRootInfo(string Name, long FreeBytes, long TotalBytes, int FolderCount);

/// <summary>Host-service management on a node (design §3.3, §9.1).</summary>
/// <param name="Manager"><c>systemd</c> (Linux via hostd), <c>scm</c> (Windows, P4) or <c>launchd</c> (macOS, later).</param>
/// <param name="HostdAvailable">True when mirbadllm-hostd answers on its socket.</param>
/// <param name="HostdVersion">hostd version. Null when unknown or unavailable.</param>
/// <param name="Containers">True when the agent can manage non-model containers (Docker).</param>
public sealed record HostServicesCapability(string Manager, bool HostdAvailable, string? HostdVersion, bool Containers);

/// <summary>Backup-share mount support on a node (design §3.3, §9.3; topology a only).</summary>
/// <param name="Supported">True when the agent can forward backup-mount requests to hostd.</param>
public sealed record BackupMountCapability(bool Supported);

/// <summary>
/// A node's memory at one moment (design §4.3 <c>NodeMemorySnapshot</c>): the host pool and, on discrete nodes, every
/// GPU. On a unified node <see cref="Gpus"/> carries the GPUs with null memory fields.
/// </summary>
public sealed record NodeMemorySnapshot
{
    /// <summary>Unified or discrete.</summary>
    public required MemoryModel Model { get; init; }

    /// <summary>Host memory total (MemTotal).</summary>
    public required double HostTotalGiB { get; init; }

    /// <summary>Host memory available (MemAvailable).</summary>
    public required double HostAvailableGiB { get; init; }

    /// <summary>Per-GPU memory (discrete) or GPU identities only (unified).</summary>
    public IReadOnlyList<GpuMemorySnapshot> Gpus { get; init; } = [];

    /// <summary>Today's single-pool view (<see cref="MemorySnapshot"/>) of the host part, for code that predates nodes.</summary>
    public MemorySnapshot ToHostSnapshot() => new(HostTotalGiB, HostAvailableGiB);
}

/// <summary>One GPU's memory in a <see cref="NodeMemorySnapshot"/>. Memory fields are null on unified GPUs.</summary>
/// <param name="Index"><c>nvidia-smi</c> index.</param>
/// <param name="Uuid">GPU UUID.</param>
/// <param name="TotalGiB">VRAM total.</param>
/// <param name="UsedGiB">VRAM used.</param>
/// <param name="FreeGiB">VRAM free.</param>
public sealed record GpuMemorySnapshot(int Index, string Uuid, double? TotalGiB = null, double? UsedGiB = null, double? FreeGiB = null);

/// <summary>
/// The node's GPUs as the agent sees them now (P2, frozen by MN-101, D73): implemented on the agent by MN-104 over
/// <c>nvidia-smi --query-gpu=index,uuid,name,memory.total,…</c> and read by the launch path (MN-106) to refuse a placed UUID
/// that is not on the node with 422 <c>gpu_missing</c> before reserving (<c>IRuntimeDriver.CheckSpec</c> is synchronous, so
/// it reads <see cref="Last"/>), and by discrete admission (MN-105). Lives in Core because the container driver
/// (NodeRuntime) and the agent both use it.
/// </summary>
public interface IGpuInventory
{
    /// <summary>The last inventory read, in index order; null before the first read or when <c>nvidia-smi</c> is absent.</summary>
    IReadOnlyList<GpuCapability>? Last { get; }

    /// <summary>Reads the inventory now (updates <see cref="Last"/>). Never throws except for cancellation; empty when unavailable.</summary>
    Task<IReadOnlyList<GpuCapability>> RefreshAsync(CancellationToken ct = default);

    /// <summary>The UUIDs of <paramref name="uuids"/> that are not in <see cref="Last"/> (all of them when <see cref="Last"/> is null).</summary>
    IReadOnlyList<string> Missing(IEnumerable<string> uuids)
    {
        ArgumentNullException.ThrowIfNull(uuids);
        var present = new HashSet<string>((Last ?? []).Select(g => g.Uuid), StringComparer.Ordinal);
        return [.. uuids.Where(u => !present.Contains(u))];
    }
}
