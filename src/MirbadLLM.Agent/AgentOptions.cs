using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;

namespace MirbadLLM.Agent;

/// <summary>
/// The agent's configuration (<c>agent.json</c>, section <see cref="SectionName"/>; design §2.2, §4.1, §10.3, §11.2). The
/// node-local settings the master loses in P1 (§11.2: <c>DockerEndpoint</c>, <c>ModelsHostPath</c>, <c>CacheHostPath</c>,
/// <c>DockerNetwork</c>, <c>UseContainerNetwork</c>, <c>UpstreamHost</c>, <c>PortRangeStart</c>, <c>HostAgentSocket</c>,
/// <c>ModelsLocalPath</c>) map 1:1 onto properties here, with today's defaults, so the DGX agent creates exactly the
/// containers the controller creates today. Everything else has a default that works for the DGX container; only
/// <see cref="NodeId"/> and <see cref="StateDirectory"/> are required.
/// <code>
/// {
///   "Agent": {
///     "NodeId": "dgx",
///     "StateDirectory": "/var/lib/mirbadllm-agent",
///     "Port": 9443,
///     "ModelsRoot": "/models",
///     "ModelsHostPath": "/srv/mirbadllm/models",
///     "CacheHostPath": "/srv/mirbadllm/cache",
///     "DockerEndpoint": "unix:///var/run/docker.sock",
///     "HostdSocket": "/run/mirbadllm-hostd/hostd.sock",
///     "AllowTagPulls": true,
///     "ApprovedImages": [ "vllm/vllm-openai@sha256:…" ],
///     "BootstrapDirectory": "/run/mirbadllm-bootstrap"
///   }
/// }
/// </code>
/// Environment variables override the file (<see cref="EnvironmentPrefix"/>, e.g. <c>MIRBADLLM_AGENT_Agent__Port=9444</c>).
/// </summary>
public sealed partial class AgentOptions
{
    /// <summary>Configuration section of <c>agent.json</c>.</summary>
    public const string SectionName = "Agent";

    /// <summary>Prefix of environment variables that override <c>agent.json</c>.</summary>
    public const string EnvironmentPrefix = "MIRBADLLM_AGENT_";

    /// <summary>Environment variable naming the <c>agent.json</c> path when <c>--config</c> is absent.</summary>
    public const string ConfigPathEnvironmentVariable = "MIRBADLLM_AGENT_CONFIG";

    /// <summary>
    /// This node's id (design §3.1: <c>^[a-z0-9][a-z0-9-]{0,39}$</c>; the DGX is <c>dgx</c>). Must equal the node id in the
    /// agent certificate; a bootstrap bundle for another node is refused. Required.
    /// </summary>
    public string NodeId { get; set; } = "";

    /// <summary>
    /// The state directory (<c>agent.key</c>, <c>agent.crt</c>, <c>ca.crt</c>, <c>node-uid</c>, <c>fencing.json</c>, the
    /// operation log; a Docker named volume on the DGX, plan §3.6 S4). Required.
    /// </summary>
    public string StateDirectory { get; set; } = "";

    /// <summary>Address the mTLS listener binds; null, empty or <c>*</c> = every address.</summary>
    public string? ListenAddress { get; set; }

    /// <summary>mTLS port (design §4.1, default 9443). 0 picks a free port (tests).</summary>
    public int Port { get; set; } = Protocol.DefaultAgentPort;

    /// <summary>Listener request body limit (≥ the 64 MiB proxy limit; MN-014 default 72 MiB).</summary>
    public long MaxRequestBodyBytes { get; set; } = AgentHostOptions.DefaultMaxRequestBodyBytes;

    /// <summary>Version reported by <c>/info</c>; null = the assembly's informational version.</summary>
    public string? AgentVersion { get; set; }

    // ---- Storage roots (today's ModelsHostPath / CacheHostPath / ModelsLocalPath) ----

    /// <summary>
    /// The <c>models</c> logical volume as the agent process sees it (today's <c>ModelsLocalPath</c>, default
    /// <c>/models</c>). Folder listing, inspection, deletion and download progress are confined to it.
    /// </summary>
    public string ModelsRoot { get; set; } = "/models";

    /// <summary>The <c>models</c> volume as Docker sees it: the bind source of model and download containers (today's <c>ModelsHostPath</c>).</summary>
    public string ModelsHostPath { get; set; } = "/srv/mirbadllm/models";

    /// <summary>The <c>cache</c> volume as the agent sees it (reported in the capability report); null = not mounted into the agent.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>The <c>cache</c> volume as Docker sees it (today's <c>CacheHostPath</c>).</summary>
    public string CacheHostPath { get; set; } = "/srv/mirbadllm/cache";

    // ---- Docker (today's DockerEndpoint / DockerNetwork / UseContainerNetwork / UpstreamHost) ----

    /// <summary>Docker Engine endpoint: <c>unix:///var/run/docker.sock</c>, <c>npipe://./pipe/docker_engine</c> or <c>tcp://host:2375</c>.</summary>
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";

    /// <summary>Docker network model containers join (alias <c>ss-{id}</c>), as today.</summary>
    public string DockerNetwork { get; set; } = "mirbadllm";

    /// <summary>
    /// Reach models by <c>http://ss-{id}:8000/</c> on <see cref="DockerNetwork"/> (the agent container is on that network);
    /// false = the published loopback port on <see cref="UpstreamHost"/> (agent on the host, local development).
    /// </summary>
    public bool UseContainerNetwork { get; set; } = true;

    /// <summary>Host used with a model's published port when <see cref="UseContainerNetwork"/> is false.</summary>
    public string UpstreamHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// How long the agent waits at start for Docker to answer before it reconciles anyway (design D56: launches that
    /// need Docker then fail <c>docker_unavailable</c>). Bounded so a dead daemon cannot keep the node invisible.
    /// </summary>
    public TimeSpan DockerStartupTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Upper bound of one Docker Engine call (create, start, stop, remove, inspect, list; not image pulls or followed
    /// logs, which stream). A hung call would otherwise hold a model's lock and its start reservation forever (W2 review
    /// MN-092 item 8). Must exceed the 30 s stop timeout the driver passes to Docker.
    /// </summary>
    public TimeSpan DockerCallTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Image of the Hugging Face download helper container (today's <c>VllmImage</c>: the vLLM image ships
    /// <c>huggingface_hub</c>).
    /// </summary>
    public string DownloadHelperImage { get; set; } = new MirbadLLMOptions().VllmImage;

    /// <summary>
    /// User the download helper container runs as (MN-126): <c>auto</c> (default) = this agent's own uid:gid on Linux when
    /// it is not root and can write <see cref="ModelsRoot"/>, so the agent can later delete the folders the helper writes;
    /// otherwise (Windows, a root agent, an unwritable root) the image's default user. <c>image</c> = always the image's
    /// default user (root for the vLLM image, the behaviour before MN-126). Anything else (<c>uid[:gid]</c>) is passed to
    /// Docker as is.
    /// </summary>
    public string DownloadHelperUser { get; set; } = NodeRuntime.DownloadHelperUser.Auto;

    // ---- Ports and admission (today's PortRangeStart / MemoryReserveGiB) ----

    /// <summary>First loopback host port handed out to model containers (today's <c>PortRangeStart</c>).</summary>
    public int PortRangeStart { get; set; } = 8101;

    /// <summary>Last loopback host port handed out (inclusive).</summary>
    public int PortRangeEnd { get; set; } = 65535;

    /// <summary>Reserve used when re-checking a reservation rebuilt without its admission inputs (today's <c>MemoryReserveGiB</c>).</summary>
    public double MemoryReserveGiB { get; set; } = 8;

    /// <summary>Concurrent starts on the node; null = the memory model's default (unified: 1).</summary>
    public int? MaxConcurrentStarts { get; set; }

    /// <summary>
    /// Forces the node's memory model (<c>Unified</c> or <c>Discrete</c>; W9 review MN-132, D102). Null (default) = the GPU
    /// probe decides once at startup (<see cref="AgentMemoryModel"/>: discrete when a GPU reports its own VRAM). The DGX's
    /// <c>agent.dgx.json</c> sets <c>Unified</c>, so its P1 admission, <c>/status</c> and feature list never depend on how
    /// <c>nvidia-smi</c> reports the GB10's memory.
    /// </summary>
    public MemoryModel? MemoryModel { get; set; }

    /// <summary>
    /// What runs the model weights on this node: unset or <c>cuda</c> (NVIDIA, the behaviour before this setting existed),
    /// <c>cpu</c> (no usable GPU: llama.cpp on the CPU, containers created without a GPU request, <c>nvidia-smi</c> never
    /// run) or <c>vulkan</c> (llama.cpp's Vulkan build on AMD or Intel GPUs, experimental). The installers write it; it
    /// is reported as <see cref="NodeCapabilities.Accelerator"/>. Re-run the installer after adding or removing a GPU.
    /// </summary>
    public string? Accelerator { get; set; }

    /// <summary><see cref="Accelerator"/> in its stored form: null (NVIDIA or macOS), <c>cpu</c> or <c>vulkan</c>.</summary>
    public string? EffectiveAccelerator => NodeAccelerators.Normalize(Accelerator);

    // ---- Image policy (design §10.3; one setting for pulls and launches, W2 review MN-092 item 5) ----

    /// <summary>Approved images as <c>repository@sha256:…</c>: pulls naming a digest and launches whose spec names one must match.</summary>
    public IList<string> ApprovedImages { get; } = [];

    /// <summary>
    /// True where tag-only images are allowed (the P1 DGX: today's images are tags): standalone pulls without a digest are
    /// accepted and launches need no approved digest. False (default): pulls need an approved digest and so do launches
    /// (the driver's <c>RequireApprovedDigest</c>).
    /// </summary>
    public bool AllowTagPulls { get; set; }

    /// <summary>Advertise NIM container delivery on this configured Linux/DGX node.</summary>
    public bool EnableNim { get; set; }

    // ---- Host services (today's HostAgentSocket) ----

    /// <summary>mirbadllm-hostd's Unix socket (bind-mounted read-only into the agent container).</summary>
    public string HostdSocket { get; set; } = HostServicesOptions.DefaultHostAgentSocket;

    /// <summary>True when hostd is expected on this node, so a missing socket is Degraded from the first check on.</summary>
    public bool HostdExpected { get; set; }

    /// <summary>Manage systemd units through hostd (<c>services.systemd</c>, <c>gpu-processes</c>).</summary>
    public bool ManageSystemdUnits { get; set; } = true;

    /// <summary>
    /// macOS: manage launchd jobs through the macOS hostd (<c>services.launchd</c>, docs/macos/implementation-plan.md M10).
    /// Read on macOS only, where it takes the place of <see cref="ManageSystemdUnits"/>; off until hostd is installed
    /// (<c>install-agent.sh install-hostd</c> turns it on).
    /// </summary>
    public bool ManageLaunchdJobs { get; set; }

    /// <summary>Manage non-model Docker containers (<c>services.docker</c>).</summary>
    public bool ManageContainers { get; set; } = true;

    /// <summary>Forward backup-share mounts to hostd (<c>backup-mount</c>, topology a).</summary>
    public bool ManageBackupMount { get; set; } = true;

    /// <summary>The master's container, never controlled by the agent (design §10.3).</summary>
    public string MasterContainerName { get; set; } = "mirbadllm";

    /// <summary>The agent's own container, never controlled by the agent (design §10.3).</summary>
    public string AgentContainerName { get; set; } = "mirbadllm-agent";

    // ---- Proxy ----

    /// <summary>Requests proxied at the same time on this node (design §7.2); excess → 503 <c>server_busy</c>.</summary>
    public int ProxyMaxInFlight { get; set; } = Protocol.DefaultMaxInFlightPerNode;

    /// <summary>
    /// How long a proxied model may send nothing before its request ends (<see cref="Proxy.AgentProxyOptions.UpstreamIdleTimeout"/>);
    /// zero turns the limit off.
    /// </summary>
    public TimeSpan ProxyUpstreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    // ---- Enrollment (design §3.2) ----

    /// <summary>
    /// Bootstrap mode (the DGX, design §3.2): the directory the master writes <c>bootstrap.json</c> into
    /// (<see cref="BootstrapBundle.DefaultDirectory"/> on the DGX). When set, a bundle found there at start is used to
    /// enroll (replacing an older identity) and then deleted; an agent without certificates waits up to
    /// <see cref="BootstrapWait"/> for one. Null: the agent must already be enrolled (<c>mirbadllm-agent join</c>).
    /// </summary>
    public string? BootstrapDirectory { get; set; }

    /// <summary>How long an agent without certificates waits for the bootstrap bundle before it gives up.</summary>
    public TimeSpan BootstrapWait { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Addresses the master can reach this agent at, sent with a bootstrap enrollment (IP, DNS name or URL).</summary>
    public IList<string> Addresses { get; } = [];

    // ---- P3/P4: Docker switch, executables, supervised processes (MN-202, design D116; design-p3p4 §A.1, §A.4.3) ----

    /// <summary>
    /// Docker on this node (<c>Agent:Docker</c>). <see cref="AgentDockerOptions.Enabled"/> defaults to true everywhere
    /// (Linux agents and the DGX unchanged); the Windows example (<c>agent.windows.example.json</c>) sets it false: no Docker
    /// probe, no startup wait, no container driver, no Docker features or <c>docker_unreachable</c> reason (MN-214 builds
    /// the switch, MN-232 wires it).
    /// </summary>
    public AgentDockerOptions Docker { get; } = new();

    /// <summary>
    /// Logical executables for process delivery (<c>Agent:Executables</c>, design-p3p4 §A.1.2): written by
    /// <c>install-agent.ps1 -LlamaCpp</c>, read by the executable catalogue (MN-222). Empty = no process runtime.
    /// </summary>
    public IList<ExecutableOptions> Executables { get; } = [];

    /// <summary>Supervised processes (<c>Agent:Process</c>, design-p3p4 §A.1.3): start timeout, restart budget, log files.</summary>
    public AgentProcessOptions Process { get; } = new();

    // ---- Lifecycle ----

    /// <summary>
    /// How long a stopping agent waits for requests and running operations to unwind (in-flight operations that do not
    /// finish stay non-terminal in the log and are reconciled at the next start).
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the agent checks its health and publishes a <c>health</c> event when the Degraded reasons changed
    /// (MN-047 / D68). Defaults to the heartbeat interval, so a connected master sees Online ↔ Degraded within one heartbeat.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = Protocol.HeartbeatInterval;

    /// <summary>Reads the <see cref="SectionName"/> section of <paramref name="configuration"/> and validates it.</summary>
    public static AgentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new AgentOptions();
        configuration.GetSection(SectionName).Bind(options);
        options.Validate();
        return options;
    }

    /// <summary>Throws <see cref="ArgumentException"/> naming the first unusable setting.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(NodeId) || !NodeIdRegex().IsMatch(NodeId))
        {
            throw new ArgumentException($"Agent:NodeId '{NodeId}' is not a node id (lower-case letters, digits and '-', at most 40).", nameof(NodeId));
        }

        Require(StateDirectory, nameof(StateDirectory));
        Require(ModelsRoot, nameof(ModelsRoot));
        Require(ModelsHostPath, nameof(ModelsHostPath));
        Require(CacheHostPath, nameof(CacheHostPath));
        Require(DockerEndpoint, nameof(DockerEndpoint));
        Require(DockerNetwork, nameof(DockerNetwork));
        Require(DownloadHelperImage, nameof(DownloadHelperImage));
        if (!DownloadHelperUserRegex().IsMatch(DownloadHelperUser ?? ""))
        {
            throw new ArgumentException($"Agent:DownloadHelperUser '{DownloadHelperUser}' must be auto, image, or user[:group].", nameof(DownloadHelperUser));
        }
        Require(HostdSocket, nameof(HostdSocket));
        if (PortRangeStart is < 1 or > 65535 || PortRangeEnd < PortRangeStart || PortRangeEnd > 65535)
        {
            throw new ArgumentException($"Agent:PortRangeStart..PortRangeEnd ({PortRangeStart}..{PortRangeEnd}) must lie within 1–65535.", nameof(PortRangeStart));
        }

        if (MaxConcurrentStarts is <= 0)
        {
            throw new ArgumentException("Agent:MaxConcurrentStarts must be positive when set.", nameof(MaxConcurrentStarts));
        }

        if (MemoryReserveGiB < 0)
        {
            throw new ArgumentException("Agent:MemoryReserveGiB must not be negative.", nameof(MemoryReserveGiB));
        }

        if (EffectiveAccelerator is { } accelerator && MemoryModel is { } forced &&
            forced != (accelerator == NodeAccelerators.Cpu ? Core.MemoryModel.Unified : Core.MemoryModel.Discrete))
        {
            throw new ArgumentException(
                $"Agent:MemoryModel {forced} contradicts Agent:Accelerator {accelerator} (cpu admits against host memory, vulkan per GPU); remove Agent:MemoryModel.",
                nameof(MemoryModel));
        }

        if (ProxyUpstreamIdleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentException("Agent:ProxyUpstreamIdleTimeout must not be negative.", nameof(ProxyUpstreamIdleTimeout));
        }

        if (ProxyMaxInFlight < 1)
        {
            throw new ArgumentException("Agent:ProxyMaxInFlight must be at least 1.", nameof(ProxyMaxInFlight));
        }

        if (HealthCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("Agent:HealthCheckInterval must be positive.", nameof(HealthCheckInterval));
        }

        if (DockerStartupTimeout < TimeSpan.Zero || BootstrapWait < TimeSpan.Zero || ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Agent:DockerStartupTimeout and BootstrapWait must not be negative; ShutdownTimeout must be positive.", nameof(ShutdownTimeout));
        }

        if (DockerCallTimeout <= TimeSpan.FromSeconds(30))
        {
            throw new ArgumentException("Agent:DockerCallTimeout must exceed the driver's 30 s stop timeout.", nameof(DockerCallTimeout));
        }

        foreach (string image in ApprovedImages)
        {
            var reference = ImageReference.Parse(image);
            if (reference.Repository.Length == 0 || !ImageReference.IsValidDigest(reference.Digest) || reference.Tag is not null)
            {
                throw new ArgumentException($"Agent:ApprovedImages entry '{image}' is not repository@sha256:<64 hex>.", nameof(ApprovedImages));
            }
        }

        ValidateProcessNode();
        ToHostOptions().Validate();
    }

    /// <summary>
    /// The P3/P4 settings (MN-232, design-p3p4 §A.1.2, §A.1.3): <see cref="Process"/> and <see cref="Executables"/> are
    /// checked on every node, so a bad <c>agent.json</c> is named here and not inside the process driver. Both default to
    /// "nothing configured", so a P1/P2 <c>agent.json</c> (the DGX's) passes exactly as before.
    /// <para>Paths are <b>not</b> required to be rooted here: <c>agent.windows.example.json</c> must bind and validate on
    /// any build host. The process driver's own <c>ProcessRuntimeOptions.Validate</c> demands absolute paths when it is
    /// built, which happens only on the node itself; the composition additionally names a missing
    /// <see cref="CacheRoot"/> (the <c>{cache}</c> a native runtime is given) before it registers the driver.</para>
    /// </summary>
    private void ValidateProcessNode()
    {
        if (Process.StartTimeout <= TimeSpan.Zero || Process.RestartWindow <= TimeSpan.Zero || Process.HealthPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Agent:Process StartTimeout, RestartWindow and HealthPollInterval must be positive.", nameof(Process));
        }

        if (Process.RestartBudget < 0 || Process.LogFilesKept < 0 || Process.LogMaxBytes < 1)
        {
            throw new ArgumentException(
                "Agent:Process RestartBudget and LogFilesKept must not be negative and LogMaxBytes must be at least 1.", nameof(Process));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var executable in Executables)
        {
            if (executable is null || string.IsNullOrWhiteSpace(executable.Name))
            {
                throw new ArgumentException("Every Agent:Executables entry needs a Name (the logical name a launch asks for).", nameof(Executables));
            }

            if (!names.Add(executable.Name))
            {
                // Two blocks for one name would each get their own default (ExecutableCatalog derives it per block), so
                // two entries could carry IsDefault and `Find(name, null)` would pick whichever JSON ordering put first —
                // a launch fingerprint decided by file order (MN-234).
                throw new ArgumentException(
                    $"Agent:Executables lists '{executable.Name}' twice; put every version of an executable in one entry.", nameof(Executables));
            }

            if (executable.Versions.Count == 0)
            {
                throw new ArgumentException($"Agent:Executables entry '{executable.Name}' has no Versions.", nameof(Executables));
            }

            foreach (var version in executable.Versions)
            {
                if (version is null || string.IsNullOrWhiteSpace(version.Version))
                {
                    throw new ArgumentException($"Every version of Agent:Executables '{executable.Name}' needs a Version (the release tag).", nameof(Executables));
                }

                if (string.IsNullOrWhiteSpace(version.Path) || string.IsNullOrWhiteSpace(version.Manifest))
                {
                    throw new ArgumentException(
                        $"Agent:Executables '{executable.Name}' {version.Version} needs a Path and a Manifest (written by install-agent.ps1 -LlamaCpp).",
                        nameof(Executables));
                }

                if (!seen.Add(executable.Name + "\u0000" + version.Version))
                {
                    throw new ArgumentException($"Agent:Executables lists '{executable.Name}' {version.Version} twice.", nameof(Executables));
                }
            }

            if (executable.Default is { Length: > 0 } preferred &&
                !executable.Versions.Any(v => string.Equals(v.Version, preferred, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Agent:Executables '{executable.Name}' has Default '{preferred}', which is not one of its Versions.", nameof(Executables));
            }
        }
    }

    /// <summary>The listener settings for <see cref="AgentHostBuilder.Configure"/> (MN-014).</summary>
    public AgentHostOptions ToHostOptions() => new()
    {
        StateDirectory = StateDirectory,
        ListenAddress = ListenAddress,
        Port = Port,
        MaxRequestBodyBytes = MaxRequestBodyBytes,
        AgentVersion = AgentVersion,
    };

    /// <summary>The container driver's settings (MN-015): today's bind sources, network, upstream rule and the image policy.</summary>
    public ContainerRuntimeOptions ToContainerRuntimeOptions()
    {
        var options = new ContainerRuntimeOptions
        {
            NodeId = NodeId,
            ModelsHostPath = ModelsHostPath,
            CacheHostPath = CacheHostPath,
            ModelsLocalPath = ModelsRoot,
            CacheLocalPath = CacheRoot,
            DockerNetwork = DockerNetwork,
            UseContainerNetwork = UseContainerNetwork,
            UpstreamHost = UpstreamHost,
            RequireApprovedDigest = !AllowTagPulls,
            GpuAccess = EffectiveAccelerator switch
            {
                NodeAccelerators.Cpu => ContainerGpuAccess.None,
                NodeAccelerators.Vulkan => ContainerGpuAccess.DeviceNodes,
                _ => ContainerGpuAccess.Nvidia,
            },
        };
        foreach (string image in ApprovedImages)
        {
            options.ApprovedImages.Add(image);
        }

        return options;
    }

    /// <summary>The reservation manager's settings (MN-016).</summary>
    public AdmissionOptions ToAdmissionOptions() => new()
    {
        NodeId = NodeId,
        MemoryModel = Core.MemoryModel.Unified,
        MaxConcurrentStarts = MaxConcurrentStarts,
        PortRangeStart = PortRangeStart,
        PortRangeEnd = PortRangeEnd,
        DefaultReserveGiB = MemoryReserveGiB,
    };

    /// <summary>Applies the storage settings (MN-020) to <paramref name="storage"/>.</summary>
    public void ConfigureStorage(AgentStorageOptions storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        storage.ModelsRoot = ModelsRoot;
        storage.CacheRoot = CacheRoot;
        storage.AllowTagPulls = AllowTagPulls;
        storage.ApprovedImages.Clear();
        foreach (string image in ApprovedImages)
        {
            storage.ApprovedImages.Add(image);
        }
    }

    /// <summary>Applies the host-service settings (MN-023) to <paramref name="hostServices"/>.</summary>
    public void ConfigureHostServices(AgentHostServicesOptions hostServices)
    {
        ArgumentNullException.ThrowIfNull(hostServices);
        hostServices.HostdSocket = HostdSocket;
        hostServices.HostdExpected = HostdExpected;
        hostServices.DockerEndpoint = DockerEndpoint;
        hostServices.Systemd = ManageSystemdUnits;
        hostServices.Containers = ManageContainers;
        hostServices.BackupMount = ManageBackupMount;
        hostServices.MasterContainerName = MasterContainerName;
        hostServices.AgentContainerName = AgentContainerName;
    }

    /// <summary>
    /// The controller settings the moved <c>HuggingFaceDownloader</c> still reads (helper image, network, bind sources),
    /// filled from this node's settings.
    /// </summary>
    public MirbadLLMOptions ToDownloaderOptions() => new()
    {
        VllmImage = DownloadHelperImage,
        DockerEndpoint = DockerEndpoint,
        ModelsHostPath = ModelsHostPath,
        CacheHostPath = CacheHostPath,
        DockerNetwork = DockerNetwork,
        UseContainerNetwork = UseContainerNetwork,
        UpstreamHost = UpstreamHost,
        PortRangeStart = PortRangeStart,
        MemoryReserveGiB = MemoryReserveGiB,
    };

    /// <summary>Where the downloader measures progress (today's <c>ModelsLocalPath</c>).</summary>
    public ModelStorageOptions ToModelStorageOptions() => new() { ModelsLocalPath = ModelsRoot };

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Agent:{name} is required.", name);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,31})?$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadHelperUserRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdRegex();
}

/// <summary><c>Agent:Docker</c> (MN-202, D116).</summary>
public sealed class AgentDockerOptions
{
    /// <summary>
    /// False on a node without Docker (the Windows PC, design-p3p4 §A.4.3). Default true, so every existing
    /// <c>agent.json</c> keeps today's behaviour.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary><c>Agent:Process</c>: supervision of native processes (design-p3p4 §A.1.3; MN-202, D116; used by MN-211).</summary>
public sealed class AgentProcessOptions
{
    /// <summary>
    /// How long a process may stay Starting (llama-server answers 503 while it loads) before it is Failed: large GGUFs
    /// from a slow disk take minutes. Default 10 minutes.
    /// </summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Restarts after Ready allowed within <see cref="RestartWindow"/> (default 2): the next unexpected exit is Failed
    /// with the exit code and the log tail. An exit before Ready is Failed at once (no restart).
    /// </summary>
    public int RestartBudget { get; set; } = 2;

    /// <summary>Window of <see cref="RestartBudget"/> (default 10 minutes).</summary>
    public TimeSpan RestartWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Size at which <c>{state}/logs/ss-{id}.log</c> rotates (default 50 MiB).</summary>
    public long LogMaxBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Rotated log files kept (<c>.1</c>, <c>.2</c>; default 2).</summary>
    public int LogFilesKept { get; set; } = 2;

    /// <summary>How often <c>/health</c> is polled while Starting (default 2 s).</summary>
    public TimeSpan HealthPollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
