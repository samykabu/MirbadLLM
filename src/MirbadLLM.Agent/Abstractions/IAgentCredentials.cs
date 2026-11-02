using System.Security.Cryptography.X509Certificates;

namespace MirbadLLM.Agent.Abstractions;

/// <summary>
/// The agent's TLS identity and trust (design §3.1, §4.2, §10.2), read by the mTLS listener (MN-014: Kestrel
/// <c>ServerCertificateSelector</c> and client-certificate validation) and changed by enrollment (MN-035) and rotation
/// (MN-036, <c>/pki/*</c>). Kept in the state directory: <c>agent.key</c> (0600), <c>agent.crt</c>, <c>ca.crt</c>, the
/// pending rotation material.
/// <para>Invariants:</para>
/// <list type="bullet">
/// <item><see cref="ServerCertificate"/> is what <b>new</b> connections are offered: the pending certificate once a
/// rotation installed it (design §10.2 step 3), otherwise the current one. Existing connections keep theirs.</item>
/// <item>Masters are accepted only when their client certificate chains to one of <see cref="TrustAnchors"/> (custom root
/// trust, never system roots), has EKU clientAuth and the SAN URI <c>spiffe://mirbadllm/master</c>.</item>
/// <item>Every change is persisted (atomic, flushed) before it becomes visible here; the old key is deleted only after a
/// commit.</item>
/// </list>
/// </summary>
public interface IAgentCredentials
{
    /// <summary>True once the agent holds a key, a certificate and at least one trust anchor (otherwise only join/bootstrap modes can run).</summary>
    bool IsEnrolled { get; }

    /// <summary>Node id the certificate was issued for.</summary>
    string NodeId { get; }

    /// <summary>Node uid received at enrollment.</summary>
    string NodeUid { get; }

    /// <summary>The certificate (with private key) offered on new TLS connections.</summary>
    X509Certificate2 ServerCertificate { get; }

    /// <summary>CA certificates masters must chain to (two during a CA rotation).</summary>
    IReadOnlyList<X509Certificate2> TrustAnchors { get; }

    /// <summary>Incremented whenever the server certificate or the anchors change (listeners re-read on change).</summary>
    long Version { get; }
}
