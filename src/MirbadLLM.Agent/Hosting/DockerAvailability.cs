using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// The no-Docker switch (design-p3p4 §A.4.3, MN-214): <c>Agent:Docker:Enabled</c>, true by default (Linux agents and the
/// DGX unchanged), false on the Windows PC. MN-214 builds the switch; the composition (MN-232) wires it. With Docker off:
/// <list type="bullet">
/// <item>no probe: the composition (MN-232) does not register the Docker-only contributors — the container runtime's
/// capability and its <c>docker_unreachable</c> reason — at all, so nothing can touch the Docker Engine. The
/// <see cref="Gate(ICapabilityContributor)"/> and <see cref="Gate(IHealthContributor)"/> wrappers are kept as the seam
/// for a composition that does register them (MN-214's own tests use both); MN-234 ruled them worth keeping rather than
/// pruning, because registration-time exclusion and these wrappers are two different guarantees and only the first one
/// is in force today;</item>
/// <item>no wait: <see cref="WaitForDockerAsync"/> returns at once instead of the bounded start-up wait (D56), which on
/// Windows would otherwise spend the SCM's 30 s (§A.4.1);</item>
/// <item>no Docker features or reasons: <see cref="Filter()"/> is registered after every other capability contributor and
/// <see cref="Apply(NodeCapabilities)"/> / <see cref="Apply(IReadOnlyList{HealthReason})"/> remove what a mixed
/// contributor (storage, host services, launch features) still adds: the <see cref="DockerFeatures"/>, the container
/// runtimes, the image list and the containers flag of the host services, and <see cref="DockerHealthReasons"/>.</item>
/// </list>
/// With Docker on, every member is the identity: the wrappers call through, <see cref="Apply(NodeCapabilities)"/> returns
/// the same instance, so the DGX's capability report stays byte-identical.
/// </summary>
public sealed class DockerAvailability
{
    /// <summary>Configuration key of the switch.</summary>
    public const string ConfigurationKey = AgentOptions.SectionName + ":Docker:Enabled";

    /// <summary>Docker is on (the default).</summary>
    public static DockerAvailability On { get; } = new(true);

    /// <summary>Docker is off.</summary>
    public static DockerAvailability Off { get; } = new(false);

    /// <summary>Features only a Docker node reports (removed with Docker off).</summary>
    public static IReadOnlyList<string> DockerFeatures { get; } =
    [
        AgentFeatures.RuntimeVllmContainer,
        AgentFeatures.RuntimeLlamaCppContainer,
        AgentFeatures.DownloadsHfContainer,
        AgentFeatures.ServicesDocker,
        AgentFeatures.ImagesDigestPinned,
    ];

    /// <summary>Degraded reasons only a Docker node reports (removed with Docker off).</summary>
    public static IReadOnlyList<string> DockerHealthReasons { get; } = [HealthReasonCodes.DockerUnreachable];

    private DockerAvailability(bool enabled) => Enabled = enabled;

    /// <summary>True when the node runs Docker.</summary>
    public bool Enabled { get; }

    /// <summary>The switch of <paramref name="options"/> (<see cref="AgentOptions.Docker"/>).</summary>
    public static DockerAvailability From(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Docker.Enabled ? On : Off;
    }

    /// <summary>The switch read from <paramref name="configuration"/> (<see cref="ConfigurationKey"/>; absent = on).</summary>
    public static DockerAvailability From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue(ConfigurationKey, defaultValue: true) ? On : Off;
    }

    /// <summary>
    /// The bounded start-up wait for Docker (design D56) when Docker is on; with Docker off it returns false at once and
    /// <paramref name="wait"/> is never called (no probe, no wait). <paramref name="wait"/> is the existing wait
    /// (<c>DockerProbe.WaitAsync(limit, ct)</c>).
    /// </summary>
    public Task<bool> WaitForDockerAsync(Func<CancellationToken, Task<bool>> wait, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wait);
        return Enabled ? wait(ct) : Task.FromResult(false);
    }

    /// <summary>
    /// The bounded start-up wait through the composition's Docker probe (the one <c>Program.CreateAsync</c> runs today); with
    /// Docker off the probe is not even resolved.
    /// </summary>
    internal Task<bool> WaitForDockerAsync(IServiceProvider services, TimeSpan limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return WaitForDockerAsync(token => services.GetRequiredService<DockerProbe>().WaitAsync(limit, token), ct);
    }

    /// <summary>A Docker-only capability contributor: called with Docker on, skipped (report unchanged) with Docker off.</summary>
    public ICapabilityContributor Gate(ICapabilityContributor dockerOnly)
    {
        ArgumentNullException.ThrowIfNull(dockerOnly);
        return Enabled ? dockerOnly : SkippedCapability.Instance;
    }

    /// <summary>A Docker-only health contributor: called with Docker on, skipped (no reasons) with Docker off.</summary>
    public IHealthContributor Gate(IHealthContributor dockerOnly)
    {
        ArgumentNullException.ThrowIfNull(dockerOnly);
        return Enabled ? dockerOnly : SkippedHealth.Instance;
    }

    /// <summary>
    /// The image engine the storage package lists and pulls with: the Docker one with Docker on; with Docker off one that
    /// lists no images and never calls Docker (so the storage contributor's image listing is no probe either), and refuses
    /// pulls with <see cref="NotSupportedException"/>.
    /// </summary>
    public IContainerImageEngine Gate(IContainerImageEngine docker)
    {
        ArgumentNullException.ThrowIfNull(docker);
        return Enabled ? docker : NoImages.Instance;
    }

    /// <summary>
    /// <see cref="Gate(IContainerImageEngine)"/> for a composition that must not even build the Docker client with Docker
    /// off (MN-232: no <c>DockerEngineClient</c>, no Docker <see cref="HttpClient"/>): <paramref name="docker"/> is called
    /// only when Docker is on.
    /// </summary>
    public IContainerImageEngine Gate(Func<IContainerImageEngine> docker)
    {
        ArgumentNullException.ThrowIfNull(docker);
        return Enabled ? Gate(docker()) : NoImages.Instance;
    }

    /// <summary>
    /// Routes that need the Docker Engine and that no feature gates, so they are reachable on a node without Docker
    /// (design D129 (3), MN-232): with Docker off the request pipeline answers them 503 <c>docker_unavailable</c>
    /// <b>before</b> an operation is accepted, instead of accepting a pull that fails inside the engine. <c>GET /images</c>
    /// is not one of them: the gated engine lists nothing, which is the truthful answer.
    /// </summary>
    public static IReadOnlyList<AgentRoute> DockerOnlyRoutes { get; } = [AgentRoutes.ImagePull];

    /// <summary>
    /// Null when <paramref name="route"/> may run on this node, else the refusal to answer with (503
    /// <c>docker_unavailable</c>). Always null with Docker on, so the DGX's pipeline is unchanged.
    /// </summary>
    public AgentError? Refuse(AgentRoute? route) =>
        Enabled || route is null || !DockerOnlyRoutes.Contains(route)
            ? null
            : new AgentError
            {
                Code = ErrorCodes.DockerUnavailable,
                Message = $"This node runs no Docker ({ConfigurationKey} = false), so {route.Name} is unavailable.",
            };

    /// <summary>
    /// The last capability contributor: <see cref="Apply(NodeCapabilities)"/> on the folded report. Registered after every
    /// other one (with Docker on it changes nothing).
    /// </summary>
    public ICapabilityContributor Filter() => new FilterContributor(this);

    /// <summary>A health contributor whose reasons pass through <see cref="Apply(IReadOnlyList{HealthReason})"/>.</summary>
    public IHealthContributor Filter(IHealthContributor inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return Enabled ? inner : new FilteredHealth(this, inner);
    }

    /// <summary>
    /// <paramref name="report"/> without anything Docker (Docker off): the <see cref="DockerFeatures"/>, the container
    /// runtimes, the images, and <see cref="HostServicesCapability.Containers"/>. With Docker on, the same instance.
    /// </summary>
    public NodeCapabilities Apply(NodeCapabilities report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (Enabled)
        {
            return report;
        }

        var features = report.Features.Where(f => !DockerFeatures.Contains(f, StringComparer.Ordinal)).ToList();
        var runtimes = report.Runtimes.Where(r => r.Delivery != LaunchDelivery.Container).ToList();
        var hostServices = report.HostServices is { Containers: true } services ? services with { Containers = false } : report.HostServices;
        return features.Count == report.Features.Count && runtimes.Count == report.Runtimes.Count &&
            report.Images.Count == 0 && ReferenceEquals(hostServices, report.HostServices)
            ? report
            : report with { Features = features, Runtimes = runtimes, Images = [], HostServices = hostServices };
    }

    /// <summary><paramref name="reasons"/> without the <see cref="DockerHealthReasons"/> (Docker off); unchanged with Docker on.</summary>
    public IReadOnlyList<HealthReason> Apply(IReadOnlyList<HealthReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (Enabled || !reasons.Any(r => DockerHealthReasons.Contains(r.Code, StringComparer.Ordinal)))
        {
            return reasons;
        }

        return [.. reasons.Where(r => !DockerHealthReasons.Contains(r.Code, StringComparer.Ordinal))];
    }

    private sealed class SkippedCapability : ICapabilityContributor
    {
        public static readonly SkippedCapability Instance = new();

        public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default) => Task.FromResult(current);
    }

    private sealed class SkippedHealth : IHealthContributor
    {
        public static readonly SkippedHealth Instance = new();

        public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HealthReason>>([]);
    }

    private sealed class NoImages : IContainerImageEngine
    {
        public static readonly NoImages Instance = new();

        public Task<IReadOnlyList<ImageInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ImageInfo>>([]);

        public Task<ImageInfo?> InspectAsync(string reference, CancellationToken ct = default) => Task.FromResult<ImageInfo?>(null);

        public Task PullAsync(string repository, string tagOrDigest, Action<ImagePullProgress> progress, CancellationToken ct = default) =>
            throw new NotSupportedException("Docker is off on this node (Agent:Docker:Enabled = false): images cannot be pulled.");
    }

    private sealed class FilterContributor(DockerAvailability docker) : ICapabilityContributor
    {
        public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default) =>
            Task.FromResult(docker.Apply(current));
    }

    private sealed class FilteredHealth(DockerAvailability docker, IHealthContributor inner) : IHealthContributor
    {
        public async Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default) =>
            docker.Apply(await inner.CheckAsync(ct).ConfigureAwait(false));
    }
}
