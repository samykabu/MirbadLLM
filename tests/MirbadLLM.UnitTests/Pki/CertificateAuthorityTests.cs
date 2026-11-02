using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Pki;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Pki;

/// <summary>MN-004: CA creation/loading and issuance with server-controlled content (design §3.1, §3.2, §10.2).</summary>
public sealed class CertificateAuthorityTests
{
    private static readonly string[] Lan = ["192.0.2.10", "dgx.lan", "fd00::10"];

    // ---------------------------------------------------------------- CA create / load

    [Fact]
    public void Create_IsSelfSignedP256CaForTenYears()
    {
        ManualTimeProvider time = new(PkiTestSupport.Epoch);
        using CertificateAuthority ca = CertificateAuthority.Create(time);

        X509Certificate2 cert = ca.Certificate;
        Assert.False(cert.HasPrivateKey);
        Assert.Equal(cert.SubjectName.Name, cert.IssuerName.Name);
        Assert.Contains("CN=mirbadllm CA ", cert.Subject, StringComparison.Ordinal);
        X509BasicConstraintsExtension basic = Assert.Single(cert.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.True(basic.CertificateAuthority);
        Assert.True(basic.Critical);
        Assert.True(basic.HasPathLengthConstraint);
        Assert.Equal(0, basic.PathLengthConstraint);
        X509KeyUsageExtension usage = Assert.Single(cert.Extensions.OfType<X509KeyUsageExtension>());
        Assert.True(usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign));
        using ECDsa? key = cert.GetECDsaPublicKey();
        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
        Assert.Equal(PkiDefaults.CaLifetime, ca.NotAfter - PkiTestSupport.Epoch);
        Assert.Equal(Spki.Sha256(cert), ca.SpkiSha256);
    }

    [Fact]
    public void Create_TwoCasHaveDistinctSubjects()
    {
        using CertificateAuthority a = CertificateAuthority.Create();
        using CertificateAuthority b = CertificateAuthority.Create();
        Assert.NotEqual(a.Certificate.Subject, b.Certificate.Subject);
    }

    [Fact]
    public void Load_RoundTripsPemAndStillIssues()
    {
        using CertificateAuthority created = CertificateAuthority.Create();
        string keyPem = created.ExportPrivateKeyPem();
        using CertificateAuthority loaded = CertificateAuthority.Load(created.CertificatePem, keyPem);

        Assert.Equal(created.Certificate.RawData, loaded.Certificate.RawData);
        Assert.Equal(created.SpkiSha256, loaded.SpkiSha256);

        string csr = PkiTestSupport.NewCsr(out ECDsa agentKey);
        using (agentKey)
        using (IssuedCertificate issued = loaded.IssueAgentCertificate(csr, "dgx", Lan))
        using (TrustAnchorSet trust = TrustAnchorSet.Create([created.Certificate]))
        {
            PeerValidator validator = PeerValidator.ForAgent("dgx", trust, [issued.SpkiSha256]);
            Assert.True(validator.Validate(issued.Certificate).IsValid);
        }
    }

    [Fact]
    public void Load_AcceptsSec1EcPrivateKey()
    {
        using CertificateAuthority created = CertificateAuthority.Create();
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(created.ExportPrivateKeyPem());
        string sec1 = key.ExportECPrivateKeyPem();
        using CertificateAuthority loaded = CertificateAuthority.Load(created.CertificatePem, sec1);
        Assert.Equal(created.SpkiSha256, loaded.SpkiSha256);
    }

    [Fact]
    public void Load_MismatchedKeyRefusedWithoutEchoingTheKey()
    {
        using CertificateAuthority a = CertificateAuthority.Create();
        using CertificateAuthority b = CertificateAuthority.Create();
        string otherKey = b.ExportPrivateKeyPem();

        PkiException ex = Assert.Throws<PkiException>(() => CertificateAuthority.Load(a.CertificatePem, otherKey));
        Assert.Equal(PkiErrorKind.KeyMismatch, ex.Kind);
        Assert.DoesNotContain("PRIVATE KEY", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(otherKey.Split('\n')[1], ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_PublicKeyPemInsteadOfPrivateKeyRefused()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string publicPem = ca.Certificate.PublicKey.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfoPem();
        PkiException ex = Assert.Throws<PkiException>(() => CertificateAuthority.Load(ca.CertificatePem, publicPem));
        Assert.Equal(PkiErrorKind.KeyMismatch, ex.Kind);
    }

    [Fact]
    public void Load_LeafCertificateIsNotACa()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        using IssuedCredential master = ca.IssueMasterClientCertificate();
        PkiException ex = Assert.Throws<PkiException>(() => CertificateAuthority.Load(master.CertificatePem, master.ExportPrivateKeyPem()));
        Assert.Equal(PkiErrorKind.NotACertificateAuthority, ex.Kind);
    }

    [Fact]
    public void Load_RsaCaRefused()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=rsa ca", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        PkiException ex = Assert.Throws<PkiException>(() => CertificateAuthority.Load(cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem()));
        Assert.Equal(PkiErrorKind.UnsupportedKey, ex.Kind);
    }

    [Fact]
    public void Load_GarbageCertificateRefused()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        PkiException ex = Assert.Throws<PkiException>(() => CertificateAuthority.Load("not a pem", ca.ExportPrivateKeyPem()));
        Assert.Equal(PkiErrorKind.InvalidCertificate, ex.Kind);
    }

    [Fact]
    public void ToString_NeverContainsKeyMaterial()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        using IssuedCredential master = ca.IssueMasterClientCertificate();
        string keyBody = ca.ExportPrivateKeyPem().Split('\n')[1];
        string masterKeyBody = master.ExportPrivateKeyPem().Split('\n')[1];

        Assert.DoesNotContain(keyBody, ca.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", ca.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(masterKeyBody, master.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", master.ToString(), StringComparison.Ordinal);
        Assert.False(master.PublicCertificate.HasPrivateKey);
    }

    // ---------------------------------------------------------------- agent issuance from a CSR

    [Fact]
    public void IssueAgent_KeepsOnlyThePublicKeyAndSetsServerControlledContent()
    {
        ManualTimeProvider time = new(PkiTestSupport.Epoch);
        using CertificateAuthority ca = CertificateAuthority.Create(time);
        string csr = PkiTestSupport.NewCsr(out ECDsa agentKey, "pc-office");
        using (agentKey)
        using (IssuedCertificate issued = ca.IssueAgentCertificate(csr, "pc-office", Lan))
        {
            X509Certificate2 cert = issued.Certificate;

            // Same public key as the CSR; the pin is the agent key's SPKI hash.
            Assert.Equal(agentKey.ExportSubjectPublicKeyInfo(), cert.PublicKey.ExportSubjectPublicKeyInfo());
            Assert.Equal(Spki.Sha256(agentKey), issued.SpkiSha256);
            Assert.False(cert.HasPrivateKey);
            Assert.Equal(ca.Certificate.SubjectName.Name, cert.IssuerName.Name);
            Assert.Contains("CN=pc-office", cert.Subject, StringComparison.Ordinal);

            SanEntries san = Assert.IsType<SanEntries>(CertificateSans.Read(cert));
            Assert.Equal([Protocol.IssuedNodeSpiffeId("pc-office")], san.Uris);
            Assert.Equal(["dgx.lan"], san.DnsNames);
            Assert.Equal([IPAddress.Parse("192.0.2.10"), IPAddress.Parse("fd00::10")], san.IpAddresses);

            X509EnhancedKeyUsageExtension eku = Assert.Single(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>());
            Assert.Equal(
                [PkiDefaults.ServerAuthOid, PkiDefaults.ClientAuthOid],
                eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToArray());

            X509BasicConstraintsExtension basic = Assert.Single(cert.Extensions.OfType<X509BasicConstraintsExtension>());
            Assert.False(basic.CertificateAuthority);
            X509KeyUsageExtension usage = Assert.Single(cert.Extensions.OfType<X509KeyUsageExtension>());
            Assert.Equal(X509KeyUsageFlags.DigitalSignature, usage.KeyUsages);

            Assert.Equal(PkiTestSupport.Epoch - PkiDefaults.NotBeforeBackdate, issued.NotBefore);
            Assert.Equal(PkiTestSupport.Epoch + TimeSpan.FromDays(90), issued.NotAfter);
            Assert.Equal(PkiDefaults.AgentCertificateLifetime, TimeSpan.FromDays(90));
        }
    }

    [Fact]
    public void IssueAgent_IgnoresEveryExtensionAndTheSubjectRequestedInTheCsr()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        using ECDsa agentKey = PkiDefaults.CreateKey();

        // A hostile CSR: asks to be a CA, to be the master, for code signing, and adds a private extension.
        CertificateRequest hostile = new("CN=mirbadllm master, O=evil", agentKey, HashAlgorithmName.SHA256);
        hostile.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        hostile.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        hostile.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
        SubjectAlternativeNameBuilder san = new();
        san.AddUri(new Uri(Protocol.MasterSpiffeId));
        san.AddUri(new Uri("spiffe://mirbadllm/node/other"));
        san.AddDnsName("*.example.com");
        hostile.CertificateExtensions.Add(san.Build());
        hostile.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.99999.1", [0x05, 0x00], critical: true));
        string csr = hostile.CreateSigningRequestPem();

        using IssuedCertificate issued = ca.IssueAgentCertificate(csr, "dgx", ["10.0.0.5"]);
        X509Certificate2 cert = issued.Certificate;

        Assert.DoesNotContain("evil", cert.Subject, StringComparison.Ordinal);
        Assert.Contains("CN=dgx", cert.Subject, StringComparison.Ordinal);
        Assert.False(Assert.Single(cert.Extensions.OfType<X509BasicConstraintsExtension>()).CertificateAuthority);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, Assert.Single(cert.Extensions.OfType<X509KeyUsageExtension>()).KeyUsages);
        Assert.Equal(
            [PkiDefaults.ServerAuthOid, PkiDefaults.ClientAuthOid],
            Assert.Single(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()).EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToArray());
        SanEntries entries = Assert.IsType<SanEntries>(CertificateSans.Read(cert));
        Assert.Equal([Protocol.IssuedNodeSpiffeId("dgx")], entries.Uris);
        Assert.Empty(entries.DnsNames);
        Assert.Equal([IPAddress.Parse("10.0.0.5")], entries.IpAddresses);
        Assert.Null(cert.Extensions["1.3.6.1.4.1.99999.1"]);

        // Exactly the six extensions the master chooses, nothing else.
        string[] expected =
        [
            "2.5.29.19", // basicConstraints
            "2.5.29.15", // keyUsage
            "2.5.29.37", // extKeyUsage
            "2.5.29.17", // subjectAltName
            "2.5.29.14", // subjectKeyIdentifier
            "2.5.29.35", // authorityKeyIdentifier
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), cert.Extensions.Cast<X509Extension>().Select(e => e.Oid!.Value!).Order(StringComparer.Ordinal));
        Assert.Equal(agentKey.ExportSubjectPublicKeyInfo(), cert.PublicKey.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void IssueAgent_CsrWithBadSignatureRefused()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        using ECDsa key = PkiDefaults.CreateKey();
        byte[] der = new CertificateRequest("CN=dgx", key, HashAlgorithmName.SHA256).CreateSigningRequest();

        // Flip a bit in the last byte (inside the signature's s value); the structure still parses.
        der[^1] ^= 0x01;
        string tampered = PemEncoding.WriteString("CERTIFICATE REQUEST", der);

        PkiException ex = Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(tampered, "dgx", []));
        Assert.Equal(PkiErrorKind.CsrSignatureInvalid, ex.Kind);
    }

    [Fact]
    public void IssueAgent_CsrForAKeyTheSenderDoesNotHoldRefused()
    {
        // Proof of possession: the CSR names the victim's public key but is signed by the attacker's key.
        using CertificateAuthority ca = CertificateAuthority.Create();
        using ECDsa victim = PkiDefaults.CreateKey();
        using ECDsa attacker = PkiDefaults.CreateKey();
        PublicKey victimPublic = PublicKey.CreateFromSubjectPublicKeyInfo(victim.ExportSubjectPublicKeyInfo(), out _);
        CertificateRequest request = new(new X500DistinguishedName("CN=dgx"), victimPublic, HashAlgorithmName.SHA256);
        string csr = PemEncoding.WriteString("CERTIFICATE REQUEST", request.CreateSigningRequest(X509SignatureGenerator.CreateForECDsa(attacker)));

        PkiException ex = Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(csr, "dgx", []));
        Assert.Equal(PkiErrorKind.CsrSignatureInvalid, ex.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a csr")]
    [InlineData("-----BEGIN CERTIFICATE REQUEST-----\nAAAA\n-----END CERTIFICATE REQUEST-----")]
    public void IssueAgent_MalformedCsrRefused(string csr)
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        PkiException ex = Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(csr, "dgx", []));
        Assert.Equal(PkiErrorKind.InvalidCsr, ex.Kind);
    }

    [Fact]
    public void IssueAgent_OversizedCsrRefused()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string huge = new('A', PkiDefaults.MaxCsrPemLength + 1);
        Assert.Equal(PkiErrorKind.InvalidCsr, Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(huge, "dgx", [])).Kind);
    }

    [Fact]
    public void IssueAgent_RsaAndP384KeysRefused()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        using RSA rsa = RSA.Create(2048);
        string rsaCsr = new CertificateRequest("CN=dgx", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        Assert.Equal(PkiErrorKind.UnsupportedKey, Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(rsaCsr, "dgx", [])).Kind);

        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string p384Csr = new CertificateRequest("CN=dgx", p384, HashAlgorithmName.SHA384).CreateSigningRequestPem();
        Assert.Equal(PkiErrorKind.UnsupportedKey, Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(p384Csr, "dgx", [])).Kind);
    }

    [Theory]
    [InlineData("DGX")]
    [InlineData("-dgx")]
    [InlineData("dgx/../master")]
    [InlineData("")]
    public void IssueAgent_InvalidNodeIdRefused(string nodeId)
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        {
            Assert.Equal(PkiErrorKind.InvalidNodeId, Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(csr, nodeId, [])).Kind);
        }
    }

    [Theory]
    [InlineData("*.lan")]
    [InlineData("1")]
    [InlineData("10.1.2")]
    [InlineData("fe80::1%eth0")]
    [InlineData("host name")]
    [InlineData("-bad.lan")]
    [InlineData("spiffe://mirbadllm/master")]
    public void IssueAgent_InvalidAddressRefused(string address)
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        {
            Assert.Equal(PkiErrorKind.InvalidAddress, Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(csr, "dgx", [address])).Kind);
        }
    }

    [Fact]
    public void IssueAgent_DuplicateAddressesCollapsedAndDnsLowerCased()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        using (IssuedCertificate issued = ca.IssueAgentCertificate(csr, "dgx", ["DGX.lan", "dgx.lan", "192.0.2.10", " 192.0.2.10 "]))
        {
            SanEntries san = CertificateSans.Read(issued.Certificate)!;
            Assert.Equal(["dgx.lan"], san.DnsNames);
            Assert.Single(san.IpAddresses);
        }
    }

    [Fact]
    public void IssueAgent_LifetimeAboveNinetyDaysOrNotPositiveRefused()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        {
            Assert.Equal(PkiErrorKind.InvalidLifetime, Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(csr, "dgx", [], TimeSpan.FromDays(91))).Kind);
            Assert.Equal(PkiErrorKind.InvalidLifetime, Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(csr, "dgx", [], TimeSpan.Zero)).Kind);
            using IssuedCertificate shorter = ca.IssueAgentCertificate(csr, "dgx", [], TimeSpan.FromDays(7));
            Assert.True(shorter.NotAfter - shorter.NotBefore <= TimeSpan.FromDays(7) + PkiDefaults.NotBeforeBackdate);
        }
    }

    [Fact]
    public void IssueAgent_ClampedToCaExpiryAndRefusedOnceCaExpired()
    {
        ManualTimeProvider time = new(PkiTestSupport.Epoch);
        using CertificateAuthority ca = CertificateAuthority.Create(time, TimeSpan.FromDays(30));
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        {
            using (IssuedCertificate issued = ca.IssueAgentCertificate(csr, "dgx", []))
            {
                Assert.Equal(ca.NotAfter, issued.NotAfter);
            }

            time.Advance(TimeSpan.FromDays(31));
            Assert.Equal(PkiErrorKind.CaNotValid, Assert.Throws<PkiException>(() => ca.IssueAgentCertificate(csr, "dgx", [])).Kind);
        }
    }

    [Fact]
    public void IssueAgent_SerialNumbersAreRandomAndPositive()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        {
            HashSet<string> serials = new(StringComparer.Ordinal);
            for (int i = 0; i < 20; i++)
            {
                using IssuedCertificate issued = ca.IssueAgentCertificate(csr, "dgx", []);
                Assert.Equal(32, issued.SerialNumber.Length);
                Assert.True(Convert.FromHexString(issued.SerialNumber)[0] < 0x80);
                Assert.True(serials.Add(issued.SerialNumber));
            }
        }
    }

    // ---------------------------------------------------------------- master certificates

    [Fact]
    public void IssueMasterClient_MasterSanClientAuthThirtyDaysWithItsOwnKey()
    {
        ManualTimeProvider time = new(PkiTestSupport.Epoch);
        using CertificateAuthority ca = CertificateAuthority.Create(time);
        using IssuedCredential master = ca.IssueMasterClientCertificate();

        SanEntries san = CertificateSans.Read(master.PublicCertificate)!;
        Assert.Equal([Protocol.IssuedMasterSpiffeId], san.Uris);
        Assert.Empty(san.DnsNames);
        Assert.Empty(san.IpAddresses);
        Assert.Equal(
            [PkiDefaults.ClientAuthOid],
            Assert.Single(master.PublicCertificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()).EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToArray());
        Assert.Equal(PkiTestSupport.Epoch + TimeSpan.FromDays(30), master.NotAfter);
        Assert.Equal(PkiErrorKind.InvalidLifetime, Assert.Throws<PkiException>(() => ca.IssueMasterClientCertificate(TimeSpan.FromDays(31))).Kind);

        using X509Certificate2 tls = master.CreateTlsCertificate();
        Assert.True(tls.HasPrivateKey);
        Assert.Equal(master.PublicCertificate.RawData, tls.RawData);

        using IssuedCredential reloaded = TlsCredentials.LoadCredential(master.CertificatePem, master.ExportPrivateKeyPem());
        Assert.Equal(master.SpkiSha256, reloaded.SpkiSha256);
    }

    [Fact]
    public void IssueEnrollServer_MasterSanPlusDnsServerAuth()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        using IssuedCredential enroll = ca.IssueEnrollServerCertificate(["enroll.mirbad.example.com", "mirbadllm", "192.0.2.191"]);

        SanEntries san = CertificateSans.Read(enroll.PublicCertificate)!;
        Assert.Equal([Protocol.IssuedMasterSpiffeId], san.Uris);
        Assert.Equal(["enroll.mirbad.example.com", "mirbadllm"], san.DnsNames);
        Assert.Equal([IPAddress.Parse("192.0.2.191")], san.IpAddresses);
        Assert.Equal(
            [PkiDefaults.ServerAuthOid],
            Assert.Single(enroll.PublicCertificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()).EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToArray());
        Assert.Equal(PkiDefaults.MasterCertificateLifetime, enroll.NotAfter - enroll.NotBefore - PkiDefaults.NotBeforeBackdate);

        // The issuing CA travels in a non-critical private extension (OpenSSL does not send self-signed roots).
        X509Extension carrier = Assert.Single(enroll.PublicCertificate.Extensions.Cast<X509Extension>(), e => e.Oid?.Value == PkiDefaults.EmbeddedCaCertificateOid);
        Assert.False(carrier.Critical);
        Assert.Equal(ca.Certificate.RawData, carrier.RawData);

        using IssuedCredential client = ca.IssueMasterClientCertificate();
        Assert.Null(client.PublicCertificate.Extensions[PkiDefaults.EmbeddedCaCertificateOid]);
    }

    // ---------------------------------------------------------------- renewal points

    [Fact]
    public void RenewalPoints_FollowTheDesign()
    {
        ManualTimeProvider time = new(PkiTestSupport.Epoch);
        using CertificateAuthority ca = CertificateAuthority.Create(time);
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        using (IssuedCertificate agent = ca.IssueAgentCertificate(csr, "dgx", []))
        using (IssuedCredential master = ca.IssueMasterClientCertificate())
        {
            Assert.Equal(agent.NotBefore + TimeSpan.FromDays(60), PkiDefaults.AgentRenewalDue(agent.Certificate));
            Assert.Equal(master.NotBefore + ((master.NotAfter - master.NotBefore) / 2), PkiDefaults.MasterReissueDue(master.PublicCertificate));
            Assert.False(PkiDefaults.IsInRenewalWarningWindow(agent.Certificate, PkiTestSupport.Epoch + TimeSpan.FromDays(75)));
            Assert.True(PkiDefaults.IsInRenewalWarningWindow(agent.Certificate, PkiTestSupport.Epoch + TimeSpan.FromDays(76)));
        }
    }
}
