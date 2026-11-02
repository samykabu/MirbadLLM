using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace MirbadLLM.Pki;

/// <summary>
/// An atomically swappable <see cref="PeerValidator"/> for long-lived TLS endpoints (added by MN-090 from the MN-010
/// spike: "pins are read on every handshake"). The callbacks below read <see cref="Current"/> per handshake, so:
/// <list type="bullet">
/// <item>the master adds a node's pending pin (§10.2 step 2) with <see cref="Set"/> and the node's existing control and
/// data handlers accept the agent's new certificate on their next new connection, instead of flapping until the
/// handlers are rebuilt at commit;</item>
/// <item>the agent swaps its master validator after <c>PUT /pki/trust</c> (CA rotation, §10.2) without restarting the
/// Kestrel listener.</item>
/// </list>
/// Connections that are already established are not re-validated; handler replacement on commit (§7.2, §10.2 step 4)
/// still drains them. Thread-safe.
/// </summary>
public sealed class PeerValidatorSource
{
    private PeerValidator _current;

    /// <summary>Creates the source with its first validator.</summary>
    public PeerValidatorSource(PeerValidator initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>The validator the next handshake uses.</summary>
    public PeerValidator Current => Volatile.Read(ref _current);

    /// <summary>Replaces the validator; handshakes that start afterwards use <paramref name="next"/>.</summary>
    public void Set(PeerValidator next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }

    /// <summary>Kestrel <c>ClientCertificateValidation</c> shape, against <see cref="Current"/>.</summary>
    public bool ValidateClientCertificate(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors) =>
        Current.ValidateClientCertificate(certificate, chain, sslPolicyErrors);

    /// <summary><see cref="RemoteCertificateValidationCallback"/> shape, against <see cref="Current"/>.</summary>
    public bool ValidateRemoteCertificate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors) =>
        Current.ValidateRemoteCertificate(sender, certificate, chain, sslPolicyErrors);
}
