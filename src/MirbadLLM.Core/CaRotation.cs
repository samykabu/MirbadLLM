namespace MirbadLLM.Core;

/// <summary>Stage of a CA rotation on the master (design §10.2 "CA rotation"; P2, frozen by MN-101, D73; MN-116).</summary>
public enum CaRotationStage
{
    /// <summary>New CA generated; the master trusts both roots for agent certificates (step 1).</summary>
    Started,

    /// <summary>Adding the new CA as a second anchor on every Online agent (<c>PUT /pki/trust</c>, step 2).</summary>
    AgentsTrusting,

    /// <summary>Master certificates (client, enroll listener) re-issued under the new CA (step 3).</summary>
    MasterReissued,

    /// <summary>Rotating every agent certificate under the new CA (two-phase, step 4).</summary>
    AgentsRotating,

    /// <summary>Every non-revoked node committed: removing the old root from the master, then from each agent (step 5).</summary>
    Closing,

    /// <summary>Done: only the new CA is trusted.</summary>
    Closed,

    /// <summary>Closed early by the operator: nodes that had not rotated must be re-paired (<c>join --rekey</c>).</summary>
    ClosedEarly,
}

/// <summary>One node's progress in a CA rotation.</summary>
public sealed record CaRotationNodeProgress
{
    /// <summary>Node id.</summary>
    public required string NodeId { get; init; }

    /// <summary>Node uid at the start.</summary>
    public required string NodeUid { get; init; }

    /// <summary>
    /// The node's <see cref="NodeRecord.EnrolledAt"/> when this entry was made; a re-pair (rekey) changes it and restarts
    /// the entry (W7 review MN-126). Null in entries written before it existed (they are kept). Omitted when null.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EnrolledAt { get; init; }

    /// <summary>The agent trusts the new CA (second anchor).</summary>
    public bool TrustAdded { get; init; }

    /// <summary>The agent committed a certificate under the new CA (its agent-certificate rotation id).</summary>
    public string? CertificateRotationId { get; init; }

    /// <summary>True once that rotation committed.</summary>
    public bool CertificateRotated { get; init; }

    /// <summary>The agent's anchors were narrowed to the new CA only.</summary>
    public bool OldTrustRemoved { get; init; }

    /// <summary>Last error, for the Nodes page.</summary>
    public string? Error { get; init; }
}

/// <summary>A CA rotation record (design §10.2), persisted in <see cref="MasterTrustDocument"/> before each step it names.</summary>
public sealed record CaRotationRecord
{
    /// <summary>Random rotation id.</summary>
    public required string RotationId { get; init; }

    /// <summary>Base64 SHA-256 SPKI of the CA being retired.</summary>
    public required string OldCaSpkiSha256 { get; init; }

    /// <summary>Base64 SHA-256 SPKI of the new CA.</summary>
    public required string NewCaSpkiSha256 { get; init; }

    /// <summary>Current stage.</summary>
    public required CaRotationStage Stage { get; init; }

    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the stage last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Who started it (<see cref="AuditActors"/>).</summary>
    public string? Actor { get; init; }

    /// <summary>Per-node progress (every non-revoked node at the start, plus nodes enrolled during the rotation).</summary>
    public IReadOnlyList<CaRotationNodeProgress> Nodes { get; init; } = [];
}

/// <summary>One CA the master trusts for agent certificates.</summary>
/// <param name="CaCertPem">The CA certificate (PEM, public).</param>
/// <param name="SpkiSha256">Base64 SHA-256 of its SPKI.</param>
/// <param name="AddedAt">When it was added.</param>
public sealed record MasterTrustAnchor(string CaCertPem, string SpkiSha256, DateTimeOffset AddedAt);

/// <summary>
/// <c>{DataPath}/pki/trust.json</c> (design §10.2 CA rotation step 1): the roots the master accepts agent certificates
/// under — one normally, two during a rotation — and the open (or last) rotation. Absent = the CA in <c>pki/</c> alone
/// (P1). Public material only; the CA keys stay in their own files. Written atomically with
/// <see cref="MasterStateJson.Options"/>.
/// </summary>
public sealed record MasterTrustDocument
{
    /// <summary>Schema version of this file format.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>File name under <c>{DataPath}/pki</c>.</summary>
    public const string FileName = "trust.json";

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Trusted roots, current CA first.</summary>
    public IReadOnlyList<MasterTrustAnchor> Anchors { get; init; } = [];

    /// <summary>The open or last CA rotation; null when none ever ran.</summary>
    public CaRotationRecord? Rotation { get; init; }
}

/// <summary>
/// The roots the master validates agent certificates under (design §10.2 dual trust; P2, frozen by MN-101, D73),
/// implemented by MN-116 over <see cref="MasterTrustDocument"/> and read by the agent client factory when it builds a
/// node's handlers. Without a registration the master trusts its CA alone (P1).
/// </summary>
public interface IMasterTrustAnchors
{
    /// <summary>Incremented whenever <see cref="AnchorPems"/> changes; handlers built under an older version are replaced.</summary>
    long Version { get; }

    /// <summary>CA certificates (PEM), current first; one or two.</summary>
    IReadOnlyList<string> AnchorPems { get; }
}
