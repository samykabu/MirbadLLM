using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;

namespace MirbadLLM.Web.Enrollment;

/// <summary>
/// TLS of the master's enroll listener (design §3.2, D35): a server certificate issued by the master CA with SAN
/// <c>spiffe://mirbadllm/master</c> plus <see cref="EnrollmentOptions.ListenerAddresses"/>, which carries the CA in its
/// private extension, so the listener does <b>not</b> put the CA in its TLS chain. No client certificate is asked for
/// (<see cref="PkiTls.CreateServerOptions"/> with a null client validator).
/// <para>The certificate context is built once and cached; it is re-issued at half its lifetime (design §10.2) on the
/// next handshake. Replaced certificates are not disposed: open connections may still use them.</para>
/// </summary>
public sealed class EnrollListenerTls
{
    /// <summary>TLS handshake timeout of the listener.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly CertificateAuthority _ca;
    private readonly IReadOnlyList<string> _addresses;
    private readonly TimeProvider _time;
    private readonly ILogger<EnrollListenerTls> _logger;
    private readonly MasterCertificates? _master;
    private readonly Lock _lock = new();
    private State? _state;

    /// <param name="ca">The master CA (issues the listener certificate when <paramref name="master"/> is null).</param>
    /// <param name="options">Enrollment options (listener addresses).</param>
    /// <param name="time">Clock for the half-life re-issue.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="master">
    /// The master's persisted certificates (MN-036). When registered, the listener serves
    /// <see cref="MasterCertificates.EnrollCertificateContext"/> (re-issued at half life by the rotation service and kept
    /// across restarts) instead of issuing its own, so there is one enroll certificate (W3 review MN-094). It must be
    /// created with the same listener addresses (<see cref="EnrollmentOptions.EffectiveListenerAddresses"/>).
    /// </param>
    public EnrollListenerTls(CertificateAuthority ca, IOptions<EnrollmentOptions> options, TimeProvider time, ILogger<EnrollListenerTls> logger, MasterCertificates? master = null)
    {
        ArgumentNullException.ThrowIfNull(ca);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _ca = ca;
        _addresses = options.Value.EffectiveListenerAddresses;
        _time = time;
        _logger = logger;
        _master = master;
    }

    /// <summary>The listener certificate in use (public part), issuing it first if needed.</summary>
    public X509Certificate2 Certificate => _master?.EnrollCredential.PublicCertificate ?? Current().Certificate;

    /// <summary>Server options for one handshake (Kestrel <c>TlsHandshakeCallbackOptions.OnConnection</c>).</summary>
    public SslServerAuthenticationOptions CreateServerOptions() =>
        PkiTls.CreateServerOptions(_master?.EnrollCertificateContext ?? Current().Context, clientValidator: null);

    /// <summary>
    /// Configures a Kestrel endpoint as the enroll listener: HTTP/1.1 and HTTP/2, TLS options built per handshake by
    /// <see cref="CreateServerOptions"/>, connections tagged as enroll-listener connections. MN-040 calls it from <c>kestrel.ListenAnyIP(options.Port, tls.ConfigureEndpoint)</c>.
    /// </summary>
    public void ConfigureEndpoint(ListenOptions listen)
    {
        ArgumentNullException.ThrowIfNull(listen);
        listen.Protocols = HttpProtocols.Http1AndHttp2;

        // Tags every connection of this endpoint, so /enroll answers only here and EnrollEndpoints.UseEnrollListenerGate
        // keeps every other route off this port.
        listen.Use(next => connection =>
        {
            connection.Items[EnrollEndpoints.ListenerConnectionMarker] = true;
            return next(connection);
        });
        listen.UseHttps(new TlsHandshakeCallbackOptions
        {
            HandshakeTimeout = HandshakeTimeout,
            OnConnection = _ => ValueTask.FromResult(CreateServerOptions()),
        });
    }

    private State Current()
    {
        var now = _time.GetUtcNow();
        var state = Volatile.Read(ref _state);
        if (state is not null && now < state.ReissueAt)
        {
            return state;
        }

        lock (_lock)
        {
            state = _state;
            if (state is not null && now < state.ReissueAt)
            {
                return state;
            }

            using var credential = _ca.IssueEnrollServerCertificate(_addresses);
            var tls = credential.CreateTlsCertificate();
            var context = PkiTls.CreateContext(tls);
            var publicCertificate = X509CertificateLoader.LoadCertificate(credential.PublicCertificate.RawData);
            var next = new State(publicCertificate, context, PkiDefaults.MasterReissueDue(publicCertificate));
            Volatile.Write(ref _state, next);
            EnrollmentLog.ListenerCertificateIssued(_logger, credential.SpkiSha256, credential.NotAfter);
            return next;
        }
    }

    private sealed record State(X509Certificate2 Certificate, SslStreamCertificateContext Context, DateTimeOffset ReissueAt);
}
