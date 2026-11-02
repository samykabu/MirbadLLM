using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Pki;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// <see cref="IAgentCredentials"/> read from the state directory (design §10.2): <c>agent.crt</c> + <c>agent.key</c>
/// (loaded with <see cref="TlsCredentials.FromPem"/>, i.e. the PKCS#12 round trip SChannel needs), <c>ca.crt</c> (one or
/// two CA certificates) and <c>node-uid</c>. The node id is read from the certificate's SAN URI
/// <c>spiffe://mirbadllm/node/{nodeId}</c>, so it can never disagree with what the master pinned.
/// <para>Enrollment (MN-035) and rotation (MN-036) write these files atomically (<see cref="PkiFiles"/>) and call
/// <see cref="Reload"/>, or register their own <see cref="IAgentCredentials"/> before
/// <see cref="AgentHostBuilder.Configure"/> (it only adds this one when none is registered).</para>
/// <para>A certificate without a non-empty <c>node-uid</c> counts as not enrolled (W2 review MN-092).</para>
/// <para>Material that fails to load leaves the agent not enrolled (<see cref="IsEnrolled"/> false; the listener then
/// refuses every handshake) rather than half-loaded. Certificates replaced by a reload are not disposed: existing TLS
/// connections may still use them.</para>
/// </summary>
public sealed class FileAgentCredentials : IAgentCredentials
{
    /// <summary>Agent certificate (PEM).</summary>
    public const string CertificateFileName = "agent.crt";

    /// <summary>Agent private key (PKCS#8 or SEC1 PEM, 0600).</summary>
    public const string KeyFileName = "agent.key";

    /// <summary>CA certificate(s) masters must chain to (PEM, one or two certificates).</summary>
    public const string CaFileName = "ca.crt";

    /// <summary>Node uid received at enrollment (plain text, one line).</summary>
    public const string NodeUidFileName = "node-uid";

    private readonly string _directory;
    private readonly ILogger<FileAgentCredentials> _logger;
    private readonly Lock _lock = new();
    private Snapshot _current = Snapshot.Empty;

    /// <summary>Loads the credentials from <paramref name="options"/>' state directory.</summary>
    public FileAgentCredentials(AgentHostOptions options, ILogger<FileAgentCredentials> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateDirectory);
        _directory = options.StateDirectory;
        _logger = logger;
        Reload();
    }

    /// <inheritdoc />
    public bool IsEnrolled => Volatile.Read(ref _current).Certificate is not null;

    /// <inheritdoc />
    public string NodeId => Volatile.Read(ref _current).NodeId;

    /// <inheritdoc />
    public string NodeUid => Volatile.Read(ref _current).NodeUid;

    /// <inheritdoc />
    public X509Certificate2 ServerCertificate =>
        Volatile.Read(ref _current).Certificate
        ?? throw new InvalidOperationException("The agent is not enrolled: no certificate, key or CA in the state directory.");

    /// <inheritdoc />
    public IReadOnlyList<X509Certificate2> TrustAnchors => Volatile.Read(ref _current).Anchors;

    /// <inheritdoc />
    public long Version => Volatile.Read(ref _current).Version;

    /// <summary>
    /// Re-reads the state directory and publishes the result (incrementing <see cref="Version"/>). New TLS connections use
    /// the new certificate and trust; established ones keep theirs (design §10.2 step 3). Returns <see cref="IsEnrolled"/>.
    /// A reload that finds missing or invalid files while valid credentials are loaded keeps the loaded ones (and logs
    /// why): a half-written rotation must not take the agent off the network (W2 review MN-092). Only an agent that was
    /// never enrolled stays without credentials.
    /// </summary>
    public bool Reload()
    {
        lock (_lock)
        {
            long version = _current.Version + 1;
            Snapshot next = Load(version);
            if (next.Certificate is null && _current.Certificate is not null)
            {
                return true;
            }

            Volatile.Write(ref _current, next);
            return next.Certificate is not null;
        }
    }

    private Snapshot Load(long version)
    {
        string? certificatePem = PkiFiles.ReadIfExists(Path.Combine(_directory, CertificateFileName));
        string? keyPem = PkiFiles.ReadIfExists(Path.Combine(_directory, KeyFileName));
        string? caPem = PkiFiles.ReadIfExists(Path.Combine(_directory, CaFileName));
        string nodeUid = PkiFiles.ReadIfExists(Path.Combine(_directory, NodeUidFileName))?.Trim() ?? "";
        // A certificate without its node uid is not an identity (W2 review MN-092): enrollment (MN-035) writes node-uid
        // before agent.crt, and /info and the operation log must never carry an empty uid.
        if (certificatePem is null || keyPem is null || caPem is null || nodeUid.Length == 0)
        {
            AgentHostLog.CredentialsMissing(_logger, _directory);
            return Snapshot.Empty with { Version = version, NodeUid = nodeUid };
        }

        X509Certificate2? certificate = null;
        try
        {
            certificate = TlsCredentials.FromPem(certificatePem, keyPem);
            if (!Protocol.TryParseNodeSpiffeId(CertificateSans.GetSingleUri(certificate), out string nodeId))
            {
                throw new PkiException(PkiErrorKind.InvalidCertificate, "The agent certificate has no single node SPIFFE id.");
            }

            List<X509Certificate2> anchors;
            using (TrustAnchorSet trust = TrustAnchorSet.FromPem([caPem]))
            {
                anchors = [.. trust.Anchors.Select(a => X509CertificateLoader.LoadCertificate(a.RawData))];
            }

            string spki = Spki.Sha256(certificate);
            DateTime notAfter = certificate.NotAfter.ToUniversalTime();
            AgentHostLog.CredentialsLoaded(_logger, nodeId, spki, notAfter, anchors.Count);

            return new Snapshot(version, certificate, anchors, nodeId, nodeUid);
        }
        catch (Exception ex) when (ex is PkiException or IOException or System.Security.Cryptography.CryptographicException)
        {
            certificate?.Dispose();
            AgentHostLog.CredentialsInvalid(_logger, ex, _directory);
            return Snapshot.Empty with { Version = version, NodeUid = nodeUid };
        }
    }

    private sealed record Snapshot(long Version, X509Certificate2? Certificate, IReadOnlyList<X509Certificate2> Anchors, string NodeId, string NodeUid)
    {
        public static Snapshot Empty { get; } = new(0, null, [], "", "");
    }
}
