namespace MirbadLLM.Core;

/// <summary>
/// Benchmark/tuning job limits (design §6.5 item 1, D15; P2, frozen by MN-101, D73), bound from <c>MirbadLLM:Jobs</c>.
/// One job per node, at most <see cref="MaxConcurrent"/> across nodes; implemented by <c>ModelJobService</c> (MN-111).
/// </summary>
public sealed class JobOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:Jobs";

    /// <summary>Default of <see cref="MaxConcurrent"/> (owner decision D15).</summary>
    public const int DefaultMaxConcurrent = 2;

    /// <summary>Jobs at once across all nodes; a further request is 409 <c>job_limit_reached</c> naming the limit. At least 1.</summary>
    public int MaxConcurrent { get; set; } = DefaultMaxConcurrent;
}

/// <summary>Kinds of maintenance windows and jobs (<see cref="MaintenanceState.JobKind"/>, <see cref="NodeJobSummary.Kind"/>).</summary>
public static class MaintenanceJobKinds
{
    /// <summary>A benchmark run (today's value).</summary>
    public const string Benchmark = "benchmark";

    /// <summary>A tuning run (today's value).</summary>
    public const string Tuning = "tuning";

    /// <summary>
    /// Fleet recovery (design §9.4 recovery mode, P2 MN-113/MN-130): <c>/v1</c> answers 503 <c>maintenance</c> "restoring"
    /// while the master runs in recovery mode. Global (no node).
    /// </summary>
    public const string Restore = "restore";
}

/// <summary>
/// One element of <c>GET /api/jobs</c> (always an array, design §5.6, D34) and the job part of a node view: a running
/// benchmark or tuning job with the node it holds. <c>GET /api/nodes/{id}/job</c> answers today's job snapshot shape for
/// that node (404 when none); <c>/api/benchmark</c> and <c>/api/tuning</c> keep their shape for the bootstrap node
/// (MN-111).
/// </summary>
public sealed record NodeJobSummary
{
    /// <summary>Job id.</summary>
    public required string JobId { get; init; }

    /// <summary><see cref="MaintenanceJobKinds.Benchmark"/> or <see cref="MaintenanceJobKinds.Tuning"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The node the job holds (maintenance is node-scoped from P2).</summary>
    public required string NodeId { get; init; }

    /// <summary>Model under test.</summary>
    public required string ModelId { get; init; }

    /// <summary>When the job started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Best estimate of the end (drives <c>Retry-After</c>); null when unknown.</summary>
    public DateTimeOffset? EstimatedEnd { get; init; }

    /// <summary>Human phase, as in today's job snapshot.</summary>
    public string? Phase { get; init; }

    /// <summary>Models on the node that keep serving (exempt, design §6.5 item 3).</summary>
    public IReadOnlyList<string> ExemptModelIds { get; init; } = [];
}
