using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Pki;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Pki;

/// <summary>
/// MN-004: the validators and TLS credentials in a real <see cref="SslStream"/> handshake over loopback, so the callback
/// shapes and the platform key handling (SChannel on Windows, OpenSSL on Linux) are exercised, not only the pure checks.
/// </summary>
public sealed class TlsHandshakeTests : IDisposable
{
    private readonly CertificateAuthority _ca = CertificateAuthority.Create();
    private readonly TrustAnchorSet _trust;
    private readonly ECDsa _agentKey;
    private readonly IssuedCertificate _agentCert;
    private readonly X509Certificate2 _agentTls;
    private readonly IssuedCredential _master;
    private readonly X509Certificate2 _masterTls;

    public TlsHandshakeTests()
    {
        _trust = TrustAnchorSet.Create([_ca.Certificate]);
        string csr = PkiTestSupport.NewCsr(out _agentKey);
        _agentCert = _ca.IssueAgentCertificate(csr, "dgx", ["127.0.0.1"]);
        _agentTls = TlsCredentials.FromPem(_agentCert.CertificatePem, _agentKey.ExportPkcs8PrivateKeyPem());
        _master = _ca.IssueMasterClientCertificate();
        _masterTls = _master.CreateTlsCertificate();
    }

    public void Dispose()
    {
        _agentTls.Dispose();
        _agentCert.Dispose();
        _agentKey.Dispose();
        _masterTls.Dispose();
        _master.Dispose();
        _trust.Dispose();
        _ca.Dispose();
    }

    [Fact]
    public async Task MutualTls_MasterAndAgentAcceptEachOther()
    {
        PeerValidator agentSide = PeerValidator.ForMasterClient(_trust);
        PeerValidator masterSide = PeerValidator.ForAgent("dgx", _trust, [_agentCert.SpkiSha256]);

        (bool serverOk, bool clientOk) = await HandshakeAsync(
            SslStreamCertificateContext.Create(_agentTls, null, offline: true), agentSide, _masterTls, masterSide);

        Assert.True(serverOk);
        Assert.True(clientOk);
    }

    [Fact]
    public async Task MutualTls_AgentRefusesAClientFromAnotherCa()
    {
        using CertificateAuthority rogue = CertificateAuthority.Create();
        using IssuedCredential rogueMaster = rogue.IssueMasterClientCertificate();
        using X509Certificate2 rogueTls = rogueMaster.CreateTlsCertificate();
        List<PeerValidationResult> rejected = [];
        PeerValidator agentSide = PeerValidator.ForMasterClient(_trust).WithRejectionObserver(rejected.Add);
        PeerValidator masterSide = PeerValidator.ForAgent("dgx", _trust, [_agentCert.SpkiSha256]);

        (bool serverOk, bool clientOk) = await HandshakeAsync(
            SslStreamCertificateContext.Create(_agentTls, null, offline: true), agentSide, rogueTls, masterSide);

        Assert.False(serverOk && clientOk);
        Assert.Contains(rejected, r => r.Failure == PeerValidationFailure.UntrustedChain);
    }

    [Fact]
    public async Task MutualTls_MasterRefusesAnUnpinnedAgent()
    {
        using ECDsa other = PkiDefaults.CreateKey();
        PeerValidator agentSide = PeerValidator.ForMasterClient(_trust);
        PeerValidator masterSide = PeerValidator.ForAgent("dgx", _trust, [Spki.Sha256(other)]);

        (bool serverOk, bool clientOk) = await HandshakeAsync(
            SslStreamCertificateContext.Create(_agentTls, null, offline: true), agentSide, _masterTls, masterSide);

        Assert.False(clientOk);
        _ = serverOk;
    }

    [Fact]
    public async Task EnrollListener_JoiningAgentFindsThePinnedCaInTheServerChain()
    {
        // The enroll listener sends its CA in the chain; the joining agent knows only the CA hash (design §3.2).
        using IssuedCredential enroll = _ca.IssueEnrollServerCertificate(["localhost"]);
        using X509Certificate2 enrollTls = enroll.CreateTlsCertificate();
        X509Certificate2Collection chain = [_ca.Certificate];
        PeerValidator joining = PeerValidator.ForEnrollListener(Spki.ToJoinHash(_ca.SpkiSha256));

        (bool serverOk, bool clientOk) = await HandshakeAsync(
            SslStreamCertificateContext.Create(enrollTls, chain, offline: true), serverValidator: null, clientCertificate: null, joining);

        Assert.True(serverOk);
        Assert.True(clientOk);
    }

    [Fact]
    public async Task EnrollListener_JoiningAgentNeedsNoChainFromTheServer()
    {
        // The server sends only its leaf (as OpenSSL does for a self-signed root); the CA embedded in the leaf suffices.
        using IssuedCredential enroll = _ca.IssueEnrollServerCertificate(["localhost"]);
        using X509Certificate2 enrollTls = enroll.CreateTlsCertificate();
        PeerValidator joining = PeerValidator.ForEnrollListener(Spki.ToJoinHash(_ca.SpkiSha256));

        (bool serverOk, bool clientOk) = await HandshakeAsync(
            SslStreamCertificateContext.Create(enrollTls, null, offline: true), serverValidator: null, clientCertificate: null, joining);

        Assert.True(serverOk);
        Assert.True(clientOk);
    }

    [Fact]
    public async Task EnrollListener_JoiningAgentRefusesAnotherCa()
    {
        using CertificateAuthority rogue = CertificateAuthority.Create();
        using IssuedCredential enroll = rogue.IssueEnrollServerCertificate(["localhost"]);
        using X509Certificate2 enrollTls = enroll.CreateTlsCertificate();
        PeerValidator joining = PeerValidator.ForEnrollListener(Spki.ToJoinHash(_ca.SpkiSha256));

        (_, bool clientOk) = await HandshakeAsync(
            SslStreamCertificateContext.Create(enrollTls, [rogue.Certificate], offline: true), serverValidator: null, clientCertificate: null, joining);

        Assert.False(clientOk);
    }

    private static async Task<(bool ServerOk, bool ClientOk)> HandshakeAsync(
        SslStreamCertificateContext serverContext,
        PeerValidator? serverValidator,
        X509Certificate2? clientCertificate,
        PeerValidator clientValidator)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<bool> serverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            await using SslStream ssl = new(accepted.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = serverContext,
                    ClientCertificateRequired = serverValidator is not null,
                    RemoteCertificateValidationCallback = serverValidator is null ? null : serverValidator.ValidateRemoteCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token);
                byte[] buffer = new byte[1];
                int read = await ssl.ReadAsync(buffer, timeout.Token);
                if (read == 1)
                {
                    await ssl.WriteAsync(buffer, timeout.Token);
                }

                return read == 1;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                return false;
            }
        });

        bool clientOk;
        using (TcpClient client = new())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await using SslStream ssl = new(client.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ClientCertificates = clientCertificate is null ? null : [clientCertificate],
                    RemoteCertificateValidationCallback = clientValidator.ValidateRemoteCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token);
                await ssl.WriteAsync(new byte[] { 42 }, timeout.Token);
                byte[] echo = new byte[1];
                clientOk = await ssl.ReadAsync(echo, timeout.Token) == 1 && echo[0] == 42;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                clientOk = false;
            }
        }

        bool serverOk = await serverTask;
        return (serverOk, clientOk);
    }
}
