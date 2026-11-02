using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Enrollment;

/// <summary>
/// <c>mirbadllm-agent join</c> (design §3.2): what the operator copied from "Add node". <see cref="ToString"/> never
/// prints the token.
/// </summary>
public sealed record JoinParameters
{
    /// <summary>Enroll listener base address, e.g. <c>https://mirbad.example.com:8443</c> (https only).</summary>
    public required Uri EnrollUrl { get; init; }

    /// <summary>Single-use join token. Secret.</summary>
    public required string Token { get; init; }

    /// <summary>The CA's SPKI hash, <c>sha256:{base64}</c> (or bare base64 / hex).</summary>
    public required string CaHash { get; init; }

    /// <summary>The node id the token is bound to.</summary>
    public required string NodeId { get; init; }

    /// <summary>The agent's state directory (<c>agent.key</c>, <c>agent.crt</c>, <c>ca.crt</c>, <c>node-uid</c>).</summary>
    public required string StateDirectory { get; init; }

    /// <summary>Addresses the master can reach this agent at (IP, DNS name or <c>https://host:port</c>).</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>The capability report sent with the request (optional).</summary>
    public NodeCapabilities? Capabilities { get; init; }

    /// <summary>Replace an existing identity in the state directory.</summary>
    public bool Force { get; init; }

    /// <summary>Redacts the token.</summary>
    public override string ToString() => $"JoinParameters {{ NodeId = {NodeId}, EnrollUrl = {EnrollUrl}, CaHash = {CaHash}, Token = {Protocol.RedactedValue} }}";
}

/// <summary>Bootstrap mode (design §3.2, DGX): enroll from the bundle the master wrote into the shared directory.</summary>
public sealed record BootstrapParameters
{
    /// <summary>The agent's state directory.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>The shared bootstrap directory (default <see cref="BootstrapBundle.DefaultDirectory"/>).</summary>
    public string BundleDirectory { get; init; } = BootstrapBundle.DefaultDirectory;

    /// <summary>When set, a bundle for another node id is refused (the agent's configured node id).</summary>
    public string? ExpectedNodeId { get; init; }

    /// <summary>Addresses the master can reach this agent at.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>The capability report sent with the request (optional).</summary>
    public NodeCapabilities? Capabilities { get; init; }
}

/// <summary>A successful enrollment: what was stored in the state directory (public data only).</summary>
/// <param name="NodeId">Node id (the certificate's SPIFFE id).</param>
/// <param name="NodeUid">Node uid from the master.</param>
/// <param name="CertificateSpkiSha256">SPKI hash of the new agent key: the pin the operator compares in the approval dialog.</param>
/// <param name="NotAfter">Certificate expiry.</param>
/// <param name="PendingApproval">True when the operator still has to approve the node.</param>
public sealed record EnrollmentOutcome(string NodeId, string NodeUid, string CertificateSpkiSha256, DateTimeOffset NotAfter, bool PendingApproval);
