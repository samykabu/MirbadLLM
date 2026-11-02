using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Pki;

/// <summary>Why a peer certificate was refused.</summary>
public enum PeerValidationFailure
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>The peer presented no certificate.</summary>
    NoCertificate,

    /// <summary>The presented certificate is a CA, not an end-entity certificate.</summary>
    NotEndEntity,

    /// <summary>Outside its validity period (expired or not yet valid) at the validator's clock.</summary>
    NotTimeValid,

    /// <summary>The chain does not end, directly, in one of the trusted roots (wrong CA, bad signature, unknown CA hash).</summary>
    UntrustedChain,

    /// <summary>The certificate lacks the required extended key usage.</summary>
    WrongEku,

    /// <summary>The certificate does not carry exactly one URI SAN equal to the expected SPIFFE id.</summary>
    WrongSan,

    /// <summary>The certificate's SPKI hash is not one of the accepted pins.</summary>
    PinMismatch,
}

/// <summary>Outcome of a peer validation. Holds public data only (safe to log).</summary>
/// <param name="Failure">Why it failed; <see cref="PeerValidationFailure.None"/> on success.</param>
/// <param name="Detail">Human-readable detail (public certificate data only).</param>
/// <param name="Subject">The presented certificate's subject, when there was one.</param>
/// <param name="SpkiSha256">The presented certificate's SPKI pin, when there was one.</param>
public sealed record PeerValidationResult(PeerValidationFailure Failure, string? Detail, string? Subject, string? SpkiSha256)
{
    /// <summary>True when the peer is accepted.</summary>
    public bool IsValid => Failure == PeerValidationFailure.None;
}

/// <summary>
/// TLS peer validation used by both sides (design §3.1, §3.2, §4.2): a chain that ends <b>directly</b> in one of the
/// configured roots (custom root trust only, never system roots, no revocation lookups, no downloads), the leaf within
/// validity at the validator's clock, not a CA, carrying the required EKU, exactly one URI SAN equal to the expected
/// SPIFFE id (ordinal), and, when pins are configured, an SPKI hash equal to one of them (current or pending pin during a
/// rotation). Hostname checks are replaced by these checks, so TLS name-mismatch errors are ignored.
/// <para>Immutable and thread-safe: a pin or trust change builds a new validator. Long-lived TLS endpoints hold it in a
/// <see cref="PeerValidatorSource"/>, whose callbacks read the current validator on every handshake, so a pending pin
/// (§10.2 step 2) or a new trust anchor (<c>PUT /pki/trust</c>) applies to the next connection without rebuilding the
/// handler or the Kestrel listener (MN-090; MN-010 finding). Plug it
/// into Kestrel with <see cref="ValidateClientCertificate"/> (<c>HttpsConnectionAdapterOptions.ClientCertificateValidation</c>)
/// and into <c>SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback</c> / <c>SslStream</c> with
/// <see cref="ValidateRemoteCertificate"/>.</para>
/// </summary>
public sealed class PeerValidator
{
    private readonly X509Certificate2[] _roots;
    private readonly string[] _rootPins;
    private readonly string[] _pins;
    private readonly TimeProvider _time;
    private readonly Action<PeerValidationResult>? _onRejected;
    private readonly Action<PeerValidationResult>? _onResult;

    private PeerValidator(
        X509Certificate2[] roots,
        string[] rootPins,
        string expectedSpiffeId,
        string requiredEkuOid,
        string[] pins,
        TimeProvider time,
        Action<PeerValidationResult>? onRejected,
        Action<PeerValidationResult>? onResult = null)
    {
        _roots = roots;
        _rootPins = rootPins;
        ExpectedSpiffeId = expectedSpiffeId;
        RequiredEkuOid = requiredEkuOid;
        _pins = pins;
        _time = time;
        _onRejected = onRejected;
        _onResult = onResult;
    }

    /// <summary>The URI SAN required (exactly; the pre-rename trust domain's form is accepted too).</summary>
    public string ExpectedSpiffeId { get; }

    /// <summary>The EKU OID required.</summary>
    public string RequiredEkuOid { get; }

    /// <summary>Accepted SPKI pins; empty when the validator does not pin the leaf.</summary>
    public IReadOnlyList<string> Pins => _pins;

    /// <summary>
    /// Master → agent (design §4.2 "Master (client)"): roots = the master's CA trust (both CAs during a CA rotation), EKU
    /// serverAuth, SAN exactly <c>spiffe://mirbadllm/node/{nodeId}</c>, SPKI equal to one of <paramref name="pins"/>
    /// (current, plus pending during a rotation). At least one pin is required: the master never talks to an unpinned agent.
    /// </summary>
    public static PeerValidator ForAgent(string nodeId, TrustAnchorSet roots, IEnumerable<string> pins, TimeProvider? time = null)
    {
        if (!Protocol.IsValidNodeId(nodeId))
        {
            throw new PkiException(PkiErrorKind.InvalidNodeId, "The node id breaks the node id rule.");
        }

        ArgumentNullException.ThrowIfNull(pins);
        string[] pinList = [.. pins.Where(p => p is not null).Distinct(StringComparer.Ordinal)];
        if (pinList.Length == 0)
        {
            throw new ArgumentException("At least one SPKI pin is required to validate an agent.", nameof(pins));
        }

        foreach (string pin in pinList)
        {
            if (!Spki.IsValidPin(pin))
            {
                throw new ArgumentException("A pin is not a base64 SHA-256 value.", nameof(pins));
            }
        }

        return new PeerValidator(CopyRoots(roots), [], Protocol.NodeSpiffeId(nodeId), PkiDefaults.ServerAuthOid, pinList, time ?? TimeProvider.System, null);
    }

    /// <summary>
    /// Agent ← master client certificate (design §4.2 "Agent (server)"): roots = the agent's trust anchors (two during a
    /// CA rotation), EKU clientAuth, SAN exactly <c>spiffe://mirbadllm/master</c>. No leaf pin (the master certificate is
    /// re-issued every 15 days).
    /// </summary>
    public static PeerValidator ForMasterClient(TrustAnchorSet roots, TimeProvider? time = null) =>
        new(CopyRoots(roots), [], Protocol.MasterSpiffeId, PkiDefaults.ClientAuthOid, [], time ?? TimeProvider.System, null);

    /// <summary>
    /// Joining agent → enroll listener (design §3.2) when only the CA hash is known (<c>--ca-hash sha256:H</c>): the root
    /// is a self-signed CA whose SPKI hash equals <paramref name="caSpkiSha256"/>, taken from
    /// <paramref name="knownCaCertificates"/>, from the CA certificate embedded in the presented leaf
    /// (<see cref="PkiDefaults.EmbeddedCaCertificateOid"/>, set by <see cref="CertificateAuthority.IssueEnrollServerCertificate"/>),
    /// or from the certificates the server sent (SChannel forwards them; OpenSSL on Linux drops a self-signed root from the
    /// sent chain, which is why the leaf carries the CA). The source never grants trust: only the pinned hash does, and the
    /// leaf must still chain directly to that CA. EKU serverAuth; SAN exactly <c>spiffe://mirbadllm/master</c>. Send the
    /// token only after this accepts.
    /// </summary>
    public static PeerValidator ForEnrollListener(string caSpkiSha256, IEnumerable<X509Certificate2>? knownCaCertificates = null, TimeProvider? time = null)
    {
        if (!Spki.TryParseJoinHash(caSpkiSha256, out string pin))
        {
            throw new ArgumentException("The CA hash is not a SHA-256 value.", nameof(caSpkiSha256));
        }

        X509Certificate2[] known = knownCaCertificates is null ? [] : [.. knownCaCertificates.Select(c => X509CertificateLoader.LoadCertificate(c.RawData))];
        return new PeerValidator(known, [pin], Protocol.MasterSpiffeId, PkiDefaults.ServerAuthOid, [], time ?? TimeProvider.System, null);
    }

    /// <summary>
    /// Agent → enroll listener when the CA certificate is known (bootstrap bundle): custom root = <paramref name="roots"/>,
    /// EKU serverAuth, SAN exactly <c>spiffe://mirbadllm/master</c>.
    /// </summary>
    public static PeerValidator ForEnrollListener(TrustAnchorSet roots, TimeProvider? time = null) =>
        new(CopyRoots(roots), [], Protocol.MasterSpiffeId, PkiDefaults.ServerAuthOid, [], time ?? TimeProvider.System, null);

    /// <summary>A copy of this validator that reports every rejection to <paramref name="observer"/> (for logging; results hold public data only).</summary>
    public PeerValidator WithRejectionObserver(Action<PeerValidationResult> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new PeerValidator(_roots, _rootPins, ExpectedSpiffeId, RequiredEkuOid, _pins, _time, observer, _onResult);
    }

    /// <summary>
    /// A copy of this validator that reports <b>every</b> result, accepted or not, to <paramref name="observer"/> (added by
    /// MN-090 for MN-021: a handshake that fails with <c>SecureConnectionError</c> after the validator <i>accepted</i> the
    /// agent points at ALPN/protocol, "HTTP/2 not negotiated", not at the agent's identity). Keeps any rejection observer.
    /// </summary>
    public PeerValidator WithResultObserver(Action<PeerValidationResult> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new PeerValidator(_roots, _rootPins, ExpectedSpiffeId, RequiredEkuOid, _pins, _time, _onRejected, observer);
    }

    /// <summary>
    /// Kestrel <c>ClientCertificateValidation</c> shape. Platform chain errors are ignored: the chain is rebuilt here
    /// against the custom roots only.
    /// </summary>
    public bool ValidateClientCertificate(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors) =>
        Report(Validate(certificate, PeerCertificates(chain))).IsValid;

    /// <summary>
    /// <see cref="RemoteCertificateValidationCallback"/> shape (<c>SocketsHttpHandler.SslOptions</c>, <c>SslStream</c>).
    /// Name-mismatch and platform chain errors are ignored (replaced by the SAN, custom-root and pin checks); a missing
    /// certificate is refused.
    /// </summary>
    public bool ValidateRemoteCertificate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null || sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return Report(Fail(PeerValidationFailure.NoCertificate, "The peer presented no certificate.", null)).IsValid;
        }

        if (certificate is X509Certificate2 typed)
        {
            return Report(Validate(typed, PeerCertificates(chain))).IsValid;
        }

        using X509Certificate2 converted = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Report(Validate(converted, PeerCertificates(chain))).IsValid;
    }

    /// <summary>
    /// Validates a peer certificate. <paramref name="peerSuppliedCertificates"/> are only used, in CA-hash mode
    /// (<see cref="ForEnrollListener(string, IEnumerable{X509Certificate2}?, TimeProvider?)"/>), as candidates for the
    /// pinned root; they never extend trust otherwise.
    /// </summary>
    public PeerValidationResult Validate(X509Certificate2? certificate, IEnumerable<X509Certificate2>? peerSuppliedCertificates = null)
    {
        if (certificate is null)
        {
            return Fail(PeerValidationFailure.NoCertificate, "The peer presented no certificate.", null);
        }

        if (IsCa(certificate))
        {
            return Fail(PeerValidationFailure.NotEndEntity, "The peer certificate is a CA certificate.", certificate);
        }

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset notBefore = new(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        DateTimeOffset notAfter = new(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (now < notBefore || now > notAfter)
        {
            return Fail(PeerValidationFailure.NotTimeValid, $"Valid {notBefore:O} to {notAfter:O}; now {now:O}.", certificate);
        }

        X509Certificate2[] roots = ResolveRoots(certificate, peerSuppliedCertificates, out List<X509Certificate2> owned);
        try
        {
            if (roots.Length == 0)
            {
                return Fail(PeerValidationFailure.UntrustedChain, "No CA with the expected SPKI hash was presented.", certificate);
            }

            string? chainProblem = CheckChain(certificate, roots, now);
            if (chainProblem is not null)
            {
                return Fail(PeerValidationFailure.UntrustedChain, chainProblem, certificate);
            }
        }
        finally
        {
            foreach (X509Certificate2 candidate in owned)
            {
                candidate.Dispose();
            }
        }

        if (!HasEku(certificate, RequiredEkuOid))
        {
            return Fail(PeerValidationFailure.WrongEku, $"The certificate does not allow {RequiredEkuOid}.", certificate);
        }

        string? uri = CertificateSans.GetSingleUri(certificate);
        if (!Protocol.SpiffeIdMatches(uri, ExpectedSpiffeId))
        {
            return Fail(PeerValidationFailure.WrongSan, $"Expected exactly one URI SAN '{ExpectedSpiffeId}'.", certificate);
        }

        if (_pins.Length > 0 && !Spki.MatchesAny(certificate, _pins))
        {
            return Fail(PeerValidationFailure.PinMismatch, "The certificate key does not match the pinned key.", certificate);
        }

        return new PeerValidationResult(PeerValidationFailure.None, null, certificate.Subject, Spki.Sha256(certificate));
    }

    private static X509Certificate2[] CopyRoots(TrustAnchorSet roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return [.. roots.Anchors.Select(a => X509CertificateLoader.LoadCertificate(a.RawData))];
    }

    private static List<X509Certificate2> PeerCertificates(X509Chain? chain)
    {
        List<X509Certificate2> result = [];
        if (chain is null)
        {
            return result;
        }

        result.AddRange(chain.ChainPolicy.ExtraStore);
        foreach (X509ChainElement element in chain.ChainElements)
        {
            result.Add(element.Certificate);
        }

        return result;
    }

    private X509Certificate2[] ResolveRoots(X509Certificate2 leaf, IEnumerable<X509Certificate2>? peerSupplied, out List<X509Certificate2> owned)
    {
        owned = [];
        if (_rootPins.Length == 0)
        {
            return _roots;
        }

        // CA-hash mode: pick self-signed CA certificates whose SPKI hash is pinned. The pin, not the source, grants trust.
        List<X509Certificate2> selected = [];
        X509Certificate2? embedded = ReadEmbeddedCa(leaf);
        if (embedded is not null)
        {
            owned.Add(embedded);
        }

        IEnumerable<X509Certificate2> candidates = _roots.Concat(embedded is null ? [] : [embedded]).Concat(peerSupplied ?? []);
        foreach (X509Certificate2 candidate in candidates)
        {
            if (!_rootPins.Any(pin => Spki.Matches(candidate, pin)))
            {
                continue;
            }

            try
            {
                CertificateAuthority.EnsureIsCa(candidate);
            }
            catch (PkiException)
            {
                continue;
            }

            if (selected.Any(s => s.RawData.AsSpan().SequenceEqual(candidate.RawData)))
            {
                continue;
            }

            X509Certificate2 copy = X509CertificateLoader.LoadCertificate(candidate.RawData);
            owned.Add(copy);
            selected.Add(copy);
        }

        return [.. selected];
    }

    private static X509Certificate2? ReadEmbeddedCa(X509Certificate2 leaf)
    {
        X509Extension? found = null;
        foreach (X509Extension extension in leaf.Extensions)
        {
            if (extension.Oid?.Value == PkiDefaults.EmbeddedCaCertificateOid)
            {
                if (found is not null)
                {
                    return null;
                }

                found = extension;
            }
        }

        if (found is null || found.RawData.Length > 8 * 1024)
        {
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(found.RawData);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static string? CheckChain(X509Certificate2 leaf, X509Certificate2[] roots, DateTimeOffset now)
    {
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.VerificationTimeIgnored = false;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        try
        {
            bool built = chain.Build(leaf);
            X509ChainStatusFlags flags = X509ChainStatusFlags.NoError;
            foreach (X509ChainStatus status in chain.ChainStatus)
            {
                flags |= status.Status;
            }

            if (!built || flags != X509ChainStatusFlags.NoError)
            {
                return $"The chain does not validate to a trusted root ({flags}).";
            }

            // This PKI has no intermediates: the leaf must be issued directly by a trusted root.
            if (chain.ChainElements.Count != 2)
            {
                return "The certificate is not issued directly by a trusted CA.";
            }

            X509Certificate2 anchor = chain.ChainElements[^1].Certificate;
            if (!roots.Any(r => r.RawData.AsSpan().SequenceEqual(anchor.RawData)))
            {
                return "The chain ends in an untrusted root.";
            }

            return null;
        }
        finally
        {
            foreach (X509ChainElement element in chain.ChainElements)
            {
                element.Certificate.Dispose();
            }
        }
    }

    private static bool IsCa(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority);

    private static bool HasEku(X509Certificate2 certificate, string oid)
    {
        bool found = false;
        foreach (X509EnhancedKeyUsageExtension eku in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
        {
            foreach (System.Security.Cryptography.Oid usage in eku.EnhancedKeyUsages)
            {
                found |= usage.Value == oid;
            }
        }

        return found;
    }

    private static PeerValidationResult Fail(PeerValidationFailure failure, string detail, X509Certificate2? certificate) =>
        new(failure, detail, certificate?.Subject, certificate is null ? null : Spki.Sha256(certificate));

    private PeerValidationResult Report(PeerValidationResult result)
    {
        if (!result.IsValid)
        {
            _onRejected?.Invoke(result);
        }

        _onResult?.Invoke(result);
        return result;
    }
}
