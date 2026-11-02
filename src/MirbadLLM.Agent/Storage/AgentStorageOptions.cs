namespace MirbadLLM.Agent.Storage;

/// <summary>
/// Node-local storage settings of the agent (design §4.3 images/folders/downloads, §6.4, §10.3), bound from the agent's
/// configuration (<c>agent.json</c>, section <c>MirbadLLM:Agent:Storage</c>) by the host (MN-026). The master never
/// sends a host path: it names folders under <see cref="ModelsRoot"/> and images by reference + digest.
/// </summary>
public sealed class AgentStorageOptions
{
    /// <summary>Configuration section the host binds.</summary>
    public const string SectionName = "MirbadLLM:Agent:Storage";

    /// <summary>
    /// Where the agent process sees the <c>models</c> logical volume (the helper container mounts the same host directory
    /// at <c>/models</c>, <c>MirbadLLMOptions.ModelsHostPath</c>). Every folder operation is confined to it.
    /// </summary>
    public string ModelsRoot { get; set; } = "/models";

    /// <summary>Where the agent sees the <c>cache</c> logical volume, reported in the capability report. Null = not reported.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>
    /// Approved images (design §10.3) as <c>repository@sha256:…</c>. A pull naming a digest must match one entry
    /// (repository and digest); anything else is 422 <c>spec_rejected</c>.
    /// </summary>
    public IList<string> ApprovedImages { get; } = [];

    /// <summary>
    /// True only where the node allows pulls by tag without a digest (the P1 DGX, design §4.3 <c>ImagePullRequest.Digest</c>).
    /// Default false: tags alone are refused.
    /// </summary>
    public bool AllowTagPulls { get; set; }

    /// <summary>How often a running download's helper container is polled for progress and its outcome.</summary>
    public TimeSpan DownloadPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Minimum interval between durable progress records of a running download or image pull (events are published on
    /// every observed change; the operation log is not written on every poll).
    /// </summary>
    public TimeSpan ProgressRecordInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Free-space headroom required on top of the bytes a download still needs (design §6.4: remaining + 5 %).</summary>
    public double DiskHeadroomFraction { get; set; } = 0.05;

    /// <summary>A model root with less free space than this fraction makes the node Degraded (design §3.5: 5 %).</summary>
    public double LowFreeSpaceFraction { get; set; } = 0.05;

    /// <summary>How many finished downloads <c>GET /downloads</c> keeps listing (in memory, per agent boot).</summary>
    public int RecentDownloadsKept { get; set; } = 50;
}
