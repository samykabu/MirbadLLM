using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Contract;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// Configuration of a <see cref="FakeAgent"/>. Defaults: a unified 128 GiB node (100 GiB available) speaking this build's
/// protocol with every P1 feature, seeded with <see cref="AgentContractSeed"/>, fast simulated durations.
/// </summary>
internal sealed record FakeAgentOptions
{
    /// <summary>Every feature a P1 DGX agent offers.</summary>
    public static IReadOnlyList<string> P1Features { get; } =
    [
        AgentFeatures.RuntimeVllmContainer, AgentFeatures.DownloadsHfContainer, AgentFeatures.ServicesSystemd,
        AgentFeatures.ServicesDocker, AgentFeatures.BackupMount, AgentFeatures.GpuProcesses, AgentFeatures.TelemetryV1,
    ];

    /// <summary>Node id (SAN of the agent certificate).</summary>
    public string NodeId { get; init; } = "dgx";

    /// <summary>Node uid; null = random.</summary>
    public string? NodeUid { get; init; }

    /// <summary>Agent version reported by <c>/info</c> and <c>/capabilities</c>.</summary>
    public string AgentVersion { get; init; } = "0.0.0-fake";

    /// <summary>Protocol range (e.g. N−1 or non-overlapping for compatibility tests, design §13 item 4).</summary>
    public ProtocolRange Protocol { get; init; } = MirbadLLM.Agent.Contracts.Protocol.Range;

    /// <summary>Feature flags (design §3.3, §3.6); a route whose feature is missing answers 501 <c>not_supported</c>.</summary>
    public IReadOnlyList<string> Features { get; init; } = P1Features;

    /// <summary>Clock of the simulation (instances, operations, retention, telemetry). Transport and TLS use the system clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Unified memory total.</summary>
    public double TotalMemoryGiB { get; init; } = 128;

    /// <summary>Unified memory available with no instance running (Ready instances take their budget from it).</summary>
    public double AvailableMemoryGiB { get; init; } = 100;

    /// <summary>The node's GPU (unified: one GPU sharing host memory).</summary>
    public string GpuUuid { get; init; } = "GPU-fake-0000-0000-0000-000000000000";

    /// <summary>
    /// Discrete mode (P2, MN-104; <see cref="FakeAgent.RenumberGpus"/>/<see cref="FakeAgent.RemoveGpu"/> are the knobs):
    /// these GPUs with their own VRAM, in index order (e.g. <see cref="FakeDiscreteGpu.TwoBy16"/>), and the memory model
    /// <c>discrete</c>; <see cref="TotalMemoryGiB"/>/<see cref="AvailableMemoryGiB"/> are then the host RAM. Null (default) =
    /// the P1 unified node with one GPU (<see cref="GpuUuid"/>). Does not add P2 features: list them in <see cref="Features"/>.
    /// </summary>
    public IReadOnlyList<FakeDiscreteGpu>? DiscreteGpus { get; init; }

    /// <summary>Time from start to Ready.</summary>
    public TimeSpan StartDuration { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Time to stop an instance.</summary>
    public TimeSpan StopDuration { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Time to pull a missing image.</summary>
    public TimeSpan PullDuration { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>A pull longer than this re-checks admission before create (design §4.8: 30 s).</summary>
    public TimeSpan PullRecheckAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time a download takes.</summary>
    public TimeSpan DownloadDuration { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>Heartbeat interval on <c>/events</c>.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = MirbadLLM.Agent.Contracts.Protocol.HeartbeatInterval;

    /// <summary>Telemetry sampling interval (a sample is also taken at every boot).</summary>
    public TimeSpan TelemetryInterval { get; init; } = MirbadLLM.Agent.Contracts.Protocol.TelemetryInterval;

    /// <summary>Telemetry ring window.</summary>
    public TimeSpan TelemetryWindow { get; init; } = MirbadLLM.Agent.Contracts.Protocol.TelemetryRingWindow;

    /// <summary>Events kept for replay.</summary>
    public int EventRingCapacity { get; init; } = MirbadLLM.Agent.Contracts.Protocol.EventRingCapacity;

    /// <summary>Oldest replayable event.</summary>
    public TimeSpan EventRingMaxAge { get; init; } = MirbadLLM.Agent.Contracts.Protocol.EventRingMaxAge;

    /// <summary>Bounded queue per subscriber.</summary>
    public int SubscriberQueueCapacity { get; init; } = MirbadLLM.Agent.Contracts.Protocol.EventSubscriberQueueCapacity;

    /// <summary>Terminal records always kept besides those within <c>expiresAt</c> + 10 min (design §4.4 item 6).</summary>
    public int MinRetainedRecords { get; init; } = OperationRetention.MinRetainedRecords;

    /// <summary>Launch specs and pulls without a digest are accepted (P1 DGX); a digest, when given, must be approved.</summary>
    public bool AllowTagPulls { get; init; } = true;

    /// <summary>In-flight proxied requests; excess → 503 <c>server_busy</c> (design §7.2).</summary>
    public int MaxInFlightProxy { get; init; } = MirbadLLM.Agent.Contracts.Protocol.DefaultMaxInFlightPerNode;

    /// <summary>State directory (operation log, fencing triple); null = a new temp directory deleted on dispose.</summary>
    public string? StateDirectory { get; init; }

    /// <summary>The master CA; null = a new one owned by the fake.</summary>
    public CertificateAuthority? Ca { get; init; }

    /// <summary>Seed <see cref="AgentContractSeed"/> (images, folders, units, containers).</summary>
    public bool SeedContractFixture { get; init; } = true;

    /// <summary>hostd answers (host services, backup mount, GPU processes).</summary>
    public bool HostdAvailable { get; init; } = true;

    /// <summary>
    /// Runtimes reported in <c>/capabilities</c> besides the vLLM container one (e.g. a llama.cpp process runtime with its
    /// versions, MN-226's editor tests). Empty by default, so every existing capability report is unchanged.
    /// </summary>
    public IReadOnlyList<RuntimeCapability> ExtraRuntimes { get; init; } = [];

    /// <summary>
    /// A P3/P4 node that reports no vLLM container runtime at all (MN-233): <c>/capabilities</c> then lists exactly
    /// <see cref="ExtraRuntimes"/>, as the Windows agent with Docker off does. False (default) keeps the vLLM container
    /// runtime first, so every existing capability report is unchanged.
    /// </summary>
    public bool NoContainerRuntime { get; init; }

    /// <summary>
    /// The OS reported in <c>/capabilities</c> (MN-233: a Windows node). Null = the fake's Linux answer,
    /// so every existing capability report is unchanged.
    /// </summary>
    public NodeOsInfo? Os { get; init; }

    /// <summary>The architecture reported in <c>/capabilities</c>; null = the fake's <c>arm64</c>.</summary>
    public string? Arch { get; init; }

    /// <summary>
    /// The host-services manager reported in <c>/capabilities</c> (MN-233: <c>scm</c> on Windows, MN-228); null = the
    /// fake's <c>systemd</c>.
    /// </summary>
    public string? HostServicesManager { get; init; }

    /// <summary>
    /// GPU memory a <see cref="LaunchDelivery.Process"/> instance reports as measured at its start
    /// (<see cref="RuntimeStatus.MeasuredGpuGiB"/>, design-p3p4 §A.1.9 / <c>NvidiaSmiGpuStartMeter</c>), set once the
    /// instance is Ready. Null (default) = not measured, as a container instance always answers.
    /// </summary>
    public double? ProcessMeasuredGpuGiB { get; init; }
}
