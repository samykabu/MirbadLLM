using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Pki;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Pki;

/// <summary>MN-004: SPKI pins, trust anchor sets, TLS credentials, CA storage (design §3.1, §3.2, §10.2).</summary>
public sealed class PkiMaterialTests
{
    // ---------------------------------------------------------------- SPKI pins

    [Fact]
    public void Spki_IsBase64Sha256OfTheDerSubjectPublicKeyInfo()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        byte[] expected = SHA256.HashData(ca.Certificate.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.Equal(Convert.ToBase64String(expected), Spki.Sha256(ca.Certificate));
        Assert.Equal(44, ca.SpkiSha256.Length);
        Assert.True(Spki.IsValidPin(ca.SpkiSha256));
        Assert.True(Spki.Matches(ca.Certificate, ca.SpkiSha256));
    }

    [Fact]
    public void Spki_SameKeyDifferentCertificatesSamePin()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        using (IssuedCertificate a = ca.IssueAgentCertificate(csr, "dgx", []))
        using (IssuedCertificate b = ca.IssueAgentCertificate(csr, "dgx", ["10.0.0.1"]))
        {
            Assert.Equal(a.SpkiSha256, b.SpkiSha256);
            Assert.NotEqual(a.Certificate.RawData, b.Certificate.RawData);
        }
    }

    [Fact]
    public void Spki_JoinHashRoundTripsAndAcceptsHexAndBase64Url()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        string pin = ca.SpkiSha256;
        string join = Spki.ToJoinHash(pin);
        Assert.StartsWith("sha256:", join, StringComparison.Ordinal);

        Assert.True(Spki.TryParseJoinHash(join, out string parsed));
        Assert.Equal(pin, parsed);

        byte[] raw = Convert.FromBase64String(pin);
        Assert.True(Spki.TryParseJoinHash("sha256:" + Convert.ToHexString(raw), out parsed));
        Assert.Equal(pin, parsed);
        string base64Url = pin.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(Spki.TryParseJoinHash(base64Url, out parsed));
        Assert.Equal(pin, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha256:abcd")]
    [InlineData("sha256:!!!!")]
    [InlineData("md5:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Spki_MalformedJoinHashRefused(string? value)
    {
        Assert.False(Spki.TryParseJoinHash(value, out _));
    }

    [Fact]
    public void Spki_PinEqualsRejectsMalformedValues()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        Assert.True(Spki.PinEquals(ca.SpkiSha256, ca.SpkiSha256));
        Assert.False(Spki.PinEquals(ca.SpkiSha256, null));
        Assert.False(Spki.PinEquals("x", "x"));
        Assert.False(Spki.MatchesAny(ca.Certificate, []));
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", Spki.ToDisplayFingerprint(ca.SpkiSha256));
    }

    // ---------------------------------------------------------------- trust anchors

    [Fact]
    public void TrustSet_FromPemDescribesAnchorsAndDeduplicates()
    {
        using CertificateAuthority a = CertificateAuthority.Create();
        using CertificateAuthority b = CertificateAuthority.Create();
        using TrustAnchorSet set = TrustAnchorSet.FromPem([a.CertificatePem, b.CertificatePem, a.CertificatePem]);

        Assert.Equal(2, set.Anchors.Count);
        Assert.Equal([a.SpkiSha256, b.SpkiSha256], set.SpkiSha256s);
        Assert.True(set.ContainsSpki(b.SpkiSha256));
        IReadOnlyList<TrustAnchorInfo> info = set.Describe();
        Assert.Equal(a.ToTrustAnchorInfo(), info[0]);
        Assert.Equal(a.Certificate.Subject, info[0].Subject);

        using TrustAnchorSet again = TrustAnchorSet.FromPem(set.ToPem());
        Assert.Equal(set.SpkiSha256s, again.SpkiSha256s);
    }

    [Fact]
    public void TrustSet_RefusesLeavesEmptyAndMoreThanTwoAnchors()
    {
        using CertificateAuthority a = CertificateAuthority.Create();
        using CertificateAuthority b = CertificateAuthority.Create();
        using CertificateAuthority c = CertificateAuthority.Create();
        using IssuedCredential leaf = a.IssueMasterClientCertificate();

        Assert.Equal(PkiErrorKind.NotACertificateAuthority, Assert.Throws<PkiException>(() => TrustAnchorSet.FromPem([leaf.CertificatePem])).Kind);
        Assert.Equal(PkiErrorKind.CaMaterialInvalid, Assert.Throws<PkiException>(() => TrustAnchorSet.FromPem([])).Kind);
        Assert.Equal(PkiErrorKind.CaMaterialInvalid, Assert.Throws<PkiException>(() => TrustAnchorSet.FromPem([a.CertificatePem, b.CertificatePem, c.CertificatePem])).Kind);
        Assert.Equal(PkiErrorKind.InvalidCertificate, Assert.Throws<PkiException>(() => TrustAnchorSet.FromPem(["garbage"])).Kind);
    }

    [Fact]
    public void TrustSet_OwnsCopiesSoValidatorsSurviveDisposal()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        using IssuedCredential master = ca.IssueMasterClientCertificate();
        PeerValidator validator;
        using (TrustAnchorSet set = TrustAnchorSet.Create([ca.Certificate]))
        {
            validator = PeerValidator.ForMasterClient(set);
        }

        Assert.True(validator.Validate(master.PublicCertificate).IsValid);
    }

    // ---------------------------------------------------------------- TLS credentials

    [Fact]
    public void TlsCredentials_FromPemChecksThePair()
    {
        using CertificateAuthority ca = CertificateAuthority.Create();
        using IssuedCredential a = ca.IssueMasterClientCertificate();
        using IssuedCredential b = ca.IssueMasterClientCertificate();

        using (X509Certificate2 tls = TlsCredentials.FromPem(a.CertificatePem, a.ExportPrivateKeyPem()))
        {
            Assert.True(tls.HasPrivateKey);
            Assert.Equal(a.SpkiSha256, Spki.Sha256(tls));
        }

        PkiException ex = Assert.Throws<PkiException>(() => TlsCredentials.FromPem(a.CertificatePem, b.ExportPrivateKeyPem()));
        Assert.Equal(PkiErrorKind.KeyMismatch, ex.Kind);
        Assert.Throws<PkiException>(() => TlsCredentials.Create(a.PublicCertificate));
    }

    [Fact]
    public void TlsCredentials_AgentKeyPlusIssuedCertificateLoad()
    {
        // What the agent does after enrollment: its own key + the certificate the master returned.
        using CertificateAuthority ca = CertificateAuthority.Create();
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        using (IssuedCertificate issued = ca.IssueAgentCertificate(csr, "dgx", []))
        using (X509Certificate2 tls = TlsCredentials.FromPem(issued.CertificatePem, key.ExportPkcs8PrivateKeyPem()))
        {
            Assert.True(tls.HasPrivateKey);
        }
    }

    [Fact]
    public void Csr_AgentSideCreationValidatesInputs()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Equal(PkiErrorKind.UnsupportedKey, Assert.Throws<PkiException>(() => CertificateSigningRequests.CreatePem(p384, "dgx")).Kind);
        using ECDsa key = PkiDefaults.CreateKey();
        Assert.Equal(PkiErrorKind.InvalidNodeId, Assert.Throws<PkiException>(() => CertificateSigningRequests.CreatePem(key, "Bad Id")).Kind);

        string csr = CertificateSigningRequests.CreatePem(key, "pc-office");
        PublicKey extracted = CertificateSigningRequests.VerifyAndExtractPublicKey(csr);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), extracted.ExportSubjectPublicKeyInfo());
    }

    // ---------------------------------------------------------------- CA store

    [Fact]
    public void CaStore_CreatesOnFirstBootThenLoadsTheSameCa()
    {
        using TempDirectory dir = new();
        string pki = Path.Combine(dir.Path, "pki");

        string first;
        using (CertificateAuthority created = CaStore.LoadOrCreate(pki, null, null))
        {
            first = created.SpkiSha256;
        }

        Assert.True(File.Exists(Path.Combine(pki, CaStore.CertificateFileName)));
        Assert.Contains("PRIVATE KEY", File.ReadAllText(Path.Combine(pki, CaStore.KeyFileName)), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(pki, "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(pki, CaStore.KeyFileName)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(pki));
        }

        using CertificateAuthority loaded = CaStore.LoadOrCreate(pki, null, null);
        Assert.Equal(first, loaded.SpkiSha256);
    }

    [Fact]
    public void CaStore_ConfiguredCaUsedWithoutWritingItsKey()
    {
        using TempDirectory dir = new();
        using CertificateAuthority source = CertificateAuthority.Create();
        using CertificateAuthority ca = CaStore.LoadOrCreate(dir.Path, source.CertificatePem, source.ExportPrivateKeyPem());
        Assert.Equal(source.SpkiSha256, ca.SpkiSha256);
        Assert.False(File.Exists(Path.Combine(dir.Path, CaStore.KeyFileName)));
    }

    [Fact]
    public void CaStore_ConfiguredAndDiskSameIsFineDifferentRefusesToStart()
    {
        using TempDirectory dir = new();
        string onDiskSpki;
        string onDiskCert;
        string onDiskKey;
        using (CertificateAuthority created = CaStore.LoadOrCreate(dir.Path, null, null))
        {
            onDiskSpki = created.SpkiSha256;
            onDiskCert = created.CertificatePem;
            onDiskKey = created.ExportPrivateKeyPem();
        }

        using (CertificateAuthority same = CaStore.LoadOrCreate(dir.Path, onDiskCert, onDiskKey))
        {
            Assert.Equal(onDiskSpki, same.SpkiSha256);
        }

        using CertificateAuthority other = CertificateAuthority.Create();
        PkiException ex = Assert.Throws<PkiException>(() => CaStore.LoadOrCreate(dir.Path, other.CertificatePem, other.ExportPrivateKeyPem()));
        Assert.Equal(PkiErrorKind.CaConflict, ex.Kind);
    }

    [Fact]
    public void CaStore_HalfPresentMaterialRefused()
    {
        using TempDirectory dir = new();
        using CertificateAuthority ca = CertificateAuthority.Create();

        Assert.Equal(PkiErrorKind.CaMaterialInvalid, Assert.Throws<PkiException>(() => CaStore.LoadOrCreate(dir.Path, ca.CertificatePem, null)).Kind);

        PkiFiles.WritePublic(Path.Combine(dir.Path, CaStore.CertificateFileName), ca.CertificatePem);
        Assert.Equal(PkiErrorKind.CaMaterialInvalid, Assert.Throws<PkiException>(() => CaStore.LoadOrCreate(dir.Path, null, null)).Kind);
    }

    [Fact]
    public void CaStore_MismatchedPairOnDiskRefused()
    {
        using TempDirectory dir = new();
        using CertificateAuthority a = CertificateAuthority.Create();
        using CertificateAuthority b = CertificateAuthority.Create();
        PkiFiles.WritePublic(Path.Combine(dir.Path, CaStore.CertificateFileName), a.CertificatePem);
        PkiFiles.WriteSecret(Path.Combine(dir.Path, CaStore.KeyFileName), b.ExportPrivateKeyPem());
        Assert.Equal(PkiErrorKind.KeyMismatch, Assert.Throws<PkiException>(() => CaStore.LoadOrCreate(dir.Path, null, null)).Kind);
    }

    [Fact]
    public void PkiFiles_OverwriteIsAtomicAndLeavesNoTempFiles()
    {
        using TempDirectory dir = new();
        string path = Path.Combine(dir.Path, "agent.key");
        PkiFiles.WriteSecret(path, "one");
        PkiFiles.WriteSecret(path, "two");
        Assert.Equal("two", PkiFiles.ReadIfExists(path));
        Assert.Null(PkiFiles.ReadIfExists(Path.Combine(dir.Path, "missing")));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mirbadllm-pki-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
