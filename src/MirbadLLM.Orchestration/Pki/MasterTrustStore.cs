using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration.Pki;

/// <summary>
/// <see cref="IMasterTrustAnchors"/> over <c>{DataPath}/pki/trust.json</c> (<see cref="MasterTrustDocument"/>, design §10.2
/// "CA rotation" step 1, D80): the roots the master validates agent certificates under (one normally, two during a CA
/// rotation) and the open or last <see cref="CaRotationRecord"/>, which <see cref="CaRotationService"/> persists before each
/// step it names. Absent file = the boot CA alone (P1); nothing is written until a rotation starts.
/// <para>Writes are atomic (temporary sibling flushed to disk, then moved over the file, parent directory flushed best
/// effort, D69) and change memory only after the file was replaced. <see cref="Version"/> increases whenever the anchors
/// change; the agent client factory rebuilds its handlers on the next call after a change. Public material only: the CA
/// keys live in their own files (<see cref="CaStore"/>, <see cref="NextCaKeyFileName"/>). Thread-safe.</para>
/// </summary>
public sealed class MasterTrustStore : IMasterTrustAnchors, IDisposable
{
    /// <summary>The new CA's certificate while a rotation is open (public).</summary>
    public const string NextCaCertificateFileName = "ca-next.crt";

    /// <summary>The new CA's key while a rotation is open (secret, 0600).</summary>
    public const string NextCaKeyFileName = "ca-next.key";

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _lock = new();
    private MasterTrustDocument _document;
    private IReadOnlyList<string> _anchorPems;
    private long _version = 1;

    private MasterTrustStore(string directory, MasterTrustDocument document)
    {
        Directory = directory;
        _path = Path.Combine(directory, MasterTrustDocument.FileName);
        _document = document;
        _anchorPems = [.. document.Anchors.Select(a => a.CaCertPem)];
    }

    /// <summary>The PKI directory.</summary>
    public string Directory { get; }

    /// <inheritdoc />
    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AnchorPems
    {
        get
        {
            lock (_lock)
            {
                return _anchorPems;
            }
        }
    }

    /// <summary>The document as last persisted.</summary>
    public MasterTrustDocument Document
    {
        get
        {
            lock (_lock)
            {
                return _document;
            }
        }
    }

    /// <summary>
    /// Loads <c>trust.json</c> from <paramref name="pkiDirectory"/>, or starts from <paramref name="bootCa"/> alone when it is
    /// absent. Every anchor is checked to be a CA whose SPKI matches its recorded pin. A file whose anchors do not include
    /// the boot CA (a CA replaced by hand or a restored backup) keeps its rotation record but trusts the boot CA alone.
    /// Throws <see cref="PkiException"/> for an unreadable file (the master must not guess its trust).
    /// </summary>
    public static MasterTrustStore Load(string pkiDirectory, X509Certificate2 bootCa, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pkiDirectory);
        ArgumentNullException.ThrowIfNull(bootCa);
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        string path = Path.Combine(pkiDirectory, MasterTrustDocument.FileName);
        MasterTrustAnchor boot = AnchorOf(bootCa, now);
        if (!File.Exists(path))
        {
            return new MasterTrustStore(pkiDirectory, new MasterTrustDocument { Anchors = [boot] });
        }

        MasterTrustDocument document;
        try
        {
            document = JsonSerializer.Deserialize<MasterTrustDocument>(File.ReadAllText(path, Encoding.UTF8), MasterStateJson.Options)
                ?? throw new JsonException("empty document");
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            throw new PkiException(PkiErrorKind.CaMaterialInvalid, $"{MasterTrustDocument.FileName} in '{pkiDirectory}' cannot be read: {ex.Message}");
        }

        if (document.SchemaVersion != MasterTrustDocument.CurrentSchemaVersion)
        {
            throw new PkiException(PkiErrorKind.CaMaterialInvalid, $"{MasterTrustDocument.FileName} has schema version {document.SchemaVersion}; this master reads {MasterTrustDocument.CurrentSchemaVersion}.");
        }

        Check(document.Anchors);
        if (!document.Anchors.Any(a => Spki.PinEquals(a.SpkiSha256, boot.SpkiSha256)))
        {
            document = document with { Anchors = [boot] };
        }

        return new MasterTrustStore(pkiDirectory, document);
    }

    /// <summary>The anchor entry of a CA certificate.</summary>
    public static MasterTrustAnchor AnchorOf(X509Certificate2 ca, DateTimeOffset addedAt)
    {
        ArgumentNullException.ThrowIfNull(ca);
        return new MasterTrustAnchor(ca.ExportCertificatePem(), Spki.Sha256(ca), addedAt);
    }

    /// <summary>The anchors as a trust set (the caller disposes it).</summary>
    public TrustAnchorSet CreateTrustSet() => TrustAnchorSet.FromPem(AnchorPems);

    /// <summary>
    /// Read-modify-write of the document, persisted before memory changes. Returns the stored document. The anchors are
    /// checked (1 to 2 CAs, pins matching); <see cref="Version"/> increases when they changed.
    /// </summary>
    public async Task<MasterTrustDocument> UpdateAsync(Func<MasterTrustDocument, MasterTrustDocument> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MasterTrustDocument current = Document;
            MasterTrustDocument next = update(current) with { SchemaVersion = MasterTrustDocument.CurrentSchemaVersion };
            Check(next.Anchors);
            Write(next);
            bool anchorsChanged = !next.Anchors.Select(a => a.SpkiSha256).SequenceEqual(current.Anchors.Select(a => a.SpkiSha256), StringComparer.Ordinal);
            lock (_lock)
            {
                _document = next;
                if (anchorsChanged)
                {
                    _anchorPems = [.. next.Anchors.Select(a => a.CaCertPem)];
                    _version++;
                }
            }

            return next;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// True while a CA rotation is open and <paramref name="nodeId"/> has not committed a certificate under the new CA
    /// (health reason <see cref="HealthReasonCodes.CaRotationPending"/>; the node monitor and the Nodes page read it).
    /// </summary>
    public bool IsPending(string nodeId) => IsPending(Document.Rotation, nodeId);

    /// <summary>The rule of <see cref="IsPending(string)"/> on a record.</summary>
    public static bool IsPending(CaRotationRecord? rotation, string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        return rotation is { } r && IsOpen(r.Stage) &&
               r.Nodes.FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal)) is not { CertificateRotated: true };
    }

    /// <summary>The Degraded reason text for <see cref="HealthReasonCodes.CaRotationPending"/>, or null.</summary>
    public string? PendingReason(string nodeId) => IsPending(nodeId)
        ? $"A CA rotation is open ({Document.Rotation!.RotationId}, stage {Document.Rotation.Stage}) and this node has not committed a certificate under the new CA."
        : null;

    /// <summary>True for a stage before <see cref="CaRotationStage.Closed"/>.</summary>
    public static bool IsOpen(CaRotationStage stage) => stage is not (CaRotationStage.Closed or CaRotationStage.ClosedEarly);

    /// <summary>
    /// Boot repair, called by the composition <b>before</b> <see cref="CaStore.LoadOrCreate"/> (MN-130): CA rotation step 3
    /// replaces <c>ca.key</c> and <c>ca.crt</c> with the new CA (two files, so a crash can leave them unmatched). When
    /// <c>trust.json</c> records a rotation at or past <see cref="CaRotationStage.MasterReissued"/> and the new CA's files
    /// are present and match the recorded pin, the promotion is completed here, so the master starts with the new CA.
    /// Returns true when it wrote anything. Never touches the files before that stage.
    /// </summary>
    public static bool RecoverCaFiles(string pkiDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(pkiDirectory);
        string trustPath = Path.Combine(pkiDirectory, MasterTrustDocument.FileName);
        string nextCert = Path.Combine(pkiDirectory, NextCaCertificateFileName);
        string nextKey = Path.Combine(pkiDirectory, NextCaKeyFileName);
        if (!File.Exists(trustPath) || !File.Exists(nextCert) || !File.Exists(nextKey))
        {
            return false;
        }

        CaRotationRecord? rotation;
        try
        {
            rotation = JsonSerializer.Deserialize<MasterTrustDocument>(File.ReadAllText(trustPath, Encoding.UTF8), MasterStateJson.Options)?.Rotation;
        }
        catch (JsonException)
        {
            return false; // Load reports the broken file.
        }

        if (rotation is null || rotation.Stage < CaRotationStage.MasterReissued)
        {
            return false;
        }

        string nextCertPem = File.ReadAllText(nextCert, Encoding.ASCII);
        string nextKeyPem = File.ReadAllText(nextKey, Encoding.ASCII);
        using (CertificateAuthority next = CertificateAuthority.Load(nextCertPem, nextKeyPem))
        {
            if (!Spki.PinEquals(next.SpkiSha256, rotation.NewCaSpkiSha256))
            {
                return false;
            }
        }

        return PromoteCaFiles(pkiDirectory, nextCertPem, nextKeyPem, hook: null);
    }

    /// <summary>
    /// Makes <c>ca.key</c>/<c>ca.crt</c> the given CA (key first, then certificate), skipping a file that already holds it.
    /// <paramref name="hook"/> is called between the two writes (fault injection).
    /// </summary>
    internal static bool PromoteCaFiles(string pkiDirectory, string certificatePem, string keyPem, Action? hook)
    {
        string certPath = Path.Combine(pkiDirectory, CaStore.CertificateFileName);
        string keyPath = Path.Combine(pkiDirectory, CaStore.KeyFileName);
        bool wrote = false;
        if (!SamePem(PkiFiles.ReadIfExists(keyPath), keyPem))
        {
            PkiFiles.WriteSecret(keyPath, keyPem);
            wrote = true;
        }

        hook?.Invoke();
        if (!SamePem(PkiFiles.ReadIfExists(certPath), certificatePem))
        {
            PkiFiles.WritePublic(certPath, certificatePem);
            wrote = true;
        }

        return wrote;
    }

    /// <inheritdoc />
    public void Dispose() => _writeGate.Dispose();

    private static bool SamePem(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim().ReplaceLineEndings("\n"), b.Trim().ReplaceLineEndings("\n"), StringComparison.Ordinal);

    private static void Check(IReadOnlyList<MasterTrustAnchor> anchors)
    {
        if (anchors.Count is 0 or > TrustAnchorSet.MaxAnchors)
        {
            throw new PkiException(PkiErrorKind.CaMaterialInvalid, $"{MasterTrustDocument.FileName} must hold 1 to {TrustAnchorSet.MaxAnchors} anchors.");
        }

        foreach (MasterTrustAnchor anchor in anchors)
        {
            try
            {
                using TrustAnchorSet one = TrustAnchorSet.FromPem([anchor.CaCertPem]);
                if (one.Anchors.Count != 1 || !one.ContainsSpki(anchor.SpkiSha256))
                {
                    throw new PkiException(PkiErrorKind.CaMaterialInvalid, $"A {MasterTrustDocument.FileName} anchor does not match its pin {anchor.SpkiSha256}.");
                }
            }
            catch (CryptographicException ex)
            {
                throw new PkiException(PkiErrorKind.CaMaterialInvalid, $"A {MasterTrustDocument.FileName} anchor is not a certificate: {ex.Message}");
            }
        }
    }

    private void Write(MasterTrustDocument document)
    {
        PkiFiles.EnsureDirectory(Directory);
        string json = JsonSerializer.Serialize(document, MasterStateJson.Options);
        string temp = Path.Combine(Directory, $".{MasterTrustDocument.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
            DirectorySync.TryFlushParent(_path);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Best effort; the name is unique and dot-prefixed.
            }

            throw;
        }
    }
}
