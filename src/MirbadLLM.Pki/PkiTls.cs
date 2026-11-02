using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace MirbadLLM.Pki;

/// <summary>
/// The TLS settings every mirbadllm mTLS endpoint shares, in one place (added by MN-090 from the MN-010 spike, so
/// MN-014, MN-021, MN-035 and MN-036 do not each re-type them). Both builders take a <see cref="PeerValidatorSource"/>,
/// so pins and trust can change between handshakes.
/// <para>Certificates passed in must come from <see cref="TlsCredentials"/> / <see cref="IssuedCredential"/> (PKCS#12
/// round trip): an in-memory key is refused by SChannel on Windows (<c>0x8009030D</c>, MN-010 finding 4).</para>
/// </summary>
public static class PkiTls
{
    /// <summary>
    /// SNI sent to agents. Agents are addressed by IP; identity is the SAN URI + pin, so the name only has to be constant.
    /// </summary>
    public const string AgentTargetHost = "mirbadllm-agent";

    /// <summary>TLS 1.3 (negotiated on Windows and Linux in MN-010) and 1.2 (kept for macOS agents).</summary>
    public const SslProtocols Protocols = SslProtocols.Tls13 | SslProtocols.Tls12;

    /// <summary>
    /// Client options for the master's agent handlers (<c>SocketsHttpHandler.SslOptions</c>, design §7.2) and for any raw
    /// <see cref="SslStream"/> client (the joining agent, MN-035, passes a null certificate):
    /// <list type="bullet">
    /// <item><c>ClientCertificateContext</c> rather than <c>ClientCertificates</c>: always sent, no issuer filtering,
    /// chain built once;</item>
    /// <item><c>AllowTlsResume = false</c>: on Windows a new handler otherwise presented another handler's client
    /// certificate (a cached SChannel credential, not session resumption), which breaks the §10.2 "fresh handler proves
    /// the new pin" step;</item>
    /// <item>custom validation only (platform <see cref="SslPolicyErrors"/> ignored), no revocation lookups.</item>
    /// </list>
    /// The caller still sets on the handler: <c>UseProxy = false</c> (an <c>HTTPS_PROXY</c> in the master's environment
    /// must never carry agent traffic), <c>UseCookies = false</c>, <c>AllowAutoRedirect = false</c>,
    /// <c>AutomaticDecompression = None</c>, the §7.2 pool/ping/connect settings, and per request
    /// <c>Version20</c> + <c>RequestVersionExact</c>. The handler negotiates ALPN itself.
    /// </summary>
    public static SslClientAuthenticationOptions CreateClientOptions(
        SslStreamCertificateContext? clientCertificate,
        PeerValidatorSource serverValidator,
        string targetHost = AgentTargetHost)
    {
        ArgumentNullException.ThrowIfNull(serverValidator);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        return new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ClientCertificateContext = clientCertificate,
            RemoteCertificateValidationCallback = serverValidator.ValidateRemoteCertificate,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = Protocols,
            AllowTlsResume = false,
        };
    }

    /// <summary>
    /// Server options for the agent's mTLS listener (MN-014) and the master's enroll listener (MN-035, with a null
    /// <paramref name="clientValidator"/>: no client certificate is asked for). Built per handshake, e.g. from Kestrel's
    /// <c>ListenOptions.UseHttps(new TlsHandshakeCallbackOptions { OnConnection = _ =&gt; ValueTask.FromResult(
    /// PkiTls.CreateServerOptions(currentContext, validators)) })</c>: <paramref name="certificate"/> is the current
    /// certificate context (swap it for hot rotation, §10.2 step 3; cache the context rather than rebuilding it per
    /// handshake), and the validator is read from the source on every handshake. With a client validator the client
    /// certificate is required. ALPN offers h2 then http/1.1 (the agent endpoint is <c>Http1AndHttp2</c>, §7.2).
    /// The agent also raises Kestrel's <c>Limits.MaxRequestBodySize</c> to at least 64 MiB plus headroom (default ≈ 28.6 MiB
    /// would 413 bodies the master accepts) — a Kestrel setting, not a TLS one.
    /// </summary>
    public static SslServerAuthenticationOptions CreateServerOptions(SslStreamCertificateContext certificate, PeerValidatorSource? clientValidator)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new SslServerAuthenticationOptions
        {
            ServerCertificateContext = certificate,
            ClientCertificateRequired = clientValidator is not null,
            RemoteCertificateValidationCallback = clientValidator is null ? null : clientValidator.ValidateRemoteCertificate,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = Protocols,
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
        };
    }

    /// <summary>
    /// A certificate context for <see cref="CreateClientOptions"/> / <see cref="CreateServerOptions"/>: offline (no AIA
    /// fetches), with <paramref name="intermediates"/> sent after the leaf. mirbadllm chains are leaf → root, so this is
    /// normally empty; a self-signed root is never needed in the chain (the enroll-listener leaf carries the CA in its
    /// private extension instead, MN-004).
    /// </summary>
    public static SslStreamCertificateContext CreateContext(X509Certificate2 certificateWithKey, X509Certificate2Collection? intermediates = null)
    {
        ArgumentNullException.ThrowIfNull(certificateWithKey);
        if (!certificateWithKey.HasPrivateKey)
        {
            throw new ArgumentException("The certificate has no private key.", nameof(certificateWithKey));
        }

        return SslStreamCertificateContext.Create(certificateWithKey, intermediates, offline: true);
    }
}
