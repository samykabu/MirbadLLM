using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Pki;

/// <summary>Which master certificates <see cref="MasterCertificates.ReissueIfDue"/> replaced.</summary>
[Flags]
public enum MasterCertificateChange
{
    /// <summary>Nothing was due.</summary>
    None = 0,

    /// <summary>The client certificate the master presents to agents.</summary>
    Client = 1,

    /// <summary>The enroll listener's server certificate.</summary>
    Enroll = 2,
}

/// <summary>
/// The master's own certificates (design §10.2 "Master certificates"): the client certificate presented to agents (SAN
/// <c>spiffe://mirbadllm/master</c>, EKU clientAuth) and the enroll listener's server certificate, both 30 days and
/// re-issued at half life by <see cref="ReissueIfDue"/>. The old certificate is not revoked: it stays valid until it
/// expires, so connections still using it (draining agent handlers, an in-flight enroll handshake) never see a gap.
/// <para>Kept in the PKI directory (<c>master-client.crt/.key</c>, <c>enroll.crt/.key</c>, keys 0600) when one is given,
/// so a restart does not re-issue; material that does not load, does not chain to the CA or has expired is replaced.</para>
/// <para>The enroll listener (MN-035/MN-040) should read <see cref="EnrollCertificateContext"/> per handshake to pick up
/// a re-issue; the agent client factory is updated by <see cref="CertificateRotationService"/>. Thread-safe.</para>
/// <para><b>CA rotation</b> (design §10.2, MN-116): <see cref="Authority"/> is the CA that issues from now on (the boot CA
/// until <see cref="ReissueUnder"/> switches it to the new CA at step 3). Before that switch the current client certificate
/// is kept as the <see cref="PreviousClientCredential"/> (<c>master-client.previous.crt/.key</c>), so the master can still
/// reach a node that has not added the new CA yet (it was offline at step 2) until <see cref="DropPreviousClient"/> at the
/// end of the rotation. This class never disposes a <see cref="CertificateAuthority"/>: their owners do.</para>
/// </summary>
public sealed class MasterCertificates : IDisposable
{
    /// <summary>Master client certificate file.</summary>
    public const string ClientCertificateFileName = "master-client.crt";

    /// <summary>Master client key file (0600).</summary>
    public const string ClientKeyFileName = "master-client.key";

    /// <summary>Enroll listener certificate file.</summary>
    public const string EnrollCertificateFileName = "enroll.crt";

    /// <summary>Enroll listener key file (0600).</summary>
    public const string EnrollKeyFileName = "enroll.key";

    /// <summary>The client certificate kept from before a CA rotation's re-issue (CA rotation, MN-116).</summary>
    public const string PreviousClientCertificateFileName = "master-client.previous.crt";

    /// <summary>Its key (0600).</summary>
    public const string PreviousClientKeyFileName = "master-client.previous.key";

    private CertificateAuthority _ca;
    private readonly string? _directory;
    private readonly string[] _enrollAddresses;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<IDisposable> _retired = [];
    private IssuedCredential _client;
    private EnrollMaterial _enroll;
    private IssuedCredential? _previousClient;

    private MasterCertificates(CertificateAuthority ca, string? directory, string[] enrollAddresses, TimeProvider time)
    {
        _ca = ca;
        _directory = directory;
        _enrollAddresses = enrollAddresses;
        _time = time;
        _client = LoadOrIssue(ClientCertificateFileName, ClientKeyFileName, PeerValidator.ForMasterClient, () => _ca.IssueMasterClientCertificate());
        _enroll = new EnrollMaterial(LoadOrIssue(EnrollCertificateFileName, EnrollKeyFileName, PeerValidator.ForEnrollListener,
            () => _ca.IssueEnrollServerCertificate(_enrollAddresses)));
        _previousClient = LoadPrevious();
    }

    /// <summary>The CA that issues the master certificates (and, through <see cref="CertificateRotationService"/>, agent certificates).</summary>
    public CertificateAuthority Authority
    {
        get
        {
            lock (_lock)
            {
                return _ca;
            }
        }
    }

    /// <summary>The client certificate kept from before a CA rotation's re-issue, or null. Do not dispose.</summary>
    public IssuedCredential? PreviousClientCredential
    {
        get
        {
            lock (_lock)
            {
                return _previousClient;
            }
        }
    }

    /// <summary>The client certificate (with key) the master presents to agents. Do not dispose; it is replaced on re-issue.</summary>
    public IssuedCredential ClientCredential
    {
        get
        {
            lock (_lock)
            {
                return _client;
            }
        }
    }

    /// <summary>The enroll listener's certificate (with key). Do not dispose.</summary>
    public IssuedCredential EnrollCredential
    {
        get
        {
            lock (_lock)
            {
                return _enroll.Credential;
            }
        }
    }

    /// <summary>The enroll listener's certificate context for <c>PkiTls.CreateServerOptions</c>, read per handshake.</summary>
    public SslStreamCertificateContext EnrollCertificateContext
    {
        get
        {
            lock (_lock)
            {
                return _enroll.Context;
            }
        }
    }

    /// <summary>
    /// Loads the master certificates from <paramref name="directory"/> (null = memory only) or issues them from
    /// <paramref name="ca"/>. <paramref name="enrollAddresses"/> are the enroll listener's DNS names / IPs.
    /// </summary>
    public static MasterCertificates LoadOrIssue(CertificateAuthority ca, string? directory, IEnumerable<string>? enrollAddresses = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ca);
        return new MasterCertificates(ca, directory, [.. enrollAddresses ?? []], time ?? TimeProvider.System);
    }

    /// <summary>
    /// Re-issues each certificate whose half life has passed (<see cref="PkiDefaults.MasterReissueDue"/>), persisting it
    /// first. Returns what changed.
    /// </summary>
    public MasterCertificateChange ReissueIfDue()
    {
        DateTimeOffset now = _time.GetUtcNow();
        MasterCertificateChange change = MasterCertificateChange.None;
        lock (_lock)
        {
            if (now >= PkiDefaults.MasterReissueDue(_client.PublicCertificate))
            {
                IssuedCredential next = _ca.IssueMasterClientCertificate();
                Persist(ClientCertificateFileName, ClientKeyFileName, next);
                _retired.Add(_client);
                _client = next;
                change |= MasterCertificateChange.Client;
            }

            if (now >= PkiDefaults.MasterReissueDue(_enroll.Credential.PublicCertificate))
            {
                IssuedCredential next = _ca.IssueEnrollServerCertificate(_enrollAddresses);
                Persist(EnrollCertificateFileName, EnrollKeyFileName, next);
                _retired.Add(_enroll);
                _enroll = new EnrollMaterial(next);
                change |= MasterCertificateChange.Enroll;
            }
        }

        return change;
    }

    /// <summary>True when both master certificates chain to <paramref name="authority"/> and are valid now.</summary>
    public bool IsIssuedBy(CertificateAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        using TrustAnchorSet trust = TrustAnchorSet.Create([authority.Certificate]);
        lock (_lock)
        {
            return PeerValidator.ForMasterClient(trust, _time).Validate(_client.PublicCertificate).IsValid &&
                   PeerValidator.ForEnrollListener(trust, _time).Validate(_enroll.Credential.PublicCertificate).IsValid;
        }
    }

    /// <summary>
    /// CA rotation step 3 (design §10.2): makes <paramref name="authority"/> the issuing CA and re-issues both master
    /// certificates under it (persisted first), unless both already chain to it (a resumed step). The old certificates
    /// stay valid until they expire (retired, not disposed). Returns what was re-issued.
    /// </summary>
    public MasterCertificateChange ReissueUnder(CertificateAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        bool issued = IsIssuedBy(authority);
        lock (_lock)
        {
            _ca = authority;
            if (issued)
            {
                return MasterCertificateChange.None;
            }

            IssuedCredential client = _ca.IssueMasterClientCertificate();
            IssuedCredential enroll = _ca.IssueEnrollServerCertificate(_enrollAddresses);
            Persist(ClientCertificateFileName, ClientKeyFileName, client);
            Persist(EnrollCertificateFileName, EnrollKeyFileName, enroll);
            _retired.Add(_client);
            _retired.Add(_enroll);
            _client = client;
            _enroll = new EnrollMaterial(enroll);
            return MasterCertificateChange.Client | MasterCertificateChange.Enroll;
        }
    }

    /// <summary>
    /// Keeps the current client certificate as <see cref="PreviousClientCredential"/> (persisted when a directory is set)
    /// unless one is kept already. Called before a CA rotation re-issues the client certificate.
    /// </summary>
    public IssuedCredential RetainPreviousClient()
    {
        lock (_lock)
        {
            if (_previousClient is not null)
            {
                return _previousClient;
            }

            IssuedCredential copy = TlsCredentials.LoadCredential(_client.CertificatePem, _client.ExportPrivateKeyPem());
            Persist(PreviousClientCertificateFileName, PreviousClientKeyFileName, copy);
            _previousClient = copy;
            return copy;
        }
    }

    /// <summary>Forgets the previous client certificate (end of a CA rotation): its files are deleted, the instance retired.</summary>
    public void DropPreviousClient()
    {
        lock (_lock)
        {
            if (_directory is not null)
            {
                // Key first: a crash in between leaves only a certificate, which LoadPrevious ignores.
                File.Delete(Path.Combine(_directory, PreviousClientKeyFileName));
                File.Delete(Path.Combine(_directory, PreviousClientCertificateFileName));
                MirbadLLM.Core.DirectorySync.TryFlushParent(Path.Combine(_directory, PreviousClientCertificateFileName));
            }

            if (_previousClient is not null)
            {
                _retired.Add(_previousClient);
                _previousClient = null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _previousClient?.Dispose();
            _client.Dispose();
            _enroll.Dispose();
            foreach (IDisposable retired in _retired)
            {
                retired.Dispose();
            }

            _retired.Clear();
        }
    }

    private IssuedCredential LoadOrIssue(string certificateFile, string keyFile, Func<TrustAnchorSet, TimeProvider?, PeerValidator> validatorFor, Func<IssuedCredential> issue)
    {
        if (_directory is not null)
        {
            string? certificatePem = PkiFiles.ReadIfExists(Path.Combine(_directory, certificateFile));
            string? keyPem = PkiFiles.ReadIfExists(Path.Combine(_directory, keyFile));
            if (certificatePem is not null && keyPem is not null)
            {
                IssuedCredential? loaded = null;
                try
                {
                    loaded = TlsCredentials.LoadCredential(certificatePem, keyPem);
                    using TrustAnchorSet trust = TrustAnchorSet.Create([_ca.Certificate]);
                    if (validatorFor(trust, _time).Validate(loaded.PublicCertificate).IsValid)
                    {
                        return loaded;
                    }
                }
                catch (Exception ex) when (ex is PkiException or CryptographicException)
                {
                    // Unreadable material is replaced below.
                }

                loaded?.Dispose();
            }
        }

        IssuedCredential issued = issue();
        Persist(certificateFile, keyFile, issued);
        return issued;
    }

    /// <summary>The kept previous client certificate, when its files hold a matching, unexpired pair.</summary>
    private IssuedCredential? LoadPrevious()
    {
        if (_directory is null)
        {
            return null;
        }

        string? certificatePem = PkiFiles.ReadIfExists(Path.Combine(_directory, PreviousClientCertificateFileName));
        string? keyPem = PkiFiles.ReadIfExists(Path.Combine(_directory, PreviousClientKeyFileName));
        if (certificatePem is null || keyPem is null)
        {
            return null;
        }

        try
        {
            IssuedCredential loaded = TlsCredentials.LoadCredential(certificatePem, keyPem);
            if (loaded.NotAfter > _time.GetUtcNow())
            {
                return loaded;
            }

            loaded.Dispose();
        }
        catch (Exception ex) when (ex is PkiException or CryptographicException)
        {
            // Unusable: the nodes that still need it must then be re-paired.
        }

        return null;
    }

    private void Persist(string certificateFile, string keyFile, IssuedCredential credential)
    {
        if (_directory is null)
        {
            return;
        }

        PkiFiles.EnsureDirectory(_directory);
        PkiFiles.WriteSecret(Path.Combine(_directory, keyFile), credential.ExportPrivateKeyPem());
        PkiFiles.WritePublic(Path.Combine(_directory, certificateFile), credential.CertificatePem);
    }

    private sealed class EnrollMaterial : IDisposable
    {
        private readonly X509Certificate2 _tls;

        public EnrollMaterial(IssuedCredential credential)
        {
            Credential = credential;
            _tls = credential.CreateTlsCertificate();
            Context = PkiTls.CreateContext(_tls);
        }

        public IssuedCredential Credential { get; }

        public SslStreamCertificateContext Context { get; }

        public void Dispose()
        {
            _tls.Dispose();
            Credential.Dispose();
        }
    }
}
