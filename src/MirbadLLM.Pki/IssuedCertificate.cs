using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MirbadLLM.Pki;

/// <summary>
/// A certificate the CA issued for a key it does not hold (agent certificates from a CSR). Public data only.
/// </summary>
public sealed class IssuedCertificate : IDisposable
{
    internal IssuedCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        CertificatePem = certificate.ExportCertificatePem();
        SpkiSha256 = Spki.Sha256(certificate);
    }

    /// <summary>The certificate (no private key).</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The certificate as PEM.</summary>
    public string CertificatePem { get; }

    /// <summary>Base64 SHA-256 of its SPKI: the pin the master stores (design §3.1).</summary>
    public string SpkiSha256 { get; }

    /// <summary>Serial number (hex).</summary>
    public string SerialNumber => Certificate.SerialNumber;

    /// <summary>Validity start (UTC).</summary>
    public DateTimeOffset NotBefore => new(Certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>Validity end (UTC).</summary>
    public DateTimeOffset NotAfter => new(Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);

    /// <inheritdoc />
    public void Dispose() => Certificate.Dispose();

    /// <inheritdoc />
    public override string ToString() => $"IssuedCertificate {{ Subject = {Certificate.Subject}, Spki = {SpkiSha256}, NotAfter = {NotAfter:O} }}";
}

/// <summary>
/// A certificate together with its private key (master client and enroll-listener certificates, which the master
/// generates itself; or an agent's own certificate loaded from its state directory). <see cref="ToString"/> never prints
/// the key; the key is only exported through <see cref="ExportPrivateKeyPem"/>, which callers must write to a 0600 file.
/// </summary>
public sealed class IssuedCredential : IDisposable
{
    private readonly ECDsa _key;

    internal IssuedCredential(X509Certificate2 publicCertificate, ECDsa key)
    {
        PublicCertificate = publicCertificate;
        _key = key;
        CertificatePem = publicCertificate.ExportCertificatePem();
        SpkiSha256 = Spki.Sha256(publicCertificate);
    }

    /// <summary>The certificate without its key (safe to share, log the subject of, or put in a chain).</summary>
    public X509Certificate2 PublicCertificate { get; }

    /// <summary>The certificate as PEM.</summary>
    public string CertificatePem { get; }

    /// <summary>Base64 SHA-256 of its SPKI.</summary>
    public string SpkiSha256 { get; }

    /// <summary>Validity start (UTC).</summary>
    public DateTimeOffset NotBefore => new(PublicCertificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>Validity end (UTC).</summary>
    public DateTimeOffset NotAfter => new(PublicCertificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>The private key as PKCS#8 PEM. Secret: store with owner-only permissions (<see cref="PkiFiles.WriteSecret"/>); never log.</summary>
    public string ExportPrivateKeyPem() => _key.ExportPkcs8PrivateKeyPem();

    /// <summary>
    /// A certificate with its private key usable by <c>SslStream</c>, Kestrel and <c>SocketsHttpHandler</c> on every
    /// platform (<see cref="TlsCredentials.Create"/>). The caller owns and disposes it.
    /// </summary>
    public X509Certificate2 CreateTlsCertificate()
    {
        using X509Certificate2 withKey = PublicCertificate.CopyWithPrivateKey(_key);
        return TlsCredentials.Create(withKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        PublicCertificate.Dispose();
        _key.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => $"IssuedCredential {{ Subject = {PublicCertificate.Subject}, Spki = {SpkiSha256}, NotAfter = {NotAfter:O} }}";
}
