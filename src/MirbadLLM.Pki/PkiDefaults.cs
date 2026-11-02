using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MirbadLLM.Pki;

/// <summary>
/// Fixed PKI parameters from design §3.1 and §10.2: algorithms, lifetimes and renewal points. Changing a value here
/// changes what the master issues and when it renews.
/// </summary>
public static class PkiDefaults
{
    /// <summary>CA lifetime: 10 years (design §10.2).</summary>
    public static readonly TimeSpan CaLifetime = TimeSpan.FromDays(3652);

    /// <summary>Agent certificate lifetime and maximum: 90 days (design §3.1).</summary>
    public static readonly TimeSpan AgentCertificateLifetime = TimeSpan.FromDays(90);

    /// <summary>Age at which an agent certificate is renewed (two-phase rotation): 60 days (design §10.2).</summary>
    public static readonly TimeSpan AgentRenewalAge = TimeSpan.FromDays(60);

    /// <summary>Master client and enroll-listener certificate lifetime and maximum: 30 days (design §10.2).</summary>
    public static readonly TimeSpan MasterCertificateLifetime = TimeSpan.FromDays(30);

    /// <summary>A node whose certificate expires within this window is Degraded (design §10.2).</summary>
    public static readonly TimeSpan RenewalWarningWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// <c>NotBefore</c> is set this far in the past so a peer whose clock is slightly behind accepts a fresh certificate.
    /// Equal to the protocol's maximum request skew (design §4.2).
    /// </summary>
    public static readonly TimeSpan NotBeforeBackdate = TimeSpan.FromMinutes(5);

    /// <summary>Largest CSR PEM accepted (a P-256 CSR is well under 1 KiB).</summary>
    public const int MaxCsrPemLength = 16 * 1024;

    /// <summary>Most SAN addresses accepted on one certificate.</summary>
    public const int MaxSanAddresses = 32;

    /// <summary>Signature hash for every certificate: SHA-256 with ECDSA P-256.</summary>
    public static readonly HashAlgorithmName SignatureHash = HashAlgorithmName.SHA256;

    /// <summary>EKU id-kp-serverAuth (1.3.6.1.5.5.7.3.1).</summary>
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>EKU id-kp-clientAuth (1.3.6.1.5.5.7.3.2).</summary>
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>Subject Alternative Name extension (2.5.29.17).</summary>
    public const string SubjectAltNameOid = "2.5.29.17";

    /// <summary>
    /// Private, non-critical extension on the enroll-listener certificate whose value is the DER of the self-signed CA
    /// certificate that issued it. A joining agent that knows only the CA hash finds the CA here: OpenSSL (Linux) never
    /// sends a self-signed root in the TLS chain, so the chain alone cannot carry it. The extension grants no trust by
    /// itself; the CA it carries must match the pinned hash and must have signed the leaf. The OID sits on an unregistered
    /// private-enterprise number far above any assigned one; every arc fits in 32 bits because Windows CryptoAPI cannot
    /// decode larger arcs (which rules out a 2.25 UUID OID).
    /// </summary>
    public const string EmbeddedCaCertificateOid = "1.3.6.1.4.1.3337916915.1.1";

    /// <summary>Organisation in every subject this PKI issues.</summary>
    public const string Organization = "mirbadllm";

    /// <summary>ECDSA public key algorithm (id-ecPublicKey).</summary>
    internal const string EcPublicKeyOid = "1.2.840.10045.2.1";

    /// <summary>NIST P-256 named curve (prime256v1).</summary>
    internal const string NistP256Oid = "1.2.840.10045.3.1.7";

    /// <summary>A new ECDSA P-256 key: the only key type this PKI uses (design §3.1, §10.2).</summary>
    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>When an agent certificate should be renewed: <c>NotBefore + 60 days</c> (clamped to its expiry).</summary>
    public static DateTimeOffset AgentRenewalDue(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        DateTimeOffset notBefore = new(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        DateTimeOffset notAfter = new(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        DateTimeOffset due = notBefore + AgentRenewalAge;
        return due < notAfter ? due : notAfter;
    }

    /// <summary>When a master certificate should be re-issued: at half its lifetime (design §10.2).</summary>
    public static DateTimeOffset MasterReissueDue(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        DateTimeOffset notBefore = new(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        DateTimeOffset notAfter = new(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        return notBefore + ((notAfter - notBefore) / 2);
    }

    /// <summary>True when <paramref name="certificate"/> expires within <see cref="RenewalWarningWindow"/> of <paramref name="now"/> (or already has).</summary>
    public static bool IsInRenewalWarningWindow(X509Certificate2 certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        DateTimeOffset notAfter = new(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        return notAfter - now <= RenewalWarningWindow;
    }
}
