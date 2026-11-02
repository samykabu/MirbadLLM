using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>Agent-side stage of a certificate rotation (design §4.3 <c>GET /pki/rotations/{id}</c>, §10.2).</summary>
public enum PkiRotationStage
{
    /// <summary>A pending key exists (persisted) and its CSR was returned; repeating the start returns the same CSR.</summary>
    Pending,

    /// <summary>The new certificate is stored as pending and presented on new connections; the old one still works.</summary>
    CertInstalled,

    /// <summary>Committed over a fresh connection that negotiated the new certificate; the old key is deleted. Irreversible.</summary>
    Committed,

    /// <summary>Aborted before commit; the pending key and certificate were dropped.</summary>
    Aborted,
}

/// <summary>
/// <c>POST /agent/v1/pki/rotations</c> (design §10.2 step 1): the agent creates a new key, stores it as pending (fsync'd)
/// and returns the CSR as the result (<see cref="PkiRotationCsr"/>). Repeating the same rotation returns the same CSR.
/// </summary>
public sealed record PkiRotationStartRequest : AgentMutation
{
    /// <summary>Rotation id chosen by the master (persisted in its rotation record first).</summary>
    public required string RotationId { get; init; }
}

/// <summary>Result of <see cref="PkiRotationStartRequest"/>.</summary>
/// <param name="RotationId">Rotation id.</param>
/// <param name="CsrPem">PKCS#10 CSR of the pending key (PEM). The master uses only its public key (§3.2).</param>
public sealed record PkiRotationCsr(string RotationId, string CsrPem);

/// <summary>
/// <c>PUT /agent/v1/pki/rotations/{rotationId}/cert</c> (design §10.2 step 2): store the new certificate as pending and
/// present it on new connections. Result: <see cref="PkiRotationStatus"/>.
/// </summary>
public sealed record PkiRotationCertRequest : AgentMutation
{
    /// <summary>Rotation id (equals the path parameter).</summary>
    public required string RotationId { get; init; }

    /// <summary>The issued certificate (PEM), followed by any intermediate.</summary>
    public required string CertPem { get; init; }
}

/// <summary>
/// <c>POST /agent/v1/pki/rotations/{rotationId}/commit</c> (design §10.2 steps 3–4): sent over a fresh connection that
/// negotiated the new certificate. Irreversible and idempotent. Result: <see cref="PkiRotationStatus"/>.
/// </summary>
public sealed record PkiRotationCommitRequest : AgentMutation
{
    /// <summary>Rotation id (equals the path parameter).</summary>
    public required string RotationId { get; init; }
}

/// <summary>
/// <c>POST /agent/v1/pki/rotations/{rotationId}/abort</c> (design §10.2 step 5): only before commit; afterwards 409
/// <c>already_committed</c>. Result: <see cref="PkiRotationStatus"/>.
/// </summary>
public sealed record PkiRotationAbortRequest : AgentMutation
{
    /// <summary>Rotation id (equals the path parameter).</summary>
    public required string RotationId { get; init; }
}

/// <summary><c>GET /agent/v1/pki/rotations/{rotationId}</c> (design §4.3): the rotation as the agent knows it.</summary>
public sealed record PkiRotationStatus
{
    /// <summary>Rotation id.</summary>
    public required string RotationId { get; init; }

    /// <summary>Agent-side stage.</summary>
    public required PkiRotationStage Stage { get; init; }

    /// <summary>SPKI SHA-256 (base64) of the certificate in use for existing connections.</summary>
    public string? CurrentSpkiSha256 { get; init; }

    /// <summary>SPKI SHA-256 of the pending key. Null after commit or abort.</summary>
    public string? PendingSpkiSha256 { get; init; }

    /// <summary>Expiry of the pending (or, after commit, current) certificate.</summary>
    public DateTimeOffset? CertNotAfter { get; init; }

    /// <summary>When the stage last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One trust anchor (CA) the agent accepts masters from (design §10.2).</summary>
/// <param name="SpkiSha256">Base64 SHA-256 of the CA's SubjectPublicKeyInfo (the hash in the join command).</param>
/// <param name="Subject">CA subject.</param>
/// <param name="NotAfter">CA expiry.</param>
public sealed record TrustAnchorInfo(string SpkiSha256, string Subject, DateTimeOffset NotAfter);

/// <summary><c>GET /agent/v1/pki/trust</c> (design §4.3): current trust anchors and the accepted master SAN.</summary>
public sealed record PkiTrustResponse
{
    /// <summary>CA anchors (two during a CA rotation).</summary>
    public IReadOnlyList<TrustAnchorInfo> Anchors { get; init; } = [];

    /// <summary>The master SAN URI the agent requires (<see cref="Protocol.MasterSpiffeId"/>).</summary>
    public string MasterSan { get; init; } = Protocol.IssuedMasterSpiffeId;
}

/// <summary>
/// <c>PUT /agent/v1/pki/trust</c> (design §10.2 CA rotation): replace the trust anchors. Must keep at least one anchor that
/// validates the calling master. P1 implements the endpoint; the procedure is P2 (MN-116). Result:
/// <see cref="PkiTrustResponse"/>.
/// </summary>
public sealed record PkiTrustSetRequest : AgentMutation
{
    /// <summary>CA certificates (PEM).</summary>
    public required IReadOnlyList<string> Anchors { get; init; }
}

/// <summary>
/// <c>POST https://{enroll listener}/enroll</c> (design §3.2): the only agent→master call. Sent only after the agent
/// verified the listener's chain ends in the CA whose SPKI hash it was given and the server SAN is the master's.
/// <see cref="ToString"/> never prints the token.
/// </summary>
public sealed record EnrollRequest
{
    /// <summary>Single-use join token. Secret.</summary>
    public required string Token { get; init; }

    /// <summary>The node id the token is bound to.</summary>
    public required string NodeId { get; init; }

    /// <summary>PKCS#10 CSR (PEM) of a P-256 key generated on the node; its signature is the proof of possession.</summary>
    public required string CsrPem { get; init; }

    /// <summary>Addresses the agent can be reached at (the operator confirms which go into the SAN).</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>The agent's capability report.</summary>
    public NodeCapabilities? Capabilities { get; init; }

    /// <summary>Redacts the token.</summary>
    public override string ToString() => $"EnrollRequest {{ NodeId = {NodeId}, Token = {Protocol.RedactedValue} }}";
}

/// <summary>
/// <c>POST https://{enroll listener}/enroll/rekey</c> (design §9.4 step 5, §10.2 item 6; MN-114, D73): re-pairs an
/// <b>enrolled</b> node that lost or let expire its certificate, keeping its node uid, its state and its running models;
/// only the key and certificate are replaced. Sent by <c>mirbadllm-agent join --rekey</c> under the same TLS check as
/// <see cref="EnrollRequest"/> (the CA hash comes with the command), with a join token of purpose
/// <see cref="JoinTokenPurpose.Rekey"/> bound to the node. The master refuses (403 <c>enrollment_refused</c>, one code on
/// purpose) unless the token is valid for <see cref="NodeId"/>, the node exists, is not revoked, and its uid equals
/// <see cref="NodeUid"/>. On success the pins are replaced (current = the new key, pending cleared), any open rotation of
/// the node is aborted on the master, and the approval state is kept. Answer: <see cref="EnrollResponse"/> with the same
/// node uid. A P1 agent never calls it. <see cref="ToString"/> never prints the token.
/// </summary>
public sealed record RekeyRequest
{
    /// <summary>Single-use join token of purpose rekey. Secret.</summary>
    public required string Token { get; init; }

    /// <summary>The enrolled node's id.</summary>
    public required string NodeId { get; init; }

    /// <summary>The node uid the agent holds in its state directory (must equal the registry's).</summary>
    public required string NodeUid { get; init; }

    /// <summary>PKCS#10 CSR (PEM) of a new P-256 key generated on the node (proof of possession).</summary>
    public required string CsrPem { get; init; }

    /// <summary>Addresses the agent can be reached at.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>The agent's capability report.</summary>
    public NodeCapabilities? Capabilities { get; init; }

    /// <summary>Redacts the token.</summary>
    public override string ToString() => $"RekeyRequest {{ NodeId = {NodeId}, NodeUid = {NodeUid}, Token = {Protocol.RedactedValue} }}";
}

/// <summary>Answer of <c>/enroll</c> (design §3.2). Certificate content (SAN, EKU, lifetime) is chosen by the master.</summary>
public sealed record EnrollResponse
{
    /// <summary>The master CA certificate (PEM): the agent's only trust anchor.</summary>
    public required string CaCertPem { get; init; }

    /// <summary>The issued agent certificate (PEM).</summary>
    public required string AgentCertPem { get; init; }

    /// <summary>Node uid generated by the master.</summary>
    public required string NodeUid { get; init; }

    /// <summary>The master SAN the agent must require (<see cref="Protocol.MasterSpiffeId"/>).</summary>
    public string MasterSpiffeId { get; init; } = Protocol.IssuedMasterSpiffeId;

    /// <summary>True when the node starts PendingApproval (false for the pre-approved bootstrap node).</summary>
    public bool PendingApproval { get; init; }
}

/// <summary>
/// The DGX bootstrap bundle (design §3.2): written by the master to a directory shared only between the two containers
/// (<c>/run/mirbadllm-bootstrap</c>, mode 0700, uid 1654), read and deleted by the agent after a successful enrollment.
/// Holds the CA certificate and a single-use token, never the CA key. <see cref="ToString"/> never prints the token.
/// </summary>
public sealed record BootstrapBundle
{
    /// <summary>File name inside the bootstrap directory.</summary>
    public const string FileName = "bootstrap.json";

    /// <summary>Default bootstrap directory.</summary>
    public const string DefaultDirectory = "/run/mirbadllm-bootstrap";

    /// <summary>Node id to enroll as (the bootstrap node id, <c>dgx</c>).</summary>
    public required string NodeId { get; init; }

    /// <summary>Single-use join token (purpose bootstrap). Secret.</summary>
    public required string Token { get; init; }

    /// <summary>The master CA certificate (PEM).</summary>
    public required string CaCertPem { get; init; }

    /// <summary>Base64 SHA-256 of the CA SPKI; the agent checks the enroll listener's chain against it before sending the token.</summary>
    public required string CaSpkiSha256 { get; init; }

    /// <summary>Enroll listener base address, e.g. <c>https://mirbadllm:8443</c>.</summary>
    public required Uri EnrollEndpoint { get; init; }

    /// <summary>When the bundle was written.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Redacts the token.</summary>
    public override string ToString() => $"BootstrapBundle {{ NodeId = {NodeId}, EnrollEndpoint = {EnrollEndpoint}, Token = {Protocol.RedactedValue} }}";
}
