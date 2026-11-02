using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>
/// The master's TLS material for agent connections (design §7.2, §10.2): the client certificate (created <b>once</b> per
/// credential with <see cref="IssuedCredential.CreateTlsCertificate"/>, MN-090: Windows persists a key container per load),
/// its <see cref="SslStreamCertificateContext"/>, and a private copy of the trust anchors used to validate agents.
/// <para>Reference counted: the factory holds one reference while the material is current and every handler generation
/// built from it holds one; the anchors are disposed when the last one is released.</para>
/// <para>The client certificate is <b>never disposed here</b>: its lifetime is its reachability. Disposing the handlers
/// does not wait for connection attempts already running (an attempt outlives the request that started it, e.g. a
/// cancelled <c>/info</c> poll), and on Windows such an attempt hands the certificate's native context to SChannel
/// (<c>AcquireCredentialsHandle</c>) without holding a reference to it. Disposing it then is a use-after-free that kills
/// the process (0xC0000005 in <c>schannel!SslLoadCertificate</c>, seen in the test suite, MN-047). Once no handshake
/// references the <see cref="Context"/> any more, the finalizer frees it and deletes its key.</para>
/// </summary>
internal sealed class MasterTlsMaterial
{
    private readonly X509Certificate2 _tlsCertificate;
    private int _references = 1;

    public MasterTlsMaterial(IssuedCredential credential, TrustAnchorSet trust)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(trust);
        _tlsCertificate = credential.CreateTlsCertificate();
        try
        {
            Context = PkiTls.CreateContext(_tlsCertificate);
            Trust = TrustAnchorSet.Create(trust.Anchors);
            ClientSpkiSha256 = credential.SpkiSha256;
        }
        catch
        {
            _tlsCertificate.Dispose();
            throw;
        }
    }

    /// <summary>The client certificate context every agent handler presents.</summary>
    public SslStreamCertificateContext Context { get; }

    /// <summary>The master's trust anchors for agent certificates (one CA, two during a CA rotation).</summary>
    public TrustAnchorSet Trust { get; }

    /// <summary>SPKI pin of the master client certificate (diagnostics).</summary>
    public string ClientSpkiSha256 { get; }

    /// <summary>True once every reference was released.</summary>
    public bool IsDisposed => Volatile.Read(ref _references) <= 0;

    /// <summary>Adds a reference. Only valid while the caller already holds one (the factory's, under its lock).</summary>
    public MasterTlsMaterial Acquire()
    {
        ObjectDisposedException.ThrowIf(Interlocked.Increment(ref _references) <= 1, this);
        return this;
    }

    /// <summary>
    /// Drops a reference; the last one disposes the anchors (validators hold their own copies). The certificate is left to
    /// the finalizer, see the remarks: a background handshake may still be using it.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            Trust.Dispose();
        }
    }
}
