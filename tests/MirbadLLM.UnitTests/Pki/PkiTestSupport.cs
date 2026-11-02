using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Pki;

namespace MirbadLLM.UnitTests.Pki;

/// <summary>A settable clock (hand-written fake; no TimeProvider testing package in the repo).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Builders for test certificates, including ones this PKI would never issue (to prove the validators refuse them).</summary>
internal static class PkiTestSupport
{
    public static readonly DateTimeOffset Epoch = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static string NewCsr(out ECDsa key, string nodeId = "dgx")
    {
        key = PkiDefaults.CreateKey();
        return CertificateSigningRequests.CreatePem(key, nodeId);
    }

    /// <summary>
    /// A leaf signed by <paramref name="issuer"/>'s key with arbitrary content. <paramref name="issuerKeyPem"/> is the
    /// issuer's private key; <paramref name="issuer"/> its certificate.
    /// </summary>
    public static X509Certificate2 SignCustom(
        X509Certificate2 issuer,
        string issuerKeyPem,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        IEnumerable<string> uris,
        IEnumerable<string> ekus,
        bool isCa = false,
        bool includeEku = true,
        ECDsa? subjectKey = null,
        IEnumerable<IPAddress>? ips = null,
        IEnumerable<X509Extension>? extraExtensions = null)
    {
        using ECDsa issuerKey = ECDsa.Create();
        issuerKey.ImportFromPem(issuerKeyPem);
        ECDsa key = subjectKey ?? PkiDefaults.CreateKey();
        try
        {
            CertificateRequest request = new($"CN=custom-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                isCa ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
            if (includeEku)
            {
                OidCollection oids = [];
                foreach (string eku in ekus)
                {
                    oids.Add(new Oid(eku));
                }

                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
            }

            SubjectAlternativeNameBuilder san = new();
            bool any = false;
            foreach (string uri in uris)
            {
                san.AddUri(new Uri(uri));
                any = true;
            }

            foreach (IPAddress ip in ips ?? [])
            {
                san.AddIpAddress(ip);
                any = true;
            }

            if (any)
            {
                request.CertificateExtensions.Add(san.Build());
            }

            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            foreach (X509Extension extension in extraExtensions ?? [])
            {
                request.CertificateExtensions.Add(extension);
            }
            byte[] serial = RandomNumberGenerator.GetBytes(16);
            serial[0] = (byte)((serial[0] & 0x7F) | 0x40);
            using X509Certificate2 cert = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), notBefore, notAfter, serial);
            return X509CertificateLoader.LoadCertificate(cert.RawData);
        }
        finally
        {
            if (subjectKey is null)
            {
                key.Dispose();
            }
        }
    }

    public static TrustAnchorSet Trust(params CertificateAuthority[] cas) => TrustAnchorSet.Create(cas.Select(c => c.Certificate));
}
