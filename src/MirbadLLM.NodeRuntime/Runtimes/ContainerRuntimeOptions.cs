using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Runtimes;

/// <summary>
/// Node-local settings of <see cref="ContainerRuntimeDriver"/> (design §4.3, §10.3). The master never sends a host path:
/// launch specs name the logical volumes <c>models</c>/<c>cache</c>, which resolve to the roots configured here.
/// <see cref="FromMirbadLLMOptions"/> maps today's controller settings so the DGX agent creates exactly the containers
/// the pre-P1 <c>DockerModelRuntime</c> created.
/// </summary>
public sealed class ContainerRuntimeOptions
{
    /// <summary>Most GPUs one container may get on a <see cref="ContainerGpuAccess.DeviceNodes"/> node.</summary>
    public const int MaxDeviceNodeGpus = 1;

    /// <summary>This node's id, written as <c>mirbadllm.node</c> on every instance the driver creates.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Host path of the <c>models</c> volume as Docker sees it (bind source; today's <c>ModelsHostPath</c>).</summary>
    public string ModelsHostPath { get; set; } = "/srv/mirbadllm/models";

    /// <summary>Host path of the <c>cache</c> volume as Docker sees it (today's <c>CacheHostPath</c>).</summary>
    public string CacheHostPath { get; set; } = "/srv/mirbadllm/cache";

    /// <summary>
    /// The <c>models</c> root as the agent process sees it, used to confine a mount <c>subPath</c> after resolving
    /// symlinks (design §10.3). Null = <see cref="ModelsHostPath"/> (agent running on the host).
    /// </summary>
    public string? ModelsLocalPath { get; set; }

    /// <summary>The <c>cache</c> root as the agent process sees it. Null = <see cref="CacheHostPath"/>.</summary>
    public string? CacheLocalPath { get; set; }

    /// <summary>Docker network the instances join (alias <c>ss-{id}</c>), as today.</summary>
    public string DockerNetwork { get; set; } = "mirbadllm";

    /// <summary>Reach instances by <c>http://ss-{id}:{port}/</c> on <see cref="DockerNetwork"/> (else the loopback port).</summary>
    public bool UseContainerNetwork { get; set; } = true;

    /// <summary>Host used with the published port when <see cref="UseContainerNetwork"/> is false.</summary>
    public string UpstreamHost { get; set; } = "127.0.0.1";

    /// <summary>The fixed runtime port inside the container (vLLM: 8000); a spec asking for another is refused.</summary>
    public int ContainerPort { get; set; } = 8000;

    /// <summary>
    /// Approved images as <c>repository@sha256:…</c> (design §10.3). When a spec carries an image digest and this list is
    /// not empty, the image repository and digest must match an entry; the container is then created from
    /// <c>repository@digest</c> (MN-107).
    /// </summary>
    public IList<string> ApprovedImages { get; } = [];

    /// <summary>
    /// Refuse launches without an approved digest (design §10.3, MN-107): a tag-only spec, or any digest while
    /// <see cref="ApprovedImages"/> is empty, is 422 <c>spec_rejected</c>, and the node reports
    /// <c>images.digest-pinned</c>. The agent sets it to <c>!AllowTagPulls</c>; the DGX keeps <c>AllowTagPulls = true</c>
    /// (false here) so it launches today's tag-only images unchanged.
    /// </summary>
    public bool RequireApprovedDigest { get; set; }

    /// <summary>
    /// How a model container gets its GPUs. <see cref="ContainerGpuAccess.Nvidia"/> (the default) is the NVIDIA device
    /// request every container had before; the agent sets the other values from <c>Agent:Accelerator</c>.
    /// </summary>
    public ContainerGpuAccess GpuAccess { get; set; } = ContainerGpuAccess.Nvidia;

    /// <summary>The <c>models</c> root the agent checks paths against.</summary>
    internal string ModelsLocalRoot => string.IsNullOrEmpty(ModelsLocalPath) ? ModelsHostPath : ModelsLocalPath;

    /// <summary>The <c>cache</c> root the agent checks paths against.</summary>
    internal string CacheLocalRoot => string.IsNullOrEmpty(CacheLocalPath) ? CacheHostPath : CacheLocalPath;

    /// <summary>
    /// Options equal to today's controller settings (bootstrap node, design §2.2): same bind sources, network and
    /// upstream rule as the pre-P1 <c>DockerModelRuntime</c>.
    /// </summary>
    public static ContainerRuntimeOptions FromMirbadLLMOptions(MirbadLLMOptions options, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return new ContainerRuntimeOptions
        {
            NodeId = nodeId,
            ModelsHostPath = options.ModelsHostPath,
            CacheHostPath = options.CacheHostPath,
            DockerNetwork = options.DockerNetwork,
            UseContainerNetwork = options.UseContainerNetwork,
            UpstreamHost = options.UpstreamHost,
        };
    }
}

/// <summary>How <see cref="ContainerRuntimeDriver"/> gives a container its GPUs.</summary>
public enum ContainerGpuAccess
{
    /// <summary>The NVIDIA Container Toolkit's device request (every GPU, or the selected UUIDs).</summary>
    Nvidia,

    /// <summary>No GPU: a CPU-only node. The create body carries no device request.</summary>
    None,

    /// <summary>
    /// A Vulkan node (AMD or Intel GPUs, experimental): no NVIDIA device request; the selected GPU's DRM render node is
    /// mapped into the container. One GPU per container (<see cref="ContainerRuntimeOptions.MaxDeviceNodeGpus"/>): the
    /// order in which Mesa lists several devices inside the container is not something the agent controls, so a split
    /// over two cards could put each share on the wrong one.
    /// </summary>
    DeviceNodes,
}
