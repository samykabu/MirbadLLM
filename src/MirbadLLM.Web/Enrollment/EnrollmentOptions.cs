using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Web.Enrollment;

/// <summary>
/// Settings of the master's enroll listener and the DGX bootstrap bundle (design §3.2), bound from
/// <c>MirbadLLM:Enrollment</c> by the master composition (MN-040), which also adds the Kestrel endpoint on
/// <see cref="Port"/> with <see cref="EnrollListenerTls.ConfigureEndpoint"/> and maps <see cref="EnrollEndpoints.MapEnrollEndpoint"/>.
/// </summary>
public sealed class EnrollmentOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:Enrollment";

    /// <summary>Largest <c>/enroll</c> body accepted (a CSR is under 1 KiB; the capability report a few KiB).</summary>
    public const long MaxRequestBodyBytes = 256 * 1024;

    /// <summary>Enroll listener port (design §3.2, default 8443).</summary>
    public int Port { get; set; } = Protocol.DefaultEnrollPort;

    /// <summary>
    /// DNS names / IP literals put into the enroll-listener certificate's SAN next to <c>spiffe://mirbadllm/master</c>
    /// (informational: the joining agent checks the CA hash and the SPIFFE id, not the name). Default: the master's
    /// container name on the DGX's Docker network.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Bound from configuration.")]
    public List<string>? ListenerAddresses { get; set; }

    /// <summary>Writes the bootstrap bundle for the bootstrap node (<c>MirbadLLM:Nodes:BootstrapNodeId</c>, default <c>dgx</c>) while it is not enrolled.</summary>
    public bool BootstrapEnabled { get; set; } = true;

    /// <summary>Directory shared only between the master and the agent container (mode 0700, uid 1654).</summary>
    public string BootstrapDirectory { get; set; } = BootstrapBundle.DefaultDirectory;

    /// <summary>The enroll endpoint written into the bundle, as the agent container reaches the master.</summary>
    public Uri BootstrapEnrollEndpoint { get; set; } = new($"https://mirbadllm:{Protocol.DefaultEnrollPort}");

    /// <summary>
    /// Lifetime of the bootstrap token (10 min – 24 h, design §3.2). The bundle is replaced with a fresh token when less
    /// than <see cref="BootstrapRefreshMargin"/> remains, so an agent that starts late still finds a valid one.
    /// </summary>
    public TimeSpan BootstrapTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Remaining token lifetime below which the bundle is rewritten.</summary>
    public TimeSpan BootstrapRefreshMargin { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often the bootstrap bundle is checked.</summary>
    public TimeSpan BootstrapCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Agent address of the bootstrap node's record, which the master composition writes on first boot before the agent
    /// enrolls (<c>MasterHosting.EnsureBootstrapNodeAsync</c>). Null = <c>https://mirbadllm-agent:9443</c>, the agent
    /// container on the DGX's Docker network (its benchmark runs are labelled <c>agent-mtls-loopback</c>: same host, W4
    /// review MN-096). Enrollment keeps the address of an existing record; only a record enrollment itself creates falls
    /// back to the first address the agent reported, else its source IP, on the default agent port.
    /// </summary>
    public string? BootstrapAgentAddress { get; set; }

    /// <summary>Enroll attempts per client IP per <see cref="RateLimitWindow"/> (design §3.2: like <c>/login/submit</c>, 5/minute).</summary>
    public int RateLimitPermits { get; set; } = AdminPasswordGuard.PermitLimit;

    /// <summary>Rate-limit window.</summary>
    public TimeSpan RateLimitWindow { get; set; } = AdminPasswordGuard.Window;

    /// <summary>The listener SAN entries in effect.</summary>
    public IReadOnlyList<string> EffectiveListenerAddresses =>
        ListenerAddresses is { Count: > 0 } configured
            ? [.. configured.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())]
            : ["mirbadllm", "localhost"];
}
