using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Pki;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Pki;
using MirbadLLM.Web.Enrollment;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required here.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-035: the enroll listener, the bootstrap bundle and the agent's <c>join</c>/bootstrap modes (design §3.2) over real
/// TLS on loopback (SChannel on Windows, OpenSSL on Linux), so the CA-hash check runs where the token would be sent.
/// </summary>
public sealed class EnrollmentTests : IDisposable
{
    // SKI, key usage, SAN, basic constraints, AKI, EKU: what CertificateAuthority.IssueAgentCertificate sets, nothing else.
    private static readonly string[] MasterChosenExtensions = ["2.5.29.14", "2.5.29.15", "2.5.29.17", "2.5.29.19", "2.5.29.35", "2.5.29.37"];
    private static readonly string[] AgentEkus = [PkiDefaults.ServerAuthOid, PkiDefaults.ClientAuthOid];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-enroll-" + Guid.NewGuid().ToString("N"));
    private readonly CertificateAuthority _ca = CertificateAuthority.Create();

    public void Dispose()
    {
        _ca.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DataDir => Path.Combine(_root, "data");

    private string StateDir(string name = "agent") => Path.Combine(_root, "state-" + name);

    private string BundleDir => Path.Combine(_root, "bootstrap");

    private static AgentEnrollment Agent() => new(TimeProvider.System, NullLogger<AgentEnrollment>.Instance);

    private JoinParameters Join(Master master, string token, string nodeId = "pc-office", string? caHash = null, string? state = null, bool force = false) => new()
    {
        EnrollUrl = master.Https,
        Token = token,
        CaHash = caHash ?? Spki.ToJoinHash(_ca.SpkiSha256),
        NodeId = nodeId,
        StateDirectory = state ?? StateDir(),
        Addresses = ["192.0.2.50"],
        Force = force,
    };

    // ---- join: the happy path ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Join_EnrollsPendingApproval_AndStoresALoadableIdentity()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        EnrollmentOutcome outcome = await Agent().JoinAsync(Join(master, issue.Token));

        Assert.True(outcome.PendingApproval);
        Assert.Equal("pc-office", outcome.NodeId);
        Assert.Matches("^[0-9a-f]{32}$", outcome.NodeUid);

        NodeRecord node = Assert.IsType<NodeRecord>(await master.Registry.GetAsync("pc-office"));
        Assert.False(node.Approved);
        Assert.Null(node.CertSpkiSha256);
        Assert.Equal(outcome.CertificateSpkiSha256, node.PendingCertSpkiSha256);
        Assert.Equal(outcome.NodeUid, node.Uid);
        Assert.Equal("127.0.0.1", node.EnrolledFrom);
        Assert.Equal("https://192.0.2.50:9443", node.Address);
        Assert.NotNull((await master.Registry.ListJoinTokensAsync()).Single().UsedAt);

        FileAgentCredentials credentials = new(new AgentHostOptions { StateDirectory = StateDir() }, NullLogger<FileAgentCredentials>.Instance);
        Assert.True(credentials.IsEnrolled);
        Assert.Equal("pc-office", credentials.NodeId);
        Assert.Equal(outcome.NodeUid, credentials.NodeUid);
        Assert.Equal(outcome.CertificateSpkiSha256, Spki.Sha256(credentials.ServerCertificate));
        Assert.Equal(_ca.SpkiSha256, Spki.Sha256(Assert.Single(credentials.TrustAnchors)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(StateDir(), FileAgentCredentials.KeyFileName)));
        }
    }

    [Fact]
    public async Task Join_TwiceWithForce_GetsANewUid_AndRefusesWithoutForce()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue first = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        JoinTokenIssue second = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        EnrollmentOutcome one = await Agent().JoinAsync(Join(master, first.Token));
        int requests = master.Requests;

        AgentEnrollmentException refused = await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().JoinAsync(Join(master, second.Token)));
        Assert.Equal(EnrollmentFailure.AlreadyEnrolled, refused.Failure);
        Assert.Equal(requests, master.Requests);

        EnrollmentOutcome two = await Agent().JoinAsync(Join(master, second.Token, force: true));
        Assert.NotEqual(one.NodeUid, two.NodeUid);
        Assert.NotEqual(one.CertificateSpkiSha256, two.CertificateSpkiSha256);
        NodeRecord node = Assert.Single(await master.Registry.ListAsync());
        Assert.Equal(two.NodeUid, node.Uid);
        Assert.Equal(two.CertificateSpkiSha256, node.PendingCertSpkiSha256);
    }

    [Fact]
    public async Task Join_WithForce_DropsThePreviousIdentitysRotationState()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue first = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        JoinTokenIssue second = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        await Agent().JoinAsync(Join(master, first.Token));

        // A rotation of the first identity was open (any stage); its files must not survive the re-enrollment (MN-094):
        // RotatingAgentCredentials would present or promote them at the next start.
        foreach (string file in new[] { AgentRotationStore.DocumentFileName, AgentRotationStore.PendingKeyFileName, AgentRotationStore.PendingCertificateFileName })
        {
            File.WriteAllText(Path.Combine(StateDir(), file), "left over");
        }

        EnrollmentOutcome two = await Agent().JoinAsync(Join(master, second.Token, force: true));

        Assert.False(File.Exists(Path.Combine(StateDir(), AgentRotationStore.DocumentFileName)));
        Assert.False(File.Exists(Path.Combine(StateDir(), AgentRotationStore.PendingKeyFileName)));
        Assert.False(File.Exists(Path.Combine(StateDir(), AgentRotationStore.PendingCertificateFileName)));
        var credentials = new FileAgentCredentials(new AgentHostOptions { StateDirectory = StateDir() }, NullLogger<FileAgentCredentials>.Instance);
        Assert.True(credentials.IsEnrolled);
        Assert.Equal(two.CertificateSpkiSha256, Spki.Sha256(credentials.ServerCertificate));
    }

    [Fact]
    public async Task BootstrapToken_OfAnOlderBundle_CannotReEnrollAnEnrolledBootstrapNode()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue older = await master.Registry.CreateJoinTokenAsync("dgx", TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap);
        JoinTokenIssue current = await master.Registry.CreateJoinTokenAsync("dgx", TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap);
        using HttpClient client = master.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, master, current.Token, "dgx")).Status);
        NodeRecord enrolled = (await master.Registry.GetAsync("dgx"))!;

        // The older token is unused and unexpired, but a bootstrap token pre-approves: it must not take over the node (MN-094).
        var (status, body) = await PostAsync(client, master, older.Token, "dgx");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(ErrorCodes.EnrollmentRefused, body, StringComparison.Ordinal);
        NodeRecord after = (await master.Registry.GetAsync("dgx"))!;
        Assert.Equal(enrolled.Uid, after.Uid);
        Assert.Equal(enrolled.CertSpkiSha256, after.CertSpkiSha256);
    }

    // ---- the CA hash: the token is only sent to the authenticated master ---------------------------------------------

    [Fact]
    public async Task Join_WithAnotherCaHash_NeverSendsTheToken()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        using CertificateAuthority other = CertificateAuthority.Create();

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().JoinAsync(Join(master, issue.Token, caHash: Spki.ToJoinHash(other.SpkiSha256))));

        Assert.Equal(EnrollmentFailure.ServerNotTrusted, ex.Failure);
        Assert.Equal(0, master.Requests);
        Assert.Equal(0, master.Registry.ConsumeCalls);
        Assert.Null((await master.Registry.ListJoinTokensAsync()).Single().UsedAt);
        Assert.False(Directory.Exists(StateDir()) && Directory.EnumerateFiles(StateDir()).Any());
    }

    [Fact]
    public async Task Join_ToAManInTheMiddleWhoseLeafCarriesTheRealCa_NeverSendsTheToken()
    {
        // The rogue copies the real CA (DER) into its own leaf's private extension: the hash matches, the signature does not.
        using CertificateAuthority rogue = CertificateAuthority.Create();
        X509Extension realCa = new(PkiDefaults.EmbeddedCaCertificateOid, _ca.Certificate.RawData, critical: false);
        SslStreamCertificateContext context = CustomListenerContext(rogue, [realCa]);
        await using Master master = await Master.StartAsync(DataDir, _ca, serverTls: () => PkiTls.CreateServerOptions(context, null));
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().JoinAsync(Join(master, issue.Token)));

        Assert.Equal(EnrollmentFailure.ServerNotTrusted, ex.Failure);
        Assert.Equal(0, master.Requests);
        Assert.Equal(0, master.Registry.ConsumeCalls);
    }

    [Fact]
    public async Task Join_ToALeafWithoutTheEmbeddedCa_AndNoChain_NeverSendsTheToken()
    {
        // A genuine leaf of the right CA, but without the D35 extension and with only the leaf in the TLS chain: the agent
        // cannot find a CA with hash H, so it refuses (on every platform) before sending anything.
        SslStreamCertificateContext context = CustomListenerContext(_ca, []);
        await using Master master = await Master.StartAsync(DataDir, _ca, serverTls: () => PkiTls.CreateServerOptions(context, null));
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().JoinAsync(Join(master, issue.Token)));

        Assert.Equal(EnrollmentFailure.ServerNotTrusted, ex.Failure);
        Assert.Equal(0, master.Requests);
    }

    [Fact]
    public void CaHash_LeafWithoutTheExtension_IsAcceptedOnlyThroughTheChainPath()
    {
        // Isolates the chain path of PeerValidator.ForEnrollListener(H): without the extension the CA must come from the
        // peer-supplied chain (SChannel sends it; OpenSSL drops a self-signed root, hence D35).
        using ECDsa key = PkiDefaults.CreateKey();
        using X509Certificate2 leaf = ListenerLeaf(_ca, key, []);
        PeerValidator validator = PeerValidator.ForEnrollListener(Spki.ToJoinHash(_ca.SpkiSha256));

        Assert.True(validator.Validate(leaf, [_ca.Certificate]).IsValid);
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(leaf).Failure);

        using CertificateAuthority other = CertificateAuthority.Create();
        Assert.Equal(PeerValidationFailure.UntrustedChain, validator.Validate(leaf, [other.Certificate]).Failure);
    }

    [Fact]
    public async Task EnrollListener_CertificateCarriesTheCaInItsExtension_NotInTheChain()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        X509Certificate2 listener = master.App.Services.GetRequiredService<EnrollListenerTls>().Certificate;

        X509Extension embedded = Assert.Single(listener.Extensions.Cast<X509Extension>(), e => e.Oid?.Value == PkiDefaults.EmbeddedCaCertificateOid);
        Assert.Equal(_ca.Certificate.RawData, embedded.RawData);
        Assert.Equal(Protocol.IssuedMasterSpiffeId, CertificateSans.GetSingleUri(listener));
        Assert.True(PeerValidator.ForEnrollListener(Spki.ToJoinHash(_ca.SpkiSha256)).Validate(listener).IsValid);
    }

    [Fact]
    public void EnrollListener_WithMasterCertificates_ServesThePersistedEnrollCertificate()
    {
        // W3 review MN-094: one enroll certificate (MasterCertificates, re-issued at half life and kept across restarts).
        using var master = MirbadLLM.Orchestration.Pki.MasterCertificates.LoadOrIssue(_ca, Path.Combine(DataDir, "pki"), ["localhost"]);
        var tls = new EnrollListenerTls(_ca, Options.Create(new EnrollmentOptions()), TimeProvider.System, NullLogger<EnrollListenerTls>.Instance, master);

        Assert.Equal(master.EnrollCredential.PublicCertificate.RawData, tls.Certificate.RawData);
        Assert.Same(master.EnrollCertificateContext, tls.CreateServerOptions().ServerCertificateContext);
    }

    // ---- tokens: reused / expired / wrong node / revoked, one generic answer ----------------------------------------

    [Fact]
    public async Task ReusedExpiredWrongNodeAndRevokedTokens_AreRefusedWithOneGenericAnswer()
    {
        ManualTimeProvider registryTime = new(DateTimeOffset.UtcNow);
        await using Master master = await Master.StartAsync(DataDir, _ca, registryTime: registryTime);
        using HttpClient client = master.CreateClient();

        JoinTokenIssue used = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, master, used.Token, "pc-office")).Status);
        JoinTokenIssue otherNode = await master.Registry.CreateJoinTokenAsync("pc-lab", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        await master.Registry.UpsertAsync(new NodeRecord { Id = "pc-gone", Uid = "00112233445566778899aabbccddeeff", Address = "https://10.0.0.9:9443" });
        JoinTokenIssue revoked = await master.Registry.CreateJoinTokenAsync("pc-gone", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        await master.Registry.RevokeAsync("pc-gone");
        JoinTokenIssue expired = await master.Registry.CreateJoinTokenAsync("pc-old", TimeSpan.FromMinutes(10), JoinTokenPurpose.Join);
        registryTime.Advance(TimeSpan.FromMinutes(11));

        (HttpStatusCode Status, string Body)[] answers =
        [
            await PostAsync(client, master, used.Token, "pc-office"),
            await PostAsync(client, master, expired.Token, "pc-old"),
            await PostAsync(client, master, otherNode.Token, "pc-office"),
            await PostAsync(client, master, revoked.Token, "pc-gone"),
            await PostAsync(client, master, "unknown-token", "pc-office"),
        ];

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Forbidden, a.Status));
        Assert.Single(answers.Select(a => a.Body).Distinct());
        AgentErrorResponse error = JsonSerializer.Deserialize<AgentErrorResponse>(answers[0].Body, AgentJson.Options)!;
        Assert.Equal(ErrorCodes.EnrollmentRefused, error.Error.Code);
        Assert.DoesNotContain("pc-lab", answers[2].Body, StringComparison.Ordinal);

        // The wrong-node attempt did not burn the token: it still enrolls its own node.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, master, otherNode.Token, "pc-lab")).Status);
        Assert.Null(await master.Registry.GetAsync("pc-old"));
    }

    [Fact]
    public async Task Join_WithAUsedToken_ReportsRefused()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        await Agent().JoinAsync(Join(master, issue.Token));

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().JoinAsync(Join(master, issue.Token, state: StateDir("second"))));

        Assert.Equal(EnrollmentFailure.Refused, ex.Failure);
        Assert.False(Directory.Exists(StateDir("second")) && Directory.EnumerateFiles(StateDir("second")).Any());
    }

    [Fact]
    public async Task ConcurrentEnrollsWithOneToken_IssueExactlyOneCertificate()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        EnrollmentService service = master.App.Services.GetRequiredService<EnrollmentService>();
        List<ECDsa> keys = [.. Enumerable.Range(0, 16).Select(_ => PkiDefaults.CreateKey())];
        try
        {
            EnrollmentResult[] results = await Task.WhenAll(keys.Select(key => Task.Run(() => service.EnrollAsync(new EnrollRequest
            {
                Token = issue.Token,
                NodeId = "pc-office",
                CsrPem = CertificateSigningRequests.CreatePem(key, "pc-office"),
            }, IPAddress.Loopback))));

            EnrollmentResult winner = Assert.Single(results, r => r.Status == EnrollmentStatus.Enrolled);
            Assert.All(results.Where(r => r != winner), r => Assert.Equal(EnrollmentStatus.Refused, r.Status));
            using X509Certificate2 issued = X509Certificate2.CreateFromPem(winner.Response!.AgentCertPem);
            Assert.Equal(Spki.Sha256(issued), (await master.Registry.GetAsync("pc-office"))!.PendingCertSpkiSha256);
        }
        finally
        {
            keys.ForEach(k => k.Dispose());
        }
    }

    [Theory]
    [InlineData(8.0, 1.0)]     // the Mac mini: a flat 8 GiB reserve left nothing for a model
    [InlineData(16.0, 2.0)]
    [InlineData(64.0, 8.0)]    // the PC keeps the old figure
    [InlineData(119.7, 8.0)]   // the DGX too
    [InlineData(4.0, 1.0)]
    public async Task ANewNode_StartsWithAReserveOfAnEighthOfItsMemory(double totalGiB, double reserveGiB)
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("mac-1", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        using ECDsa key = PkiDefaults.CreateKey();

        EnrollmentResult result = await master.App.Services.GetRequiredService<EnrollmentService>().EnrollAsync(new EnrollRequest
        {
            Token = issue.Token,
            NodeId = "mac-1",
            CsrPem = CertificateSigningRequests.CreatePem(key, "mac-1"),
            Capabilities = new NodeCapabilities
            {
                Protocol = Protocol.Range,
                AgentVersion = "1.0.0",
                Os = new NodeOsInfo("macos", "macOS 27"),
                Arch = "arm64",
                Memory = new NodeMemoryInfo(MemoryModel.Unified, totalGiB),
            },
        }, IPAddress.Loopback);

        Assert.Equal(EnrollmentStatus.Enrolled, result.Status);
        Assert.Equal(reserveGiB, (await master.Registry.GetAsync("mac-1"))!.MemoryReserveGiB);
        Assert.Equal(8, NodeRecord.DefaultMemoryReserveGiB(null));
    }

    // ---- the CSR: proof of possession, server-controlled content ----------------------------------------------------

    [Fact]
    public async Task CsrWithExtraExtensions_GetsOnlyTheMasterChosenOnes()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        using ECDsa key = PkiDefaults.CreateKey();
        CertificateRequest csr = new("CN=evil, O=attacker", key, HashAlgorithmName.SHA256);
        csr.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        csr.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
        SubjectAlternativeNameBuilder san = new();
        san.AddUri(new Uri(Protocol.MasterSpiffeId));
        san.AddDnsName("mirbad.example.com");
        csr.CertificateExtensions.Add(san.Build());
        csr.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.99999.1", [0x05, 0x00], false));
        csr.CertificateExtensions.Add(new X509Extension(PkiDefaults.EmbeddedCaCertificateOid, _ca.Certificate.RawData, false));

        EnrollmentResult result = await master.App.Services.GetRequiredService<EnrollmentService>().EnrollAsync(new EnrollRequest
        {
            Token = issue.Token,
            NodeId = "pc-office",
            CsrPem = csr.CreateSigningRequestPem(),
        }, IPAddress.Loopback);

        Assert.Equal(EnrollmentStatus.Enrolled, result.Status);
        using X509Certificate2 cert = X509Certificate2.CreateFromPem(result.Response!.AgentCertPem);
        Assert.Equal(
            MasterChosenExtensions,
            cert.Extensions.Cast<X509Extension>().Select(e => e.Oid!.Value!).Order(StringComparer.Ordinal));
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, cert.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
        Assert.Equal(
            AgentEkus,
            cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        SanEntries sans = CertificateSans.Read(cert)!;
        Assert.Equal([Protocol.IssuedNodeSpiffeId("pc-office")], sans.Uris);
        Assert.Empty(sans.DnsNames);
        Assert.Empty(sans.IpAddresses);
        Assert.Contains("CN=pc-office", cert.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", cert.Subject, StringComparison.Ordinal);
        Assert.True(cert.NotAfter.ToUniversalTime() - cert.NotBefore.ToUniversalTime() <= PkiDefaults.AgentCertificateLifetime + PkiDefaults.NotBeforeBackdate);
        Assert.Equal(Spki.Sha256(key), Spki.Sha256(cert));
        Assert.Equal(_ca.Certificate.SubjectName.RawData, cert.IssuerName.RawData);
    }

    [Fact]
    public async Task CsrWithoutProofOfPossession_IsRefused_AndDoesNotBurnTheToken()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        using HttpClient client = master.CreateClient();
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        using ECDsa key = PkiDefaults.CreateKey();
        string pem = CertificateSigningRequests.CreatePem(key, "pc-office");
        byte[] der = Convert.FromBase64String(pem[PemEncoding.Find(pem).Base64Data]);
        der[^1] ^= 0x01; // the last byte of the ECDSA signature's s value
        string tampered = PemEncoding.WriteString("CERTIFICATE REQUEST", der);

        (HttpStatusCode status, string body) = await PostAsync(client, master, issue.Token, "pc-office", tampered);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(ErrorCodes.EnrollmentRefused, body, StringComparison.Ordinal);
        Assert.Null((await master.Registry.ListJoinTokensAsync()).Single().UsedAt);
        Assert.Equal(0, master.Registry.ConsumeCalls);
    }

    // ---- listener hygiene: rate limit, TLS only, malformed bodies -----------------------------------------------------

    [Fact]
    public async Task RateLimit_FivePerMinutePerIp_ThenRateLimitedWithoutTouchingTokens()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca, configure: o => o.RateLimitPermits = MirbadLLM.Web.AdminPasswordGuard.PermitLimit);
        using HttpClient client = master.CreateClient();

        List<(HttpStatusCode Status, string Body)> answers = [];
        for (int i = 0; i < 6; i++)
        {
            answers.Add(await PostAsync(client, master, "guess-" + i, "pc-office"));
        }

        Assert.All(answers.Take(5), a => Assert.Equal(HttpStatusCode.Forbidden, a.Status));
        Assert.Equal(HttpStatusCode.TooManyRequests, answers[5].Status);
        Assert.Equal(ErrorCodes.RateLimited, JsonSerializer.Deserialize<AgentErrorResponse>(answers[5].Body, AgentJson.Options)!.Error.Code);
        Assert.Equal(5, master.Registry.ConsumeCalls);
    }

    [Fact]
    public async Task Enroll_OverPlainHttp_IsNotFound()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        using HttpClient plain = new();
        using ECDsa key = PkiDefaults.CreateKey();

        using HttpResponseMessage response = await plain.PostAsync(new Uri(master.Http, "/enroll"), JsonContent.Create(new EnrollRequest
        {
            Token = issue.Token,
            NodeId = "pc-office",
            CsrPem = CertificateSigningRequests.CreatePem(key, "pc-office"),
        }, options: AgentJson.Options));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, master.Registry.ConsumeCalls);
    }

    [Fact]
    public async Task EnrollListener_ServesOnlyPostEnroll()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        using HttpClient client = master.CreateClient();
        using HttpClient plain = new();

        using HttpResponseMessage onListener = await client.GetAsync(new Uri(master.Https, "/other"));
        using HttpResponseMessage getEnroll = await client.GetAsync(new Uri(master.Https, "/enroll"));
        using HttpResponseMessage onMainPort = await plain.GetAsync(new Uri(master.Http, "/other"));

        Assert.Equal(HttpStatusCode.NotFound, onListener.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, getEnroll.StatusCode);
        Assert.Equal(HttpStatusCode.OK, onMainPort.StatusCode);
    }

    [Fact]
    public async Task EnrollListener_OverHttp2_IsRecognised()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        using HttpClient client = master.CreateClient();
        using ECDsa key = PkiDefaults.CreateKey();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(master.Https, "/enroll"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(new EnrollRequest
            {
                Token = issue.Token,
                NodeId = "pc-office",
                CsrPem = CertificateSigningRequests.CreatePem(key, "pc-office"),
            }, options: AgentJson.Options),
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MalformedBodies_AreInvalidRequest_AndConsumeNothing()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        using HttpClient client = master.CreateClient();
        Uri enroll = new(master.Https, "/enroll");

        foreach (string body in new[] { "not json", "{}", """{"token":"t","nodeId":"Bad Id","csrPem":"x"}""", "null" })
        {
            using HttpResponseMessage response = await client.PostAsync(enroll, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Oversized: refused from Content-Length before the body is read. Kestrel then closes the connection instead of
        // draining the rest, so a client still uploading may see the reset rather than the 400; either way nothing ran.
        try
        {
            using HttpResponseMessage huge = await client.PostAsync(enroll, new StringContent(new string('x', (int)EnrollmentOptions.MaxRequestBodyBytes + 1), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Connection reset while uploading (see above).
        }

        Assert.Equal(0, master.Registry.ConsumeCalls);
    }
    [Fact]
    public async Task Join_WaitsForASlowHandshake_TheConnectTimeoutIsTcpOnly()
    {
        // The 3 s enroll connect timeout bounds the TCP connect; the TLS handshake (with the CA-hash chain check, slow on
        // a loaded Windows machine) counts against the 30 s total, so a reachable master that answers late still enrolls.
        TimeSpan delay = AgentCallPolicies.For(AgentCallClass.Enroll).Connect + TimeSpan.FromSeconds(1);
        await using Master master = await Master.StartAsync(DataDir, _ca, handshakeDelay: delay);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        EnrollmentOutcome outcome = await Agent().JoinAsync(Join(master, issue.Token));

        Assert.True(outcome.PendingApproval);
        Assert.True(EnrollmentStateFiles.HasIdentity(StateDir()));
    }

    [Fact]
    public async Task Join_ToAClosedPort_IsUnreachable_AndSendsNothing()
    {
        int port = TestPorts.Reserve(); // reserved for this process and never bound: the connect is refused
        JoinParameters parameters = new()
        {
            EnrollUrl = new Uri($"https://127.0.0.1:{port}/"),
            Token = "token",
            CaHash = Spki.ToJoinHash(_ca.SpkiSha256),
            NodeId = "pc-office",
            StateDirectory = StateDir(),
        };

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().JoinAsync(parameters));

        Assert.Equal(EnrollmentFailure.Unreachable, ex.Failure);
        Assert.False(Directory.Exists(StateDir()) && Directory.EnumerateFiles(StateDir()).Any());
    }

    // ---- the agent checks the answer before storing anything ----------------------------------------------------------

    [Fact]
    public async Task Join_RefusesAnAnswerWhoseCaIsNotTheExpectedOne()
    {
        using CertificateAuthority other = CertificateAuthority.Create();
        await using Master master = await Master.StartAsync(DataDir, _ca, fakeEnroll: async http =>
        {
            EnrollRequest request = (await http.Request.ReadFromJsonAsync<EnrollRequest>(AgentJson.Options))!;
            using IssuedCertificate cert = other.IssueAgentCertificate(request.CsrPem, request.NodeId, null);
            await http.Response.WriteAsJsonAsync(new EnrollResponse { CaCertPem = other.CertificatePem, AgentCertPem = cert.CertificatePem, NodeUid = "abc" }, AgentJson.Options);
        });

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().JoinAsync(Join(master, "token")));

        Assert.Equal(EnrollmentFailure.InvalidResponse, ex.Failure);
        Assert.False(Directory.Exists(StateDir()) && Directory.EnumerateFiles(StateDir()).Any());
    }

    [Fact]
    public async Task Join_RefusesACertificateForAnotherKeyOrNode()
    {
        foreach (bool otherKey in new[] { true, false })
        {
            await using Master master = await Master.StartAsync(DataDir, _ca, fakeEnroll: async http =>
            {
                EnrollRequest request = (await http.Request.ReadFromJsonAsync<EnrollRequest>(AgentJson.Options))!;
                using ECDsa foreign = PkiDefaults.CreateKey();
                string csr = otherKey ? CertificateSigningRequests.CreatePem(foreign, request.NodeId) : request.CsrPem;
                using IssuedCertificate cert = _ca.IssueAgentCertificate(csr, otherKey ? request.NodeId : "pc-other", null);
                await http.Response.WriteAsJsonAsync(new EnrollResponse { CaCertPem = _ca.CertificatePem, AgentCertPem = cert.CertificatePem, NodeUid = "abc" }, AgentJson.Options);
            });

            AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().JoinAsync(Join(master, "token")));

            Assert.Equal(EnrollmentFailure.InvalidResponse, ex.Failure);
            Assert.False(Directory.Exists(StateDir()) && Directory.EnumerateFiles(StateDir()).Any());
        }
    }

    // ---- bootstrap bundle ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bootstrap_BundleHasNoCaKey_EnrollsPreApproved_AndIsDeleted()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        using BootstrapBundleWriter writer = Writer(master);

        Assert.Equal(BootstrapBundleState.Written, await writer.EnsureAsync());
        string bundleText = File.ReadAllText(writer.BundlePath);
        Assert.DoesNotContain("PRIVATE KEY", bundleText, StringComparison.Ordinal);
        string caKeyBody = _ca.ExportPrivateKeyPem().Split('\n')[1].Trim();
        Assert.DoesNotContain(caKeyBody, bundleText, StringComparison.Ordinal);
        BootstrapBundle bundle = JsonSerializer.Deserialize<BootstrapBundle>(bundleText, AgentJson.Options)!;
        Assert.Equal("dgx", bundle.NodeId);
        Assert.Equal(_ca.SpkiSha256, bundle.CaSpkiSha256);
        Assert.Equal(_ca.CertificatePem, bundle.CaCertPem);
        Assert.Equal(JoinTokenPurpose.Bootstrap, (await master.Registry.ListJoinTokensAsync()).Single().Purpose);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(writer.BundlePath));
        }

        Assert.Equal(BootstrapBundleState.Current, await writer.EnsureAsync());
        Assert.Single(await master.Registry.ListJoinTokensAsync());

        EnrollmentOutcome? outcome = await Agent().BootstrapAsync(new BootstrapParameters
        {
            StateDirectory = StateDir(),
            BundleDirectory = BundleDir,
            ExpectedNodeId = "dgx",
        });

        Assert.NotNull(outcome);
        Assert.False(outcome.PendingApproval);
        Assert.False(File.Exists(writer.BundlePath));
        NodeRecord dgx = (await master.Registry.GetAsync("dgx"))!;
        Assert.True(dgx.Approved);
        Assert.Equal(outcome.CertificateSpkiSha256, dgx.CertSpkiSha256);
        Assert.Null(dgx.PendingCertSpkiSha256);
        Assert.Equal(outcome.NodeUid, File.ReadAllText(Path.Combine(StateDir(), FileAgentCredentials.NodeUidFileName)).Trim());

        Assert.Equal(BootstrapBundleState.Enrolled, await writer.EnsureAsync());
        Assert.False(File.Exists(writer.BundlePath));
        Assert.Null(await Agent().BootstrapAsync(new BootstrapParameters { StateDirectory = StateDir(), BundleDirectory = BundleDir }));
    }

    [Fact]
    public async Task Bootstrap_ListenerOfAnotherCa_NeverGetsTheToken_AndTheBundleIsKept()
    {
        using CertificateAuthority other = CertificateAuthority.Create();
        await using Master master = await Master.StartAsync(DataDir, other);
        using BootstrapBundleWriter writer = Writer(master, ca: _ca);
        await writer.EnsureAsync();

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().BootstrapAsync(new BootstrapParameters { StateDirectory = StateDir(), BundleDirectory = BundleDir }));

        Assert.Equal(EnrollmentFailure.ServerNotTrusted, ex.Failure);
        Assert.Equal(0, master.Requests);
        Assert.True(File.Exists(writer.BundlePath));
    }

    [Fact]
    public async Task Bootstrap_BundleWhoseCaDoesNotMatchItsHash_IsInvalid()
    {
        using CertificateAuthority other = CertificateAuthority.Create();
        Directory.CreateDirectory(BundleDir);
        File.WriteAllText(Path.Combine(BundleDir, BootstrapBundle.FileName), JsonSerializer.Serialize(new BootstrapBundle
        {
            NodeId = "dgx",
            Token = "t",
            CaCertPem = other.CertificatePem,
            CaSpkiSha256 = _ca.SpkiSha256,
            EnrollEndpoint = new Uri("https://127.0.0.1:1"),
        }, AgentJson.Options));

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().BootstrapAsync(new BootstrapParameters { StateDirectory = StateDir(), BundleDirectory = BundleDir }));

        Assert.Equal(EnrollmentFailure.BundleInvalid, ex.Failure);
    }

    [Fact]
    public async Task BundleWriter_RevokedBootstrapNode_WritesNothing()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        await master.Registry.UpsertAsync(new NodeRecord { Id = "dgx", Uid = "00112233445566778899aabbccddeeff", Address = "https://127.0.0.1:9443" });
        await master.Registry.RevokeAsync("dgx");
        using BootstrapBundleWriter writer = Writer(master);

        Assert.Equal(BootstrapBundleState.Revoked, await writer.EnsureAsync());
        Assert.False(File.Exists(writer.BundlePath));
        Assert.Empty(await master.Registry.ListJoinTokensAsync());
    }

    // ---- agent credentials and the CLI --------------------------------------------------------------------------------

    [Fact]
    public async Task Credentials_WithACertificateButNoNodeUid_AreNotEnrolled()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        await Agent().JoinAsync(Join(master, issue.Token));
        File.Delete(Path.Combine(StateDir(), FileAgentCredentials.NodeUidFileName));

        FileAgentCredentials credentials = new(new AgentHostOptions { StateDirectory = StateDir() }, NullLogger<FileAgentCredentials>.Instance);

        Assert.False(credentials.IsEnrolled);
    }

    [Fact]
    public async Task JoinCommand_Enrolls_AndNeverPrintsTheToken()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue issue = await master.Registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        using StringWriter output = new();
        using StringWriter error = new();

        int exit = await JoinCommand.RunAsync(
            ["--enroll-url", master.Https.ToString(), "--token", issue.Token, "--ca-hash", Spki.ToJoinHash(_ca.SpkiSha256),
             "--node-id", "pc-office", "--state-dir", StateDir(), "--address", "192.0.2.50"],
            output, error);

        Assert.True(exit == JoinCommand.ExitOk, $"join exited {exit}: {error}"); // the error names a transport timeout, if any
        Assert.Contains("pc-office", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(issue.Token, output.ToString() + error.ToString(), StringComparison.Ordinal);

        using StringWriter again = new();
        Assert.Equal(JoinCommand.ExitFailed, await JoinCommand.RunAsync(
            ["--enroll-url", master.Https.ToString(), "--token", issue.Token, "--ca-hash", Spki.ToJoinHash(_ca.SpkiSha256),
             "--node-id", "pc-office", "--state-dir", StateDir("other")],
            output, again));
        Assert.DoesNotContain(issue.Token, again.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JoinCommand_ParsesTheTokenFromTheEnvironment_AndRejectsBadArguments()
    {
        string hash = Spki.ToJoinHash(_ca.SpkiSha256);

        Assert.True(JoinCommand.TryParse(["--enroll-url", "https://m:8443", "--ca-hash", hash, "--node-id", "n1"], "env-token", out JoinParameters? parsed, out _));
        Assert.Equal("env-token", parsed!.Token);
        Assert.Equal(JoinCommand.DefaultStateDirectory, parsed.StateDirectory);
        Assert.DoesNotContain("env-token", parsed.ToString(), StringComparison.Ordinal);

        Assert.False(JoinCommand.TryParse(["--enroll-url", "http://m:8443", "--token", "t", "--ca-hash", hash, "--node-id", "n1"], null, out _, out _));
        Assert.False(JoinCommand.TryParse(["--enroll-url", "https://m:8443", "--token", "t", "--ca-hash", "sha256:nope", "--node-id", "n1"], null, out _, out _));
        Assert.False(JoinCommand.TryParse(["--enroll-url", "https://m:8443", "--ca-hash", hash, "--node-id", "n1"], null, out _, out string? missing));
        Assert.Contains("--token", missing, StringComparison.Ordinal);
        Assert.False(JoinCommand.TryParse(["--token", "secret-value", "--bogus"], null, out _, out string? unknown));
        Assert.DoesNotContain("secret-value", unknown, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private BootstrapBundleWriter Writer(Master master, CertificateAuthority? ca = null) => new(
        master.Registry,
        ca ?? _ca,
        Options.Create(new EnrollmentOptions { BootstrapDirectory = BundleDir, BootstrapEnrollEndpoint = master.Https }),
        Options.Create(new RegistryMigrationOptions()),
        TimeProvider.System,
        NullLogger<BootstrapBundleWriter>.Instance);

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient client, Master master, string token, string nodeId, string? csrPem = null)
    {
        using ECDsa key = PkiDefaults.CreateKey();
        using HttpResponseMessage response = await client.PostAsync(new Uri(master.Https, "/enroll"), JsonContent.Create(new EnrollRequest
        {
            Token = token,
            NodeId = nodeId,
            CsrPem = csrPem ?? CertificateSigningRequests.CreatePem(key, nodeId),
        }, options: AgentJson.Options));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A listener leaf (SAN master, serverAuth) signed by <paramref name="issuer"/> with the given extra extensions.</summary>
    private static X509Certificate2 ListenerLeaf(CertificateAuthority issuer, ECDsa key, IEnumerable<X509Extension> extra) =>
        PkiTestSupport.SignCustom(issuer.Certificate, issuer.ExportPrivateKeyPem(), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1),
            [Protocol.MasterSpiffeId], [PkiDefaults.ServerAuthOid], subjectKey: key, extraExtensions: extra);

    private static SslStreamCertificateContext CustomListenerContext(CertificateAuthority issuer, IEnumerable<X509Extension> extra)
    {
        using ECDsa key = PkiDefaults.CreateKey();
        using X509Certificate2 leaf = ListenerLeaf(issuer, key, extra);
        using X509Certificate2 withKey = leaf.CopyWithPrivateKey(key);
        return PkiTls.CreateContext(TlsCredentials.Create(withKey));
    }

    /// <summary>Counts token consumptions (the "was the token ever sent" assertion on the server side).</summary>
    private sealed class RecordingRegistry(JsonNodeRegistry inner) : INodeRegistry
    {
        private int _consumeCalls;

        public int ConsumeCalls => Volatile.Read(ref _consumeCalls);

        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);

        public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => inner.UpsertAsync(node, ct);

        public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);

        public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => inner.RevokeAsync(id, ct);

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);

        public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
            inner.CreateJoinTokenAsync(nodeId, lifetime, purpose, ct);

        public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _consumeCalls);
            return inner.TryConsumeJoinTokenAsync(token, nodeId, ct);
        }

        public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) => inner.ListJoinTokensAsync(ct);

        public Task<int> ExpireUnusedJoinTokensAsync(string nodeId, CancellationToken ct = default) => inner.ExpireUnusedJoinTokensAsync(nodeId, ct);

        public Task<int> PruneJoinTokensAsync(TimeSpan olderThan, CancellationToken ct = default) => inner.PruneJoinTokensAsync(olderThan, ct);
    }

    /// <summary>The master's enroll listener on loopback: real TLS endpoint + a plain-HTTP endpoint, as MN-040 will compose it.</summary>
    private sealed class Master : IAsyncDisposable
    {
        private readonly JsonNodeRegistry _inner;
        private int _requests;

        private Master(JsonNodeRegistry inner, WebApplication app)
        {
            _inner = inner;
            Registry = new RecordingRegistry(inner);
            App = app;
        }

        public RecordingRegistry Registry { get; }

        public WebApplication App { get; private set; }

        public Uri Https { get; private set; } = null!;

        public Uri Http { get; private set; } = null!;

        /// <summary>HTTP requests that reached the application (i.e. after a completed TLS handshake).</summary>
        public int Requests => Volatile.Read(ref _requests);

        private string CaPin { get; set; } = "";

        public static async Task<Master> StartAsync(
            string dataDir,
            CertificateAuthority ca,
            Action<EnrollmentOptions>? configure = null,
            Func<SslServerAuthenticationOptions>? serverTls = null,
            RequestDelegate? fakeEnroll = null,
            TimeProvider? registryTime = null,
            TimeSpan handshakeDelay = default)
        {
            JsonNodeRegistry inner = new(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), registryTime ?? TimeProvider.System);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls();
            Master master = new(inner, null!) { CaPin = ca.SpkiSha256 };
            builder.Services.AddSingleton(ca);
            builder.Services.AddSingleton<INodeRegistry>(master.Registry);
            builder.Services.AddMirbadLLMEnrollment(builder.Configuration);
            builder.Services.PostConfigure<EnrollmentOptions>(o =>
            {
                o.BootstrapEnabled = false;
                o.RateLimitPermits = 100;
                configure?.Invoke(o);
            });
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, 0, listen =>
                {
                    if (handshakeDelay > TimeSpan.Zero)
                    {
                        // A slow handshake: the TCP connection is accepted at once, the TLS answer comes late.
                        listen.Use(next => async connection =>
                        {
                            await Task.Delay(handshakeDelay);
                            await next(connection);
                        });
                    }

                    if (serverTls is null)
                    {
                        kestrel.ApplicationServices.GetRequiredService<EnrollListenerTls>().ConfigureEndpoint(listen);
                    }
                    else
                    {
                        listen.Protocols = HttpProtocols.Http1AndHttp2;
                        listen.UseHttps(new TlsHandshakeCallbackOptions { OnConnection = _ => ValueTask.FromResult(serverTls()) });
                    }
                });
                kestrel.Listen(IPAddress.Loopback, 0);
            });

            WebApplication app = builder.Build();
            master.App = app;
            app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref master._requests);
                await next(context);
            });
            app.UseEnrollListenerGate();
            app.MapGet("/other", () => "other");
            if (fakeEnroll is null)
            {
                app.MapEnrollEndpoint();
            }
            else
            {
                app.MapPost("/enroll", fakeEnroll);
            }

            await app.StartAsync();
            foreach (string address in app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses)
            {
                Uri uri = new(address);
                if (uri.Scheme == Uri.UriSchemeHttps)
                {
                    master.Https = new Uri($"https://127.0.0.1:{uri.Port}/");
                }
                else
                {
                    master.Http = new Uri($"http://127.0.0.1:{uri.Port}/");
                }
            }

            return master;
        }

        /// <summary>A client that trusts the listener the way a joining agent does (CA hash).</summary>
        public HttpClient CreateClient() => new(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = PkiTls.CreateClientOptions(null, new PeerValidatorSource(PeerValidator.ForEnrollListener(CaPin)), "localhost"),
        });

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            _inner.Dispose();
        }
    }
}
