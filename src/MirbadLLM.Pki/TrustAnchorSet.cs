using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Pki;

/// <summary>
/// The CA certificates a side trusts (design §4.2, §10.2): one normally, two (old + new) during a CA rotation. Every
/// anchor is checked to be a self-issued P-256 CA; duplicates (same SPKI) are dropped. Immutable: a trust change builds a
/// new set and new validators (handlers are rebuilt, design §7.2).
/// </summary>
public sealed class TrustAnchorSet : IDisposable
{
    /// <summary>Most anchors accepted: the current CA and the one it is being rotated to.</summary>
    public const int MaxAnchors = 2;

    private readonly List<X509Certificate2> _anchors;

    private TrustAnchorSet(List<X509Certificate2> anchors)
    {
        _anchors = anchors;
    }

    /// <summary>The anchors (public certificates).</summary>
    public IReadOnlyList<X509Certificate2> Anchors => _anchors;

    /// <summary>Builds a set from CA certificates (copies them; the caller keeps ownership of its instances).</summary>
    public static TrustAnchorSet Create(IEnumerable<X509Certificate2> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        List<X509Certificate2> anchors = [];
        try
        {
            foreach (X509Certificate2 certificate in certificates)
            {
                ArgumentNullException.ThrowIfNull(certificate);
                CertificateAuthority.EnsureIsCa(certificate);
                if (anchors.Any(a => Spki.PinEquals(Spki.Sha256(a), Spki.Sha256(certificate))))
                {
                    continue;
                }

                anchors.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
            }

            if (anchors.Count is 0 or > MaxAnchors)
            {
                throw new PkiException(PkiErrorKind.CaMaterialInvalid, $"A trust set holds 1 to {MaxAnchors} CA certificates.");
            }

            return new TrustAnchorSet(anchors);
        }
        catch
        {
            foreach (X509Certificate2 anchor in anchors)
            {
                anchor.Dispose();
            }

            throw;
        }
    }

    /// <summary>Builds a set from CA PEMs (each entry may hold one certificate), e.g. <c>PkiTrustSetRequest.Anchors</c>.</summary>
    public static TrustAnchorSet FromPem(IEnumerable<string> certificatePems)
    {
        ArgumentNullException.ThrowIfNull(certificatePems);
        List<X509Certificate2> loaded = [];
        try
        {
            foreach (string pem in certificatePems)
            {
                loaded.AddRange(PemReader.LoadCertificates(pem));
            }

            return Create(loaded);
        }
        finally
        {
            foreach (X509Certificate2 certificate in loaded)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>The SPKI pins of the anchors.</summary>
    public IReadOnlyList<string> SpkiSha256s => [.. _anchors.Select(Spki.Sha256)];

    /// <summary>True when an anchor has this SPKI pin (constant-time compare per anchor).</summary>
    public bool ContainsSpki(string pin) => _anchors.Aggregate(false, (found, a) => found | Spki.PinEquals(Spki.Sha256(a), pin));

    /// <summary>The anchors as PEM, one entry per certificate.</summary>
    public IReadOnlyList<string> ToPem() => [.. _anchors.Select(a => a.ExportCertificatePem())];

    /// <summary>The anchors for <c>GET /pki/trust</c>.</summary>
    public IReadOnlyList<TrustAnchorInfo> Describe() => [.. _anchors.Select(Describe)];

    /// <summary>A trust anchor entry for one CA certificate.</summary>
    public static TrustAnchorInfo Describe(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new TrustAnchorInfo(Spki.Sha256(certificate), certificate.Subject, new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (X509Certificate2 anchor in _anchors)
        {
            anchor.Dispose();
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"TrustAnchorSet {{ {string.Join(", ", SpkiSha256s)} }}";
}
