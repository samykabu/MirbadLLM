namespace MirbadLLM.Core;

/// <summary>The two restore modes of the Restore tab (design §9.4, D29; P2, frozen by MN-101, D73; MN-113).</summary>
public enum RestoreMode
{
    /// <summary>
    /// Default, any master: imports model definitions, tags, sources, API keys (with the passphrase) and the Services
    /// container lists. Never imports the CA, node trust state or fencing data. A model keeps its node only when a node
    /// here has the same uid; otherwise the operator picks a target node per model or leaves it unplaced.
    /// </summary>
    ConfigurationImport,

    /// <summary>
    /// Fleet disaster recovery, only on an empty master (409 <c>master_not_empty</c> otherwise) started in recovery mode:
    /// restores master identity, CA, nodes with pins and state, models with placements, keys and service lists, then the
    /// takeover runs (design §9.4 items 1–6). Needs a v2 backup with the <c>pki</c> section and the passphrase (400
    /// <c>backup_unsuitable</c> otherwise).
    /// </summary>
    FleetRecovery,
}

/// <summary>How the master boots (design §9.4 recovery mode).</summary>
public enum RecoveryMode
{
    /// <summary>Normal start.</summary>
    None,

    /// <summary>
    /// Recovery mode for fleet DR: no model seeding, CA generation, bootstrap node, auto-start, backup scheduler or agent
    /// connections; serves only login, the Restore tab and <c>/healthz</c> (<c>/readyz</c> not ready, <c>/v1</c> 503
    /// <c>maintenance</c> "restoring"); installs the backup before normal initialization, removes the marker and restarts.
    /// </summary>
    FleetRestore,
}

/// <summary>
/// Recovery-mode switch (design §9.4), bound from <c>MirbadLLM:Recovery</c> (environment
/// <c>MirbadLLM__Recovery__Mode=FleetRestore</c>) or an empty <c>{DataPath}/recovery</c> marker file. Read once at
/// startup by the composition (MN-130), which then registers the restricted host; MN-113 installs the backup.
/// </summary>
public sealed class RecoveryOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:Recovery";

    /// <summary>Marker file name under the data path; its presence alone selects <see cref="RecoveryMode.FleetRestore"/>.</summary>
    public const string MarkerFileName = "recovery";

    /// <summary>The configured mode. Default: <see cref="RecoveryMode.None"/>.</summary>
    public RecoveryMode Mode { get; set; } = RecoveryMode.None;

    /// <summary>The effective mode: <see cref="Mode"/>, or <see cref="RecoveryMode.FleetRestore"/> when the marker file exists.</summary>
    public static RecoveryMode Effective(RecoveryOptions? options, string dataPath)
    {
        if (options?.Mode is RecoveryMode.FleetRestore)
        {
            return RecoveryMode.FleetRestore;
        }

        return !string.IsNullOrEmpty(dataPath) && File.Exists(Path.Combine(dataPath, MarkerFileName))
            ? RecoveryMode.FleetRestore
            : RecoveryMode.None;
    }

    /// <summary>True when the master must start in recovery mode.</summary>
    public static bool IsActive(RecoveryOptions? options, string dataPath) => Effective(options, dataPath) != RecoveryMode.None;
}

/// <summary>
/// The operator's choice for one model of a configuration import whose node is not matched by uid (design §9.4): a
/// target node (and GPUs on a discrete one), or unplaced.
/// </summary>
/// <param name="ModelId">Model id in the backup.</param>
/// <param name="TargetNodeId">Target node; null = leave unplaced.</param>
/// <param name="GpuUuids">Target GPUs on a discrete node; null on unified nodes.</param>
public sealed record RestoreTarget(string ModelId, string? TargetNodeId, IReadOnlyList<string>? GpuUuids = null);

/// <summary>How a backed-up model's node maps onto this master (design §9.4 "same uid, not slug").</summary>
public enum RestorePlacementMatch
{
    /// <summary>A node here has the backup node's uid: the model keeps its node.</summary>
    SameUid,

    /// <summary>Only the slug matches (maybe another machine): the operator picks a target.</summary>
    SlugOnly,

    /// <summary>No node matches: the operator picks a target or leaves it unplaced.</summary>
    None,

    /// <summary>The model was unplaced in the backup.</summary>
    Unplaced,
}

/// <summary>
/// Whether the master is empty enough for fleet DR (design §9.4 "provisional material"): the seeded model, a CA that never
/// issued an agent certificate, an unused bootstrap token/bundle, Data Protection keys and empty key/service stores do not
/// count; anything else does.
/// </summary>
/// <param name="IsEmpty">True when fleet DR may run.</param>
/// <param name="Blockers">Plain-words reasons it may not (a node, a non-seed model, an issued agent certificate, a used join token).</param>
public sealed record MasterEmptiness(bool IsEmpty, IReadOnlyList<string> Blockers);
