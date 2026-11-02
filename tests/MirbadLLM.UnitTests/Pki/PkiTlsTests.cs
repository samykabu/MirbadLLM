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
/// MN-090: the shared TLS option builders (<see cref="PkiTls"/>) in the production shape over a real loopback handshake,
/// and <see cref="PeerValidatorSource"/>: a validator swapped between handshakes applies to the next connection while the
/// <b>same</b> options objects stay in use (pending pin on the master, new trust anchor on the agent, design §10.2).
/// </summary>
public sealed class PkiTlsTests : IDisposable
{
    private readonly CertificateAuthority _ca = CertificateAuthority.Create();
    private readonly TrustAnchorSet _trust;
    private readonly ECDsa _agentKey;
    private readonly IssuedCertificate _agentCert;
    private readonly X509Certificate2 _agentTls;
    private readonly IssuedCredential _master;
    private readonly X509Certificate2 _masterTls;

    public PkiTlsTests()
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
    public void ClientOptions_CarryTheSpikeSettings()
    {
        var source = new PeerValidatorSource(PeerValidator.ForAgent("dgx", _trust, [_agentCert.SpkiSha256]));

        var options = PkiTls.CreateClientOptions(PkiTls.CreateContext(_masterTls), source);

        Assert.False(options.AllowTlsResume);
        Assert.Equal(PkiTls.AgentTargetHost, options.TargetHost);
        Assert.NotNull(options.ClientCertificateContext);
        Assert.Null(options.ClientCertificates);
        Assert.Equal(X509RevocationMode.NoCheck, options.CertificateRevocationCheckMode);
        Assert.Equal(SslProtocols.Tls13 | SslProtocols.Tls12, options.EnabledSslProtocols);
    }

    [Fact]
    public void ServerOptions_RequireAClientCertificate_OnlyWithAValidator_AndOfferH2First()
    {
        var context = PkiTls.CreateContext(_agentTls);

        var agent = PkiTls.CreateServerOptions(context, new PeerValidatorSource(PeerValidator.ForMasterClient(_trust)));
        var enroll = PkiTls.CreateServerOptions(context, clientValidator: null);

        Assert.True(agent.ClientCertificateRequired);
        Assert.False(enroll.ClientCertificateRequired);
        Assert.Null(enroll.RemoteCertificateValidationCallback);
        Assert.Equal([SslApplicationProtocol.Http2, SslApplicationProtocol.Http11], agent.ApplicationProtocols);
        Assert.Throws<ArgumentException>(() => PkiTls.CreateContext(_ca.Certificate)); // public certificate, no key
    }

    [Fact]
    public async Task ProductionShape_MutualTls_Succeeds_AndTheResultObserverSeesTheAcceptance()
    {
        List<PeerValidationResult> seen = [];
        var masterSide = new PeerValidatorSource(PeerValidator.ForAgent("dgx", _trust, [_agentCert.SpkiSha256]).WithResultObserver(seen.Add));
        var agentSide = new PeerValidatorSource(PeerValidator.ForMasterClient(_trust));

        (bool serverOk, bool clientOk) = await HandshakeAsync(
            PkiTls.CreateServerOptions(PkiTls.CreateContext(_agentTls), agentSide),
            PkiTls.CreateClientOptions(PkiTls.CreateContext(_masterTls), masterSide));

        Assert.True(serverOk);
        Assert.True(clientOk);
        Assert.Contains(seen, r => r.IsValid && r.SpkiSha256 == _agentCert.SpkiSha256);
    }

    [Fact]
    public async Task MasterSide_APendingPinSetOnTheSource_AppliesToTheNextHandshake_WithTheSameOptions()
    {
        // §10.2 steps 2–3: the agent now serves a certificate with a new key; the master's handler (same options object,
        // as a pooled SocketsHttpHandler keeps them) refuses it until the pending pin is added, then accepts it.
        string csr = PkiTestSupport.NewCsr(out ECDsa newKey);
        using ECDsa ownedKey = newKey;
        using IssuedCertificate rotated = _ca.IssueAgentCertificate(csr, "dgx", ["127.0.0.1"]);
        using X509Certificate2 rotatedTls = TlsCredentials.FromPem(rotated.CertificatePem, newKey.ExportPkcs8PrivateKeyPem());
        var masterSide = new PeerValidatorSource(PeerValidator.ForAgent("dgx", _trust, [_agentCert.SpkiSha256]));
        var client = PkiTls.CreateClientOptions(PkiTls.CreateContext(_masterTls), masterSide);
        var server = PkiTls.CreateServerOptions(PkiTls.CreateContext(rotatedTls), new PeerValidatorSource(PeerValidator.ForMasterClient(_trust)));

        (_, bool before) = await HandshakeAsync(server, client);
        masterSide.Set(PeerValidator.ForAgent("dgx", _trust, [_agentCert.SpkiSha256, rotated.SpkiSha256]));
        (bool serverOk, bool after) = await HandshakeAsync(server, client);

        Assert.False(before);
        Assert.True(after);
        Assert.True(serverOk);
    }

    [Fact]
    public async Task AgentSide_ANewTrustAnchorSetOnTheSource_AppliesToTheNextHandshake_WithTheSameOptions()
    {
        // CA rotation (§10.2): PUT /pki/trust adds the new CA; a master certificate from it is accepted from the next
        // handshake on, without restarting the listener.
        using CertificateAuthority next = CertificateAuthority.Create();
        using IssuedCredential nextMaster = next.IssueMasterClientCertificate();
        using X509Certificate2 nextMasterTls = nextMaster.CreateTlsCertificate();
        using TrustAnchorSet both = TrustAnchorSet.Create([_ca.Certificate, next.Certificate]);
        var agentSide = new PeerValidatorSource(PeerValidator.ForMasterClient(_trust));
        var server = PkiTls.CreateServerOptions(PkiTls.CreateContext(_agentTls), agentSide);
        var client = PkiTls.CreateClientOptions(
            PkiTls.CreateContext(nextMasterTls), new PeerValidatorSource(PeerValidator.ForAgent("dgx", _trust, [_agentCert.SpkiSha256])));

        (bool refused, _) = await HandshakeAsync(server, client);
        agentSide.Set(PeerValidator.ForMasterClient(both));
        (bool accepted, bool clientOk) = await HandshakeAsync(server, client);

        Assert.False(refused);
        Assert.True(accepted);
        Assert.True(clientOk);
    }

    private static async Task<(bool ServerOk, bool ClientOk)> HandshakeAsync(SslServerAuthenticationOptions server, SslClientAuthenticationOptions client)
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
                await ssl.AuthenticateAsServerAsync(server, timeout.Token);
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
        using (TcpClient tcp = new())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await using SslStream ssl = new(tcp.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(client, timeout.Token);
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
