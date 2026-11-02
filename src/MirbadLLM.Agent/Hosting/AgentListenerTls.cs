using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// TLS state of the agent's mTLS listener (design §4.2 "Agent (server)", §7.2, §10.2), consulted on <b>every</b>
/// handshake:
/// <list type="bullet">
/// <item>the server certificate context is built once per <see cref="IAgentCredentials.Version"/> and cached, so a
/// rotation (§10.2 step 3) is a swap: new connections get the new certificate, established ones keep theirs;</item>
/// <item>masters are validated by <see cref="PeerValidator.ForMasterClient"/> over the credentials' trust anchors, held
/// in <see cref="MasterValidators"/> (a <see cref="PeerValidatorSource"/>, read per handshake), with every refusal logged
/// (the master only sees a failed handshake, MN-010);</item>
/// <item>the options come from <see cref="PkiTls.CreateServerOptions"/>: client certificate required, custom-root
/// validation only, TLS 1.3/1.2, ALPN h2 + http/1.1.</item>
/// </list>
/// When the agent is not enrolled every handshake is refused.
/// </summary>
public sealed class AgentListenerTls
{
    private readonly IAgentCredentials _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentListenerTls> _logger;
    private readonly Lock _lock = new();
    private State? _state;

    /// <summary>Creates the listener state over the agent's credentials.</summary>
    public AgentListenerTls(IAgentCredentials credentials, TimeProvider time, ILogger<AgentListenerTls> logger)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _credentials = credentials;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The master validator the next handshake uses. Rebuilt from <see cref="IAgentCredentials.TrustAnchors"/> whenever
    /// <see cref="IAgentCredentials.Version"/> changes; <see cref="SetMasterTrust"/> swaps it explicitly
    /// (<c>PUT /pki/trust</c>, MN-036). Null until the first handshake or refresh with enrolled credentials.
    /// </summary>
    public PeerValidatorSource? MasterValidators => Volatile.Read(ref _state)?.Validators;

    /// <summary>
    /// Server options for one handshake (Kestrel <c>TlsHandshakeCallbackOptions.OnConnection</c>). Throws
    /// <see cref="InvalidOperationException"/> when the agent is not enrolled, which makes Kestrel abort the handshake.
    /// </summary>
    public SslServerAuthenticationOptions CreateServerOptions()
    {
        State state = Current();
        return PkiTls.CreateServerOptions(state.Context, state.Validators);
    }

    /// <summary>
    /// Replaces the master trust used from the next handshake on (CA rotation, design §10.2; P1 sets the agent's trust
    /// only through <c>PUT /pki/trust</c>). The credentials must be updated as well, or the next credentials change
    /// rebuilds the validator from <see cref="IAgentCredentials.TrustAnchors"/>.
    /// </summary>
    public void SetMasterTrust(TrustAnchorSet trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        State state = Current();
        state.Validators.Set(BuildValidator(trust));
    }

    /// <summary>Re-reads the credentials now if their version changed (otherwise the next handshake does).</summary>
    public void Refresh() => _ = Current();

    private State Current()
    {
        long version = _credentials.Version;
        State? state = Volatile.Read(ref _state);
        if (state is not null && state.Version == version)
        {
            return state;
        }

        lock (_lock)
        {
            version = _credentials.Version;
            state = _state;
            if (state is not null && state.Version == version)
            {
                return state;
            }

            if (!_credentials.IsEnrolled)
            {
                AgentHostLog.HandshakeWithoutCredentials(_logger);
                throw new InvalidOperationException("The agent is not enrolled; the mTLS listener refuses connections.");
            }

            X509Certificate2 certificate = _credentials.ServerCertificate;
            SslStreamCertificateContext context = PkiTls.CreateContext(certificate);
            PeerValidator validator;
            using (TrustAnchorSet trust = TrustAnchorSet.Create(_credentials.TrustAnchors))
            {
                validator = BuildValidator(trust);
            }

            PeerValidatorSource validators = state?.Validators ?? new PeerValidatorSource(validator);
            validators.Set(validator);
            State next = new(version, context, validators);
            Volatile.Write(ref _state, next);
            string spki = Spki.Sha256(certificate);
            AgentHostLog.ListenerCredentials(_logger, version, spki, _credentials.TrustAnchors.Count);

            return next;
        }
    }

    private PeerValidator BuildValidator(TrustAnchorSet trust) =>
        PeerValidator.ForMasterClient(trust, _time).WithRejectionObserver(result =>
            AgentHostLog.MasterCertificateRefused(_logger, result.Failure, result.Detail ?? "", result.Subject ?? "-", result.SpkiSha256 ?? "-"));

    private sealed record State(long Version, SslStreamCertificateContext Context, PeerValidatorSource Validators);
}
