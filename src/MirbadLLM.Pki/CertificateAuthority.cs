using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Pki;

/// <summary>
/// The master CA (design §10.2): ECDSA P-256, self-signed, 10 years. Issues agent certificates from CSRs with
/// server-controlled content (§3.2) and the master's own client and enroll-listener certificates.
/// <para>Every issued leaf gets exactly the extensions chosen here: basicConstraints CA=false (critical), keyUsage
/// digitalSignature (critical), the EKU(s) of its role, a SAN with exactly one SPIFFE URI plus validated DNS/IP entries,
/// subject and authority key identifiers. Nothing from a CSR other than its verified public key is used.</para>
/// <para><see cref="ToString"/> never prints key material; the key leaves the object only through
/// <see cref="ExportPrivateKeyPem"/>.</para>
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    private readonly ECDsa _key;
    private readonly TimeProvider _time;

    private CertificateAuthority(X509Certificate2 certificate, ECDsa key, TimeProvider time)
    {
        Certificate = certificate;
        _key = key;
        _time = time;
        CertificatePem = certificate.ExportCertificatePem();
        SpkiSha256 = Spki.Sha256(certificate);
    }

    /// <summary>The CA certificate (no private key). Use it as the custom trust root.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The CA certificate as PEM (sent to agents in <c>EnrollResponse.CaCertPem</c> and the bootstrap bundle).</summary>
    public string CertificatePem { get; }

    /// <summary>Base64 SHA-256 of the CA SPKI: the hash in the join command (design §3.2).</summary>
    public string SpkiSha256 { get; }

    /// <summary>CA expiry (UTC).</summary>
    public DateTimeOffset NotAfter => new(Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>Creates a new CA with a fresh P-256 key. The subject carries a short SPKI-derived suffix so two CAs (during a CA rotation) never share a name.</summary>
    public static CertificateAuthority Create(TimeProvider? time = null, TimeSpan? lifetime = null)
    {
        time ??= TimeProvider.System;
        TimeSpan validity = lifetime ?? PkiDefaults.CaLifetime;
        if (validity <= TimeSpan.Zero || validity > PkiDefaults.CaLifetime)
        {
            throw new PkiException(PkiErrorKind.InvalidLifetime, "The CA lifetime must be positive and at most 10 years.");
        }

        ECDsa key = PkiDefaults.CreateKey();
        try
        {
            string suffix = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..8];
            X500DistinguishedNameBuilder subject = new();
            subject.AddOrganizationName(PkiDefaults.Organization);
            subject.AddCommonName($"mirbadllm CA {suffix}");

            CertificateRequest request = new(subject.Build(), key, PkiDefaults.SignatureHash);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            DateTimeOffset now = Truncate(time.GetUtcNow());
            using X509Certificate2 withKey = request.CreateSelfSigned(now - PkiDefaults.NotBeforeBackdate, now + validity);
            X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
            return new CertificateAuthority(publicOnly, key, time);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads a CA from PEM (certificate + unencrypted PKCS#8 or SEC1 key) and checks it: the key is P-256 and matches the
    /// certificate, and the certificate is a self-issued CA allowed to sign certificates. Throws <see cref="PkiException"/>.
    /// </summary>
    public static CertificateAuthority Load(string certificatePem, string privateKeyPem, TimeProvider? time = null)
    {
        X509Certificate2 certificate = PemReader.LoadCertificate(certificatePem);
        ECDsa? key = null;
        try
        {
            EnsureIsCa(certificate);
            key = PemReader.LoadP256PrivateKey(privateKeyPem);
            KeyPairs.EnsureMatches(certificate, key);
            return new CertificateAuthority(certificate, key, time ?? TimeProvider.System);
        }
        catch
        {
            certificate.Dispose();
            key?.Dispose();
            throw;
        }
    }

    /// <summary>The CA private key as PKCS#8 PEM. Secret: only for <c>{DataPath}/pki/ca.key</c> (0600) and an encrypted backup.</summary>
    public string ExportPrivateKeyPem() => _key.ExportPkcs8PrivateKeyPem();

    /// <summary>
    /// Signs <paramref name="data"/> with the CA key (ECDSA P-256, SHA-256, DER), the form <c>openssl dgst -sha256 -verify</c>
    /// checks: the master's pushed agent-update orders (docs/multinode/agent-updates.md U5), verified by the node's root
    /// update helper against its copy of this CA's certificate.
    /// </summary>
    public byte[] SignData(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>This CA as a trust anchor entry (<c>GET /pki/trust</c>).</summary>
    public TrustAnchorInfo ToTrustAnchorInfo() => TrustAnchorSet.Describe(Certificate);

    /// <summary>
    /// Issues an agent certificate from a CSR (design §3.1, §3.2, §10.2 step 2). The CSR signature is verified (proof of
    /// possession) and only its P-256 public key is used. The master sets everything else: subject <c>CN={nodeId}</c>,
    /// SAN <c>URI:spiffe://mirbadllm/node/{nodeId}</c> plus <paramref name="addresses"/> (operator-confirmed IPs / DNS
    /// names), EKU serverAuth + clientAuth, and a lifetime of at most 90 days (default 90).
    /// </summary>
    public IssuedCertificate IssueAgentCertificate(string csrPem, string nodeId, IEnumerable<string>? addresses, TimeSpan? lifetime = null)
    {
        if (!Protocol.IsValidNodeId(nodeId))
        {
            throw new PkiException(PkiErrorKind.InvalidNodeId, "The node id breaks the node id rule.");
        }

        TimeSpan validity = CheckLifetime(lifetime, PkiDefaults.AgentCertificateLifetime);
        (IReadOnlyList<IPAddress> ips, IReadOnlyList<string> dns) = CertificateSans.ParseAddresses(addresses);
        PublicKey publicKey = CertificateSigningRequests.VerifyAndExtractPublicKey(csrPem);

        X500DistinguishedNameBuilder subject = new();
        subject.AddOrganizationName(PkiDefaults.Organization);
        subject.AddOrganizationalUnitName("node");
        subject.AddCommonName(nodeId);

        X509Certificate2 certificate = Sign(subject.Build(), publicKey, Protocol.IssuedNodeSpiffeId(nodeId), ips, dns,
            [PkiDefaults.ServerAuthOid, PkiDefaults.ClientAuthOid], validity);
        return new IssuedCertificate(certificate);
    }

    /// <summary>
    /// Issues the master's client certificate (design §10.2): a new P-256 key generated here, SAN
    /// <c>URI:spiffe://mirbadllm/master</c> only, EKU clientAuth, lifetime at most 30 days (default 30).
    /// </summary>
    public IssuedCredential IssueMasterClientCertificate(TimeSpan? lifetime = null)
    {
        TimeSpan validity = CheckLifetime(lifetime, PkiDefaults.MasterCertificateLifetime);
        X500DistinguishedNameBuilder subject = new();
        subject.AddOrganizationName(PkiDefaults.Organization);
        subject.AddOrganizationalUnitName("master");
        subject.AddCommonName("mirbadllm master");
        return IssueWithNewKey(subject.Build(), [], [], [PkiDefaults.ClientAuthOid], validity);
    }

    /// <summary>
    /// Issues the enroll listener's server certificate (design §3.2, §10.2): a new P-256 key generated here, SAN
    /// <c>URI:spiffe://mirbadllm/master</c> plus the configured DNS names / IPs, EKU serverAuth, lifetime at most 30 days
    /// (default 30). The certificate also carries the CA certificate in the non-critical
    /// <see cref="PkiDefaults.EmbeddedCaCertificateOid"/> extension, so an agent joining with only the CA hash finds the
    /// CA on every platform (OpenSSL does not send a self-signed root in the TLS chain).
    /// </summary>
    public IssuedCredential IssueEnrollServerCertificate(IEnumerable<string>? addresses, TimeSpan? lifetime = null)
    {
        TimeSpan validity = CheckLifetime(lifetime, PkiDefaults.MasterCertificateLifetime);
        (IReadOnlyList<IPAddress> ips, IReadOnlyList<string> dns) = CertificateSans.ParseAddresses(addresses);
        X500DistinguishedNameBuilder subject = new();
        subject.AddOrganizationName(PkiDefaults.Organization);
        subject.AddOrganizationalUnitName("master");
        subject.AddCommonName(dns.Count > 0 ? dns[0] : "mirbadllm enroll");
        X509Extension embeddedCa = new(PkiDefaults.EmbeddedCaCertificateOid, Certificate.RawData, critical: false);
        return IssueWithNewKey(subject.Build(), ips, dns, [PkiDefaults.ServerAuthOid], validity, embeddedCa);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Certificate.Dispose();
        _key.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => $"CertificateAuthority {{ Subject = {Certificate.Subject}, Spki = {SpkiSha256}, NotAfter = {NotAfter:O} }}";

    internal static void EnsureIsCa(X509Certificate2 certificate)
    {
        bool isCa = false;
        bool canSign = false;
        foreach (X509Extension extension in certificate.Extensions)
        {
            switch (extension)
            {
                case X509BasicConstraintsExtension basic:
                    isCa = basic.CertificateAuthority;
                    break;
                case X509KeyUsageExtension usage:
                    canSign = usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign);
                    break;
            }
        }

        if (!isCa || !canSign || !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
        {
            throw new PkiException(PkiErrorKind.NotACertificateAuthority, $"'{certificate.Subject}' is not a self-signed CA with keyCertSign.");
        }

        if (!KeyPairs.IsP256(certificate.PublicKey))
        {
            throw new PkiException(PkiErrorKind.UnsupportedKey, $"CA '{certificate.Subject}' does not use an ECDSA P-256 key.");
        }
    }

    private IssuedCredential IssueWithNewKey(
        X500DistinguishedName subject,
        IReadOnlyList<IPAddress> ips,
        IReadOnlyList<string> dns,
        string[] ekus,
        TimeSpan validity,
        X509Extension? extra = null)
    {
        ECDsa key = PkiDefaults.CreateKey();
        try
        {
            PublicKey publicKey = PublicKey.CreateFromSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
            X509Certificate2 certificate = Sign(subject, publicKey, Protocol.IssuedMasterSpiffeId, ips, dns, ekus, validity, extra);
            return new IssuedCredential(certificate, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private X509Certificate2 Sign(
        X500DistinguishedName subject,
        PublicKey publicKey,
        string spiffeId,
        IReadOnlyList<IPAddress> ips,
        IReadOnlyList<string> dns,
        string[] ekus,
        TimeSpan validity,
        X509Extension? extra = null)
    {
        DateTimeOffset now = Truncate(_time.GetUtcNow());
        DateTimeOffset caNotBefore = new(Certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        DateTimeOffset caNotAfter = NotAfter;
        if (now < caNotBefore || now >= caNotAfter)
        {
            throw new PkiException(PkiErrorKind.CaNotValid, $"CA '{Certificate.Subject}' is not valid at {now:O}.");
        }

        DateTimeOffset notBefore = Max(now - PkiDefaults.NotBeforeBackdate, caNotBefore);
        DateTimeOffset notAfter = Min(now + validity, caNotAfter);

        CertificateRequest request = new(subject, publicKey, PkiDefaults.SignatureHash);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

        OidCollection usages = [];
        foreach (string eku in ekus)
        {
            usages.Add(new Oid(eku));
        }

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));

        SubjectAlternativeNameBuilder san = new();
        san.AddUri(new Uri(spiffeId, UriKind.Absolute));
        foreach (string name in dns)
        {
            san.AddDnsName(name);
        }

        foreach (IPAddress ip in ips)
        {
            san.AddIpAddress(ip);
        }

        request.CertificateExtensions.Add(san.Build(false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(publicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Certificate, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        if (extra is not null)
        {
            request.CertificateExtensions.Add(extra);
        }

        X509SignatureGenerator generator = X509SignatureGenerator.CreateForECDsa(_key);
        return request.Create(Certificate.SubjectName, generator, notBefore, notAfter, NewSerialNumber());
    }

    private static TimeSpan CheckLifetime(TimeSpan? requested, TimeSpan maximum)
    {
        TimeSpan lifetime = requested ?? maximum;
        if (lifetime <= TimeSpan.Zero || lifetime > maximum)
        {
            throw new PkiException(PkiErrorKind.InvalidLifetime, $"The certificate lifetime must be positive and at most {maximum.TotalDays:0} days.");
        }

        return lifetime;
    }

    private static byte[] NewSerialNumber()
    {
        // 128 random bits, positive (high bit clear) and with a non-zero first byte so the DER length is fixed.
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x40);
        return serial;
    }

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
