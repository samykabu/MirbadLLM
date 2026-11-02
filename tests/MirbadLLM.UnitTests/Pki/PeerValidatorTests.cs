using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Pki;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Pki;

/// <summary>MN-004: the TLS validators both sides use (design §3.1, §3.2, §4.2, §10.2).</summary>
public sealed class PeerValidatorTests : IDisposable
{
    private readonly ManualTimeProvider _time = new(PkiTestSupport.Epoch);
    private readonly CertificateAuthority _ca;
    private readonly CertificateAuthority _otherCa;
    private readonly TrustAnchorSet _trust;
    private readonly ECDsa _agentKey;
    private readonly IssuedCertificate _agent;
    private readonly IssuedCredential _masterClient;
    private readonly IssuedCredential _enroll;

    public PeerValidatorTests()
    {
        _ca = CertificateAuthority.Create(_time);
        _otherCa = CertificateAuthority.Create(_time);
        _trust = PkiTestSupport.Trust(_ca);
        string csr = PkiTestSupport.NewCsr(out _agentKey);
        _agent = _ca.IssueAgentCertificate(csr, "dgx", ["192.0.2.10"]);
        _masterClient = _ca.IssueMasterClientCertificate();
        _enroll = _ca.IssueEnrollServerCertificate(["mirbadllm"]);
    }

    public void Dispose()
    {
        _agent.Dispose();
        _masterClient.Dispose();
        _enroll.Dispose();
        _agentKey.Dispose();
        _trust.Dispose();
        _ca.Dispose();
        _otherCa.Dispose();
    }

    // ---------------------------------------------------------------- master validating an agent

    [Fact]
    public void Agent_RightChainSanEkuAndPinAccepted()
    {
        PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [_agent.SpkiSha256], _time).Validate(_agent.Certificate);
        Assert.True(result.IsValid, result.Detail);
        Assert.Equal(_agent.SpkiSha256, result.SpkiSha256);
    }

    [Fact]
    public void Agent_WrongCaRefused()
    {
        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        using (IssuedCertificate foreign = _otherCa.IssueAgentCertificate(csr, "dgx", []))
        {
            PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [foreign.SpkiSha256], _time).Validate(foreign.Certificate);
            Assert.Equal(PeerValidationFailure.UntrustedChain, result.Failure);
        }
    }

    [Fact]
    public void Agent_CertificateOfAnotherNodeRefused()
    {
        // Same CA, same key pinned, but the SAN names another node: one node cannot impersonate another.
        PeerValidationResult result = PeerValidator.ForAgent("pc-office", _trust, [_agent.SpkiSha256], _time).Validate(_agent.Certificate);
        Assert.Equal(PeerValidationFailure.WrongSan, result.Failure);
    }

    [Fact]
    public void Agent_MasterClientCertificateRefusedForEku()
    {
        // The master's client certificate is clientAuth only and names the master.
        PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [_masterClient.SpkiSha256], _time).Validate(_masterClient.PublicCertificate);
        Assert.Equal(PeerValidationFailure.WrongEku, result.Failure);
    }

    [Fact]
    public void Agent_CorrectSanButWithoutServerAuthRefused()
    {
        using X509Certificate2 clientOnly = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [Protocol.NodeSpiffeId("dgx")], [PkiDefaults.ClientAuthOid]);
        PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [Spki.Sha256(clientOnly)], _time).Validate(clientOnly);
        Assert.Equal(PeerValidationFailure.WrongEku, result.Failure);
    }

    [Fact]
    public void Agent_CertificateWithoutAnyEkuExtensionRefused()
    {
        // An absent EKU extension means "any usage" to a platform chain; this PKI requires it explicitly.
        using X509Certificate2 noEku = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [Protocol.NodeSpiffeId("dgx")], [], includeEku: false);
        PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [Spki.Sha256(noEku)], _time).Validate(noEku);
        Assert.Equal(PeerValidationFailure.WrongEku, result.Failure);
    }

    [Theory]
    [InlineData("spiffe://mirbadllm/node/dgx/")]
    [InlineData("spiffe://mirbadllm/node/dgx2")]
    [InlineData("spiffe://other/node/dgx")]
    [InlineData("https://mirbadllm/node/dgx")]
    public void Agent_SanMustMatchExactly(string uri)
    {
        using X509Certificate2 cert = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [uri], [PkiDefaults.ServerAuthOid]);
        PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [Spki.Sha256(cert)], _time).Validate(cert);
        Assert.Equal(PeerValidationFailure.WrongSan, result.Failure);
    }

    [Fact]
    public void Agent_TwoUriSansRefusedEvenIfOneMatches()
    {
        using X509Certificate2 cert = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10),
            [Protocol.NodeSpiffeId("dgx"), Protocol.MasterSpiffeId], [PkiDefaults.ServerAuthOid]);
        PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [Spki.Sha256(cert)], _time).Validate(cert);
        Assert.Equal(PeerValidationFailure.WrongSan, result.Failure);
    }

    [Fact]
    public void Agent_NoSanRefused()
    {
        using X509Certificate2 cert = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [], [PkiDefaults.ServerAuthOid]);
        Assert.Equal(PeerValidationFailure.WrongSan, PeerValidator.ForAgent("dgx", _trust, [Spki.Sha256(cert)], _time).Validate(cert).Failure);
    }

    [Fact]
    public void Agent_ExpiredAndNotYetValidRefused()
    {
        PeerValidator validator = PeerValidator.ForAgent("dgx", _trust, [_agent.SpkiSha256], _time);

        _time.Now = _agent.NotAfter + TimeSpan.FromSeconds(1);
        Assert.Equal(PeerValidationFailure.NotTimeValid, validator.Validate(_agent.Certificate).Failure);

        _time.Now = _agent.NotBefore - TimeSpan.FromSeconds(1);
        Assert.Equal(PeerValidationFailure.NotTimeValid, validator.Validate(_agent.Certificate).Failure);

        _time.Now = _agent.NotAfter - TimeSpan.FromMinutes(1);
        Assert.True(validator.Validate(_agent.Certificate).IsValid);
    }

    [Fact]
    public void Agent_UnpinnedKeyRefused()
    {
        using ECDsa other = PkiDefaults.CreateKey();
        PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [Spki.Sha256(other)], _time).Validate(_agent.Certificate);
        Assert.Equal(PeerValidationFailure.PinMismatch, result.Failure);
    }

    [Fact]
    public void Agent_ValidatorWithoutPinsCannotBeBuilt()
    {
        Assert.Throws<ArgumentException>(() => PeerValidator.ForAgent("dgx", _trust, [], _time));
        Assert.Throws<ArgumentException>(() => PeerValidator.ForAgent("dgx", _trust, ["not-a-pin"], _time));
    }

    [Fact]
    public void Agent_PendingAndCurrentPinSetAcceptsBothKeysDuringRotation()
    {
        // Two-phase rotation (design §10.2 step 2): the master accepts either pin until commit.
        string csr = PkiTestSupport.NewCsr(out ECDsa newKey);
        using (newKey)
        using (IssuedCertificate renewed = _ca.IssueAgentCertificate(csr, "dgx", ["192.0.2.10"]))
        {
            PeerValidator during = PeerValidator.ForAgent("dgx", _trust, [_agent.SpkiSha256, renewed.SpkiSha256], _time);
            Assert.True(during.Validate(_agent.Certificate).IsValid);
            Assert.True(during.Validate(renewed.Certificate).IsValid);

            // After commit only the promoted pin remains.
            PeerValidator after = PeerValidator.ForAgent("dgx", _trust, [renewed.SpkiSha256], _time);
            Assert.True(after.Validate(renewed.Certificate).IsValid);
            Assert.Equal(PeerValidationFailure.PinMismatch, after.Validate(_agent.Certificate).Failure);
        }
    }

    [Fact]
    public void Agent_CaCertificatePresentedAsLeafRefused()
    {
        PeerValidationResult result = PeerValidator.ForAgent("dgx", _trust, [_ca.SpkiSha256], _time).Validate(_ca.Certificate);
        Assert.Equal(PeerValidationFailure.NotEndEntity, result.Failure);
    }

    [Fact]
    public void Agent_LeafBelowAnIntermediateRefused()
    {
        // This PKI has no intermediates; a sub-CA signed by the trusted root must not extend trust.
        using ECDsa subCaKey = PkiDefaults.CreateKey();
        using X509Certificate2 subCa = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(100), [], [], isCa: true, includeEku: false, subjectKey: subCaKey);
        using X509Certificate2 leaf = PkiTestSupport.SignCustom(subCa, subCaKey.ExportPkcs8PrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [Protocol.NodeSpiffeId("dgx")], [PkiDefaults.ServerAuthOid]);

        PeerValidator validator = PeerValidator.ForAgent("dgx", _trust, [Spki.Sha256(leaf)], _time);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(leaf, [subCa]).Failure);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(leaf).Failure);
    }

    [Fact]
    public void Agent_NoCertificateRefused()
    {
        Assert.Equal(PeerValidationFailure.NoCertificate, PeerValidator.ForAgent("dgx", _trust, [_agent.SpkiSha256], _time).Validate(null).Failure);
    }

    // ---------------------------------------------------------------- agent validating the master

    [Fact]
    public void Master_ClientCertificateAccepted()
    {
        PeerValidationResult result = PeerValidator.ForMasterClient(_trust, _time).Validate(_masterClient.PublicCertificate);
        Assert.True(result.IsValid, result.Detail);
    }

    [Fact]
    public void Master_EnrollServerCertificateRefusedAsClient()
    {
        Assert.Equal(PeerValidationFailure.WrongEku, PeerValidator.ForMasterClient(_trust, _time).Validate(_enroll.PublicCertificate).Failure);
    }

    [Fact]
    public void Master_AgentCertificateRefusedAsMaster()
    {
        // An agent cert has clientAuth but the node SAN: a compromised agent cannot call other agents as the master.
        Assert.Equal(PeerValidationFailure.WrongSan, PeerValidator.ForMasterClient(_trust, _time).Validate(_agent.Certificate).Failure);
    }

    [Fact]
    public void Master_FromForeignCaRefused()
    {
        using IssuedCredential foreign = _otherCa.IssueMasterClientCertificate();
        Assert.Equal(PeerValidationFailure.UntrustedChain, PeerValidator.ForMasterClient(_trust, _time).Validate(foreign.PublicCertificate).Failure);
    }

    [Fact]
    public void Master_ExpiredRefused()
    {
        PeerValidator validator = PeerValidator.ForMasterClient(_trust, _time);
        _time.Now = _masterClient.NotAfter + TimeSpan.FromSeconds(1);
        Assert.Equal(PeerValidationFailure.NotTimeValid, validator.Validate(_masterClient.PublicCertificate).Failure);
    }

    // ---------------------------------------------------------------- CA rotation: two roots

    [Fact]
    public void CaRotation_TwoRootTrustAcceptsCertificatesFromEitherCa()
    {
        CertificateAuthority newCa = _otherCa;
        using TrustAnchorSet both = PkiTestSupport.Trust(_ca, newCa);
        using TrustAnchorSet newOnly = PkiTestSupport.Trust(newCa);

        string csr = PkiTestSupport.NewCsr(out ECDsa key);
        using (key)
        using (IssuedCertificate underNew = newCa.IssueAgentCertificate(csr, "dgx", []))
        using (IssuedCredential masterUnderNew = newCa.IssueMasterClientCertificate())
        {
            // Master side: agent certs under either CA validate while both roots are trusted.
            PeerValidator master = PeerValidator.ForAgent("dgx", both, [_agent.SpkiSha256, underNew.SpkiSha256], _time);
            Assert.True(master.Validate(_agent.Certificate).IsValid);
            Assert.True(master.Validate(underNew.Certificate).IsValid);

            // Agent side: master certs under either CA validate during the rotation.
            PeerValidator agent = PeerValidator.ForMasterClient(both, _time);
            Assert.True(agent.Validate(_masterClient.PublicCertificate).IsValid);
            Assert.True(agent.Validate(masterUnderNew.PublicCertificate).IsValid);

            // Once the old root is removed, its certificates are refused.
            Assert.Equal(PeerValidationFailure.UntrustedChain, PeerValidator.ForAgent("dgx", newOnly, [_agent.SpkiSha256], _time).Validate(_agent.Certificate).Failure);
            Assert.Equal(PeerValidationFailure.UntrustedChain, PeerValidator.ForMasterClient(newOnly, _time).Validate(_masterClient.PublicCertificate).Failure);
            Assert.True(PeerValidator.ForMasterClient(newOnly, _time).Validate(masterUnderNew.PublicCertificate).IsValid);
        }
    }

    // ---------------------------------------------------------------- enroll listener (CA hash)

    [Fact]
    public void Enroll_CaHashModeAcceptsWhenTheServerSendsThePinnedCa()
    {
        PeerValidator validator = PeerValidator.ForEnrollListener(Spki.ToJoinHash(_ca.SpkiSha256), time: _time);
        Assert.True(validator.Validate(_enroll.PublicCertificate, [_ca.Certificate]).IsValid);
    }

    [Fact]
    public void Enroll_CaHashModeFindsTheCaEmbeddedInTheLeafWithoutAnyChain()
    {
        // OpenSSL (Linux) never sends a self-signed root, so the enroll leaf carries the CA itself.
        PeerValidator validator = PeerValidator.ForEnrollListener(_ca.SpkiSha256, time: _time);
        PeerValidationResult result = validator.Validate(_enroll.PublicCertificate);
        Assert.True(result.IsValid, result.Detail);
    }

    [Fact]
    public void Enroll_CaHashModeRefusesWhenNoCaIsPresented()
    {
        // A genuine master-looking leaf without the embedded CA and without a chain: nothing to anchor the hash to.
        using X509Certificate2 bare = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [Protocol.MasterSpiffeId], [PkiDefaults.ServerAuthOid]);
        PeerValidator validator = PeerValidator.ForEnrollListener(_ca.SpkiSha256, time: _time);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(bare).Failure);
        Assert.True(validator.Validate(bare, [_ca.Certificate]).IsValid);
    }

    [Fact]
    public void Enroll_MitmLeafEmbeddingTheGenuineCaRefused()
    {
        // The attacker copies the real (public) CA certificate into its own leaf: the hash matches, the signature does not.
        X509Extension carrier = new(PkiDefaults.EmbeddedCaCertificateOid, _ca.Certificate.RawData, critical: false);
        using X509Certificate2 mitm = PkiTestSupport.SignCustom(_otherCa.Certificate, _otherCa.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [Protocol.MasterSpiffeId], [PkiDefaults.ServerAuthOid],
            extraExtensions: [carrier]);
        PeerValidator validator = PeerValidator.ForEnrollListener(_ca.SpkiSha256, time: _time);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(mitm).Failure);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(mitm, [_otherCa.Certificate, _ca.Certificate]).Failure);
    }

    [Fact]
    public void Enroll_EmbeddedCaMustBeSelfSigned()
    {
        // A certificate for the genuine CA key issued by the attacker's CA (hash matches, not self-signed) is not a root.
        using ECDsa genuinePublic = _ca.Certificate.GetECDsaPublicKey()!;
        using X509Certificate2 crossCert = PkiTestSupport.SignCustom(_otherCa.Certificate, _otherCa.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(100), [], [], isCa: true, includeEku: false, subjectKey: genuinePublic);
        Assert.Equal(_ca.SpkiSha256, Spki.Sha256(crossCert));

        X509Extension carrier = new(PkiDefaults.EmbeddedCaCertificateOid, crossCert.RawData, critical: false);
        using X509Certificate2 leaf = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [Protocol.MasterSpiffeId], [PkiDefaults.ServerAuthOid],
            extraExtensions: [carrier]);
        PeerValidator validator = PeerValidator.ForEnrollListener(_ca.SpkiSha256, time: _time);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(leaf).Failure);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(leaf, [crossCert]).Failure);
    }

    [Fact]
    public void Enroll_MalformedOrDuplicateEmbeddedCaIgnored()
    {
        X509Extension garbage = new(PkiDefaults.EmbeddedCaCertificateOid, [0x30, 0x03, 0x02, 0x01, 0x01], critical: false);
        using X509Certificate2 malformed = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
            PkiTestSupport.Epoch.AddMinutes(-1), PkiTestSupport.Epoch.AddDays(10), [Protocol.MasterSpiffeId], [PkiDefaults.ServerAuthOid],
            extraExtensions: [garbage]);
        PeerValidator validator = PeerValidator.ForEnrollListener(_ca.SpkiSha256, time: _time);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(malformed).Failure);
    }

    [Fact]
    public void EmbeddedCa_GrantsNothingOutsideCaHashMode()
    {
        // The master's agent validator and the agent's master validator use their configured roots only.
        using TrustAnchorSet foreignOnly = PkiTestSupport.Trust(_otherCa);
        Assert.Equal(PeerValidationFailure.UntrustedChain, PeerValidator.ForEnrollListener(foreignOnly, _time).Validate(_enroll.PublicCertificate).Failure);
    }

    [Fact]
    public void Enroll_CaHashModeRefusesAMitmWithItsOwnCa()
    {
        // The attacker presents a master-looking cert under its own CA and sends that CA: the hash does not match.
        using IssuedCredential mitm = _otherCa.IssueEnrollServerCertificate(["mirbadllm"]);
        PeerValidator validator = PeerValidator.ForEnrollListener(_ca.SpkiSha256, time: _time);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(mitm.PublicCertificate, [_otherCa.Certificate]).Failure);

        // Also sending the genuine CA certificate (public) does not help: the leaf is not signed by it.
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(mitm.PublicCertificate, [_otherCa.Certificate, _ca.Certificate]).Failure);
    }

    [Fact]
    public void Enroll_RequiresServerAuthAndTheMasterSan()
    {
        PeerValidator validator = PeerValidator.ForEnrollListener(_ca.SpkiSha256, [_ca.Certificate], _time);
        Assert.Equal(PeerValidationFailure.WrongEku, validator.Validate(_masterClient.PublicCertificate).Failure);
        Assert.Equal(PeerValidationFailure.WrongSan, validator.Validate(_agent.Certificate).Failure);
        Assert.True(validator.Validate(_enroll.PublicCertificate).IsValid);
    }

    [Fact]
    public void Enroll_KnownCaModeForTheBootstrapBundle()
    {
        Assert.True(PeerValidator.ForEnrollListener(_trust, _time).Validate(_enroll.PublicCertificate).IsValid);
        Assert.Throws<ArgumentException>(() => PeerValidator.ForEnrollListener("sha256:nope", time: _time));
    }

    // ---------------------------------------------------------------- callback shapes

    [Fact]
    public void Callbacks_IgnorePlatformChainAndNameErrorsButRefuseMissingCertificates()
    {
        List<PeerValidationResult> rejected = [];
        PeerValidator validator = PeerValidator.ForAgent("dgx", _trust, [_agent.SpkiSha256], _time).WithRejectionObserver(rejected.Add);

        Assert.True(validator.ValidateRemoteCertificate(this, _agent.Certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(validator.ValidateClientCertificate(_agent.Certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Empty(rejected);

        Assert.False(validator.ValidateRemoteCertificate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.False(validator.ValidateRemoteCertificate(this, _masterClient.PublicCertificate, null, SslPolicyErrors.None));
        Assert.Equal([PeerValidationFailure.NoCertificate, PeerValidationFailure.WrongEku], rejected.Select(r => r.Failure));
    }

    [Fact]
    public void Callbacks_AcceptPlainX509Certificate()
    {
#pragma warning disable SYSLIB0057 // building a base X509Certificate on purpose
        using X509Certificate plain = new(_agent.Certificate.RawData);
#pragma warning restore SYSLIB0057
        PeerValidator validator = PeerValidator.ForAgent("dgx", _trust, [_agent.SpkiSha256], _time);
        Assert.True(validator.ValidateRemoteCertificate(null, plain, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Result_CarriesOnlyPublicData()
    {
        PeerValidationResult result = PeerValidator.ForAgent("pc-office", _trust, [_agent.SpkiSha256], _time).Validate(_agent.Certificate);
        string text = result.ToString();
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        Assert.Contains(_agent.SpkiSha256, text, StringComparison.Ordinal);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), CertificateSans.Read(_agent.Certificate)!.IpAddresses[0]);
    }
}
